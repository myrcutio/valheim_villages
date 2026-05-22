using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Behaviors;
using ValheimVillages.Interfaces;
using ValheimVillages.Behaviors.Explore;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.Behaviors.Work;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.Tags;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager.AI.Memory;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Villager.AI
{
    public class VillagerAI : BaseAI, IVillagerWorkContext
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

        private Vector3 m_lastMovePos;
        private float m_lastRealMoveTime;
        private DoorHandler m_doorHandler;
        private DoorHandler DoorHandler => m_doorHandler ??= GetComponent<DoorHandler>();

        /// <summary>Time.time when the guard last arrived at a waypoint. Used for hard stuck timeout.</summary>
        private float m_lastArrivalTime;

        // Hard-stuck backoff
        private int m_consecutiveStucks;
        private float m_stuckBackoffUntil;
        private const float StuckBackoffBase = 10f;
        private const float StuckBackoffMax = 600f;

        // Exploration
        private float m_explorationStartTime;
        private Vector3? m_explorationTarget;

        // Composable behaviors (populated by BehaviorFactory from NPC definition)
        private List<IBehavior> m_behaviors = new();

        private const float PathRetryInterval = 2f;
        private const float SaveInterval = 60f;

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

            if (VillagerAgentType.EnsureRegistered())
                m_pathAgentType = VillagerAgentType.AgentType;

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
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning($"[VillagerAI] OnDestroy SaveMemories failed: {ex.Message}");
            }
            VillagerAIManager.Unregister(this);
        }

        public void RegisterOwnedBed()
        {
            m_memory.DiscoverLocation(m_bedPosition, LocationType.Bed, 0f, true);
            m_memory.BedPosition = m_bedPosition;
        }

        public override bool UpdateAI(float dt)
        {
            if (m_instance == null) return false;
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

            if (Time.time - m_lastMemorySaveTime > SaveInterval)
            {
                m_lastMemorySaveTime = Time.time;
                var zdo = GetComponent<ZNetView>()?.GetZDO();
                if (zdo != null) SaveMemories(zdo);
            }

            // Per-frame movement: drive the character toward the current waypoint
            if (m_currentWaypoint != null && NeedsMovement(m_state))
            {
                Vector3 targetPos = m_currentWaypoint.Position;
                float remaining = Vector3.Distance(transform.position, targetPos);
                var path = s_pathField?.GetValue(this) as List<Vector3>;

                if (remaining < VillagerSettings.ArrivalThreshold && (path == null || path.Count == 0))
                {
                    OnArrivedAtTarget(dt);
                }
                else
                {
                    if (path == null || path.Count == 0)
                    {
                        FindPath(targetPos);
                        path = s_pathField?.GetValue(this) as List<Vector3>;

                        if ((path == null || path.Count == 0) && NavMeshLinkPlacer.PlaceLinks())
                        {
                            FindPath(targetPos);
                            path = s_pathField?.GetValue(this) as List<Vector3>;
                        }
                        // Reject paths that are absurdly indirect (NavMesh gap workaround)
                        if (path != null && path.Count > 2 && m_state == BehaviorState.Patrolling)
                        {
                            float straightDist = Vector3.Distance(transform.position, targetPos);
                            float pathLen = 0f;
                            var prev = transform.position;
                            for (int pi = 0; pi < path.Count; pi++)
                            {
                                pathLen += Vector3.Distance(prev, path[pi]);
                                prev = path[pi];
                            }
                            if (straightDist > 1f && pathLen > straightDist * 3f)
                            {
                                path.Clear();

                                if (NavMeshLinkPlacer.PlaceLinks())
                                {
                                    FindPath(targetPos);
                                    path = s_pathField?.GetValue(this) as List<Vector3>;
                                }
                            }
                        }

                    }

                    if (path != null && path.Count > 0)
                    {
                        bool running = m_state == BehaviorState.Patrolling || remaining > 5f;
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
                        DoorHandler?.OpenDoorsAlongPath(path);

                        var diff = path[0] - transform.position;
                        var dir = diff.normalized;
                        MoveTowards(dir, running);

                        float moveDelta = Vector3.Distance(transform.position, m_lastMovePos);
                        if (moveDelta > 0.15f)
                        {
                            m_lastMovePos = transform.position;
                            m_lastRealMoveTime = Time.time;
                        }
                        else if (Time.time - m_lastRealMoveTime > DoorSettings.MovementStallThreshold)
                        {
                            DebugLog.Append("VillagerAI.cs:stall", "stall_detected", new System.Collections.Generic.Dictionary<string, object>{
                                {"stallDuration", Time.time - m_lastRealMoveTime},
                                {"hasDoorHandler", DoorHandler != null},
                                {"npcPos", transform.position.ToString("F2")},
                                {"targetPos", targetPos.ToString("F2")},
                                {"moveDelta", Vector3.Distance(transform.position, m_lastMovePos)}
                            }, "H1H2H3", "run1");
                            if (DoorHandler != null)
                            {
                                var blockingDoor = DoorHandler.GetBlockingDoor(targetPos);
                                DebugLog.Append("VillagerAI.cs:doorCheck", "blocking_door_result", new System.Collections.Generic.Dictionary<string, object>{
                                    {"foundDoor", blockingDoor != null},
                                    {"doorPos", blockingDoor != null ? blockingDoor.transform.position.ToString("F2") : "none"}
                                }, "H3", "run1");
                                if (blockingDoor != null)
                                {
                                    DoorHandler.OpenDoor(blockingDoor);
                                    m_lastRealMoveTime = Time.time;
                                }
                            }

                            if (Time.time - m_lastRealMoveTime > 1.5f && m_character != null)
                            {
                                float stepUp = path[0].y - transform.position.y;
                                if (stepUp > 0.2f)
                                {
                                    float origForce = m_character.m_jumpForce;
                                    float origForward = m_character.m_jumpForceForward;
                                    m_character.m_jumpForce *= VillagerSettings.StepJumpForceFraction;
                                    m_character.m_jumpForceForward *= VillagerSettings.StepJumpForceFraction;
                                    m_character.Jump(false);
                                    m_character.m_jumpForce = origForce;
                                    m_character.m_jumpForceForward = origForward;
                                    m_lastRealMoveTime = Time.time;
                                }
                            }
                        }
                    }
                }

                    if (m_state == BehaviorState.Patrolling)
                    {
                        if (Time.time < m_stuckBackoffUntil)
                        {
                            // Still in backoff cooldown — stay idle at bed
                        }
                        else if (Time.time - m_lastArrivalTime > VillagerSettings.PatrolHardStuckTimeoutSeconds)
                        {
                            m_consecutiveStucks++;
                            float backoff = Mathf.Min(
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
                BehaviorState.Patrolling => true,
                BehaviorState.Working => true,
                _ => false
            };
        }

        #endregion

        #region State Management
        
        public BehaviorState CurrentState => m_state;

        /// <summary>True while the AI is in a hard-stuck backoff cooldown and should not start new tasks.</summary>
        public bool IsInBackoff => Time.time < m_stuckBackoffUntil;

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
                m_lastMovePos = transform.position;
                m_lastRealMoveTime = Time.time;
            }
            
            if (newState == BehaviorState.Idle)
                this.StopMoving();
            if (waypoint != null)
                Plugin.Log?.LogDebug(
                    $"[AI:{m_villagerName}] State -> {newState}, target=({waypoint.Position.x:F1},{waypoint.Position.y:F1},{waypoint.Position.z:F1})");
            else
                Plugin.Log?.LogDebug($"[AI:{m_villagerName}] State -> {newState}");
        }

        /// <summary>
        /// Inject a pre-computed patrol circuit path. The guard follows the full path
        /// and only triggers OnArrival when reaching the final waypoint.
        /// Falls back to single-waypoint FindPath if the path is empty.
        /// </summary>
        public void SetPatrolCircuit(VillagerWaypoint finalTarget, List<Vector3> circuitPath)
        {
            m_state = BehaviorState.Patrolling;
            m_currentWaypoint = finalTarget;
            m_targetSetTime = Time.time;
            m_lastMovePos = transform.position;
            m_lastRealMoveTime = Time.time;
            m_lastArrivalTime = Time.time;

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

        private void OnArrivedAtTarget(float dt)
        {
            m_lastArrivalTime = Time.time;
            m_consecutiveStucks = 0;

            var arrCtx = new BehaviorContext();
            foreach (var b in m_behaviors)
            {
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
