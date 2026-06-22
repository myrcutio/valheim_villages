using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
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

        // Last target we passed to NavMeshAgent.SetDestination. Compared against the
        // requested target (not the agent's clamped destination) so an off-mesh /
        // unreachable-exact waypoint doesn't re-path every frame. Sentinel = never set.
        private Vector3 m_lastAgentDest = new(float.PositiveInfinity, 0f, 0f);

        private Vector3 m_homeAnchor;

        // Composable behaviors (populated by BehaviorFactory from NPC definition)
        private List<IBehavior> m_behaviors = new();

        /// <summary>
        ///     Tags of the behaviors this villager has — its "capabilities" for the
        ///     reranker scheduler (a repair task is only eligible for a villager whose
        ///     behavior set includes "repair", a cook-rescue only for "tidy", etc.).
        /// </summary>
        public IEnumerable<string> BehaviorTags
        {
            get
            {
                foreach (var b in m_behaviors)
                    yield return b.Tag;
            }
        }

        /// <summary>
        ///     First directed behavior that can execute the given task kind, or null.
        ///     Used by the scheduler dispatcher in PrimaryMode.
        /// </summary>
        public IDirectedBehavior FindDirectedBehavior(Scheduling.TaskKind kind)
        {
            foreach (var b in m_behaviors)
                if (b is IDirectedBehavior d && d.CanExecute(kind))
                    return d;
            return null;
        }

        private VillagerWaypoint m_currentWaypoint;
        private DoorHandler m_doorHandler;

        // Exploration
        private float m_explorationStartTime;
        private Vector3? m_explorationTarget;


        private float m_lastBehaviorUpdateTime;

        // One-shot override for the next behavior-reselect interval. -1 = use the
        // default cadence; >= 0 = use this many seconds for the NEXT tick only.
        // Set by the active behavior (combat) during Update to tick faster.
        private float m_nextReselectOverride = -1f;

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
            m_homeAnchor = Villager.HomeAnchor;
            VillagerType = Villager.villagerType;
            m_villagerName = Villager.villagerName;
            Memory = new VillagerMemory(m_homeAnchor);
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

            // base.Awake() resolves m_character and registers the BaseAI RPCs. If it aborted
            // (e.g. a duplicate RPC registration), m_character is null and every BaseAI tick
            // — UpdateRegeneration, MoveTo — would NRE once per frame. Don't activate a
            // half-initialised AI: fail loud and bail. With the native-component cleanup in
            // NativeNpcStripper this should no longer trigger; it's a backstop, not the fix.
            if (m_character == null)
            {
                Plugin.Log?.LogError(
                    $"[VillagerAI] base.Awake() did not complete (m_character is null) on '{name}'; " +
                    "AI will not activate. Indicates a native-component cleanup or double-init regression.");
                return;
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
                m_homeAnchor = Villager.HomeAnchor;
                m_homeAnchor = Villager.HomeAnchor;
                VillagerType = Villager.villagerType;
                m_villagerName = Villager.villagerName;
                Memory = new VillagerMemory(m_homeAnchor);
                VillagerAIManager.RegisterActive(this);
            }

            m_doorHandler = GetComponent<DoorHandler>();

            RegisterHome();
            RegisterBehaviors();


            // Stagger behavior ticks so NPCs spawned together don't all evaluate at the same time.
            // This is a countdown timer: 0 means "ready to run", positive means "wait this many more seconds".
            m_lastBehaviorUpdateTime = Random.Range(0f, VillagerSettings.BehaviorTickJitter);
        }

        private void OnDestroy()
        {
            // OnDestroy fires on death AND on unload/zone-change, so it must NOT flip the
            // record to Dead — that would wrongly kill villagers that merely streamed out of
            // range. Confirmed in-world death is handled separately by VillagerDeathPatch
            // (Character.OnDeath, which fires only on actual death). This block stays a
            // diagnostic. NOTE: ch.IsDead()/health below are unreliable for a Humanoid (it
            // doesn't override Character.IsDead, which returns false unconditionally, and
            // GetHealth reads the already-nulled ZDO after a real death). The real death-vs-
            // eviction discriminator is the ZDO validity/ownership in the DespawnRecorder
            // block below: a true death has already run ResetZDO -> invalid/null ZDO.
            var ch = Character;
            Plugin.Log?.LogWarning(
                $"[VillagerAI] OnDestroy: name='{m_villagerName}' id={UniqueId} " +
                $"pos=({Position.x:F1},{Position.y:F1},{Position.z:F1}) " +
                $"isDead={(ch != null ? ch.IsDead().ToString() : "n/a")} " +
                $"hp={(ch != null ? ch.GetHealth().ToString("F1") : "n/a")}\n" +
                $"call site:\n{System.Environment.StackTrace}");

            // Queryable capture (vv_despawns) — the dedicated server's log isn't readable
            // over MCP. Records the GameObject-destroy with owner/valid so we can tell a
            // true ZDO destroy (also captured at ZDOMan.DestroyZDO) from an out-of-area
            // instance removal (this entry present, no matching ZDO DESTROY entry).
            {
                var nv = GetComponent<ZNetView>();
                var z = nv != null ? nv.GetZDO() : null;
                Dev.DespawnRecorder.Record(
                    $"GO DESTROY (VillagerAI.OnDestroy) name='{m_villagerName}' id={UniqueId} " +
                    $"pos=({Position.x:F1},{Position.y:F1},{Position.z:F1}) " +
                    $"isDead={(ch != null ? ch.IsDead().ToString() : "n/a")} " +
                    $"valid={(z != null && z.IsValid())} owner={(z != null ? z.GetOwner() : 0)} " +
                    $"isOwner={(z != null && z.IsOwner())} isServer={(ZNet.instance != null && ZNet.instance.IsServer())}\n" +
                    $"parent:\n{this.transform.parent}");
            }

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

        [RequireAgent]
        public void RegisterHome()
        {
            Memory.HomeAnchor = m_homeAnchor;
        }

        /// <summary>Whether the spawn-settle gate has released (autonomous behaviors enabled).</summary>
        public bool IsSettled => m_settled;

        /// <summary>
        ///     True once the villager is standing on a baked village graph with a ready
        ///     agent — the precondition the spawn-settle gate waits on. Read (polled) by
        ///     <see cref="TaskQueue.Handlers.VillagerSettleHandler" />; side-effect free.
        /// </summary>
        public bool PreconditionsSettled()
        {
            if (m_homeAnchor == Vector3.zero) return false;        // broken/zombie villager
            if (m_navAgent == null || !m_navAgent.isOnNavMesh) return false; // mover not on the mesh yet
            // Reuse the rescue's notion of "on my village graph" instead of an exact
            // PointToRegionId membership test. IsStranded resolves the villager's OWN village
            // graph (by home anchor) and treats an interior lookup-grid hole that can still
            // agent-path home as fine — which MUST match how the spawn seed was validated
            // (agent navmesh + complete path). An exact PointToRegionId check would freeze,
            // forever, any healthy villager whose seed lands on a sub-meter lookup-grid hole
            // (documented perimeter/eroded cells). A genuinely stranded villager stays unsettled
            // here AND is recovered by the off-mesh rescue that runs before the gate.
            try
            {
                return !IsStranded();
            }
            catch
            {
                return false; // graph not built / not ready
            }
        }

        /// <summary>
        ///     Release the spawn-settle gate; called by the <c>villager_settle</c> task once
        ///     its preconditions are ready. Idempotent.
        /// </summary>
        public void MarkSettled(string reason)
        {
            if (m_settled) return;
            m_settled = true;
            Plugin.Log?.LogInfo($"[AI:{m_villagerName}] spawn-settled — {reason}; behaviors enabled.");
        }

        private bool m_warnedNoHome;

        // Spawn-settle gate: false until the deferred `villager_settle` task confirms this
        // villager is on its village graph with a ready agent. While false, UpdateAI holds
        // the villager in place so a first-tick behavior (e.g. flee) can't eject it.
        private bool m_settled;

        public override bool UpdateAI(float dt)
        {
            if (Villager == null) return false;
            if (!base.UpdateAI(dt)) return false;

            // A villager whose home (anchor) never resolved to a real position is broken
            // (a half-initialised / zombie ZDO). Running its movement AI aims the
            // off-mesh rescue at world origin, where BaseAI.MoveTo throws an NRE every
            // frame — flooding the log and tanking the frame rate. Skip the tick
            // (logged once) instead of spamming a per-frame crash.
            if (m_homeAnchor == Vector3.zero)
            {
                if (!m_warnedNoHome)
                {
                    m_warnedNoHome = true;
                    Plugin.Log?.LogWarning(
                        $"[AI:{m_villagerName}] No valid anchor position — skipping AI tick (broken/zombie villager).");
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

            // Spawn-settle gate: hold this villager's autonomous behavior (flee/combat/
            // work/patrol) until the deferred `villager_settle` task confirms it is on its
            // village graph with a ready agent, then flips m_settled. Reuses the task-queue
            // precondition/backoff machinery (same as [RequireAgent]) so there is no
            // per-frame race — the villager just waits. Off-mesh rescue (above) still runs so
            // a slightly-off-mesh spawn recovers; here we ensure the agent exists each tick
            // (so the precondition can observe on-mesh) and hold position otherwise, so a
            // first-tick FleeBehavior can't path the fresh villager off-graph before it settles.
            if (!m_settled)
            {
                EnsureAgentReady();
                // Re-warp a created-but-off-mesh advisory agent onto the mesh. EnsureAgent only
                // warps at creation, so an agent created off-mesh (the [RequireAgent] one-shot
                // warmed it from a far village, or TeleportHome moved the body but not the
                // agent) would stay off-mesh forever and the settle precondition (isOnNavMesh)
                // would never pass — a permanent freeze. Mirrors UpdateAgentMovement's self-heal.
                if (m_navAgent != null && !m_navAgent.isOnNavMesh
                    && NavMesh.SamplePosition(transform.position, out var meshHit, 3f, AgentFilter()))
                    m_navAgent.Warp(meshHit.position);
                StopMoving();
                return true;
            }

            // Leash: a settled villager that ends up far from its home anchor — flung onto a
            // stray/disconnected navmesh limb, knocked back, or driven off by a bad path — is
            // teleported straight home rather than wandering until it strands off-graph and is
            // culled. The bake radius is ~30m, so this generous threshold never trips on normal
            // in-village movement/patrol. (Backstop while the bake-overspill root cause is
            // instrumented — see NavMeshBake extent logging.)
            var leashDeltaXz = transform.position - m_homeAnchor;
            leashDeltaXz.y = 0f; // XZ only — don't count vertical separation (upper floors)
            if (leashDeltaXz.sqrMagnitude
                > VillagerSettings.MaxAnchorLeashMeters * VillagerSettings.MaxAnchorLeashMeters)
            {
                Plugin.Log?.LogWarning(
                    $"[AI:{m_villagerName}] Leash: {leashDeltaXz.magnitude:F0}m (XZ) from " +
                    $"anchor (> {VillagerSettings.MaxAnchorLeashMeters}m) — teleporting home.");
                TeleportHome();
                return true;
            }

            // Advisory-agent avoidance housekeeping — runs EVERY tick, idle or moving.
            // An idle villager that stops syncing keeps feeding the RVO sim its last
            // walking velocity (and, advisory-mode, its internal position drifts off at
            // that velocity), so neighbours steer around a phantom and walk through the
            // idle villager's real position. Ticking here makes idle villagers report
            // velocity ≈ 0 at their true spot; UpdateAgentMovement still re-syncs (with
            // its off-mesh warp) before it steers a moving villager.
            EnsureAgent();
            SyncAgentAvoidance();

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

                    if (Scheduling.SchedulerSettings.PrimaryMode)
                    {
                        // 1. Reactive behaviors (combat/flee/alarm) still preempt the
                        // scheduler — a villager must never ignore a threat to go repair.
                        foreach (var b in m_behaviors)
                        {
                            if (b.Priority < Scheduling.SchedulerSettings.ReactivePriorityFloor) continue;
                            if (b.WantsControl(ctx))
                            {
                                ActiveBehavior = b;
                                b.Update(dt);
                                handled = true;
                                break;
                            }
                        }

                        // 2. Scheduler is the primary work selector: assign + run the
                        // matching directed behavior. Outranks routine self-discovered work.
                        if (!handled)
                        {
                            var directed = Scheduling.SchedulerDispatcher.AssignIfIdle(this);
                            if (directed is IBehavior db && db.WantsControl(ctx))
                            {
                                ActiveBehavior = db;
                                db.Update(dt);
                                handled = true;
                            }
                        }

                        // 3. Routine work the scheduler doesn't own yet (craft/farming/
                        // patrol/haul). Directed behaviors are skipped here — in
                        // PrimaryMode they ONLY run via the dispatcher above, never by
                        // self-discovery.
                        if (!handled)
                            foreach (var b in m_behaviors)
                            {
                                if (b.Priority >= Scheduling.SchedulerSettings.ReactivePriorityFloor) continue;
                                if (b is IDirectedBehavior) continue;
                                if (b.WantsControl(ctx))
                                {
                                    ActiveBehavior = b;
                                    b.Update(dt);
                                    handled = true;
                                    break;
                                }
                            }
                    }
                    else
                    {
                        foreach (var b in m_behaviors)
                            if (b.WantsControl(ctx))
                            {
                                ActiveBehavior = b;
                                b.Update(dt);
                                handled = true;
                                break;
                            }
                    }

                    // No behavior wanted control — the villager is idle. Trigger
                    // work scanning here (this used to live in the always-on
                    // Explore fallback, which has been removed) and settle to
                    // Idle. Crafting takes over on the next tick once a scan
                    // result flips the state to Working.
                    if (!handled)
                    {
                        ActiveBehavior = null;

                        // Log-only observation when the scheduler is NOT the primary
                        // driver (PrimaryMode off) — logs the pick it would make.
                        if (!Scheduling.SchedulerSettings.PrimaryMode)
                            Scheduling.SchedulerObserver.Observe(this);

                        // Legacy (PrimaryMode off): self-scan for work when idle. In
                        // PrimaryMode the scheduler owns work-start — crafting/farming run
                        // only via a CraftWork assignment (CraftingBehaviorAdapter is a
                        // directed behavior), so self-scanning here would start work behind
                        // the board's back and double-drive it.
                        if (!Scheduling.SchedulerSettings.PrimaryMode
                            && CurrentState != BehaviorState.Working)
                            GetWorkScanner()?.TryScanForWork();
                        if (CurrentState != BehaviorState.Idle)
                            SetState(BehaviorState.Idle);
                    }
                }
                // Reset cooldown. Idle re-evaluation cadence — high enough
                // to stop the thrash, low enough to react to player input
                // (work orders, manual relocate) within a noticeable window.
                // A behavior that needs to tick faster than that (combat, which
                // must re-aim / fire / repath at near-frame rate) can shorten
                // the NEXT interval from inside its Update via
                // RequestFastReselect; the override is one-shot.
                m_lastBehaviorUpdateTime = m_nextReselectOverride >= 0f
                    ? m_nextReselectOverride
                    : VillagerSettings.BehaviorReselectIntervalSec;
                m_nextReselectOverride = -1f;
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
                // Patrol is a continuous loop — advance to the next waypoint a little
                // before reaching the current one so the guard curves through the route
                // instead of braking at each point. Every other state keeps precise
                // arrival (a station approach must be reached, not anticipated).
                var arrivalLookahead = CurrentState == BehaviorState.Patrolling
                    ? VillagerSettings.PatrolWaypointLookahead
                    : 0f;
                if (AgentHasArrived(targetPos, arrivalLookahead))
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

            // Role-based combat: combatants (guards/crossbowmen, which carry the
            // "combat" behavior) engage threats; everyone else flees toward a guard.
            // Auto-add a flee behavior to any non-combatant so "any non-guard flees"
            // without each definition opting in.
            var hasCombat = false;
            var hasFlee = false;
            foreach (var b in m_behaviors)
            {
                if (b is Behaviors.Combat.CombatBehavior) hasCombat = true;
                if (b is Behaviors.Combat.FleeBehavior) hasFlee = true;
            }

            if (!hasCombat && !hasFlee)
            {
                m_behaviors.Add(new Behaviors.Combat.FleeBehavior(this));
                m_behaviors.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }

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
                // Combat chase: the agent mover drives the villager toward the
                // current target waypoint while engaging.
                BehaviorState.Alarmed => true,
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

        /// <summary>
        ///     The villager as a <see cref="Humanoid"/> (its base prefab is a
        ///     Humanoid). Used by combat to equip weapons / ammo and call
        ///     <c>StartAttack</c>. Null only if the Character isn't a Humanoid.
        /// </summary>
        public Humanoid Humanoid => Character as Humanoid;

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

        Vector3 IVillagerStationLookup.HomeAnchor =>
            Memory != null ? Memory.HomeAnchor : default;

        /// <summary>
        ///     This villager's anchor (home) position. Station/approach lookups
        ///     anchor the VILLAGE on this — not the villager's transient
        ///     position — so a villager bumped off the graph still resolves work
        ///     against its home village instead of "no village here".
        /// </summary>
        public Vector3 HomeAnchor => m_homeAnchor;

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

        /// <summary>
        ///     Ask the AI to run the next behavior-selection/Update tick after
        ///     <paramref name="seconds"/> instead of the default reselect cadence.
        ///     One-shot — must be re-requested each Update to sustain a fast tick.
        ///     Combat uses this to re-aim/fire/repath at near-frame rate while
        ///     engaged, then lets it lapse back to the default when it disengages.
        /// </summary>
        public void RequestFastReselect(float seconds)
        {
            m_nextReselectOverride = Mathf.Max(0f, seconds);
        }

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
            bool directOrder = false, bool resetPath = true)
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
            // resetPath:false (patrol advance) deliberately KEEPS the current path so
            // the agent coasts on it while the next waypoint's path computes — without
            // this the cleared path reads as pathPending+!hasPath and the mover stops.
            if (resetPath && m_navAgent != null && m_navAgent.isOnNavMesh)
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
        /// <summary>
        ///     Public entry point for [RequireAgent]-driven setup: create this villager's
        ///     advisory agent now that the agent infrastructure is ready. No-op if the
        ///     agent already exists or the bake/registration isn't ready yet (EnsureAgent
        ///     re-checks). Kept distinct from the lazy per-tick call so a one-shot
        ///     infra-ready kick can warm idle villagers without waiting for them to move.
        /// </summary>
        public void EnsureAgentReady() => EnsureAgent();

        private void EnsureAgent()
        {
            if (m_navAgent != null) return;
            // RequireAgent gate: never create the agent before BOTH the slot-31 type is
            // registered AND a slot-31 bake is installed. Creating it earlier yields a
            // null/off-mesh agent — a villager that reports a route but never moves. The
            // per-frame movement tick re-calls this, so it self-heals the moment the bake
            // lands (no separate retry needed for owned, moving villagers).
            if (!Navigation.NavMeshBakeManager.AgentReady) return;

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
        ///     Per-tick advisory-agent housekeeping that MUST run whether or not the
        ///     villager is currently moving: glue the agent's internal position to the
        ///     physics character and report the character's REAL horizontal velocity to
        ///     the local-avoidance (RVO) sim.
        ///     <para>This used to live only inside <see cref="UpdateAgentMovement" />,
        ///     which runs only while a villager is walking to a waypoint. An IDLE
        ///     villager (arrived at its station, working, no waypoint) therefore stopped
        ///     being ticked and kept feeding the avoidance sim its LAST walking velocity
        ///     — and in advisory mode (updatePosition off) its internal sim position
        ///     drifts off at that stale velocity. Every other villager's RVO then sees a
        ///     phantom moving away from the spot the idle villager is actually standing
        ///     on, clears that predicted-vacated cell, and walks straight through it.
        ///     Ticking this every frame makes idle villagers report velocity ≈ 0 at their
        ///     true position, which is exactly what neighbours' RVO needs to steer around
        ///     them.</para>
        ///     Returns true when the agent is live and on-mesh (sync applied).
        /// </summary>
        private bool SyncAgentAvoidance()
        {
            if (m_navAgent == null || !m_navAgent.isOnNavMesh) return false;
            m_navAgent.nextPosition = transform.position;
            if (m_character != null)
            {
                var charVel = m_character.GetVelocity();
                charVel.y = 0f;
                m_navAgent.velocity = charVel;
            }

            return true;
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
        private bool AgentHasArrived(Vector3 targetPos, float lookahead = 0f)
        {
            if (m_navAgent == null || !m_navAgent.isOnNavMesh) return false;
            if (m_navAgent.pathPending || !m_navAgent.hasPath) return false;
            // Destination must be THIS target (UpdateAgentMovement sets it). On
            // the first tick after a new waypoint it isn't set yet → not arrived.
            if ((m_navAgent.destination - targetPos).sqrMagnitude > 0.25f) return false;
            if (m_navAgent.pathStatus != NavMeshPathStatus.PathComplete) return false;
            // lookahead > 0 (patrol) treats the waypoint as "reached" while still
            // approaching, so the route advances without the villager stopping.
            return m_navAgent.remainingDistance <= m_navAgent.stoppingDistance + 0.25f + lookahead;
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
            // Experiment toggle (vv_rescue). When off, strand detection / walk-home /
            // teleport-escalation are all skipped; the anchor leash (which calls TeleportHome
            // directly) is NOT affected.
            if (!OffMeshRescueEnabled) return false;
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
                    $"({transform.position.x:F1},{transform.position.z:F1}); recovering to anchor " +
                    $"({m_homeAnchor.x:F1},{m_homeAnchor.z:F1}).");
            }

            // Walk home over the terrain (base-game pathing, NOT the village agent).
            MoveTo(dt, m_homeAnchor, 1f, true);
            return true;
        }

        /// <summary>
        ///     Last-resort teleport for a villager on a disconnected navmesh island
        ///     that no path can free. Snaps to the agent mesh nearest the anchor and
        ///     moves the character (and its advisory agent) there.
        /// </summary>
        private void TeleportHome()
        {
            if (!OffMeshRescueEnabled) return;
            var dest = m_homeAnchor;
            if (NavMesh.SamplePosition(m_homeAnchor, out var hit, 5f, AgentFilter()))
                dest = hit.position;
            transform.position = dest;
            if (m_navAgent != null && m_navAgent.isOnNavMesh)
                m_navAgent.transform.Translate(dest);
            Plugin.Log?.LogWarning(
                $"[AI:{m_villagerName}] Rescue: pathing couldn't free it; teleported home to " +
                $"({dest.x:F1},{dest.y:F1},{dest.z:F1}).");
        }

        /// <summary>
        ///     Recall this villager to its registry station: relocate it to an
        ///     HNA-valid approach beside <paramref name="stationPos" /> on the village
        ///     (slot-31) graph, warp the advisory agent there, and drop to Idle so it
        ///     re-evaluates behaviors from the station. Uses the same Y-aware approach
        ///     resolver as spawn, so it can't land on a roof/upper floor. Returns false
        ///     (and does NOT move the villager) when no reachable approach resolves —
        ///     we never teleport into a non-walkable spot.
        /// </summary>
        public bool Recall(Vector3 stationPos)
        {
            if (!VillagerMovement.TryResolveApproach(stationPos, stationPos, null, out var dest))
                return false;

            transform.position = dest;
            if (m_navAgent != null && m_navAgent.isOnNavMesh)
                m_navAgent.transform.Translate(dest);
            // SetState(Idle) clears the stale path, resets recovery/stall timers, and
            // lets the behavior loop re-select from the station next tick.
            SetState(BehaviorState.Idle);
            Plugin.Log?.LogInfo(
                $"[AI:{m_villagerName}] Recalled to station at ({dest.x:F1},{dest.y:F1},{dest.z:F1}).");
            return true;
        }

        /// <summary>
        ///     True when the villager can't be reached by the village region graph
        ///     AND the agent NavMesh can't path it home — "genuinely stranded". A
        ///     region-unresolved villager that CAN still agent-path to its anchor (an
        ///     interior lookup-grid hole) is NOT stranded, so healthy interior
        ///     villagers are never rescued.
        /// </summary>
        private bool IsStranded()
        {
            var graph = Villages.Entity.VillageRegistry.GraphAt(m_homeAnchor);
            if (graph != null && graph.PointToRegionId(transform.position) != null)
                return false; // resolves to a region — on the graph, fine

            var filter = AgentFilter();
            // Off the agent mesh entirely (can't even snap nearby) → stranded.
            if (!NavMesh.SamplePosition(transform.position, out var from, 3f, filter))
                return true;
            // Can't locate the anchor on the mesh — don't start a rescue we can't finish.
            if (!NavMesh.SamplePosition(m_homeAnchor, out var to, 5f, filter))
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

            // Keep the agent's internal position glued to the physics character and
            // feed the character's REAL velocity to the avoidance sim (see
            // SyncAgentAvoidance — also run every tick for idle villagers so they
            // don't read as phantoms still moving away from where they stand).
            if (!SyncAgentAvoidance())
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

            // Only re-issue SetDestination when the REQUESTED target actually
            // changes — NOT when it merely differs from m_navAgent.destination.
            // When targetPos sits off the agent's reachable mesh (e.g. a repair
            // approach on a poly the agent can only get near), the agent clamps
            // its internal destination to the nearest reachable point, so
            // (destination - targetPos) stays > 0.25 forever; comparing against it
            // re-pathed every frame → perpetual pathPending → StopMoving → the
            // villager froze in place with desiredVelocity set but moveDir zero.
            // Tracking the last requested target lets the agent keep its computed
            // path to the clamped point and actually walk there.
            if (!m_navAgent.hasPath ||
                (m_lastAgentDest - targetPos).sqrMagnitude > 0.25f)
            {
                m_navAgent.SetDestination(targetPos);
                m_lastAgentDest = targetPos;
            }

            if (m_navAgent.pathPending)
            {
                // A new path is computing. For the continuous patrol loop, keep coasting
                // on the existing valid path so advancing to the next waypoint doesn't
                // stutter — the prior path still points forward along the route. Every
                // other case holds position rather than drift on a stale velocity.
                if (!(CurrentState == BehaviorState.Patrolling && m_navAgent.hasPath))
                {
                    StopMoving();
                    return;
                }
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
            // Deliver OnArrival to the behavior that actually drove the move (the one
            // selection set as ActiveBehavior), not merely the first that WantsControl —
            // otherwise a higher-priority self-discovering behavior can intercept a
            // directed/assigned behavior's arrival. Fall back to the scan if none is set.
            if (ActiveBehavior != null)
            {
                ActiveBehavior.OnArrival(dt);
            }
            else
            {
                foreach (var b in m_behaviors)
                    if (b.WantsControl(arrCtx))
                    {
                        b.OnArrival(dt);
                        break;
                    }
            }
        }

        #endregion
    }
}