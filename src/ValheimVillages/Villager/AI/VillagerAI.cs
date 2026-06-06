using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Behaviors;
using ValheimVillages.Behaviors.Work;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.Tags;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager.AI.Memory;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villager.Registry;
using Random = UnityEngine.Random;

namespace ValheimVillages.Villager.AI
{
    public partial class VillagerAI : BaseAI, IVillagerWorkContext
    {

        private const float SaveInterval = 60f;

        /// <summary>
        ///     Vertical/spatial radius for snapping a NavTo destination onto the
        ///     agent navmesh. Sized to catch approach points resolved up to ~2m
        ///     above the walkable surface (chest/station Y over the floor)
        ///     without mapping to a different level.
        /// </summary>
        private const float NavToSnapRadius = 2f;



        private NavMeshAgent m_navAgent;

        private Vector3 m_bedPosition;

        // Composable behaviors (populated by BehaviorFactory from NPC definition)
        private List<IBehavior> m_behaviors = new();

        private VillagerWaypoint m_currentWaypoint;
        private DoorHandler m_doorHandler;

        // Exploration
        private float m_explorationStartTime;
        private Vector3? m_explorationTarget;


        private float m_lastBehaviorUpdateTime;

        // Timing
        private float m_lastDiscoveryTime;
        private float m_lastMemorySaveTime;


        // True while a DIRECT ORDER (manual/scripted NavTo, e.g. the debug
        // "Go to Bed" button) is in flight. Outranks autonomous behavior:
        // behavior selection + the idle fallback are skipped so the order
        // isn't reset back to Idle. Set by NavTo(directOrder:true); cleared on
        // arrival (OnArrivedAtTarget), after which normal task-queue behavior
        // resumes.
        private bool m_directOrderActive;

        // Path-stall dedup: log "entered" once, "resolved" / "escalated" once,
        // instead of re-firing the diagnostic every PathStallEscapeSeconds.
        private bool m_stallLogged;
        private float m_stallStartTime;

        /// <summary>
        ///     Per-villager 30s ring buffer of AI state mutations. Read by
        ///     the incident dump system to answer "what was this villager
        ///     doing in the seconds before the failure?" — distinguishes
        ///     "TargetSet fired but PathRecompute never followed" (the path-
        ///     invalidation bug shape) from "PathRecompute returned Empty"
        ///     and similar runtime distinctions log-grepping can't easily
        ///     produce. Populated inline at SetState; consumed by
        ///     IncidentRecorder.
        /// </summary>
        internal readonly Diagnostics.AiEventRing EventRing = new Diagnostics.AiEventRing();

        /// <summary>
        ///     When set in the future, behavior selectors (notably Explore)
        ///     should leave the villager idle in place rather than walking
        ///     them off to a known location. Set by workflows that finish a
        ///     work step but expect to resume shortly (smelter polling,
        ///     cooking station polling) so the villager doesn't visibly
        ///     walk to the fire and immediately walk back. Cleared
        ///     implicitly by passage of Time.time past LingerUntilTime.
        /// </summary>
        public float LingerUntilTime { get; set; }

        /// <summary>
        ///     World position the villager should idle at while
        ///     <see cref="LingerUntilTime"/> is in the future. Set by the
        ///     workflow that armed the linger; typically the position of
        ///     the station that's still processing.
        /// </summary>
        public Vector3 LingerAtPos { get; set; }

        /// <summary>True if a linger window is currently active.</summary>
        public bool IsLingering => Time.time < LingerUntilTime;

        /// <summary>
        ///     True when the current movement was initiated by a low-priority
        ///     behavior (Explore — going to fire / shelter / wander spot)
        ///     rather than a work behavior. Used by the path-follow loop to
        ///     pick a walk vs. run speed: casual travel walks (visual cue
        ///     that the villager isn't busy), work travel runs when the
        ///     destination is more than a few meters away. Set by the
        ///     initiating behavior; cleared by <see cref="SetState"/> when
        ///     the next non-casual waypoint is assigned.
        /// </summary>
        public bool IsCasualTravel { get; set; }

        private float m_stuckBackoffUntil;



        private string m_villagerName;
        private List<Vector3> m_waypointPath;


        public VillagerAI(Villager instance)
        {
            Villager = instance;
            UniqueId = Villager.uid;
            m_bedPosition = Villager.BedPosition;
            VillagerType = Villager.villagerType;
            m_villagerName = Villager.villagerName;
            Memory = new VillagerMemory(m_bedPosition);
        }

        /// <summary>
        ///     Parameterless constructor for Unity AddComponent. Initialization happens in Awake from Villager component.
        /// </summary>
        public VillagerAI()
        {
        }

        private DoorHandler DoorHandler => m_doorHandler ??= GetComponent<DoorHandler>();

        protected override void Awake()
        {
            try
            {
                base.Awake();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[VillagerAI] base.Awake() threw: {ex.GetType().Name}: {ex.Message}");
            }

            if (VillagerAgentType.EnsureRegistered())
                m_pathAgentType = VillagerAgentType.AgentType;

            if (Villager == null)
            {
                Villager = GetComponent<Villager>();
                if (Villager == null)
                {
                    Plugin.Log?.LogError("[VillagerAI] No Villager component on this GameObject");
                    return;
                }

                UniqueId = Villager.uid;
                m_bedPosition = Villager.BedPosition;
                VillagerType = Villager.villagerType;
                m_villagerName = Villager.villagerName;
                Memory = new VillagerMemory(m_bedPosition);
                VillagerAIManager.RegisterActive(this);
            }

            m_doorHandler = GetComponent<DoorHandler>();

            RegisterOwnedBed();
            RegisterBehaviors();


            // Stagger behavior ticks so NPCs spawned together don't all evaluate at the same time.
            // This is a countdown timer: 0 means "ready to run", positive means "wait this many more seconds".
            m_lastBehaviorUpdateTime = Random.Range(0f, VillagerSettings.BehaviorTickJitter);
        }

        private void OnDestroy()
        {
            try
            {
                var zdo = GetComponent<ZNetView>()?.GetZDO();
                if (zdo != null) SaveMemories(zdo);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[VillagerAI] OnDestroy SaveMemories failed: {ex.Message}");
            }

            VillagerAIManager.Unregister(this);
        }

        public void RegisterOwnedBed()
        {
            Memory.BedPosition = m_bedPosition;
        }

        private bool m_warnedNoBed;

        public override bool UpdateAI(float dt)
        {
            if (Villager == null) return false;
            if (!base.UpdateAI(dt)) return false;

            // A villager whose home (bed) never resolved to a real position is broken
            // (a half-initialised / zombie ZDO). Running its movement AI aims the
            // off-mesh rescue at world origin, where BaseAI.MoveTo throws an NRE every
            // frame — flooding the log and tanking the frame rate. Skip the tick
            // (logged once) instead of spamming a per-frame crash.
            if (m_bedPosition == Vector3.zero)
            {
                if (!m_warnedNoBed)
                {
                    m_warnedNoBed = true;
                    Plugin.Log?.LogWarning(
                        $"[AI:{m_villagerName}] No valid bed position — skipping AI tick (broken/zombie villager).");
                }

                return false;
            }
            if (m_lastBehaviorUpdateTime > 0.0)
            {
                m_lastBehaviorUpdateTime -= dt;
                // Don't return — we still want path-follow / discovery /
                // memory save to run every tick. The cooldown only gates
                // BEHAVIOR SELECTION (which target to pursue), not movement.
                // Previously this branch returned false, which (combined
                // with the timer never being reset post-construction)
                // meant cooldown=0 was the steady state and behavior
                // selection thrashed every tick. Now: timer ticks down,
                // behavior selection skipped while > 0, path-follow runs
                // through.
            }

            if (IsPaused) return true;

            // Hold movement across a navmesh / region-graph rebuild. The bake is
            // synchronous, so the hazard is the frames right after it: a path
            // computed on the OLD mesh can steer the villager off a ledge before
            // autoRepath corrects it. Stop and drop the stale path while held;
            // CurrentState + m_currentWaypoint are untouched, so the villager
            // resumes its prior task on the fresh mesh when the hold expires.
            if (Navigation.VillageNavLock.IsHeld)
            {
                StopMoving();
                if (m_navAgent != null && m_navAgent.isOnNavMesh)
                    m_navAgent.ResetPath();
                return true;
            }

            // Off-mesh rescue runs ahead of behavior selection and the agent mover:
            // a villager stranded off the village mesh walks itself back over
            // terrain before anything else gets to act on the un-pathable position.
            if (TryOffMeshRescue(dt)) return true;

            // Shared PoIs are discovered at the village level now; the only
            // per-villager thing left to sample is the comfort the villager
            // is currently experiencing (kept for save/load + future use).
            if (Time.time - m_lastDiscoveryTime > 4f)
            {
                m_lastDiscoveryTime = Time.time;
                VillagerComfort.UpdateExperiencedComfort(transform, Memory);
            }

            // Behavior selection: gated by m_lastBehaviorUpdateTime. The
            // path-follow loop below runs every tick regardless. This split
            // is what stops the visible "twitchiness" — a villager mid-
            // Traveling shouldn't be re-evaluating "do I really want to
            // travel?" 50 times per second when its target is 5m away.
            if (m_lastBehaviorUpdateTime <= 0f)
            {
                // Off-mesh rescue: if the villager is positioned off the
                // NavMesh (spawned on top of a bed, bumped off by terrain
                // change, falling object, etc), every path query will fail
                // and they'll be stuck. Find the nearest valid mesh point
                // and walk there as the first action — preempts whatever
                // behavior would otherwise run. Returns false when the
                // villager is already on the mesh (the common case);
                // returns true and sets a movement target when a rescue
                // was needed.
                // A direct order (manual/scripted NavTo, e.g. the debug "Go to
                // Bed" button) outranks autonomous behavior: while one is in
                // flight, skip behavior selection AND the idle fallback entirely
                // so the order isn't clobbered back to Idle. The movement tick
                // below still drives the directed move. The flag is cleared on
                // arrival (OnArrivedAtTarget), after which normal task-queue
                // behavior resumes on the next tick.
                if (m_directOrderActive)
                {
                    // Hold the directed move — nothing to (re)select.
                }
                else
                {
                    var ctx = new BehaviorContext();
                    var handled = false;
                    foreach (var b in m_behaviors)
                        if (b.WantsControl(ctx))
                        {
                            ActiveBehavior = b;
                            b.Update(dt);
                            handled = true;
                            break;
                        }

                    // No behavior wanted control — the villager is idle. Trigger
                    // work scanning here (this used to live in the always-on
                    // Explore fallback, which has been removed) and settle to
                    // Idle. Crafting takes over on the next tick once a scan
                    // result flips the state to Working.
                    if (!handled)
                    {
                        ActiveBehavior = null;
                        if (CurrentState != BehaviorState.Working)
                            GetWorkScanner()?.TryScanForWork();
                        if (CurrentState != BehaviorState.Idle)
                            SetState(BehaviorState.Idle);
                    }
                }
                // Reset cooldown. Idle re-evaluation cadence — high enough
                // to stop the thrash, low enough to react to player input
                // (work orders, manual relocate) within a noticeable window.
                m_lastBehaviorUpdateTime = VillagerSettings.BehaviorReselectIntervalSec;
            }

            if (Time.time - m_lastMemorySaveTime > SaveInterval)
            {
                m_lastMemorySaveTime = Time.time;
                var zdo = GetComponent<ZNetView>()?.GetZDO();
                if (zdo != null) SaveMemories(zdo);
            }

            // Per-frame movement: drive the character toward the current waypoint
            if (m_currentWaypoint != null && NeedsMovement(CurrentState))
            {
                var targetPos = m_currentWaypoint.Position;
                var remaining = Vector3.Distance(transform.position, targetPos);
                if (AgentHasArrived(targetPos))
                {
                    // Arrival = the villager actually REACHED the resolved
                    // approach cell. That cell is already validated (standoff +
                    // complete path + line-of-sight + same level), so reaching
                    // it is the correct "ready to use the station" signal — no
                    // generous arrival radius that would let it interact several
                    // metres short (roasting/depositing through the air). For the
                    // NavMeshAgent mover that means its complete path is traversed
                    // to within the agent stopping distance; the legacy custom
                    // mover still uses the ArrivalThreshold radius.
                    OnArrivedAtTarget(dt);
                }
                else
                {
                    var agentRunning = CurrentState == BehaviorState.Patrolling
                                       || (!IsCasualTravel && remaining > 5f);
                    UpdateAgentMovement(targetPos, agentRunning);
                }
            }

            return false;
        }

        public void LoadMemories(ZDO zdo)
        {
            Memory.LoadFromZDO(zdo);
            VillagerActivityLog.Instance.LoadFromZDO(UniqueId, zdo);
            // Load persisted behavior state
            foreach (var b in m_behaviors)
                if (b is IBehaviorPersistence bp)
                    bp.Load(zdo);
        }

        public void SaveMemories(ZDO zdo)
        {
            Memory.SaveToZDO(zdo);

            VillagerActivityLog.Instance.SaveToZDO(UniqueId, zdo);
            VillagerActivityLog.Instance.MarkCommitted(UniqueId);
            VillagerActivityLog.Instance.TrimCommitted(UniqueId);

            foreach (var b in m_behaviors)
                if (b is IBehaviorPersistence bp)
                    bp.Save(zdo);
        }

        private void RegisterBehaviors()
        {
            var villagerDef = VillagerRegistry.Get(VillagerType);

            // Merge behavior keys from both the legacy "behaviors" array and "behavior:*" tags
            var behaviorKeys = new List<string>();
            if (villagerDef?.behaviors != null)
                behaviorKeys.AddRange(villagerDef.behaviors);
            if (villagerDef?.tags != null)
                behaviorKeys.AddRange(TagParser.GetValues(villagerDef.tags, "behavior"));

            m_behaviors = BehaviorFactory.CreateBehaviors(this, behaviorKeys);

            // TODO: why is this necessary? what part of farming requires crafting?
            var craftAdapter = GetBehavior<CraftingBehaviorAdapter>();
            var farmAdapter = GetBehavior<FarmBehaviorAdapter>();
            if (craftAdapter != null && farmAdapter != null)
                farmAdapter.LinkToCraftingAdapter(craftAdapter);
        }

        #region Main AI Loop

        /// <summary>
        ///     Whether the given state requires active movement toward a target.
        /// </summary>
        private static bool NeedsMovement(BehaviorState state)
        {
            return state switch
            {
                BehaviorState.Traveling => true,
                BehaviorState.Exploring => true,
                BehaviorState.Wandering => true,
                BehaviorState.Patrolling => true,
                BehaviorState.Working => true,
                _ => false,
            };
        }

        #endregion


        #region Properties

        public T GetBehavior<T>() where T : class, IBehavior
        {
            foreach (var b in m_behaviors)
                if (b is T typed)
                    return typed;
            return null;
        }

        /// <summary>Find a behavior by tag string. Used by tag-driven components.</summary>
        public IBehavior GetBehavior(string matchBehaviorTag)
        {
            foreach (var b in m_behaviors)
                if (b.Tag == matchBehaviorTag)
                    return b;
            return null;
        }

        public VillagerMemory GetMemory()
        {
            return Memory;
        }

        /// <summary>Villager component this AI is attached to.</summary>
        public Villager Villager { get; private set; }

        /// <summary>Display name for logging. Compatibility with behavior code.</summary>
        public string NpcName => m_villagerName ?? Villager?.villagerName ?? "Unknown";

        /// <summary>Unique ID for task attributes and persistence.</summary>
        public string UniqueId { get; private set; }

        /// <summary>Current world position. Compatibility with behavior code.</summary>
        public Vector3 Position => Villager != null ? Villager.transform.position : Vector3.zero;

        /// <summary>Memory (known locations). Compatibility with behavior code.</summary>
        public VillagerMemory Memory { get; private set; }

        /// <summary>This AI component (for StopMoving etc.). Compatibility with behavior code.</summary>
        public BaseAI Instance => this;

        /// <summary>ZNetView for persistence. Used by behavior persistence.</summary>
        public ZNetView NView => Villager?.nView;

        /// <summary>Character component. Compatibility with farming/work.</summary>
        public Character Character => Villager != null ? Villager.GetComponent<Character>() : null;

        /// <summary>Current movement target position. Compatibility with BehaviorLogic.</summary>
        public Vector3? CurrentTarget => m_currentWaypoint != null ? m_currentWaypoint.Position : null;

        /// <summary>Crafting behavior adapter if present. Compatibility with UI and workflows.</summary>
        public CraftingBehaviorAdapter CraftingBehavior => GetBehavior<CraftingBehaviorAdapter>();

        /// <summary>Work-order scanner for BehaviorLogic. No dependency on concrete crafting type.</summary>
        public IWorkScanBehavior GetWorkScanner()
        {
            return GetBehavior<CraftingBehaviorAdapter>();
        }

        /// <summary>Villager type string from JSON definition (e.g. "Guard", "Farmer").</summary>
        public string VillagerType { get; private set; }

        Vector3 IVillagerStationLookup.BedPosition =>
            Memory != null ? Memory.BedPosition : default;

        /// <summary>
        ///     This villager's bed (home) position. Station/approach lookups
        ///     anchor the VILLAGE on this — not the villager's transient
        ///     position — so a villager bumped off the graph still resolves work
        ///     against its home village instead of "no village here".
        /// </summary>
        public Vector3 BedPosition => m_bedPosition;

        string IVillagerWorkContext.NpcName => NpcName;
        Vector3 IVillagerWorkContext.Position => Position;

        #endregion

        #region State Management

        public BehaviorState CurrentState { get; private set; } = BehaviorState.Idle;

        /// <summary>
        ///     The behavior currently in control (highest-priority one that wanted
        ///     control on the last selection), or null when idle. For UI/status.
        /// </summary>
        public Interfaces.IBehavior ActiveBehavior { get; private set; }

        /// <summary>True while the AI is in a hard-stuck backoff cooldown and should not start new tasks.</summary>
        public bool IsInBackoff => Time.time < m_stuckBackoffUntil;

        /// <summary>
        ///     Drop the current movement waypoint and stop, WITHOUT changing
        ///     BehaviorState. Used when a workflow enters a stationary "wait"
        ///     sub-state (e.g. smelting/cooking at a station): the villager
        ///     should stay in Working so its behavior keeps ticking, but must
        ///     stop moving so the per-frame movement loop doesn't keep
        ///     re-detecting "arrived" at the station waypoint and re-firing
        ///     OnArrival (which the work state machine treats as an unexpected
        ///     arrival and abandons). SetState(state, (VillagerWaypoint)null)
        ///     does NOT clear the waypoint, so this explicit path is required.
        /// </summary>
        public void ClearWaypoint()
        {
            m_currentWaypoint = null;
            StopMoving();
        }

        public void SetState(BehaviorState newState, Vector3? target = null)
        {
            var waypoint = target.HasValue
                ? VillagerWaypoint.WithDefault(target.Value)
                : null;
            SetState(newState, waypoint);
        }

        public void SetState(BehaviorState newState, VillagerWaypoint waypoint)
        {
            var prevState = CurrentState;
            CurrentState = newState;
            if (waypoint != null)
            {
                var prevTarget = m_currentWaypoint != null ? m_currentWaypoint.Position : Vector3.zero;
                m_currentWaypoint = waypoint;
                // Clear casual-travel marker by default. Behaviors that
                // WANT casual travel (Explore wandering to a known
                // location) set it back to true AFTER this returns.
                IsCasualTravel = false;
                EventRing.RecordTargetSet(waypoint.Position, prevTarget, $"SetState({newState})");
            }

            if (prevState != newState)
                EventRing.RecordStateChange(prevState.ToString(), newState.ToString(),
                    waypoint != null ? "with_waypoint" : "no_waypoint");

            if (newState == BehaviorState.Idle || newState == BehaviorState.NeedsHelp)
                StopMoving();
            if (waypoint != null)
                Plugin.Log?.LogDebug(
                    $"[AI:{m_villagerName}] State -> {newState}, target=({waypoint.Position.x:F1},{waypoint.Position.y:F1},{waypoint.Position.z:F1})");
            else
                Plugin.Log?.LogDebug($"[AI:{m_villagerName}] State -> {newState}");
        }

        public void SetPaused(bool paused)
        {
            IsPaused = paused;
            if (paused)
                StopMoving();
        }

        public bool IsPaused { get; private set; }

        public VillagerWaypoint GetCurrentWaypoint()
        {
            return m_currentWaypoint;
        }

        /// <summary>
        ///     THE single entry point for directing the villager to a world
        ///     location. Wraps the full sequence every caller needs:
        ///     <list type="number">
        ///       <item>(optionally) snap the raw target to an HNA-valid,
        ///         complete-path-reachable approach point,</item>
        ///       <item>clear any in-flight path + reset recovery/stall timers
        ///         (via <see cref="SetState"/>),</item>
        ///       <item>set the behavior state + waypoint,</item>
        ///       <item>reset the advisory NavMeshAgent so it re-plans from
        ///         scratch instead of steering a stale internal path / leftover
        ///         off-mesh link.</item>
        ///     </list>
        ///     Returns false (and changes nothing) when snapping is requested
        ///     but no reachable approach exists — the caller decides whether to
        ///     AbandonWork, message the player, etc.
        ///     <para>Do NOT set <c>m_currentWaypoint</c> or call BaseAI.FindPath
        ///     directly — those bypass path invalidation and the agent reset and
        ///     strand the villager following a stale path. This method is the
        ///     consolidation of the formerly divergent move entry points
        ///     (native FindPath, raw SetState, TryWalkTo).</para>
        /// </summary>
        public bool NavTo(Vector3 target, BehaviorState state, string label,
            bool snapToApproach = true, System.Func<Vector3, bool> hullPredicate = null,
            bool directOrder = false)
        {
            var dest = target;
            if (snapToApproach &&
                !VillagerMovement.TryResolveApproach(target, transform.position, hullPredicate, out dest))
            {
                Plugin.Log?.LogWarning(
                    $"[AI:{m_villagerName}] NavTo('{label}') found no reachable approach to " +
                    $"({target.x:F1},{target.y:F1},{target.z:F1}); not moving.");
                return false;
            }

            // ALWAYS land the destination on the agent's navmesh surface, even
            // when an approach was pre-resolved (snapToApproach=false). Approach
            // resolvers can return a point ABOVE the walkable mesh (e.g. a
            // chest's own Y, ~0.5m over the floor it sits on). The advisory
            // NavMeshAgent then can neither arrive (its path to the off-mesh
            // point is never PathComplete) nor move (it's already at the nearest
            // mesh point, so desiredVelocity ≈ 0) — the villager strands a few
            // tenths of a metre below the target. Snapping guarantees an on-mesh
            // destination the agent can complete-path to and register arrival at.
            if (VillagerAgentType.IsRegistered &&
                NavMesh.SamplePosition(dest, out var meshHit, NavToSnapRadius, AgentFilter()))
                dest = meshHit.position;

            // SetState clears the stale path, resets recovery, resets stall
            // timers, and records the target-set event — funnel through it so
            // every move shares that invalidation.
            SetState(state, new VillagerWaypoint(dest, VillagerWaypoint.DefaultStrategyId, label));

            // Re-plan the advisory agent from scratch: drop any stale internal
            // path / off-mesh-link state left over from the previous target.
            if (m_navAgent != null && m_navAgent.isOnNavMesh)
                m_navAgent.ResetPath();

            // A direct order outranks autonomous behavior until the villager
            // arrives (OnArrivedAtTarget clears it). Workflow-issued NavTo calls
            // pass directOrder=false — they ARE the behavior and shouldn't lock
            // out re-selection.
            m_directOrderActive = directOrder;

            return true;
        }


        /// <summary>
        ///     Replace BaseAI's path with a CalculatePath result against the
        ///     villager NavMesh (slot 31), but only when the result is
        ///     <see cref="UnityEngine.AI.NavMeshPathStatus.PathComplete" />.
        ///     Returns false on partial / invalid paths so the caller can
        ///     enter the unreachable-target recovery flow rather than
        ///     walking a path that ends short and re-triggering the same
        ///     failure every tick.
        /// </summary>
        /// <summary>
        ///     Lazily create + configure the advisory NavMeshAgent. updatePosition
        ///     /updateRotation are off so the agent never moves the Valheim
        ///     character's transform — we only read its steering. agentTypeID is
        ///     slot 31 (the village bake).
        /// </summary>
        private void EnsureAgent()
        {
            if (m_navAgent != null) return;
            if (!VillagerAgentType.IsRegistered) return;

            m_navAgent = gameObject.GetComponent<NavMeshAgent>()
                         ?? gameObject.AddComponent<NavMeshAgent>();
            m_navAgent.agentTypeID = VillagerAgentType.UnityAgentTypeID;
            m_navAgent.updatePosition = false;
            m_navAgent.updateRotation = false;
            m_navAgent.updateUpAxis = false;
            m_navAgent.baseOffset = 0f;
            m_navAgent.autoBraking = true;
            m_navAgent.autoRepath = true;
            m_navAgent.autoTraverseOffMeshLink = false; // we cross links manually
            m_navAgent.speed = 5f;          // only desiredVelocity DIRECTION is used
            m_navAgent.acceleration = 12f;
            m_navAgent.angularSpeed = 1080f;
            m_navAgent.stoppingDistance = 0.3f;

            // Local avoidance (RVO) so villagers steer around each other instead
            // of jamming in hallways/corners. desiredVelocity already folds in
            // the avoidance contribution, so reading it (UpdateAgentMovement)
            // picks this up — PROVIDED the sim knows each agent's real motion,
            // which is why UpdateAgentMovement also feeds m_navAgent.velocity
            // back from the character each frame (advisory mode otherwise leaves
            // neighbours looking stationary, so RVO can't predict collisions).
            m_navAgent.obstacleAvoidanceType = ObstacleAvoidanceType.GoodQualityObstacleAvoidance;
            // Per-villager priority so head-on encounters resolve ASYMMETRICALLY
            // (one pushes through, the other yields) instead of both side-
            // stepping into a mutual deadlock. Lower value = higher priority.
            // Derive a stable 20..80 spread from the villager id so the same
            // villager always keeps the same priority across rebakes/reloads.
            m_navAgent.avoidancePriority = 20 + (Mathf.Abs(UniqueId?.GetHashCode() ?? 0) % 61);

            if (NavMesh.SamplePosition(transform.position, out var hit, 3f, AgentFilter()))
                m_navAgent.Warp(hit.position);
        }

        private static NavMeshQueryFilter AgentFilter()
        {
            return new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };
        }

        /// <summary>
        ///     Advisory NavMeshAgent movement: sync the agent to the character's
        ///     real position, let it compute/steer the path to <paramref name="targetPos"/>,
        ///     and feed its desired direction into Valheim's character movement.
        ///     The agent owns pathing + local steering + link sequencing; the
        ///     physics character still does the actual moving.
        /// </summary>
        /// <summary>
        ///     True when the NavMesh agent has actually traversed its COMPLETE
        ///     path to <paramref name="targetPos" /> — i.e. it physically reached
        ///     the (already-validated) approach cell, within the agent's stopping
        ///     distance. This replaces a generous arrival radius: since the
        ///     approach cell is resolved with standoff + complete path + LOS +
        ///     same-level gates, reaching it IS the "ready to use the station"
        ///     condition. Guards reject the not-yet-pathed, still-computing, and
        ///     partial-path cases (a partial path's end is short of the target,
        ///     so its small remainingDistance must NOT read as arrived).
        /// </summary>
        private bool AgentHasArrived(Vector3 targetPos)
        {
            if (m_navAgent == null || !m_navAgent.isOnNavMesh) return false;
            if (m_navAgent.pathPending || !m_navAgent.hasPath) return false;
            // Destination must be THIS target (UpdateAgentMovement sets it). On
            // the first tick after a new waypoint it isn't set yet → not arrived.
            if ((m_navAgent.destination - targetPos).sqrMagnitude > 0.25f) return false;
            if (m_navAgent.pathStatus != NavMeshPathStatus.PathComplete) return false;
            return m_navAgent.remainingDistance <= m_navAgent.stoppingDistance + 0.25f;
        }

        // Off-mesh rescue --------------------------------------------------

        private bool m_rescuing;
        private float m_nextRescueCheck;
        private float m_strandedSince;
        private Vector3 m_rescueLastPos;
        private float m_rescueProgressTime;

        /// <summary>How often (s) to run the CalculatePath-backed stranded check.</summary>
        private const float RescueCheckInterval = 1f;

        /// <summary>Strand must persist this long (s) before a rescue starts — debounces transient mid-rebuild blips.</summary>
        private const float StrandConfirmSeconds = 2f;

        /// <summary>If a walking rescue makes no progress for this long (s), teleport home (disconnected island).</summary>
        private const float RescueStuckSeconds = 3f;

        /// <summary>Minimum movement (m) per check to count the walking rescue as making progress.</summary>
        private const float RescueProgressEps = 0.5f;

        /// <summary>
        ///     Recover a STRANDED villager. "Stranded" = its position doesn't
        ///     resolve to a village region AND the agent can't path home (see
        ///     <see cref="IsStranded" />) — a disconnected scrap of navmesh: a
        ///     leaked exterior island, or a wall-base limbo cell. First tries to
        ///     WALK home over the terrain (<see cref="BaseAI.MoveTo" />, Valheim's
        ///     humanoid pathing, which routes around walls to a gate). If walking
        ///     makes no progress for <see cref="RescueStuckSeconds" /> — pathfinding
        ///     genuinely can't escape the island — it TELEPORTS home as a last
        ///     resort. The strand is debounced (<see cref="StrandConfirmSeconds" />)
        ///     so a transient blip during a navmesh rebuild never yanks a healthy
        ///     villager. Runs ahead of behavior selection; returns true while a
        ///     rescue is in progress.
        /// </summary>
        private bool TryOffMeshRescue(float dt)
        {
            if (!VillagerAgentType.IsRegistered) return false;

            var due = Time.time >= m_nextRescueCheck;
            if (m_rescuing)
            {
                if (due)
                {
                    m_nextRescueCheck = Time.time + RescueCheckInterval;
                    if (!IsStranded())
                    {
                        m_rescuing = false;
                        m_strandedSince = 0f;
                        Plugin.Log?.LogInfo(
                            $"[AI:{m_villagerName}] Rescue complete — back on the village graph.");
                        if (CurrentState == BehaviorState.NeedsHelp)
                            SetState(BehaviorState.Idle);
                        ClearCachedPath();
                        return false;
                    }
                }

                // Escalate to a teleport when walking can't free it (the navmesh
                // island has no path off, so MoveTo never moves us).
                if ((transform.position - m_rescueLastPos).sqrMagnitude >
                    RescueProgressEps * RescueProgressEps)
                {
                    m_rescueLastPos = transform.position;
                    m_rescueProgressTime = Time.time;
                }
                else if (Time.time - m_rescueProgressTime > RescueStuckSeconds)
                {
                    TeleportHome();
                    m_rescueLastPos = transform.position;
                    m_rescueProgressTime = Time.time;
                    return true;
                }
            }
            else
            {
                if (!due) return false;
                m_nextRescueCheck = Time.time + RescueCheckInterval;
                if (!IsStranded())
                {
                    m_strandedSince = 0f;
                    return false;
                }

                // Debounce: require the strand to persist so a transient blip while
                // a navmesh rebuild settles doesn't rescue a healthy villager.
                if (m_strandedSince <= 0f) m_strandedSince = Time.time;
                if (Time.time - m_strandedSince < StrandConfirmSeconds) return false;

                m_rescuing = true;
                m_rescueLastPos = transform.position;
                m_rescueProgressTime = Time.time;
                Plugin.Log?.LogWarning(
                    $"[AI:{m_villagerName}] Stranded off the village graph at " +
                    $"({transform.position.x:F1},{transform.position.z:F1}); recovering to bed " +
                    $"({m_bedPosition.x:F1},{m_bedPosition.z:F1}).");
            }

            // Walk home over the terrain (base-game pathing, NOT the village agent).
            MoveTo(dt, m_bedPosition, 1f, true);
            return true;
        }

        /// <summary>
        ///     Last-resort teleport for a villager on a disconnected navmesh island
        ///     that no path can free. Snaps to the agent mesh nearest the bed and
        ///     moves the character (and its advisory agent) there.
        /// </summary>
        private void TeleportHome()
        {
            var dest = m_bedPosition;
            if (NavMesh.SamplePosition(m_bedPosition, out var hit, 5f, AgentFilter()))
                dest = hit.position;
            transform.position = dest;
            if (m_navAgent != null && m_navAgent.isOnNavMesh)
                m_navAgent.Warp(dest);
            Plugin.Log?.LogWarning(
                $"[AI:{m_villagerName}] Rescue: pathing couldn't free it; teleported home to " +
                $"({dest.x:F1},{dest.y:F1},{dest.z:F1}).");
        }

        /// <summary>
        ///     True when the villager can't be reached by the village region graph
        ///     AND the agent NavMesh can't path it home — "genuinely stranded". A
        ///     region-unresolved villager that CAN still agent-path to its bed (an
        ///     interior lookup-grid hole) is NOT stranded, so healthy interior
        ///     villagers are never rescued.
        /// </summary>
        private bool IsStranded()
        {
            var graph = Navigation.RegionGraph.GetNearest(m_bedPosition);
            if (graph != null && graph.PointToRegionId(transform.position) != null)
                return false; // resolves to a region — on the graph, fine

            var filter = AgentFilter();
            // Off the agent mesh entirely (can't even snap nearby) → stranded.
            if (!NavMesh.SamplePosition(transform.position, out var from, 3f, filter))
                return true;
            // Can't locate the bed on the mesh — don't start a rescue we can't finish.
            if (!NavMesh.SamplePosition(m_bedPosition, out var to, 5f, filter))
                return false;
            var path = new NavMeshPath();
            NavMesh.CalculatePath(from.position, to.position, filter, path);
            // A complete agent path home means it can recover on its own.
            return path.status != NavMeshPathStatus.PathComplete;
        }

        private void UpdateAgentMovement(Vector3 targetPos, bool running)
        {
            EnsureAgent();
            // CRITICAL: every early exit below must StopMoving() first. Valheim's
            // Character.m_moveDir PERSISTS across frames — it keeps applying the
            // last movement command until something changes it. If this method
            // just `return`s on a frame where it can't produce a valid direction
            // (agent off-mesh and un-warpable, path still computing, zero desired
            // velocity), the character keeps walking the PREVIOUS frame's
            // direction with no target — observed as a villager bumped off-mesh
            // onto a pillar then marching in a straight line into the village
            // outer wall. Stopping on every no-move frame makes the agent hold
            // position until a valid path/velocity is available again.
            if (m_navAgent == null)
            {
                StopMoving();
                return;
            }

            // Keep the agent's internal position glued to the physics character.
            if (m_navAgent.isOnNavMesh)
            {
                m_navAgent.nextPosition = transform.position;
                // Feed the character's REAL horizontal velocity to the agent so
                // local avoidance (RVO) can predict collisions with neighbouring
                // villagers. In advisory mode the agent doesn't move itself, so
                // without this every villager looks stationary to the avoidance
                // sim and it barely steers around them — the hallway/corner
                // jamming. With true velocities, desiredVelocity (read below)
                // reflects the avoidance contribution.
                if (m_character != null)
                {
                    var charVel = m_character.GetVelocity();
                    charVel.y = 0f;
                    m_navAgent.velocity = charVel;
                }
            }
            else
            {
                // Character drifted off the agent's navmesh — warp it back.
                if (NavMesh.SamplePosition(transform.position, out var hit, 3f, AgentFilter()))
                {
                    m_navAgent.Warp(hit.position);
                }
                else
                {
                    StopMoving();
                    return;
                }
            }

            if (!m_navAgent.hasPath ||
                (m_navAgent.destination - targetPos).sqrMagnitude > 0.25f)
                m_navAgent.SetDestination(targetPos);

            if (m_navAgent.pathPending)
            {
                StopMoving();
                return;
            }

            // A failed/partial path means there's no valid route to the target
            // from here — don't drift on a stale velocity, hold position.
            if (m_navAgent.pathStatus == NavMeshPathStatus.PathInvalid)
            {
                StopMoving();
                return;
            }

            Vector3 dir;
            if (m_navAgent.isOnOffMeshLink)
            {
                // Drive straight across the link; complete it once we arrive.
                var end = m_navAgent.currentOffMeshLinkData.endPos;
                dir = end - transform.position;
                if (dir.sqrMagnitude < 0.09f) m_navAgent.CompleteOffMeshLink();
            }
            else
            {
                dir = m_navAgent.desiredVelocity;
            }

            dir.y = 0f;
            if (dir.sqrMagnitude < 1e-4f)
            {
                StopMoving();
                return;
            }

            // Doorways bake as plain walkable navmesh (door colliders are
            // excluded from the bake), so the agent routes straight through
            // them — but the PHYSICAL door is still a solid collider. Open any
            // closed player-built door ahead on the route so the character
            // isn't stopped by it. Proximity + direction gated (GetBlockingDoor),
            // link-free — replaces the old door-link + OpenDoorsAlongPath path.
            if (DoorHandler != null)
            {
                var blockingDoor = DoorHandler.GetBlockingDoor(dir);
                if (blockingDoor != null) DoorHandler.OpenDoor(blockingDoor);
            }

            MoveTowards(dir.normalized, running);
        }


        /// <summary>
        ///     Clear the cached BaseAI path so the next movement tick falls
        ///     into the path-empty branch and recomputes against the current
        ///     NavMesh. Use after a NavMesh rebake / partition rebuild — the
        ///     prior path's waypoints may sit on geometry that no longer
        ///     exists or routed through NavMeshLinks that have since been
        ///     cleared. Intent (m_currentWaypoint) and stuck timers are
        ///     left untouched.
        /// </summary>
        public void ClearCachedPath()
        {
            if (m_navAgent != null && m_navAgent.isOnNavMesh) m_navAgent.ResetPath();
        }

        private void OnArrivedAtTarget(float dt)
        {
            // Direct order fulfilled — release the behavior lockout so normal
            // task-queue behavior resumes on the next selection tick.
            m_directOrderActive = false;

            // Clear the cached path now that we've reached the waypoint.
            // The behavior's OnArrival callback typically calls SetState
            // with a NEW waypoint (next sub-state: gather → travel-to-station
            // → return-to-chest, etc). Without clearing m_path here, the
            // next tick continues following the OLD waypoint's path nodes
            // until they drain, and the new SetState's path only gets
            // computed once those stale nodes are gone. The intermediate
            // ticks can also produce "skip the links" paths because
            // CalculatePath was never re-invoked for the new target.
            // Clearing here forces the path-empty branch (and a fresh
            // TryFindPathCustom) on the very next tick after the behavior
            // assigns its new target.
            var arrCtx = new BehaviorContext();
            foreach (var b in m_behaviors)
                if (b.WantsControl(arrCtx))
                {
                    b.OnArrival(dt);
                    break;
                }
        }

        #endregion
    }
}