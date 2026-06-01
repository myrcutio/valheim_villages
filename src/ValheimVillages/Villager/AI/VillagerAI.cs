using System;
using System.Collections.Generic;
using System.Reflection;
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
using ValheimVillages.Villages;
using Random = UnityEngine.Random;

namespace ValheimVillages.Villager.AI
{
    public partial class VillagerAI : BaseAI, IVillagerWorkContext
    {
        private const float StuckBackoffBase = 10f;
        private const float StuckBackoffMax = 600f;

        private const float PathRetryInterval = 2f;
        private const float SaveInterval = 60f;

        /// <summary>
        ///     Vertical/spatial radius for snapping a NavTo destination onto the
        ///     agent navmesh. Sized to catch approach points resolved up to ~2m
        ///     above the walkable surface (chest/station Y over the floor)
        ///     without mapping to a different level.
        /// </summary>
        private const float NavToSnapRadius = 2f;

        private static readonly FieldInfo s_pathField = typeof(BaseAI).GetField(
            "m_path", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        ///     EXPERIMENT toggle: when true, villager movement is driven by a
        ///     real Unity NavMeshAgent (advisory mode — updatePosition=false; we
        ///     read desiredVelocity and feed it into Valheim's character via
        ///     MoveTowards). Lets Unity own path-following, local steering, and
        ///     off-mesh link traversal instead of the hand-rolled corner-walker.
        ///     Flip with vv_agentmover.
        /// </summary>
        public static bool NavMeshAgentMover = true;

        /// <summary>
        ///     EXPERIMENT toggle: off-mesh self-rescue (teleport the villager
        ///     back onto the nearest mesh/HNA cell when it's off-graph). Disabled
        ///     for now — climbing a hill/stairs legitimately takes the villager
        ///     off the HNA graph briefly, and the rescue was yanking it back down
        ///     mid-climb. Flip with vv_offmeshrescue.
        /// </summary>
        public static bool OffMeshRescueEnabled = false;

        private NavMeshAgent m_navAgent;

        private Vector3 m_bedPosition;

        // Composable behaviors (populated by BehaviorFactory from NPC definition)
        private List<IBehavior> m_behaviors = new();

        // Hard-stuck backoff
        private int m_consecutiveStucks;
        private VillagerWaypoint m_currentWaypoint;
        private DoorHandler m_doorHandler;

        // Exploration
        private float m_explorationStartTime;
        private Vector3? m_explorationTarget;

        /// <summary>Time.time when the guard last arrived at a waypoint. Used for hard stuck timeout.</summary>
        private float m_lastArrivalTime;

        private float m_lastBehaviorUpdateTime;

        // Timing
        private float m_lastDiscoveryTime;
        private float m_lastMemorySaveTime;

        private Vector3 m_lastMovePos;
        private float m_lastRealMoveTime;

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
        ///     produce. Populated inline at SetState/SetPatrolCircuit/
        ///     TryFindPathCustom/EnterRecovery; consumed by IncidentRecorder.
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

        /// <summary>When the current movement target was set (for stuck timeout).</summary>
        private float m_targetSetTime;

        // ----- Path-unreachable recovery state -----
        // When TryFindCompletePath fails, we don't repeatedly retry against
        // the same unreachable target (which would step-jump forever). Instead
        // we retreat to a recently-visited KnownLocation, wait an exponential
        // backoff, then re-issue the original waypoint. After
        // VillagerSettings.MaxRecoveryAttempts failed retries we fire
        // IPathUnreachableHandler.OnPathUnreachable so the behavior can give up.
        private int m_recoveryAttempts;
        private bool m_recoveryRetreating;
        private VillagerWaypoint m_recoveryOriginalWaypoint;
        private float m_recoveryRetryAt;

        // Debounce the TryFindPathCustom-failed warning so it doesn't fire
        // every tick the path stays unresolvable. Re-log only when the
        // target changes or PathFailWarnInterval seconds elapse.
        private Vector3? m_lastPathFailTarget;
        private float m_lastPathFailWarnTime;
        private const float PathFailWarnInterval = 10f;

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

            m_lastArrivalTime = Time.time;

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

        public override bool UpdateAI(float dt)
        {
            if (Villager == null) return false;
            if (!base.UpdateAI(dt)) return false;
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
                else if (!TryRescueOffMesh())
                {
                    var ctx = new BehaviorContext();
                    var handled = false;
                    foreach (var b in m_behaviors)
                        if (b.WantsControl(ctx))
                        {
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
                var path = s_pathField?.GetValue(this) as List<Vector3>;

                // Recovery: if we're retreating and have arrived at the retreat
                // target, wait for the backoff to elapse, then restore the
                // original waypoint and let next tick retry the real target.
                if (m_recoveryRetreating &&
                    VillagerMovement.IsAtPosition(transform.position, targetPos, VillagerSettings.ArrivalThreshold))
                {
                    if (Time.time >= m_recoveryRetryAt && m_recoveryOriginalWaypoint != null)
                    {
                        Plugin.Log?.LogInfo(
                            $"[AI:{m_villagerName}] Recovery backoff elapsed; retrying original target " +
                            $"({m_recoveryOriginalWaypoint.Position:F1})");
                        m_currentWaypoint = m_recoveryOriginalWaypoint;
                        m_recoveryRetreating = false;
                        m_recoveryOriginalWaypoint = null;
                        m_targetSetTime = Time.time;
                        m_lastMovePos = transform.position;
                        m_lastRealMoveTime = Time.time;
                        path?.Clear();
                    }
                    // else: idle at retreat location until backoff elapses
                }
                else if (NavMeshAgentMover
                             ? AgentHasArrived(targetPos)
                             : VillagerMovement.IsAtPosition(transform.position, targetPos, VillagerSettings.ArrivalThreshold))
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
                else if (NavMeshAgentMover)
                {
                    var agentRunning = CurrentState == BehaviorState.Patrolling
                                       || (!IsCasualTravel && remaining > 5f);
                    UpdateAgentMovement(targetPos, agentRunning);
                }
                else
                {
                    if (path == null || path.Count == 0)
                    {
                        var found = TryFindPathCustom(targetPos);
                        path = s_pathField?.GetValue(this) as List<Vector3>;

                        // No complete path to the target — surface as a
                        // diagnostic and idle. Per user direction: no
                        // PlaceLinks fallback here. If links are missing,
                        // that's something earlier in the pipeline failing
                        // to keep them in place — Plugin.cs already calls
                        // PlaceLinks proactively when RegionGraph is
                        // available and HasLinks is false, so a failure
                        // here means either that proactive call hasn't
                        // fired yet OR the target is genuinely unreachable.
                        // Recovery is gated off so the villager stays
                        // visibly stuck for investigation.
                        if (!found && !m_recoveryRetreating)
                        {
                            // Debounce: warn once per target, then at most
                            // every PathFailWarnInterval seconds. Without
                            // this, the warning fires every frame the path
                            // stays unresolvable — recovery is disabled, so
                            // nothing changes tick-to-tick.
                            var targetChanged = !m_lastPathFailTarget.HasValue ||
                                                Vector3.Distance(m_lastPathFailTarget.Value, targetPos) > 0.1f;
                            var intervalElapsed = Time.time - m_lastPathFailWarnTime > PathFailWarnInterval;
                            if (targetChanged || intervalElapsed)
                            {
                                Plugin.Log?.LogWarning(
                                    $"[AI:{m_villagerName}] TryFindPathCustom failed for target " +
                                    $"({targetPos.x:F1},{targetPos.y:F1},{targetPos.z:F1}) and auto-recovery " +
                                    $"is disabled — villager will idle here until a manual fix or partition rebake.");
                                m_lastPathFailTarget = targetPos;
                                m_lastPathFailWarnTime = Time.time;
                            }
                        }

                        // Reject paths that are absurdly indirect (NavMesh gap workaround)
                        if (path != null && path.Count > 2 && CurrentState == BehaviorState.Patrolling)
                        {
                            var straightDist = Vector3.Distance(transform.position, targetPos);
                            var pathLen = 0f;
                            var prev = transform.position;
                            for (var pi = 0; pi < path.Count; pi++)
                            {
                                pathLen += Vector3.Distance(prev, path[pi]);
                                prev = path[pi];
                            }

                            if (straightDist > 1f && pathLen > straightDist * 3f)
                            {
                                // Drop the indirect path and let next tick
                                // re-evaluate. No PlaceLinks fallback here
                                // either — same reasoning as the path-empty
                                // branch above.
                                path.Clear();
                            }
                        }
                    }

                    if (path != null && path.Count > 0)
                    {
                        // Walk when this is Explore-driven travel (visual cue
                        // that the villager isn't busy); run for Patrolling,
                        // or for distant work-driven travel. Casual travel
                        // is cleared by SetState on the next non-casual
                        // waypoint, so this naturally reverts to run-when-
                        // far once a real work order arrives.
                        var running = CurrentState == BehaviorState.Patrolling
                                      || (!IsCasualTravel && remaining > 5f);

                        while (path.Count > 1 && VillagerMovement.IsAtPosition(transform.position, path[0], VillagerSettings.PathNodePopThreshold))
                            path.RemoveAt(0);

                        if (path.Count == 1 &&
                            VillagerMovement.IsAtPosition(transform.position, path[0], VillagerSettings.PathNodePopThreshold))
                        {
                            path.Clear();
                            if (Time.time - m_targetSetTime >= PathRetryInterval)
                            {
                                m_targetSetTime = Time.time;
                                TryFindPathCustom(targetPos);
                                path = s_pathField?.GetValue(this) as List<Vector3>;
                            }
                        }

                        if (path != null && path.Count > 0)
                        {

                            var diff = path[0] - transform.position;
                            var dir = diff.normalized;
                            MoveTowards(dir, running);

                            var moveDelta = Vector3.Distance(transform.position, m_lastMovePos);
                            if (moveDelta > 0.15f)
                            {
                                m_lastMovePos = transform.position;
                                m_lastRealMoveTime = Time.time;
                                if (m_stallLogged)
                                {
                                    Plugin.Log?.LogInfo(
                                        $"[AI:{m_villagerName}] Stall resolved after " +
                                        $"{Time.time - m_stallStartTime:F1}s (moved {moveDelta:F2}m).");
                                    m_stallLogged = false;
                                }
                            }
                            else if (Time.time - m_lastRealMoveTime > DoorSettings.MovementStallThreshold)
                            {
                                DebugLog.Append("VillagerAI.cs:stall", "stall_detected", new Dictionary<string, object>
                                {
                                    { "stallDuration", Time.time - m_lastRealMoveTime },
                                    { "hasDoorHandler", DoorHandler != null },
                                    { "npcPos", transform.position.ToString("F2") },
                                    { "targetPos", targetPos.ToString("F2") },
                                    { "moveDelta", Vector3.Distance(transform.position, m_lastMovePos) },
                                }, "H1H2H3", "run1");
                                if (DoorHandler != null)
                                {
                                    var blockingDoor = DoorHandler.GetBlockingDoor(targetPos);
                                    DebugLog.Append("VillagerAI.cs:doorCheck", "blocking_door_result",
                                        new Dictionary<string, object>
                                        {
                                            { "foundDoor", blockingDoor != null },
                                            {
                                                "doorPos",
                                                blockingDoor != null
                                                    ? blockingDoor.transform.position.ToString("F2")
                                                    : "none"
                                            },
                                        }, "H3", "run1");
                                    if (blockingDoor != null)
                                    {
                                        DoorHandler.OpenDoor(blockingDoor);
                                        m_lastRealMoveTime = Time.time;
                                    }
                                }

                                if (VillagerSettings.StepJumpEnabled &&
                                    Time.time - m_lastRealMoveTime > 1.5f && m_character != null)
                                {
                                    var stepUp = path[0].y - transform.position.y;
                                    if (stepUp > 0.2f)
                                    {
                                        var origForce = m_character.m_jumpForce;
                                        var origForward = m_character.m_jumpForceForward;
                                        m_character.m_jumpForce *= VillagerSettings.StepJumpForceFraction;
                                        m_character.m_jumpForceForward *= VillagerSettings.StepJumpForceFraction;
                                        m_character.Jump();
                                        m_character.m_jumpForce = origForce;
                                        m_character.m_jumpForceForward = origForward;
                                        m_lastRealMoveTime = Time.time;
                                    }
                                }

                                // Long stall escape: the door / step-jump
                                // heuristics haven't moved us in
                                // PathStallEscapeSeconds despite a non-empty
                                // path. Most likely NavMesh.CalculatePath
                                // returned PathComplete via an island-bridge
                                // NavMeshLink that's physically untraversable
                                // (e.g. vertical jump across a ceiling).
                                // Consume a recovery attempt — three stalls
                                // hit MaxRecoveryAttempts and fire
                                // OnPathUnreachable so the behavior can
                                // AbandonWork. Fires even while retreating:
                                // a stall on the retreat path is the same
                                // failure mode and should escalate.
                                if (Time.time - m_lastRealMoveTime > VillagerSettings.PathStallEscapeSeconds)
                                {
                                    var lastNode = path[path.Count - 1];
                                    var firedNow = !m_stallLogged;
                                    if (firedNow)
                                    {
                                        m_stallLogged = true;
                                        m_stallStartTime = m_lastRealMoveTime;
                                        Plugin.Log?.LogWarning(
                                            $"[AI:{m_villagerName}] Path stall escape after " +
                                            $"{Time.time - m_lastRealMoveTime:F1}s no move. " +
                                            $"state={CurrentState} pos=({transform.position.x:F1},{transform.position.y:F1},{transform.position.z:F1}) " +
                                            $"target=({targetPos.x:F1},{targetPos.y:F1},{targetPos.z:F1}) " +
                                            $"path.Count={path.Count} " +
                                            $"path[0]=({path[0].x:F1},{path[0].y:F1},{path[0].z:F1}) " +
                                            $"path[last]=({lastNode.x:F1},{lastNode.y:F1},{lastNode.z:F1}) " +
                                            $"distToNext={Vector3.Distance(transform.position, path[0]):F2} " +
                                            $"distToTarget={Vector3.Distance(transform.position, targetPos):F2} " +
                                            $"distToLast={Vector3.Distance(transform.position, lastNode):F2} " +
                                            $"recoveryAttempts={m_recoveryAttempts} retreating={m_recoveryRetreating}");
                                        // Structured incident dump alongside the warning.
                                        // Composite dedup means re-firing the same key
                                        // (same villager + same destination bucket +
                                        // same kind) just bumps a counter on disk.
                                        Diagnostics.IncidentRecorder.Record(this, targetPos, "stall_escape");
                                    }
                                    // While retreating, escalate against the
                                    // real original target (the chest we
                                    // were trying to reach), not the retreat
                                    // target itself — otherwise we'd lose
                                    // track of what we were originally
                                    // trying to do.
                                    // Gated by AutoPathRecoveryEnabled:
                                    // when off, the stall logs but does
                                    // NOT call EnterRecovery, so the
                                    // villager stays visibly stuck and we
                                    // can investigate the underlying path
                                    // failure without recovery hiding it.

                                    // Reset the stall timer so we re-evaluate
                                    // the gate; m_stallLogged stays true so we
                                    // don't re-emit the warning every tick. A
                                    // "Stall resolved" log fires when the
                                    // villager actually moves.
                                    m_lastRealMoveTime = Time.time;
                                }
                            }
                        }
                    }

                    if (CurrentState == BehaviorState.Patrolling)
                    {
                        if (Time.time < m_stuckBackoffUntil)
                        {
                            // Still in backoff cooldown — stay idle at bed
                        }
                        else if (Time.time - m_lastArrivalTime > VillagerSettings.PatrolHardStuckTimeoutSeconds)
                        {
                            m_consecutiveStucks++;
                            var backoff = Mathf.Min(
                                StuckBackoffBase * Mathf.Pow(2f, m_consecutiveStucks - 1),
                                StuckBackoffMax);
                            m_stuckBackoffUntil = Time.time + backoff;

                            Plugin.Log?.LogWarning(
                                $"[AI:{m_villagerName}] Hard stuck timeout ({Time.time - m_lastArrivalTime:F0}s). " +
                                $"Teleporting to bed. Backoff {backoff:F0}s " +
                                $"(attempt {m_consecutiveStucks})");
                            transform.position = m_bedPosition + Vector3.up * 0.5f;
                            m_lastArrivalTime = Time.time;
                            SetState(BehaviorState.Idle);
                        }
                    }
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
            (s_pathField?.GetValue(this) as List<Vector3>)?.Clear();
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
                m_targetSetTime = Time.time;
                m_lastMovePos = transform.position;
                m_lastRealMoveTime = Time.time;
                // A fresh waypoint from a behavior cancels any in-flight
                // recovery: we're under new orders, the prior retreat /
                // backoff / attempt counter no longer applies.
                ResetRecoveryState();
                // Clear casual-travel marker by default. Behaviors that
                // WANT casual travel (Explore wandering to a known
                // location) set it back to true AFTER this returns.
                IsCasualTravel = false;
                // Invalidate any in-flight path: it was computed against the
                // previous target and would otherwise keep being followed
                // until the villager arrived at its old destination (and
                // only THEN recomputed against the new one). The Update
                // path-follow code branches on path.Count==0 to trigger a
                // fresh TryFindPathCustom against m_currentWaypoint, so
                // clearing here is the minimum surgery that produces the
                // expected "new target, new path" semantics on the next
                // tick. Confirmed by incident bundle 001_Farmer (May 2026):
                // three TargetSet events in 60ms left only the first one's
                // path live, villager stalled following stale corners.
                var path = s_pathField?.GetValue(this) as List<Vector3>;
                path?.Clear();
                EventRing.RecordTargetSet(waypoint.Position, prevTarget, $"SetState({newState})");
            }

            if (prevState != newState)
                EventRing.RecordStateChange(prevState.ToString(), newState.ToString(),
                    waypoint != null ? "with_waypoint" : "no_waypoint");

            if (newState == BehaviorState.Idle)
                StopMoving();
            if (waypoint != null)
                Plugin.Log?.LogDebug(
                    $"[AI:{m_villagerName}] State -> {newState}, target=({waypoint.Position.x:F1},{waypoint.Position.y:F1},{waypoint.Position.z:F1})");
            else
                Plugin.Log?.LogDebug($"[AI:{m_villagerName}] State -> {newState}");
        }

        /// <summary>
        ///     Inject a pre-computed patrol circuit path. The guard follows the full path
        ///     and only triggers OnArrival when reaching the final waypoint.
        ///     Falls back to single-waypoint FindPath if the path is empty.
        /// </summary>
        public void SetPatrolCircuit(VillagerWaypoint finalTarget, List<Vector3> circuitPath)
        {
            var prevState = CurrentState;
            var prevTarget = m_currentWaypoint != null ? m_currentWaypoint.Position : Vector3.zero;
            CurrentState = BehaviorState.Patrolling;
            m_currentWaypoint = finalTarget;
            m_targetSetTime = Time.time;
            m_lastMovePos = transform.position;
            m_lastRealMoveTime = Time.time;
            m_lastArrivalTime = Time.time;
            EventRing.RecordTargetSet(finalTarget.Position, prevTarget, "SetPatrolCircuit");
            if (prevState != BehaviorState.Patrolling)
                EventRing.RecordStateChange(prevState.ToString(), "Patrolling", "SetPatrolCircuit");

            var path = s_pathField?.GetValue(this) as List<Vector3>;
            if (path != null)
            {
                path.Clear();
                path.AddRange(circuitPath);
            }

            Plugin.Log?.LogDebug(
                $"[AI:{m_villagerName}] State -> Patrolling (circuit: {circuitPath.Count} path nodes)");
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
        ///     Off-mesh self-rescue gate. If the villager's current position
        ///     is more than a small radius from any walkable NavMesh
        ///     polygon, find the nearest valid mesh point and dispatch the
        ///     villager toward it as Casual (Explore-style) travel.
        ///     Returns true when a rescue was issued (caller should skip
        ///     normal behavior selection), false when the villager is
        ///     already on the mesh (the common case).
        ///     <para>Covers a class of off-mesh stalls: spawned on top of
        ///     a bed / chest / station that the bake correctly excluded
        ///     from walkable surfaces, bumped off by terrain edits, tree
        ///     felling, etc. Without this gate the villager would issue
        ///     path queries forever and they'd all return Failed because
        ///     NavMesh.SamplePosition at the start can't find a polygon.</para>
        ///     <para>Per the no-silent-fallbacks rule: if no mesh is found
        ///     within the maximum search radius, the villager is genuinely
        ///     stranded (unloaded zone, bake hole). Log loudly + return
        ///     false so the calling tick proceeds normally — better to let
        ///     the next stall-escape incident surface the issue than to
        ///     silently teleport to a fabricated point.</para>
        /// </summary>
        private bool TryRescueOffMesh()
        {
            // Disabled while we let villagers climb off-graph terrain (stairs/
            // hills) — the rescue was teleporting climbers back down. Returning
            // false = "not rescued", so the normal movement tick proceeds.
            if (!OffMeshRescueEnabled) return false;
            if (!VillagerAgentType.IsRegistered) return false;
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            // Already on the mesh AND on the HNA graph? "On the mesh"
            // alone isn't enough — Unity's voxelizer leaves sliver/orphan
            // polygons where HNA correctly excluded space, and a villager
            // standing on one of those would silently early-return here
            // even though every CalculatePath from that polygon will fail
            // (the corridor planner rejects off-HNA starts). Tight 1m
            // probe (agent body ~0.4m → 1m clearance for movement); HNA
            // resolution via the village graph containing the villager's
            // position.
            var onMesh = NavMesh.SamplePosition(transform.position, out _, 1f, filter);
            var onHna = false;
            if (onMesh)
            {
                foreach (var graph in RegionGraph.GetAll())
                {
                    if (!string.IsNullOrEmpty(graph.PointToRegionId(transform.position)))
                    {
                        onHna = true;
                        break;
                    }
                }
                // If no HNA graph is yet built for this area (e.g. villager
                // spawned before the first partition completed), treat
                // on-mesh as sufficient — the corridor planner falls
                // through to unconstrained CalculatePath in that case too.
                if (!RegionGraph.IsAnyAvailable) onHna = true;
            }

            if (onMesh && onHna)
                return false;

            // Off-mesh. Three-stage search, each weaker / more permissive:
            //   1. Elevation-aware NavMesh snap. Probe XZ rings at the
            //      villager's Y ± agentClimb so the snap target is a step
            //      the agent could plausibly reach from where they stand.
            //      Handles spawned-on-bed cleanly: drop to the floor NEXT
            //      TO the bed instead of straight through it.
            //   2. HNA lookup-cell snap. Falls back to the village's HNA
            //      lookup grid — the canonical "any walkable cell in this
            //      village" set, independent of NavMesh polygon coverage.
            //      Used when the villager has fallen into a position the
            //      bake disregarded as outside-the-village-hull (no
            //      NavMesh nearby, but HNA still knows about valid cells
            //      a few meters away).
            //   3. Broad NavMesh fallback. Wide-radius SamplePosition,
            //      accept any nearby polygon regardless of Y delta. Last
            //      resort for cases the HNA graph also can't help with
            //      (e.g. no HNA available yet at boot).
            Vector3 rescueHit;
            string snapSource;
            if (TryFindReachableMeshSnap(filter, out rescueHit))
            {
                snapSource = "elevation-aware";
            }
            else if (TryFindHnaLookupCellSnap(out rescueHit))
            {
                snapSource = "hna-lookup";
            }
            else if (NavMesh.SamplePosition(transform.position, out var broadHit, 16f, filter))
            {
                rescueHit = broadHit.position;
                snapSource = "broad-16m";
            }
            else
            {
                Plugin.Log?.LogWarning(
                    $"[AI:{m_villagerName}] Off-mesh self-rescue failed: no NavMesh polygon " +
                    $"and no HNA lookup cell within reach of " +
                    $"({transform.position.x:F1},{transform.position.y:F1},{transform.position.z:F1}). " +
                    "Villager genuinely stranded — check bake coverage or unloaded zone.");
                return false;
            }

            Plugin.Log?.LogInfo(
                $"[AI:{m_villagerName}] Off-mesh self-rescue: teleporting to mesh ({snapSource}) " +
                $"({rescueHit.x:F1},{rescueHit.y:F1},{rescueHit.z:F1}) " +
                $"from ({transform.position.x:F1},{transform.position.y:F1},{transform.position.z:F1}).");

            // Direct teleport rather than SetState(Traveling). Path-finding
            // from the current pos fails (NavMesh.SamplePosition can't snap
            // an off-mesh start) so a Traveling target wouldn't actually
            // produce movement — confirmed empirically: the previous SetState
            // approach logged "rescue dispatched" every behavior tick
            // without the villager moving. Teleporting puts them on a valid
            // mesh cell, from which normal pathing resumes.
            transform.position = rescueHit;
            m_lastMovePos = rescueHit;
            m_lastRealMoveTime = Time.time;
            return true;
        }

        /// <summary>
        ///     Find the nearest HNA lookup-grid cell to the villager's pos via
        ///     <see cref="RegionGraph.GetNearest"/> + the graph's lookup
        ///     index. Independent of NavMesh polygon coverage — used when
        ///     the villager fell into a position outside the bake's walkable
        ///     area but the village's HNA graph still knows about valid
        ///     cells nearby. Returns true and writes the cell pos on
        ///     success.
        /// </summary>
        public bool TryFindHnaLookupCellSnap(out Vector3 snapped)
        {
            snapped = default;
            var graph = RegionGraph.GetNearest(transform.position);
            if (graph == null)
            {
                Plugin.Log?.LogDebug(
                    $"[AI:{m_villagerName}] HNA snap unavailable: RegionGraph.GetNearest returned null " +
                    $"at ({transform.position.x:F1},{transform.position.y:F1},{transform.position.z:F1}). " +
                    "No villages registered yet?");
                return false;
            }
            if (!graph.IsAvailable)
            {
                Plugin.Log?.LogDebug(
                    $"[AI:{m_villagerName}] HNA snap unavailable: nearest village graph " +
                    $"'{graph.RegisteredVillageKey}' is not IsAvailable.");
                return false;
            }
            var ok = graph.TryFindNearestLookupCell(
                transform.position,
                validator: null, // any cell is fine; we just want SOME mesh
                out snapped,
                out _);
            if (!ok)
                Plugin.Log?.LogDebug(
                    $"[AI:{m_villagerName}] HNA snap unavailable: TryFindNearestLookupCell returned " +
                    $"false from village '{graph.RegisteredVillageKey}' (lookup-grid empty?).");
            return ok;
        }

        /// <summary>
        ///     Find a NavMesh point that's both close in XZ to the villager AND
        ///     at roughly their own elevation — so the agent can step onto it
        ///     from where they're standing instead of being told to drop
        ///     through solid geometry. Probes a small set of XZ offsets around
        ///     the villager's pos, samples NavMesh at each, accepts the first
        ///     whose Y delta from the villager is within agentClimb + margin.
        /// </summary>
        private bool TryFindReachableMeshSnap(NavMeshQueryFilter filter, out Vector3 snapped)
        {
            snapped = default;
            var maxStep = VillagerAgentType.TryGetClimb(out var climb) ? climb + 0.3f : 0.6f;
            var pos = transform.position;

            // Probe in concentric rings out to 6m. The center probe (offset 0)
            // catches a villager standing on a 1-cell hole in the mesh —
            // common when a piece was removed under their feet. The wider
            // rings catch the spawned-on-bed / pushed-off-foundation cases
            // where there's no mesh directly beneath them but a walkable
            // surface is a step or two to the side.
            //
            // Each candidate snap must satisfy BOTH:
            //   (1) NavMesh.SamplePosition succeeds at maxStep.
            //   (2) The hit position is on the HNA graph (or no HNA graph
            //       is built yet — then NavMesh alone is sufficient).
            // Without (2), the snap can land on the same prune-orphan
            // sliver polygon the villager was already standing on,
            // producing a rescue→teleport→rescue loop (observed
            // empirically: Farmer 36.9 → 37.0, Blacksmith 37.4 → 36.9, both
            // still off-HNA). (2) makes the rescue actually walk the
            // villager out of the prune-orphan zone.
            var hnaAvailable = RegionGraph.IsAnyAvailable;
            float[] ringRadii = { 0f, 1.5f, 3f, 4.5f, 6f };
            int[] ringSamples = { 1, 8, 12, 16, 16 };
            for (var r = 0; r < ringRadii.Length; r++)
            {
                var radius = ringRadii[r];
                var samples = ringSamples[r];
                for (var i = 0; i < samples; i++)
                {
                    var angle = samples == 1 ? 0f : 2f * Mathf.PI * i / samples;
                    var probe = new Vector3(
                        pos.x + Mathf.Cos(angle) * radius,
                        pos.y,
                        pos.z + Mathf.Sin(angle) * radius);
                    // SamplePosition with a small vertical search radius so
                    // we don't snap straight down through a bed/floor. The
                    // agent climb threshold filters anything that would
                    // require an unreachable drop or step-up.
                    if (!NavMesh.SamplePosition(probe, out var hit, maxStep, filter)) continue;
                    if (Mathf.Abs(hit.position.y - pos.y) > maxStep) continue;
                    if (hnaAvailable && !IsOnAnyHnaGraph(hit.position)) continue;
                    snapped = hit.position;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        ///     True iff <paramref name="position"/> resolves to a region in
        ///     any registered RegionGraph. Used by self-rescue to gate
        ///     NavMesh snap candidates so the rescue doesn't land on a
        ///     prune-orphaned sliver polygon (still on NavMesh, off HNA).
        /// </summary>
        private static bool IsOnAnyHnaGraph(Vector3 position)
        {
            foreach (var graph in RegionGraph.GetAll())
            {
                if (!string.IsNullOrEmpty(graph.PointToRegionId(position)))
                    return true;
            }
            return false;
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
                var blockingDoor = DoorHandler.GetBlockingDoor(targetPos);
                if (blockingDoor != null) DoorHandler.OpenDoor(blockingDoor);
            }

            MoveTowards(dir.normalized, running);
        }

        private bool TryFindPathCustom(Vector3 targetPos)
        {
            var path = s_pathField?.GetValue(this) as List<Vector3>;
            if (path == null)
            {
                EventRing.RecordPathRecompute("NullPathField", 0, targetPos);
                return false;
            }
            var ok = VillagerMovement.TryFindCompletePath(transform.position, targetPos, path);
            EventRing.RecordPathRecompute(ok ? "Complete" : "Failed", path.Count, targetPos);

            // Always log path shape — corner count, segment lengths, HNA region per corner. This
            // is the ground-truth diagnostic: lets us see whether paths are degenerate (1 corner,
            // no intermediate turns) or routing through cells the HNA graph doesn't recognize.
            var graph = ValheimVillages.Villager.AI.Navigation.RegionGraph.GetNearest(transform.position);
            if (Plugin.Log != null)
            {
                if (!ok)
                {
                    Plugin.Log.LogInfo(
                        $"[PathDiag:{m_villagerName}] CalculatePath FAILED from " +
                        $"({transform.position.x:F1},{transform.position.y:F1},{transform.position.z:F1}) " +
                        $"to ({targetPos.x:F1},{targetPos.y:F1},{targetPos.z:F1})");
                }
                else
                {
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"[PathDiag:{m_villagerName}] path corners={path.Count} ");
                    sb.Append($"from=({transform.position.x:F1},{transform.position.y:F1},{transform.position.z:F1}) ");
                    sb.Append($"target=({targetPos.x:F1},{targetPos.y:F1},{targetPos.z:F1})");
                    for (var i = 0; i < path.Count; i++)
                    {
                        var p = path[i];
                        var reg = graph?.PointToRegionId(p) ?? "(no-graph)";
                        var segLen = i == 0
                            ? Vector3.Distance(transform.position, p)
                            : Vector3.Distance(path[i - 1], p);
                        sb.Append($"\n  [{i}] ({p.x:F1},{p.y:F1},{p.z:F1}) seg={segLen:F1}m region={reg ?? "(off-graph)"}");
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                }
            }

            return ok;
        }

        /// <summary>
        ///     Engage the unreachable-target recovery flow. Swaps
        ///     <see cref="m_currentWaypoint" /> for a retreat target (most
        ///     recently visited <see cref="KnownLocation" />, or bed when
        ///     none qualify) and arms a backoff before re-trying the
        ///     original. After <see cref="VillagerSettings.MaxRecoveryAttempts" />
        ///     failed attempts, dispatches <see cref="IPathUnreachableHandler.OnPathUnreachable" />
        ///     to the active behaviors and clears the waypoint.
        /// </summary>
        private void EnterRecovery(VillagerWaypoint originalWaypoint)
        {
            m_recoveryAttempts++;
            var originalPos = originalWaypoint.Position;

            if (m_recoveryAttempts > VillagerSettings.MaxRecoveryAttempts)
            {
                Plugin.Log?.LogWarning(
                    $"[AI:{m_villagerName}] Path unreachable: gave up after " +
                    $"{m_recoveryAttempts - 1} retries to {originalPos:F1}; firing OnPathUnreachable");
                m_currentWaypoint = null;
                ResetRecoveryState();

                foreach (var b in m_behaviors)
                    if (b is IPathUnreachableHandler h)
                    {
                        h.OnPathUnreachable(originalPos);
                        break;
                    }

                return;
            }

            // Retreat toward the nearest village PoI that isn't the spot we're
            // stuck at; fall back to the bed when the village has no usable PoI.
            var retreatPos = m_bedPosition;
            var bestRetreatDistSq = float.MaxValue;
            foreach (var poi in VillagePoiRegistry.GetPois(m_bedPosition))
            {
                if (poi.IsSameLocation(originalPos)) continue;
                var dSq = (poi.Position - transform.position).sqrMagnitude;
                if (dSq < bestRetreatDistSq)
                {
                    bestRetreatDistSq = dSq;
                    retreatPos = poi.Position;
                }
            }

            var backoff = Mathf.Min(
                VillagerSettings.RecoveryBackoffBaseSeconds *
                Mathf.Pow(2f, m_recoveryAttempts - 1),
                VillagerSettings.RecoveryBackoffMaxSeconds);

            m_recoveryOriginalWaypoint = originalWaypoint;
            m_recoveryRetreating = true;
            m_recoveryRetryAt = Time.time + backoff;

            m_currentWaypoint = VillagerWaypoint.WithDefault(retreatPos);
            m_targetSetTime = Time.time;
            m_lastMovePos = transform.position;
            m_lastRealMoveTime = Time.time;
            (s_pathField?.GetValue(this) as List<Vector3>)?.Clear();

            Plugin.Log?.LogInfo(
                $"[AI:{m_villagerName}] Path unreachable to {originalPos:F1} " +
                $"(attempt {m_recoveryAttempts}/{VillagerSettings.MaxRecoveryAttempts}); " +
                $"retreating to {retreatPos:F1}, will retry after {backoff:F0}s");
        }

        private void ResetRecoveryState()
        {
            m_recoveryAttempts = 0;
            m_recoveryRetreating = false;
            m_recoveryOriginalWaypoint = null;
            m_recoveryRetryAt = 0f;
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
            (s_pathField?.GetValue(this) as List<Vector3>)?.Clear();
        }

        private void OnArrivedAtTarget(float dt)
        {
            m_lastArrivalTime = Time.time;
            m_consecutiveStucks = 0;
            // Direct order fulfilled — release the behavior lockout so normal
            // task-queue behavior resumes on the next selection tick.
            m_directOrderActive = false;
            // Successful arrival at the real target — wipe any recovery
            // bookkeeping. The next failed FindPath starts a fresh attempt
            // counter rather than inheriting prior unrelated failures.
            ResetRecoveryState();

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
            (s_pathField?.GetValue(this) as List<Vector3>)?.Clear();

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