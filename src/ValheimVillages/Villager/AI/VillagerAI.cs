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
        ///     Used by the scheduler dispatcher to route an assignment.
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

        /// <summary>
        ///     Walking speed (m/s) for idle travel — relax and wander. Half the Dvergr jog
        ///     (2.0) so an idle villager visibly strolls rather than hurrying.
        /// </summary>
        private const float CasualWalkSpeed = 1.0f;

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

            // The Dvergr prefab ships walk == jog (2.0 m/s), so the walk gait alone changes
            // nothing. Give villagers a genuinely slower walk for idle travel (relax/wander);
            // work travel keeps the prefab's jog/run.
            m_character.m_walkSpeed = CasualWalkSpeed;

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
            catch (System.Exception ex)
            {
                // "Graph not built yet" is an ordinary state and should be a return value,
                // not an exception — so anything arriving here is a genuine fault worth
                // seeing. Reported once; the villager stays unsettled, which is the safe
                // reading of "we could not confirm this villager is on the graph".
                Diagnostics.VanillaReflection.ReportFailure(
                    "VillagerAI.IsStranded (spawn-settle gate)", ex);
                return false;
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

            // Before anything else decides what this villager should do, make sure nobody is
            // still steering him from a tick that is over. See DriveDirect.
            ExpireDirectMove();

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
            // Idle drives are continuous state, so they advance every frame — NOT inside the
            // behavior-reselect gate below. Ticking them there would advance them by one
            // frame's dt per reselect interval, which runs ~40x slow and effectively freezes
            // the idle rotation. Every villager accumulates pressure whatever it is doing;
            // only one actually lingering at a spot sheds it.
            Behaviors.Relax.VillagerDrives.Tick(
                UniqueId, dt,
                (ActiveBehavior as Behaviors.Relax.RelaxBehavior)?.ServedSpotType,
                CurrentState == BehaviorState.Traveling);

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

            if (IsPaused)
            {
                // Lease lapsed — whoever asked for the pause stopped renewing it (menu
                // closed without the release arriving, client disconnected, game crashed).
                // Resume rather than stand here forever.
                if (Time.time >= m_pauseLeaseUntil)
                {
                    var routine = m_pauseIsQuiet;
                    SetPaused(false, 0f);
                    if (!routine)
                        Plugin.Log?.LogInfo(
                            $"[AI:{m_villagerName}] pause lease expired — resuming.");
                }
                else
                {
                    return true;
                }
            }

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
            var leashLimit = LeashLimit();
            if (leashDeltaXz.sqrMagnitude > leashLimit * leashLimit)
            {
                // Throttled: this fires per tick while a villager is outside the leash, and an
                // ineffective leash wrote a warning every frame for as long as it took him to
                // walk to the horizon.
                DebugLog.ThrottledWindow(
                    $"leash:{m_villagerName}", System.TimeSpan.FromSeconds(LeashLogWindowSeconds),
                    "Leash", "recovered",
                    ("villager", m_villagerName), ("dist", leashDeltaXz.magnitude),
                    ("limit", leashLimit));
                TeleportHome(true);
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
                    var tier = "none";
                    string offered = null;
                    var offeredAccepted = false;

                    // Three tiers, in strict order. The scheduler is the sole selector of
                    // WORK; there is no self-discovery path and no log-only mode.

                    // 1. Reactive behaviors (combat/flee/alarm) preempt the scheduler — a
                    // villager must never ignore a threat to go repair.
                    foreach (var b in m_behaviors)
                    {
                        if (b.Priority < Scheduling.SchedulerSettings.ReactivePriorityFloor) continue;
                        if (b.WantsControl(ctx))
                        {
                            ActiveBehavior = b;
                            b.Update(dt);
                            handled = true;
                            tier = "reactive";
                            break;
                        }
                    }

                    // 2. The scheduler assigns + runs the matching directed behavior.
                    // Every craft/farm/repair/tidy/cook task a villager performs enters
                    // here, dispatched from the village task board.
                    if (!handled)
                    {
                        var directed = Scheduling.SchedulerDispatcher.AssignIfIdle(this);
                        offered = (directed as IBehavior)?.Tag;
                        if (directed is IBehavior db && db.WantsControl(ctx))
                        {
                            ActiveBehavior = db;
                            db.Update(dt);
                            handled = true;
                            tier = "scheduler";
                            offeredAccepted = true;
                        }
                    }

                    // 3. Routine filler (wander/relax/patrol). Directed behaviors are
                    // skipped here — they ONLY run via the dispatcher above, never by
                    // self-discovery, so the board can never be bypassed.
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
                                tier = "routine";
                                break;
                            }
                        }

                    // Telemetry: which tier won this reselect, and what the scheduler offered.
                    // Keyed per villager+tier+behaviour so each distinct outcome surfaces.
                    if (Settings.LogSettings.VerboseSelect)
                        DebugLog.ThrottledWindow(
                            $"select:{m_villagerName}:{tier}:{ActiveBehavior?.Tag}:{offered}:{offeredAccepted}",
                            TimeSpan.FromSeconds(15f), "Select", "reselect",
                            ("villager", m_villagerName), ("tier", tier),
                            ("behavior", handled ? ActiveBehavior?.Tag : "none"),
                            ("scheduler_offered", offered ?? "none"), ("offer_accepted", offeredAccepted),
                            ("state", CurrentState));

                    // Nothing wanted control — settle to Idle so the next reselect starts
                    // from a clean state (and so routine behaviors, which arm on Idle, can
                    // pick up next tick).
                    if (!handled)
                    {
                        ActiveBehavior = null;
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
            else if (m_currentWaypoint == null && IsPureTravel(CurrentState))
            {
                // A pure-travel state with no waypoint is a dead end: the block above is
                // the only thing that fires OnArrivedAtTarget, and it can't run without a
                // waypoint, so nothing would ever move this villager out of Traveling.
                // Routine behaviors (wander/relax) release on Idle and otherwise pin
                // control forever — observed as a villager frozen mid-village in
                // state=Traveling, target=<none>, vel=0, doing no work at all.
                //
                // Only the PURE travel states recover this way. Working and Alarmed use
                // ClearWaypoint() deliberately to stand still while a station processes or
                // a combatant fires in place, so flipping those to Idle would abort real
                // work; Patrolling owns its own route recovery.
                SetState(BehaviorState.Idle);
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

            // Idle for every villager with no workable task: wander by default, relax at a
            // comfort spot (fire/table/seat/hot tub) when a drive is elevated. Both auto-added
            // like flee so definitions don't each opt in; guarded so a JSON that already lists
            // them (or a hot-reload re-run) doesn't double them. Relax(25) outranks wander(20)
            // but both sit under patrol(30) and all work, so any real work preempts them on the
            // next reselect tick.
            var hasRelax = false;
            var hasWander = false;
            foreach (var b in m_behaviors)
            {
                if (b is Behaviors.Relax.RelaxBehavior) hasRelax = true;
                if (b is Behaviors.Wander.WanderBehavior) hasWander = true;
            }

            if (!hasRelax) m_behaviors.Add(new Behaviors.Relax.RelaxBehavior(this));
            if (!hasWander) m_behaviors.Add(new Behaviors.Wander.WanderBehavior(this));
            m_behaviors.Sort((a, b) => b.Priority.CompareTo(a.Priority));

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
        /// <summary>
        ///     States whose ONLY purpose is getting somewhere, so losing the waypoint means
        ///     the move is over. Excludes Working/Alarmed (both park deliberately via
        ///     <see cref="ClearWaypoint" />) and Patrolling (rebuilds its own route).
        /// </summary>
        private static bool IsPureTravel(BehaviorState state)
        {
            return state switch
            {
                BehaviorState.Traveling => true,
                BehaviorState.Exploring => true,
                BehaviorState.Wandering => true,
                _ => false,
            };
        }

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

        /// <summary>
        ///     How far a target must move before it counts as a new one for logging. Below this
        ///     it is the same destination re-asserted.
        /// </summary>
        private const float StateLogTargetEpsilon = 0.25f;

        /// <summary>
        ///     How long a villager may spend on one NavMesh link before it is treated as stuck.
        ///     A doorway takes a second or two; the cap is generous so a slow crossing is never
        ///     mistaken for a stall.
        /// </summary>
        private const float MaxLinkCrossingSeconds = 8f;

        /// <summary>How often one villager's leash notice repeats while it is out of bounds.</summary>
        private const float LeashLogWindowSeconds = 30f;

        /// <summary>
        ///     Room allowed around the furthest village anchor before the leash bites. A
        ///     Forester's Post may stand 80m out by design and its woodlot reaches another 28m
        ///     past that, so a fixed 60m leash is smaller than the village it is meant to
        ///     contain — measured with a Guard whose own patrol route runs to 85m, being
        ///     "recovered" from a lap he was supposed to be walking.
        /// </summary>
        private const float LeashAnchorMargin = 35f;

        /// <summary>Recomputed occasionally rather than per tick; anchors change with the build.</summary>
        private float m_leashLimit;

        private float m_leashLimitAt;

        /// <summary>
        ///     How far this villager may get from home before it is pulled back: the furthest
        ///     anchor its village publishes, plus working room. Falls back to the flat setting
        ///     when there is no village to ask.
        /// </summary>
        private float LeashLimit()
        {
            if (m_leashLimit > 0f && Time.time - m_leashLimitAt < 10f) return m_leashLimit;
            m_leashLimitAt = Time.time;
            m_leashLimit = VillagerSettings.MaxAnchorLeashMeters;

            var village = Villages.Entity.VillageRegistry.GetVillageAt(m_homeAnchor);
            if (village != null)
                foreach (var anchor in village.Anchors)
                {
                    var d = Vector3.Distance(anchor.Position, m_homeAnchor) + LeashAnchorMargin;
                    if (d > m_leashLimit) m_leashLimit = d;
                }

            return m_leashLimit;
        }

        /// <summary>When the current link crossing began; 0 when not on one.</summary>
        private float m_linkStartedAt;

        public void SetState(BehaviorState newState, VillagerWaypoint waypoint)
        {
            var prevState = CurrentState;
            var previousTarget = m_currentWaypoint != null ? m_currentWaypoint.Position : (Vector3?)null;
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

            // Only log a CHANGE. This line used to fire on every call, and behaviours
            // re-assert their state every tick — an alarmed villager standing still wrote the
            // identical "State -> Alarmed, target=(...)" line every frame, two villagers at
            // once, and buried the log. The event ring above has always guarded on
            // prevState != newState; the log simply never did.
            var stateChanged = prevState != newState;
            var targetChanged = waypoint != null &&
                                (!previousTarget.HasValue ||
                                 Vector3.Distance(previousTarget.Value, waypoint.Position)
                                 > StateLogTargetEpsilon);
            if (!stateChanged && !targetChanged) return;

            if (waypoint != null)
                Plugin.Log?.LogDebug(
                    $"[AI:{m_villagerName}] State -> {newState}, target=({waypoint.Position.x:F1},{waypoint.Position.y:F1},{waypoint.Position.z:F1})");
            else
                Plugin.Log?.LogDebug($"[AI:{m_villagerName}] State -> {newState}");
        }

        /// <summary>
        ///     Hold this villager in place (a player has its craft menu open).
        ///
        ///     <para><paramref name="leaseSeconds" /> is how long the pause survives WITHOUT
        ///     renewal. Pause is requested remotely — see <see cref="VillagerPauseRpc" /> —
        ///     and a client that disconnects or crashes with the menu open never sends the
        ///     release. A latched pause would strand that villager forever with nothing able
        ///     to clear it, so the holder renews the lease while the menu is open and it
        ///     lapses on its own otherwise.</para>
        /// </summary>
        public void SetPaused(bool paused, float leaseSeconds, bool quiet = false)
        {
            IsPaused = paused;
            m_pauseIsQuiet = paused && quiet;
            m_pauseLeaseUntil = paused ? Time.time + leaseSeconds : 0f;
            if (paused)
                StopMoving();
        }

        /// <summary>
        ///     Stand still for a moment, on purpose and briefly — currently used so a villager
        ///     stops walking while it says a line rather than delivering it over its shoulder.
        ///
        ///     <para>Same machinery as <see cref="SetPaused" /> with the lease doing the
        ///     releasing, but marked quiet: an expiring talk-hold is the normal end of a hold,
        ///     not the "whoever paused this villager never released it" case the expiry log is
        ///     there to report.</para>
        /// </summary>
        public void HoldStill(float seconds)
        {
            SetPaused(true, seconds, true);
        }

        public bool IsPaused { get; private set; }

        /// <summary>When the current pause lapses unless renewed. See <see cref="SetPaused" />.</summary>
        private float m_pauseLeaseUntil;

        /// <summary>Whether the current pause expiring is routine (see <see cref="HoldStill" />).</summary>
        private bool m_pauseIsQuiet;

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
            m_navAgent.avoidancePriority = AvoidancePriority;

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
            m_character?.SetWalk(false);
            MoveTo(dt, m_homeAnchor, 1f, true);
            return true;
        }

        /// <summary>
        ///     Last-resort teleport for a villager on a disconnected navmesh island
        ///     that no path can free. Snaps to the agent mesh nearest the anchor and
        ///     moves the character (and its advisory agent) there.
        /// </summary>
        /// <summary>
        ///     Reposition this villager to <paramref name="dest" />.
        ///     <para>
        ///     Both callers previously did <c>transform.position = dest;</c> followed by
        ///     <c>m_navAgent.transform.Translate(dest)</c>. Translate moves a transform BY a
        ///     vector, not TO one, so the destination was applied TWICE: a recall to a station
        ///     at y=38.4 left the villager at y=76.8 and ~270m west of the village, where his
        ///     zone unloaded and he simply vanished as far as the player could tell. X doubled
        ///     as well; Z did not, because Translate defaults to LOCAL space so the XZ part
        ///     came out rotated by whichever way he happened to be facing.
        ///     </para>
        ///     <para>
        ///     <c>NavMeshAgent.Warp</c> is the supported way to reposition an agent: assigning
        ///     transform.position under an agent that is on a mesh leaves the agent's internal
        ///     position out of step with the transform.
        ///     </para>
        /// </summary>
        private bool MoveTo(Vector3 dest, string what)
        {
            if (m_navAgent == null)
            {
                transform.position = dest;
                return true;
            }

            if (m_navAgent.Warp(dest)) return true;

            // Warp refuses when the destination is not on the agent's mesh. Move anyway and
            // say so: leaving the villager where it was is the worse outcome — that is the
            // state the caller is trying to get it OUT of — and the agent re-acquires the
            // mesh on its own once it is somewhere sane.
            Plugin.Log?.LogWarning(
                $"[AI:{m_villagerName}] {what}: NavMeshAgent refused to warp to " +
                $"({dest.x:F1},{dest.y:F1},{dest.z:F1}) — moving the transform directly.");
            transform.position = dest;
            return true;
        }

        /// <param name="force">
        ///     True for the LEASH, which is a safety net and not the debug off-mesh rescue.
        ///     Sharing one method between the two meant <c>vv_rescue off</c> — the default —
        ///     silently disabled the leash as well: it logged "teleporting home" every frame
        ///     while the villager kept walking, and a Lumberjack reached 165m from a village
        ///     whose leash claims to catch him at 60. A safety net with a debug switch on it is
        ///     not a safety net.
        /// </param>
        private void TeleportHome(bool force = false)
        {
            if (!force && !OffMeshRescueEnabled) return;
            var dest = m_homeAnchor;
            if (NavMesh.SamplePosition(m_homeAnchor, out var hit, 5f, AgentFilter()))
                dest = hit.position;
            if (!MoveTo(dest, "Rescue")) return;
            Plugin.Log?.LogWarning(
                $"[AI:{m_villagerName}] Rescue: pathing couldn't free it; teleported home to " +
                $"({dest.x:F1},{dest.y:F1},{dest.z:F1}).");
        }

        /// <summary>
        ///     Recall this villager to its registry station. IMPERATIVE: the villager ends up
        ///     at the station, always.
        ///     <para>
        ///     It used to be conditional — it resolved an HNA-valid approach beside the
        ///     station and did nothing at all if none came back, on the reasoning that we
        ///     should never place a villager somewhere non-walkable. In practice that made
        ///     the button a coin flip precisely when it was needed: a villager worth
        ///     recalling is usually one that is stuck, off-graph or somewhere the approach
        ///     resolver cannot reason about, which is exactly when the resolve fails. A
        ///     villager standing at the station on imperfect ground is recoverable; one left
        ///     where it was is not.
        ///     </para>
        ///     <para>
        ///     So the approach resolver is now a preference, not a gate: use the spot it
        ///     finds when it finds one (it is Y-aware, so it avoids landing on a roof or an
        ///     upper floor), and otherwise use the station position itself.
        ///     </para>
        /// </summary>
        public void Recall(Vector3 stationPos)
        {
            var resolved = VillagerMovement.TryResolveApproach(
                stationPos, stationPos, null, out var dest);
            if (!resolved) dest = stationPos;

            MoveTo(dest, "Recall");
            // SetState(Idle) clears the stale path, resets recovery/stall timers, and
            // lets the behavior loop re-select from the station next tick.
            SetState(BehaviorState.Idle);

            // Recall is the player saying "stop what you are doing", so stop holding things.
            // A pause lease outlives a recall otherwise — the villager arrives and stands
            // there, which looks exactly like the fault they were recalling it to fix — and
            // its task claims would sit out their lease before anyone else could take them.
            SetPaused(false, 0f, true);
            var released = Scheduling.TaskBoard.ReleaseAllHeldBy(UniqueId);

            Plugin.Log?.LogInfo(
                $"[AI:{m_villagerName}] Recalled to station at " +
                $"({dest.x:F1},{dest.y:F1},{dest.z:F1})" +
                (resolved ? "." : " (no approach resolved — placed at the station itself).") +
                (released > 0 ? $" Released {released} task claim(s)." : ""));
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

            // A behaviour is driving the body itself right now — a charge at a tree. The agent
            // must not steer at the same time: it is still holding the standoff point the
            // run-up backed off to, so it spent every frame pulling him back to where he
            // started while the charge pushed him at the trunk. He never closed the last few
            // metres and every charge ended in "timed out short of the trunk".
            if (IsDrivingDirectly) return;

            // Off the mesh entirely? Then stop, whatever the agent claims it wants. Every
            // straight-line drive below — a link crossing, or desiredVelocity from an agent
            // whose internal state has come adrift — moves the BODY, and the body is not
            // bounded by the navmesh the way pathing is. Measured: three villagers found
            // standing on bare terrain with no navmesh within 5m and the nearest region
            // triangle 78m away. They could not have pathed there; they were driven, and
            // nothing was checking whether the ground under them still existed.
            if (!NavMesh.SamplePosition(transform.position, out _, OffMeshStopRadius, AgentFilter()))
            {
                DebugLog.ThrottledWindow(
                    $"offmesh:{m_villagerName}", System.TimeSpan.FromSeconds(30f),
                    "Movement", "off_mesh_stop",
                    ("villager", m_villagerName), ("pos", transform.position),
                    ("note", "no navmesh within " + OffMeshStopRadius + "m — holding position"));
                StopMoving();
                return;
            }

            Vector3 dir;
            if (m_navAgent.isOnOffMeshLink)
            {
                // Drive straight across the link; complete it once we arrive.
                var end = m_navAgent.currentOffMeshLinkData.endPos;
                dir = end - transform.position;
                if (dir.sqrMagnitude < 0.09f)
                {
                    m_navAgent.CompleteOffMeshLink();
                    m_linkStartedAt = 0f;
                }
                else
                {
                    // A link is the ONE place the agent stops pathing and walks in a straight
                    // line, so it is the one place an obstacle means walking at it forever. A
                    // doorway is a couple of metres; anything still on a link after this long
                    // is not crossing it. Give up, land where we are, and let the normal
                    // pathfinder have another go.
                    if (m_linkStartedAt <= 0f) m_linkStartedAt = Time.time;
                    else if (Time.time - m_linkStartedAt > MaxLinkCrossingSeconds)
                    {
                        Plugin.Log?.LogWarning(
                            $"[AI:{m_villagerName}] stuck crossing a NavMesh link for " +
                            $"{MaxLinkCrossingSeconds:F0}s — abandoning it and re-pathing.");
                        m_navAgent.CompleteOffMeshLink();
                        m_linkStartedAt = 0f;
                        ClearCachedPath();
                        StopMoving();
                        return;
                    }
                }
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

            // Walked into something the bake doesn't know about — another villager, a player,
            // a cart, a creature. RVO only STEERS around movers; once it fails and they are
            // actually pushing each other, it never stops. One side yields until they are
            // apart; the other keeps going. See HoldForDynamicBlocker (which drives the body
            // itself while it holds — standing still, or backing away).
            if (HoldForDynamicBlocker(dir)) return;

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

            // Idle travel (relax, wander) WALKS; without this, "not running" still meant
            // Valheim's default jog. Set every tick — the engine never clears m_walk itself,
            // so a stale flag would otherwise slow the next work trip.
            m_character?.SetWalk(IsCasualTravel && !running);
            MoveTowards(dir.normalized, running);
        }


        /// <summary>
        ///     Drive the body DIRECTLY, bypassing the agent and its pathfinding.
        ///
        ///     <para><b>The direction persists.</b> <c>BaseAI.MoveTowards</c> hands the
        ///     Character a move direction and the Character keeps using it until something
        ///     clears it — there is no "stop after this frame". So a caller that sets one and
        ///     then stops running, for any reason (preempted by a reactive behaviour, its own
        ///     early return, a throttled tick), leaves the villager walking that way for as
        ///     long as it takes someone to notice. That is not hypothetical: it is the shape of
        ///     the edge cases this AI was built around, and of a Lumberjack found 165m from
        ///     home at the same spot twice.</para>
        ///
        ///     <para>Going through here records WHEN it was steered, so
        ///     <see cref="ExpireDirectMove" /> can stop him the moment nobody is steering any
        ///     more. Callers keep calling it every tick for as long as they want movement —
        ///     which they were doing anyway; the difference is what happens when they stop.</para>
        /// </summary>
        public void DriveDirect(Vector3 dir, bool running)
        {
            m_directMoveAt = Time.time;
            m_character?.SetWalk(false); // a charge never ambles, whatever idle travel left set
            MoveTowards(dir, running);
        }

        /// <summary>
        ///     How long a direct move survives without being renewed. Longer than a behaviour
        ///     tick, short enough that an abandoned one costs a step rather than a hillside.
        /// </summary>
        private const float DirectMoveGrace = 0.5f;

        /// <summary>
        ///     How far to look for navmesh under a villager before refusing to move it. Generous
        ///     — a villager legitimately steps a little off the mesh at ledges and doorways, and
        ///     this is meant to catch being adrift, not being briefly imprecise.
        /// </summary>
        private const float OffMeshStopRadius = 5f;

        private float m_directMoveAt;

        /// <summary>
        ///     Skin added to the villager's own capsule when testing for contact. Two
        ///     physics capsules pressed together rest a hair apart, so an exact-size test
        ///     would miss the very collisions it's meant to catch.
        /// </summary>
        private const float ContactSkin = 0.05f;

        /// <summary>
        ///     How squarely the blocker must sit in the move direction (cosine) to count as
        ///     being pushed into. Brushing past something at the side is not a collision.
        /// </summary>
        private const float PushingIntoCos = 0.5f;

        /// <summary>
        ///     Layers that hold things which move and are therefore never in the bake:
        ///     characters (villagers, players, creatures) and vehicles (carts).
        /// </summary>
        private static int s_dynamicBlockerMask;

        private static readonly Collider[] s_blockerHits = new Collider[16];

        /// <summary>Time.time this villager started yielding to a blocker; 0 = not yielding.</summary>
        private float m_yieldingSince;

        /// <summary>
        ///     How long a villager waits on the same blocker before backing away from it. A
        ///     player standing still, or a body wedged against another, never "untangles" by
        ///     itself — waiting alone left villagers pinned indefinitely.
        /// </summary>
        private const float YieldTimeoutSeconds = 1.5f;

        /// <summary>How long the yielder steps directly away from the blocker before re-planning.</summary>
        private const float BackoffSeconds = 0.75f;

        /// <summary>
        ///     Back-offs from the same blocker (within <see cref="EscapeMemorySeconds" />) before
        ///     an IDLE trip (relax/wander) is dropped. Work trips never give up — they need that
        ///     destination — they just keep backing off and retrying until the blocker moves.
        /// </summary>
        private const int MaxIdleEscapes = 2;

        /// <summary>How long a back-off counts toward <see cref="MaxIdleEscapes" />.</summary>
        private const float EscapeMemorySeconds = 10f;

        private float m_backoffUntil;
        private Vector3 m_backoffDir;
        private GameObject m_escapeBlocker;
        private int m_escapeCount;
        private float m_lastEscapeAt;

        /// <summary>
        ///     True while this villager should stand still because it collided with a mover.
        ///     Triggers only on an actual collision — the villager's body touching the mover
        ///     while walking into it — never on mere proximity: RVO already steers around
        ///     movers that are merely near.
        ///
        ///     <para>Between two villagers exactly ONE yields: the one without right of way
        ///     (<see cref="HasRightOfWay" />) waits while the other keeps walking, until they
        ///     are no longer touching. Both waiting, or both re-planning into each other, is the
        ///     deadlock this exists to prevent. Against anything else — a player, a cart, a
        ///     creature — the villager always yields, since those can't be asked to.</para>
        ///
        ///     <para>Once untangled, the path is dropped so the next tick re-plans from where
        ///     the villager actually stands, not from before it was shoved.</para>
        ///
        ///     <para>If the blocker hasn't moved after <see cref="YieldTimeoutSeconds" />, the
        ///     villager backs away from it for <see cref="BackoffSeconds" /> and re-plans. An idle
        ///     trip that keeps hitting the same blocker is dropped (<see cref="MaxIdleEscapes" />).</para>
        ///
        ///     <para>Drives the body itself while holding (stop, or back away); the caller just
        ///     returns.</para>
        /// </summary>
        private bool HoldForDynamicBlocker(Vector3 dir)
        {
            if (m_backoffUntil > 0f)
            {
                if (Time.time < m_backoffUntil)
                {
                    m_character?.SetWalk(false);
                    MoveTowards(m_backoffDir, false);
                    return true;
                }

                m_backoffUntil = 0f;
                ClearCachedPath();
                StopMoving();
                return true; // backed off; the next tick re-plans from here
            }

            var blocker = FindDynamicBlocker(dir);
            if (blocker != null && !HasRightOfWay(blocker))
            {
                if (m_yieldingSince <= 0f)
                {
                    m_yieldingSince = Time.time;
                    DebugLog.ThrottledWindow(
                        $"blocked:{m_villagerName}", System.TimeSpan.FromSeconds(5f),
                        "Movement", "yielding_to_mover",
                        ("villager", m_villagerName), ("blocker", blocker.name),
                        ("pos", transform.position),
                        ("note", "waiting until untangled, then re-planning"));
                }
                else if (Time.time - m_yieldingSince >= YieldTimeoutSeconds)
                {
                    BeginBackoff(blocker);
                }

                StopMoving();
                return true;
            }

            if (m_yieldingSince <= 0f) return false;

            m_yieldingSince = 0f;
            ClearCachedPath();
            StopMoving();
            return true; // this tick's path is gone; the next tick re-plans
        }

        /// <summary>
        ///     The blocker hasn't cleared: step directly away from it, or — for an idle trip
        ///     that keeps running into the same thing — drop the trip so relax/wander pick
        ///     somewhere else.
        /// </summary>
        private void BeginBackoff(GameObject blocker)
        {
            m_yieldingSince = 0f;

            if (blocker == m_escapeBlocker && Time.time - m_lastEscapeAt <= EscapeMemorySeconds)
                m_escapeCount++;
            else
            {
                m_escapeBlocker = blocker;
                m_escapeCount = 1;
            }

            m_lastEscapeAt = Time.time;

            if (IsCasualTravel && m_escapeCount > MaxIdleEscapes)
            {
                DebugLog.Event("Movement", "idle_trip_dropped",
                    ("villager", m_villagerName), ("blocker", blocker.name),
                    ("pos", transform.position),
                    ("note", $"blocked {m_escapeCount}x — picking somewhere else"));
                m_escapeBlocker = null;
                m_escapeCount = 0;
                ClearCachedPath();
                SetState(BehaviorState.Idle);
                return;
            }

            var away = transform.position - blocker.transform.position;
            away.y = 0f;
            if (away.sqrMagnitude < 1e-4f) away = -transform.forward;
            m_backoffDir = away.normalized;
            m_backoffUntil = Time.time + BackoffSeconds;

            DebugLog.Event("Movement", "backing_off_mover",
                ("villager", m_villagerName), ("blocker", blocker.name),
                ("pos", transform.position), ("attempt", m_escapeCount));
        }

        /// <summary>
        ///     True when this villager keeps walking through a collision with
        ///     <paramref name="blocker" /> and the blocker is the one that yields. Only another
        ///     villager can yield, so against anything else this is false. Decided by the same
        ///     per-villager <c>avoidancePriority</c> RVO uses (lower = right of way), ties broken
        ///     by id, so both sides of a collision always reach opposite answers.
        /// </summary>
        private bool HasRightOfWay(GameObject blocker)
        {
            var other = blocker.GetComponent<VillagerAI>();
            if (other == null || other == this) return false;

            var mine = AvoidancePriority;
            var theirs = other.AvoidancePriority;
            if (mine != theirs) return mine < theirs;
            return string.CompareOrdinal(UniqueId, other.UniqueId) < 0;
        }

        /// <summary>
        ///     This villager's RVO priority (lower = right of way): a stable 20..80 spread from
        ///     the villager id, so the same villager keeps it across rebakes and reloads.
        /// </summary>
        private int AvoidancePriority => 20 + (Mathf.Abs(UniqueId?.GetHashCode() ?? 0) % 61);

        /// <summary>
        ///     The first mover (not this villager) that this villager's body is touching AND
        ///     walking into along <paramref name="dir" />, or null.
        /// </summary>
        private GameObject FindDynamicBlocker(Vector3 dir)
        {
            if (m_character == null) return null;
            if (s_dynamicBlockerMask == 0)
                s_dynamicBlockerMask = LayerMask.GetMask(
                    "character", "character_net", "character_ghost", "character_noenv", "vehicle");

            var radius = m_character.GetRadius() + ContactSkin;
            var height = m_character.GetHeight();
            var pos = transform.position;
            var bottom = pos + Vector3.up * radius;
            var top = pos + Vector3.up * Mathf.Max(radius, height - radius);
            var centre = pos + Vector3.up * (height * 0.5f);
            var forward = dir.normalized;

            var count = Physics.OverlapCapsuleNonAlloc(
                bottom, top, radius, s_blockerHits, s_dynamicBlockerMask, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var col = s_blockerHits[i];
                if (col == null) continue;
                var character = col.GetComponentInParent<Character>();
                if (character == m_character) continue;

                // Touching, but is he walking INTO it? Side contact while passing isn't a push.
                var toBlocker = col.bounds.center - centre;
                toBlocker.y = 0f;
                if (toBlocker.sqrMagnitude < 1e-4f ||
                    Vector3.Dot(forward, toBlocker.normalized) < PushingIntoCos) continue;

                return character != null ? character.gameObject : col.transform.root.gameObject;
            }

            return null;
        }

        /// <summary>True while a behaviour is steering the body itself, bypassing the agent.</summary>
        private bool IsDrivingDirectly =>
            m_directMoveAt > 0f && Time.time - m_directMoveAt <= DirectMoveGrace;

        /// <summary>Stop a direct move that nothing has renewed.</summary>
        private void ExpireDirectMove()
        {
            if (m_directMoveAt <= 0f) return;
            if (Time.time - m_directMoveAt <= DirectMoveGrace) return;

            m_directMoveAt = 0f;

            // Already stopped? Then whoever was steering ended properly (a charge that
            // finished or timed out calls Reset → SetState(Idle) → StopMoving) and there is
            // nothing to rescue. Warning here anyway cried wolf on every normal charge.
            var moveDir = m_character != null ? m_character.GetMoveDir() : Vector3.zero;
            moveDir.y = 0f;
            if (moveDir.sqrMagnitude < 1e-4f) return;

            StopMoving();
            Plugin.Log?.LogWarning(
                $"[AI:{m_villagerName}] a direct move was left running with nobody steering it " +
                "— stopped. (Whatever set it should have stopped it or kept setting it.)");
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