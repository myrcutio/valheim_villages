using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Behaviors;
using ValheimVillages.Behaviors.Alarm;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.Behaviors.Work;
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
    /// Sleep animation and movement tests are in VillagerAISleep.cs (partial class).
    /// </summary>
    public partial class VillagerAI
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

        // Composable behaviors (populated by BehaviorFactory from NPC definition)
        private List<IBehavior> m_behaviors = new();
        private NpcType? m_npcType;

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

                int typeInt = NView.GetZDO().GetInt("vv_npc_type", -1);
                if (typeInt >= 0)
                    m_npcType = (NpcType)typeInt;
            }

            // Create behaviors from NPC type definition
            var npcTypeDef = m_npcType.HasValue ? NpcTypeRegistry.Get(m_npcType.Value) : null;
            var behaviorTags = npcTypeDef?.behaviors;
            if (behaviorTags != null && behaviorTags.Count > 0)
            {
                m_behaviors = BehaviorFactory.CreateBehaviors(this, behaviorTags);

                // Wire up cross-behavior references
                var patrol = GetBehavior<PerimeterPatrolBehavior>();
                var alarm = GetBehavior<BreachAlarmBehavior>();
                if (patrol != null && alarm != null)
                    alarm.SetPatrolBehavior(patrol);

                var craftAdapter = GetBehavior<CraftingBehaviorAdapter>();
                var farmAdapter = GetBehavior<FarmBehaviorAdapter>();
                if (craftAdapter != null && farmAdapter != null)
                    farmAdapter.LinkToCraftingAdapter(craftAdapter);

                // Load persisted behavior state
                if (NView?.GetZDO() != null)
                {
                    foreach (var b in m_behaviors)
                        b.Load(NView.GetZDO());
                }
            }
            else
            {
                // Fallback for NPCs without behavior definitions (legacy path)
                if (m_npcType == NPCs.NpcType.Guard)
                {
                    var patrol = new PerimeterPatrolBehavior(this);
                    var alarm = new BreachAlarmBehavior(this);
                    alarm.SetPatrolBehavior(patrol);
                    m_behaviors.Add(alarm);
                    m_behaviors.Add(patrol);
                    if (NView?.GetZDO() != null)
                    {
                        patrol.Load(NView.GetZDO());
                        alarm.Load(NView.GetZDO());
                    }
                }
                else if (StationMatcher.IsWorkerType(m_npcType))
                {
                    var craft = new CraftingBehaviorAdapter(this);
                    m_behaviors.Add(craft);
                    if (m_npcType == NPCs.NpcType.Farmer)
                    {
                        var farm = new FarmBehaviorAdapter(this);
                        farm.LinkToCraftingAdapter(craft);
                        m_behaviors.Add(farm);
                    }
                }
            }

            // Stagger behavior ticks so NPCs spawned together don't all evaluate at the same time
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
        public IReadOnlyList<IBehavior> Behaviors => m_behaviors;

        /// <summary>Find a behavior by concrete type. Used by UI code (cleaned up in Phase 3i).</summary>
        public T GetBehavior<T>() where T : class, IBehavior
        {
            foreach (var b in m_behaviors)
                if (b is T typed) return typed;
            return null;
        }

        /// <summary>Find a behavior by tag string. Used by tag-driven UI components.</summary>
        public IBehavior GetBehavior(string tag)
        {
            foreach (var b in m_behaviors)
                if (b.Tag == tag) return b;
            return null;
        }

        // Backward-compatible accessors (used by existing UI, cleaned up in step 3i)
        public bool IsGuard => m_npcType == NPCs.NpcType.Guard;
        public GuardBehavior GuardBehavior => GetBehavior<PerimeterPatrolBehavior>()?.Guard;
        public CraftingBehavior CraftingBehavior => GetBehavior<CraftingBehaviorAdapter>()?.Crafting;

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
            if (CraftingBehavior != null && m_state == BehaviorState.Working && CraftingBehavior.IsWaitingForCooking)
                behaviorInterval = WorkSettings.CookingPollInterval;
            if (Time.time - m_lastBehaviorUpdateTime > behaviorInterval)
            {
                m_lastBehaviorUpdateTime = Time.time;
                if (Time.time >= m_debugLockUntil && !IsMovementTestActive)
                {
                    bool handled = false;
                    var ctx = new BehaviorContext();
                    foreach (var b in m_behaviors)
                    {
                        if (b.WantsControl(ctx))
                        {
                            b.Update(dt);
                            handled = true;
                            break;
                        }
                    }
                    if (!handled)
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
                    string subState = (CraftingBehavior?.FarmingBehavior?.IsWorking == true)
                        ? CraftingBehavior.FarmingBehavior.SubState.ToString()
                        : (CraftingBehavior?.SubState.ToString() ?? "—");
                    ValheimVillages.PathTelemetry.LogFarmerPathing(
                        NpcName, Position, targetPos, d3, dxz, subState);
                }
                // #endregion

                // Workers: on stuck pathing, cycle to next strategy or give up
                if (m_state == BehaviorState.Working && CraftingBehavior != null)
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
                            CraftingBehavior.GiveUpStuckWork(
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
                BehaviorState.Sleeping => true,
                BehaviorState.Patrolling => true,
                BehaviorState.Working => true,
                BehaviorState.Scouting => true,
                BehaviorState.CircuitTracing => true,
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

            bool arrivalHandled = false;
            var arrCtx = new BehaviorContext();
            foreach (var b in m_behaviors)
            {
                if (b.WantsControl(arrCtx))
                {
                    b.OnArrival();
                    arrivalHandled = true;
                    break;
                }
            }
            if (!arrivalHandled)
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

            VillagerActivityLog.Instance.SaveToZDO(m_uniqueId, zdo);
            VillagerActivityLog.Instance.MarkCommitted(m_uniqueId);
            VillagerActivityLog.Instance.TrimCommitted(m_uniqueId);

            foreach (var b in m_behaviors)
                b.Save(zdo);
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
    }
}
