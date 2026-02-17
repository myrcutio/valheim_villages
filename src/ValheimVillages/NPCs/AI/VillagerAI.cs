using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using ValheimVillages.NPCs.AI.Guards;
using ValheimVillages.NPCs.AI.Work;
using ValheimVillages.NPCs.AI.Work.Farming;
using ValheimVillages.TaskQueue;
using ValheimVillages.TaskQueue.Handlers;
using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Core villager AI that replaces the game's MonsterAI.UpdateAI.
    /// Inspired by MobAILib's MobAIBase pattern: holds a BaseAI reference and
    /// drives movement via reflection, avoiding fights with the game's random movement system.
    /// </summary>
    public class VillagerAI
    {
        private readonly MonsterAI m_instance;
        private readonly string m_uniqueId;
        private VillagerMemory m_memory;
        private BehaviorState m_state = BehaviorState.Idle;
        private VillagerWaypoint m_currentWaypoint;
        private bool m_paused;

        // Timing
        private float m_lastDiscoveryTime;
        private float m_lastBehaviorUpdateTime;
        private float m_lastValidationTime;
        private float m_lastMemorySaveTime;

        // Movement stall detection (passed by ref to VillagerMovement)
        private Vector3 m_lastMovementCheckPosition;
        private float m_stallStartTime;

        /// <summary>When the current movement target was set (for stuck timeout).</summary>
        private float m_targetSetTime;

        // Exploration
        private float m_explorationStartTime;
        private Vector3? m_explorationTarget;

        // Debug
        private float m_debugLockUntil;
        private float m_lastLogTime;
        private float m_lastPathTelemetryTime;

        // Movement test (null when no test is running)
        private VillagerMovementTest m_activeTest;

        // Guard-specific behavior (null for non-guard NPCs)
        private GuardBehavior m_guardBehavior;
        // Crafting behavior (null for non-worker NPCs)
        private CraftingBehavior m_craftingBehavior;
        private NpcType? m_npcType;

        // Sleep animation state
        private bool m_isSleepAnimationActive;
        private float m_savedViewRange;
        private float m_savedHearRange;
        private static readonly MethodInfo s_sleepMethod =
            typeof(MonsterAI).GetMethod("Sleep", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo s_wakeupMethod =
            typeof(MonsterAI).GetMethod("Wakeup", BindingFlags.NonPublic | BindingFlags.Instance);

        public VillagerAI(MonsterAI instance, Vector3 bedPosition, string uniqueId)
        {
            m_instance = instance;
            m_uniqueId = uniqueId;
            m_memory = new VillagerMemory(bedPosition);
            m_lastMovementCheckPosition = instance.transform.position;

            if (instance.gameObject.GetComponent<DoorHandler>() == null)
                instance.gameObject.AddComponent<DoorHandler>();

            if (NView != null && NView.GetZDO() != null)
            {
                m_memory.LoadFromZDO(NView.GetZDO());
                VillagerActivityLog.Instance.LoadFromZDO(uniqueId, NView.GetZDO());
                NView.GetZDO().Set("vv_bed_position", bedPosition);

                // Detect NPC type for specialized behavior
                int typeInt = NView.GetZDO().GetInt("vv_npc_type", -1);
                if (typeInt >= 0)
                    m_npcType = (NpcType)typeInt;
            }

            // Initialize guard-specific behavior and restore persisted patrol state
            if (m_npcType == NPCs.NpcType.Guard)
            {
                m_guardBehavior = new GuardBehavior(this);
                if (NView?.GetZDO() != null)
                    Guards.GuardPersistence.Load(m_guardBehavior, NView.GetZDO());
            }

            // Initialize crafting behavior for worker NPCs
            if (StationMatcher.IsWorkerType(m_npcType))
            {
                m_craftingBehavior = new CraftingBehavior(this);

                // Farmer NPCs also get a farming behavior for planting/harvesting
                if (m_npcType == NPCs.NpcType.Farmer)
                {
                    var farmingBehavior = new FarmingBehavior(this);
                    m_craftingBehavior.SetFarmingBehavior(farmingBehavior);
                }
            }

            // Stagger behavior ticks so NPCs spawned together don't all evaluate
            // at the same time (prevents them from picking up the same work order
            // simultaneously and traveling in a clump).
            m_lastBehaviorUpdateTime = Time.time - Random.Range(0f, VillagerSettings.BehaviorTickJitter);

            Plugin.Log?.LogInfo($"[VillagerAI] Initialized for {NpcName} (type={m_npcType}) at bed {bedPosition}");
        }

        #region Properties

        public bool HasInstance => m_instance != null;
        public MonsterAI Instance => m_instance;
        public VillagerMemory Memory => m_memory;
        public BehaviorState CurrentState => m_state;
        public Vector3? CurrentTarget => m_currentWaypoint?.Position;
        public string UniqueId => m_uniqueId;
        public Character Character => m_instance?.GetComponent<Character>();
        public ZNetView NView => m_instance?.GetComponent<ZNetView>();
        public string NpcName => Character?.m_name ?? m_instance?.gameObject.name ?? "Unknown";
        public Vector3 Position => m_instance.transform.position;
        public NpcType? NpcType => m_npcType;
        public bool IsGuard => m_npcType == NPCs.NpcType.Guard;
        public GuardBehavior GuardBehavior => m_guardBehavior;
        public CraftingBehavior CraftingBehavior => m_craftingBehavior;
        public bool IsSleepAnimationActive => m_isSleepAnimationActive;

        #endregion

        #region Sleep Animation

        /// <summary>
        /// Trigger the MonsterAI sleep animation (NPC lays down, ZDO synced).
        /// Safe to call multiple times; no-ops if already sleeping.
        /// </summary>
        public void EnterSleepAnimation()
        {
            if (m_isSleepAnimationActive) return;
            m_isSleepAnimationActive = true;

            // Zero out detection ranges so sleeping NPCs don't react to players/enemies
            m_savedViewRange = m_instance.m_viewRange;
            m_savedHearRange = m_instance.m_hearRange;
            m_instance.m_viewRange = 0f;
            m_instance.m_hearRange = 0f;

            try
            {
                s_sleepMethod?.Invoke(m_instance, null);
                Plugin.Log?.LogDebug($"[AI:{NpcName}] Entered sleep animation (detection disabled)");
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[AI:{NpcName}] Failed to enter sleep animation: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear the sleep animation (NPC stands up, ZDO synced).
        /// Safe to call multiple times; no-ops if not sleeping.
        /// </summary>
        public void ExitSleepAnimation()
        {
            if (!m_isSleepAnimationActive) return;
            m_isSleepAnimationActive = false;

            // Restore detection ranges on waking
            m_instance.m_viewRange = m_savedViewRange;
            m_instance.m_hearRange = m_savedHearRange;

            try
            {
                s_wakeupMethod?.Invoke(m_instance, null);
                Plugin.Log?.LogDebug($"[AI:{NpcName}] Exited sleep animation (detection restored)");
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[AI:{NpcName}] Failed to exit sleep animation: {ex.Message}");
            }
        }

        #endregion

        #region Main AI Loop

        /// <summary>
        /// Main AI update, called by VillagerAIPatch every frame in place of MonsterAI.UpdateAI.
        /// </summary>
        public void UpdateAI(float dt)
        {
            if (m_paused) return;

            VillagerMovement.RunBaseAIUpdates(m_instance, dt);

            // POI discovery (skip while sleeping — NPCs shouldn't detect anything)
            if (!m_isSleepAnimationActive && Time.time - m_lastDiscoveryTime > 4f)
            {
                m_lastDiscoveryTime = Time.time;
                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = POIDiscoveryHandler.DiscoveryTaskName,
                    SourceId = m_uniqueId,
                    Priority = TaskPriority.Medium,
                    TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                    Attributes = new Dictionary<string, string>
                    {
                        { "villager_id", m_uniqueId },
                        { "is_exploring", (m_state == BehaviorState.Exploring).ToString().ToLower() }
                    }
                });
            }

            // Location validation
            if (Time.time - m_lastValidationTime > 30f)
            {
                m_lastValidationTime = Time.time;
                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = POIDiscoveryHandler.ValidationTaskName,
                    SourceId = m_uniqueId,
                    Priority = TaskPriority.Low,
                    TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                    Attributes = new Dictionary<string, string>
                    {
                        { "villager_id", m_uniqueId }
                    }
                });
            }

            // Behavior decisions (skip during debug lock or active movement tests)
            float behaviorInterval = VillagerSettings.UpdateInterval;
            if (m_craftingBehavior != null && m_state == BehaviorState.Working && m_craftingBehavior.IsWaitingForCooking)
                behaviorInterval = WorkSettings.CookingPollInterval;
            if (Time.time - m_lastBehaviorUpdateTime > behaviorInterval)
            {
                m_lastBehaviorUpdateTime = Time.time;
                if (Time.time >= m_debugLockUntil && !IsMovementTestActive)
                {
                    if (m_guardBehavior != null)
                        m_guardBehavior.UpdateGuardAI(dt);
                    else if (m_craftingBehavior != null && m_state == BehaviorState.Working)
                        m_craftingBehavior.UpdateWorkAI(dt);
                    else
                        VillagerBehaviorLogic.UpdateBehavior(this);
                }
            }

            // Movement: move towards waypoint using its pathing strategy
            if (m_currentWaypoint != null && NeedsMovement(m_state))
            {
                Vector3 targetPos = m_currentWaypoint.Position;

                // #region agent log
                if (m_state == BehaviorState.Working && Time.time - m_lastPathTelemetryTime >= 1f)
                {
                    m_lastPathTelemetryTime = Time.time;
                    float d3 = Vector3.Distance(Position, targetPos);
                    float dxz = Utils.DistanceXZ(Position, targetPos);
                    string subState = (m_craftingBehavior?.FarmingBehavior?.IsWorking == true)
                        ? m_craftingBehavior.FarmingBehavior.SubState.ToString()
                        : (m_craftingBehavior?.SubState.ToString() ?? "—");
                    ValheimVillages.PathTelemetry.LogFarmerPathing(
                        NpcName, Position, targetPos, d3, dxz, subState);
                }
                // #endregion

                // Workers: on stuck pathing, cycle to next strategy or give up when wrapped to base
                if (m_state == BehaviorState.Working && m_craftingBehavior != null)
                {
                    float workThreshold = WorkSettings.WorkArrivalThreshold;
                    float dist3D = Vector3.Distance(Position, targetPos);
                    if (dist3D > workThreshold &&
                        Time.time - m_targetSetTime >= WorkSettings.WorkStuckTimeoutSeconds)
                    {
                        string currentId = m_currentWaypoint.StrategyId;
                        string nextId = PathingStrategyRegistry.GetNextStrategyId(currentId);
                        bool wrapped = PathingStrategyRegistry.IsWrappedToBase(currentId);

                        // #region agent log
                        ValheimVillages.PathTelemetry.LogStrategyEvent(
                            wrapped ? "give_up" : "cycle",
                            NpcName, currentId, nextId, dist3D, workThreshold, wrapped);
                        // #endregion

                        if (wrapped)
                        {
                            Player.m_localPlayer?.Message(MessageHud.MessageType.Center, $"{NpcName}: I can't get there!");
                            m_craftingBehavior.GiveUpStuckWork(
                                "stuck pathing " + WorkSettings.WorkStuckTimeoutSeconds + "s (not within " + workThreshold + "m)");
                            return;
                        }
                        SetState(m_state, new VillagerWaypoint(m_currentWaypoint.Position, nextId));
                        return;
                    }
                }

                var strategy = PathingStrategyRegistry.Get(m_currentWaypoint.StrategyId);
                bool arrived = strategy.MoveToward(m_instance, targetPos, dt);
                if (arrived)
                    OnArrivedAtTarget();
            }

            // Exploration arrival check (uses wider threshold)
            if (m_explorationTarget.HasValue && m_state == BehaviorState.Exploring)
            {
                if (Vector3.Distance(Position, m_explorationTarget.Value) < VillagerSettings.ArrivalThreshold * 2f)
                    VillagerBehaviorLogic.EndExploration(this, "arrived");
            }

            // Stall detection and door handling
            if (m_currentWaypoint != null && NeedsMovement(m_state))
            {
                VillagerMovement.CheckMovementStall(m_instance, m_currentWaypoint.Position,
                    ref m_lastMovementCheckPosition, ref m_stallStartTime);
            }

            // Periodic memory save
            if (Time.time - m_lastMemorySaveTime > 60f)
            {
                m_lastMemorySaveTime = Time.time;
                SaveMemory();
            }

            // Movement test progression
            if (m_activeTest != null && m_activeTest.IsActive)
                m_activeTest.Update();

            // Debug logging
            if (Time.time - m_lastLogTime > 10f)
            {
                m_lastLogTime = Time.time;
                Plugin.Log?.LogDebug($"[AI:{NpcName}] State={m_state}, Target={m_currentWaypoint != null}");
            }
        }

        /// <summary>
        /// Whether the given state requires active movement toward a target.
        /// </summary>
        private static bool NeedsMovement(BehaviorState state)
        {
            return state switch
            {
                BehaviorState.Traveling => true,
                BehaviorState.Exploring => true,
                BehaviorState.Wandering => true,
                BehaviorState.Sleeping => true,        // Move to bed first, then stop
                BehaviorState.Patrolling => true,       // Move to patrol point
                BehaviorState.Working => true,          // Crafting: move to chests/stations
                BehaviorState.Scouting => true,         // Guard: move outward to find walls
                BehaviorState.CircuitTracing => true,    // Guard: trace circle around bed
                _ => false
            };
        }

        #endregion

        #region State Management

        public void SetState(BehaviorState newState, Vector3? target = null)
        {
            VillagerWaypoint waypoint = target.HasValue
                ? VillagerWaypoint.WithDefault(target.Value)
                : null;
            SetState(newState, waypoint);
        }

        public void SetState(BehaviorState newState, VillagerWaypoint waypoint)
        {
            if (m_state == BehaviorState.Sleeping && newState != BehaviorState.Sleeping)
                ExitSleepAnimation();

            m_state = newState;
            m_currentWaypoint = waypoint;
            if (waypoint != null)
                m_targetSetTime = Time.time;
            if (newState == BehaviorState.Idle)
                m_instance.StopMoving();
            Plugin.Log?.LogDebug($"[AI:{NpcName}] State -> {newState}");
        }

        public void SetExplorationTarget(Vector3 target)
        {
            m_explorationTarget = target;
            m_explorationStartTime = Time.time;
        }

        public void ClearExploration()
        {
            m_explorationTarget = null;
            m_explorationStartTime = 0f;
        }

        public float ExplorationElapsed => m_explorationTarget.HasValue ? Time.time - m_explorationStartTime : 0f;
        public Vector3? ExplorationTarget => m_explorationTarget;
        public void SetPaused(bool paused)
        {
            m_paused = paused;
            if (paused)
                m_instance.StopMoving();
        }
        public bool IsPaused => m_paused;

        private void OnArrivedAtTarget()
        {
            Plugin.Log?.LogInfo($"[AI:{NpcName}] Arrived at destination (state={m_state})");
            m_stallStartTime = 0f;

            if (m_guardBehavior != null)
                m_guardBehavior.HandleArrival();
            else if (m_craftingBehavior != null && m_state == BehaviorState.Working)
                m_craftingBehavior.HandleWorkArrival();
            else
                VillagerBehaviorLogic.HandleArrival(this);

            if (m_activeTest != null && m_activeTest.IsActive)
                m_activeTest.OnWaypointArrived();
        }

        #endregion

        #region Persistence

        public void SaveMemory()
        {
            if (NView == null || NView.GetZDO() == null) return;

            var zdo = NView.GetZDO();
            m_memory.SaveToZDO(zdo);

            // Persist and commit activity log entries
            VillagerActivityLog.Instance.SaveToZDO(m_uniqueId, zdo);
            VillagerActivityLog.Instance.MarkCommitted(m_uniqueId);
            VillagerActivityLog.Instance.TrimCommitted(m_uniqueId);

            if (m_guardBehavior != null)
                Guards.GuardPersistence.Save(m_guardBehavior, zdo);
        }

        #endregion

        #region Debug Commands

        public void DebugLock() => m_debugLockUntil = Time.time + 60f;

        public Vector3? DebugWanderToLocationType(LocationType type)
        {
            var location = m_memory.KnownLocations
                .Where(l => l.Type == type)
                .OrderBy(l => Vector3.Distance(Position, l.Position))
                .FirstOrDefault();

            if (location != null)
            {
                DebugLock();
                SetState(BehaviorState.Traveling, location.Position);
                Plugin.Log?.LogInfo($"[DEBUG:{NpcName}] Traveling to {type}");
                return location.Position;
            }

            Plugin.Log?.LogWarning($"[DEBUG:{NpcName}] No known {type} location");
            return null;
        }

        #endregion

        #region Movement Tests

        public bool IsMovementTestActive => m_activeTest != null && m_activeTest.IsActive;

        /// <summary>
        /// Start a multi-waypoint movement test.
        /// </summary>
        public bool StartMovementTest()
        {
            if (IsMovementTestActive) return false;
            m_activeTest = new VillagerMovementTest(this);
            return m_activeTest.Start();
        }

        /// <summary>
        /// Cancel a running movement test.
        /// </summary>
        public void CancelMovementTest()
        {
            m_activeTest?.Cancel();
            m_activeTest = null;
        }

        /// <summary>
        /// Get current test progress info for the UI.
        /// </summary>
        public (int completed, int total, string label) GetTestProgress()
        {
            if (m_activeTest == null) return (0, 0, "");
            return (m_activeTest.WaypointsCompleted, m_activeTest.WaypointsTotal, m_activeTest.CurrentLabel);
        }

        #endregion
    }
}
