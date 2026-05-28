using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Behaviors;
using ValheimVillages.Behaviors.Explore;
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
    public class VillagerAI : BaseAI, IVillagerWorkContext
    {
        private const float StuckBackoffBase = 10f;
        private const float StuckBackoffMax = 600f;

        private const float PathRetryInterval = 2f;
        private const float SaveInterval = 60f;

        private static readonly FieldInfo s_pathField = typeof(BaseAI).GetField(
            "m_path", BindingFlags.NonPublic | BindingFlags.Instance);

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
        private float m_lastValidationTime;

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
            Memory.DiscoverLocation(m_bedPosition, LocationType.Bed, 0f, true);
            Memory.BedPosition = m_bedPosition;
        }

        public override bool UpdateAI(float dt)
        {
            if (Villager == null) return false;
            if (!base.UpdateAI(dt)) return false;
            if (m_lastBehaviorUpdateTime > 0.0)
            {
                m_lastBehaviorUpdateTime -= dt;
                return false;
            }

            if (IsPaused) return true;

            if (Time.time - m_lastDiscoveryTime > 4f)
            {
                m_lastDiscoveryTime = Time.time;
                VillagerPOIDiscovery.DiscoverNearbyPOIs(transform, Memory);
            }

            if (Time.time - m_lastValidationTime > 30f)
            {
                m_lastValidationTime = Time.time;
                VillagerPOIDiscovery.ValidateKnownLocations(Memory);
            }

            var ctx = new BehaviorContext();
            foreach (var b in m_behaviors)
                if (b.WantsControl(ctx))
                {
                    b.Update(dt);
                    break;
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
                else if (VillagerMovement.IsAtPosition(transform.position, targetPos, VillagerSettings.ArrivalThreshold))
                {
                    OnArrivedAtTarget(dt);
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
                            if (VillagerSettings.AutoPathRecoveryEnabled)
                            {
                                EnterRecovery(m_currentWaypoint);
                                return false;
                            }

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
                        var running = CurrentState == BehaviorState.Patrolling || remaining > 5f;

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
                            DoorHandler?.OpenDoorsAlongPath(path);

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
                                    if (VillagerSettings.AutoPathRecoveryEnabled)
                                    {
                                        if (firedNow)
                                            Plugin.Log?.LogInfo(
                                                $"[AI:{m_villagerName}] Stall escalated to recovery " +
                                                $"after {Time.time - m_stallStartTime:F1}s.");
                                        m_stallLogged = false;
                                        var escalateAgainst =
                                            m_recoveryOriginalWaypoint ?? m_currentWaypoint;
                                        EnterRecovery(escalateAgainst);
                                        return false;
                                    }

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

            // Auto-inject explore as universal lowest-priority fallback
            if (GetBehavior<ExploreBehaviorAdapter>() == null)
            {
                m_behaviors.Add(new ExploreBehaviorAdapter(this));
                m_behaviors.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            }
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

        IReadOnlyList<KnownLocation> IVillagerStationLookup.KnownLocations =>
            Memory?.KnownLocations ?? Array.Empty<KnownLocation>();

        Vector3 IVillagerStationLookup.BedPosition =>
            Memory != null ? Memory.BedPosition : default;

        string IVillagerWorkContext.NpcName => NpcName;
        Vector3 IVillagerWorkContext.Position => Position;

        #endregion

        #region State Management

        public BehaviorState CurrentState { get; private set; } = BehaviorState.Idle;

        /// <summary>True while the AI is in a hard-stuck backoff cooldown and should not start new tasks.</summary>
        public bool IsInBackoff => Time.time < m_stuckBackoffUntil;

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

        public bool FindPath(VillagerWaypoint destination)
        {
            var hasPath = FindPath(destination.Position);
            if (hasPath) m_currentWaypoint = destination;

            return hasPath;
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

            var retreat = Memory?.GetMostRecentlyVisitedLocation(originalPos);
            var retreatPos = retreat != null ? retreat.Position : m_bedPosition;

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

            // Stamp the most relevant KnownLocation as visited so the
            // unreachable-target recovery flow can pick "most recent"
            // retreat targets that the villager actually has been to.
            if (Memory != null)
            {
                foreach (var loc in Memory.KnownLocations)
                {
                    if (loc != null && loc.IsSameLocation(transform.position))
                    {
                        loc.LastVisitedAt = Time.time;
                        break;
                    }
                }
            }

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