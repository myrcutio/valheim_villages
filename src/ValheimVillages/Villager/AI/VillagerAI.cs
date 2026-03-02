using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimVillages;
using ValheimVillages.Behaviors;
using ValheimVillages.Interfaces;
using ValheimVillages.Behaviors.Alarm;
using ValheimVillages.Behaviors.Explore;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.Behaviors.Work;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.Tags;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager.AI.Memory;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Villager.AI
{
    public class VillagerAI : BaseAI, IVillagerStationLookup, IVillagerWorkContext
    {
        private Villager m_instance;
        private string m_uniqueId;
        private string m_villagerName;
        private Vector3 m_bedPosition;
        private string m_villagerType;
        
        private VillagerMemory m_memory;
        private BehaviorState m_state = BehaviorState.Idle;
        private VillagerWaypoint m_currentWaypoint;
        private List<Vector3> m_waypointPath;
        private bool m_paused;

        // Timing
        private float m_lastDiscoveryTime;
        private float m_lastBehaviorUpdateTime;
        private float m_lastValidationTime;
        private float m_lastMemorySaveTime;

        /// <summary>When the current movement target was set (for stuck timeout).</summary>
        private float m_targetSetTime;

        // Exploration
        private float m_explorationStartTime;
        private Vector3? m_explorationTarget;

        // Debug
        private float m_debugLockUntil;
        private float m_lastLogTime;

        // Composable behaviors (populated by BehaviorFactory from NPC definition)
        private List<IBehavior> m_behaviors = new();

        private const float PathRetryInterval = 2f;

        private static readonly FieldInfo s_pathField = typeof(BaseAI).GetField(
            "m_path", BindingFlags.NonPublic | BindingFlags.Instance);
        

        public VillagerAI(Villager instance)
        {
            m_instance = instance;
            m_uniqueId = m_instance.uid;
            m_bedPosition = m_instance.BedPosition;
            m_villagerType = m_instance.villagerType;
            m_villagerName = m_instance.villagerName;
            m_memory = new VillagerMemory(m_bedPosition);
        }

        /// <summary>
        /// Parameterless constructor for Unity AddComponent. Initialization happens in Awake from Villager component.
        /// </summary>
        public VillagerAI() { }
        
        protected override void Awake()
        {
            try
            {
                base.Awake();
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[VillagerAI] base.Awake() threw: {ex.GetType().Name}: {ex.Message}");
            }

            if (m_instance == null)
            {
                m_instance = GetComponent<Villager>();
                if (m_instance == null)
                {
                    Plugin.Log?.LogError("[VillagerAI] No Villager component on this GameObject");
                    return;
                }
                m_uniqueId = m_instance.uid;
                m_bedPosition = m_instance.BedPosition;
                m_villagerType = m_instance.villagerType;
                m_villagerName = m_instance.villagerName;
                m_memory = new VillagerMemory(m_bedPosition);
                VillagerAIManager.RegisterActive(this);
            }
            
            RegisterOwnedBed();
            RegisterBehaviors();

            // Stagger behavior ticks so NPCs spawned together don't all evaluate at the same time.
            // This is a countdown timer: 0 means "ready to run", positive means "wait this many more seconds".
            m_lastBehaviorUpdateTime = Random.Range(0f, VillagerSettings.BehaviorTickJitter);
        }

        private void OnDestroy()
        {
            VillagerAIManager.Unregister(this);
        }

        public void RegisterOwnedBed()
        {
            m_memory.DiscoverLocation(m_bedPosition, LocationType.Bed, 0f, true);
            m_memory.BedPosition = m_bedPosition;
        }

        protected new bool MoveTo(float dt, Vector3 point, float dist, bool run)
        {
            float b = run ? 1f : 0.5f;
            if (Vector3.Distance(m_instance.transform.position, point) <= 2f)
            {
                StopMoving();
                return true;
            }
            if (!FindPath(point))
            {
                StopMoving();
                return true;
            }
            if (m_waypointPath.Count == 0)
            {
                StopMoving();
                return true;
            }
            Vector3 nextPoint = m_waypointPath[0];
            if (Vector3.Distance(nextPoint, m_instance.transform.position) < (double) b)
            {
                m_waypointPath.RemoveAt(0);
                if (m_waypointPath.Count == 0)
                {
                    StopMoving();
                    return true;
                }
            }

            return false;
        }

        public override bool UpdateAI(float dt)
        {
            if (!base.UpdateAI(dt)) return false;
            if (m_lastBehaviorUpdateTime > 0.0)
            {
                m_lastBehaviorUpdateTime -= dt;
                return false;
            }
            if (m_paused) return true;

            if (Time.time - m_lastDiscoveryTime > 4f)
            {
                m_lastDiscoveryTime = Time.time;
                VillagerPOIDiscovery.DiscoverNearbyPOIs(transform, m_memory);
            }

            if (Time.time - m_lastValidationTime > 30f)
            {
                m_lastValidationTime = Time.time;
                VillagerPOIDiscovery.ValidateKnownLocations(m_memory);
            }

            var ctx = new BehaviorContext();
            foreach (var b in m_behaviors)
            {
                if (b.WantsControl(ctx))
                {
                    b.Update(dt);
                    break;
                }
            }

            // Per-frame movement: drive the character toward the current waypoint
            if (m_currentWaypoint != null && NeedsMovement(m_state))
            {
                Vector3 targetPos = m_currentWaypoint.Position;
                float remaining = Vector3.Distance(transform.position, targetPos);

                if (remaining < VillagerSettings.ArrivalThreshold)
                {
                    OnArrivedAtTarget();
                }
                else
                {
                    var path = s_pathField?.GetValue(this) as List<Vector3>;
                    if (path == null || path.Count == 0)
                    {
                        FindPath(targetPos);
                        path = s_pathField?.GetValue(this) as List<Vector3>;
                    }

                    if (path != null && path.Count > 0)
                    {
                        bool running = remaining > 5f;
                        float closeEnough = running ? 1f : 0.5f;

                        while (path.Count > 1 && Vector3.Distance(path[0], transform.position) <= closeEnough)
                            path.RemoveAt(0);

                        if (path.Count == 1 &&
                            Vector3.Distance(path[0], transform.position) < closeEnough)
                        {
                            path.Clear();
                            if (Time.time - m_targetSetTime >= PathRetryInterval)
                            {
                                m_targetSetTime = Time.time;
                                FindPath(targetPos);
                                path = s_pathField?.GetValue(this) as List<Vector3>;
                            }
                        }

                        if (path != null && path.Count > 0)
                        {
                            var dir = (path[0] - transform.position).normalized;
                            MoveTowards(dir, running);
                        }
                    }

                }
            }

            return false;
        }

        public void LoadMemories(ZDO zdo)
        {
            m_memory.LoadFromZDO(zdo);
            VillagerActivityLog.Instance.LoadFromZDO(m_uniqueId, zdo);
            // Load persisted behavior state
            foreach (var b in m_behaviors)
                if (b is IBehaviorPersistence bp) bp.Load(zdo);
        }

        public void SaveMemories(ZDO zdo)
        {
            m_memory.SaveToZDO(zdo);

            VillagerActivityLog.Instance.SaveToZDO(m_uniqueId, zdo);
            VillagerActivityLog.Instance.MarkCommitted(m_uniqueId);
            VillagerActivityLog.Instance.TrimCommitted(m_uniqueId);

            foreach (var b in m_behaviors)
                if (b is IBehaviorPersistence bp) bp.Save(zdo);
        }

        private void RegisterBehaviors()
        {
            var villagerDef = VillagerRegistry.Get(m_villagerType);

            // Merge behavior keys from both the legacy "behaviors" array and "behavior:*" tags
            var behaviorKeys = new List<string>();
            if (villagerDef?.behaviors != null)
                behaviorKeys.AddRange(villagerDef.behaviors);
            if (villagerDef?.tags != null)
                behaviorKeys.AddRange(TagParser.GetValues(villagerDef.tags, "behavior"));

            m_behaviors = BehaviorFactory.CreateBehaviors(this, behaviorKeys);

            // Wire up cross-behavior references
            var patrol = GetBehavior<PerimeterPatrolBehavior>();
            var alarm = GetBehavior<BreachAlarmBehavior>();
            if (patrol != null && alarm != null)
                alarm.SetPatrolBehavior(patrol);

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


        #region Properties
        
        public T GetBehavior<T>() where T : class, IBehavior
        {
            foreach (var b in m_behaviors)
                if (b is T typed) return typed;
            return null;
        }

        /// <summary>Find a behavior by tag string. Used by tag-driven components.</summary>
        public IBehavior GetBehavior(string matchBehaviorTag)
        {
            foreach (var b in m_behaviors)
                if (b.Tag == matchBehaviorTag) return b;
            return null;
        }

        public VillagerMemory GetMemory()
        {
            return m_memory;
        }

        /// <summary>Villager component this AI is attached to.</summary>
        public Villager Villager => m_instance;

        /// <summary>Display name for logging. Compatibility with behavior code.</summary>
        public string NpcName => m_villagerName ?? m_instance?.villagerName ?? "Unknown";
        /// <summary>Unique ID for task attributes and persistence.</summary>
        public string UniqueId => m_uniqueId;
        /// <summary>Current world position. Compatibility with behavior code.</summary>
        public Vector3 Position => m_instance != null ? m_instance.transform.position : Vector3.zero;
        /// <summary>Memory (known locations). Compatibility with behavior code.</summary>
        public VillagerMemory Memory => m_memory;
        /// <summary>This AI component (for StopMoving etc.). Compatibility with behavior code.</summary>
        public BaseAI Instance => this;
        /// <summary>ZNetView for persistence. Used by behavior persistence.</summary>
        public ZNetView NView => m_instance?.nView;
        /// <summary>Character component. Compatibility with farming/work.</summary>
        public Character Character => m_instance != null ? m_instance.GetComponent<Character>() : null;
        /// <summary>Current movement target position. Compatibility with BehaviorLogic.</summary>
        public Vector3? CurrentTarget => m_currentWaypoint != null ? (Vector3?)m_currentWaypoint.Position : null;
        /// <summary>Crafting behavior adapter if present. Compatibility with UI and workflows.</summary>
        public CraftingBehaviorAdapter CraftingBehavior => GetBehavior<CraftingBehaviorAdapter>();
        /// <summary>Work-order scanner for BehaviorLogic. No dependency on concrete crafting type.</summary>
        public IWorkScanBehavior GetWorkScanner() => GetBehavior<CraftingBehaviorAdapter>();
        /// <summary>Villager type string from JSON definition (e.g. "Guard", "Farmer").</summary>
        public string VillagerType => m_villagerType;

        IReadOnlyList<Schemas.KnownLocation> IVillagerStationLookup.KnownLocations =>
            Memory?.KnownLocations ?? (IReadOnlyList<Schemas.KnownLocation>)System.Array.Empty<Schemas.KnownLocation>();
        Vector3 IVillagerStationLookup.BedPosition =>
            Memory != null ? Memory.BedPosition : default;
        string IVillagerWorkContext.NpcName => NpcName;
        Vector3 IVillagerWorkContext.Position => Position;


        #endregion

        #region Main AI Loop


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
        
        public BehaviorState CurrentState => m_state;

        public void SetState(BehaviorState newState, Vector3? target = null)
        {
            VillagerWaypoint waypoint = target.HasValue
                ? VillagerWaypoint.WithDefault(target.Value)
                : null;
            SetState(newState, waypoint);
        }

        public void SetState(BehaviorState newState, VillagerWaypoint waypoint)
        {
            m_state = newState;
            if (waypoint != null)
            {
                m_currentWaypoint = waypoint;
                m_targetSetTime = Time.time;
            }
            
            if (newState == BehaviorState.Idle)
                this.StopMoving();
            Plugin.Log?.LogDebug($"[AI:{m_villagerName}] State -> {newState}");
        }

        public void SetPaused(bool paused)
        {
            m_paused = paused;
            if (paused)
                this.StopMoving();
        }
        public bool IsPaused => m_paused;
        
        public VillagerWaypoint GetCurrentWaypoint()
        {
            return m_currentWaypoint;
        }

        public bool FindPath(VillagerWaypoint destination)
        {
            var hasPath = FindPath(destination.Position);
            if (hasPath)
            {
                m_currentWaypoint = destination;
            }

            return hasPath;
        }

        private void OnArrivedAtTarget()
        {
            Plugin.Log?.LogInfo($"[AI:{m_villagerName}] Arrived at destination (state={m_state})");
            // m_stallStartTime = 0f;

            var arrCtx = new BehaviorContext();
            foreach (var b in m_behaviors)
            {
                if (b.WantsControl(arrCtx))
                {
                    b.OnArrival();
                    break;
                }
            }
        }

        #endregion
    }
}
