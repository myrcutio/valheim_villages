using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.TaskQueue;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villages;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    /// Patrol behavior state machine.
    /// Lifecycle: Scouting -> CircuitTracing -> Patrolling <-> Alarmed
    /// </summary>
    public class PatrolStateMachine
    {
        private readonly Villager.Villager m_villager;
        private readonly VillagerAI m_ai;
        private readonly PatrolRefiner m_refiner = new();
        private PatrolDiscovery m_discovery;
        private List<VillagerWaypoint> m_patrolWaypoints;
        private int m_currentWaypointIndex;
        private bool m_discoveryComplete;
        private bool m_isHnaRoute;
        private bool m_hnaPartitionRequested;
        private Vector3? m_breachPosition;

        // Stall detection
        private Vector3 m_lastProgressPosition;
        private int m_progressStalls;
        private const int MaxStallTicks = 2;
        private const float MinProgressDistance = 3f;

        /// <summary>Fast tick interval used during circuit tracing for quicker stall detection.</summary>
        private const float CircuitTracingTickInterval = 5f;

        public PatrolStateMachine(ValheimVillages.Villager.Villager villager)
        {
            m_villager = villager;
            m_ai = villager.villagerAI != null ? villager.villagerAI : villager.GetComponent<VillagerAI>();
            m_lastProgressPosition = m_villager.transform.position;
        }

        public bool IsDiscoveryComplete => m_discoveryComplete;
        public bool IsAlarmed => m_breachPosition.HasValue;
        public Vector3? BreachPosition => m_breachPosition;
        /// <summary>Bed position for map rendering and UI.</summary>
        public Vector3 BedPosition => m_ai?.Memory?.BedPosition ?? Vector3.zero;
        public int WaypointCount => m_patrolWaypoints?.Count ?? 0;
        public int ActiveWaypointCount => m_patrolWaypoints?.Count(w => w.Active) ?? 0;
        public IReadOnlyList<VillagerWaypoint> PatrolWaypoints => m_patrolWaypoints;
        public bool IsHnaRoute => m_isHnaRoute;

        /// <summary>Returns the persistent state for saving to ZDO.</summary>
        public (List<VillagerWaypoint> waypoints, int wpIndex, bool complete, Vector3? breach, bool isHna) GetPersistentState()
            => (m_patrolWaypoints, m_currentWaypointIndex, m_discoveryComplete, m_breachPosition, m_isHnaRoute);

        /// <summary>
        /// Reset village mapping. Clears waypoints, unregisters the village
        /// area, and transitions back to Idle so discovery restarts on the next tick.
        /// </summary>
        public void ResetDiscovery()
        {
            Plugin.Log?.LogWarning($"[Patrol:{m_ai.NpcName}] Discovery reset requested (debug)");
            m_patrolWaypoints?.Clear();
            m_currentWaypointIndex = 0;
            m_discoveryComplete = false;
            m_isHnaRoute = false;
            m_hnaPartitionRequested = false;
            m_breachPosition = null;
            m_discovery = null;
            ValheimVillages.Villager.AI.Navigation.HnaRegionGraph.Clear();
            VillageAreaManager.UnregisterArea(m_ai.UniqueId);
            ResetStallTracking();
            SaveState();
            m_ai.SetState(BehaviorState.Idle);
            StartDiscovery();
        }

        /// <summary>Restores state from ZDO data. Re-registers village area.</summary>
        public void RestoreState(List<VillagerWaypoint> waypoints, int wpIndex, Vector3? breach, bool isHna = false)
        {
            m_patrolWaypoints = waypoints;
            m_currentWaypointIndex = wpIndex % (waypoints?.Count ?? 1);
            m_discoveryComplete = true;
            m_breachPosition = breach;
            m_isHnaRoute = isHna;
            RegisterVillageArea();
            Plugin.Log?.LogInfo(
                $"[Patrol:{m_ai.NpcName}] Restored {m_patrolWaypoints.Count} waypoints" +
                $" (hna={isHna}), index={m_currentWaypointIndex}");
        }

        /// <summary>
        /// Returns the desired behavior tick interval for the patroller's current state.
        /// Circuit tracing uses a fast 5s tick so stuck detection triggers quickly.
        /// </summary>
        public float GetDesiredTickInterval()
        {
            return m_ai.CurrentState == BehaviorState.CircuitTracing
                ? CircuitTracingTickInterval
                : VillagerSettings.UpdateInterval;
        }

        /// <summary>Main patrol AI update, called each behavior tick.</summary>
        public void UpdatePatrolAI(float dt)
        {
            var state = m_ai.CurrentState;
            var context = VillagerBehaviorLogic.GetCurrentContext(m_ai);

            // Night: sleep (same as other villagers)
            if (context.TimeOfDay == TimeOfDay.Night && state != BehaviorState.Sleeping)
            {
                m_ai.SetState(BehaviorState.Sleeping, VillagerWaypoint.WithDefault(m_ai.Memory.BedPosition));
                ResetStallTracking();
                return;
            }

            // Waking up: resume patrol activity
            if (state == BehaviorState.Sleeping && context.TimeOfDay != TimeOfDay.Night)
            {
                if (m_discoveryComplete) AdvanceToNextWaypoint();
                else StartDiscovery();
                return;
            }

            switch (state)
            {
                case BehaviorState.Idle:
                    if (!m_discoveryComplete) StartDiscovery();
                    else if (m_breachPosition.HasValue) m_ai.SetState(BehaviorState.Alarmed);
                    else AdvanceToNextWaypoint();
                    break;
                case BehaviorState.Scouting:
                    if (m_discovery?.UpdateScouting() == true)
                        m_discovery.BeginCircuitTracing();
                    break;
                case BehaviorState.Patrolling:
                case BehaviorState.CircuitTracing:
                    CheckStallAndRecover(state);
                    break;
            }
        }

        /// <summary>Called when the patroller arrives at its target.</summary>
        public void HandleArrival()
        {
            ResetStallTracking();

            switch (m_ai.CurrentState)
            {
                case BehaviorState.Scouting:
                    m_discovery?.BeginCircuitTracing();
                    break;
                case BehaviorState.CircuitTracing:
                    if (m_discovery?.OnCircuitWaypointArrived() == true)
                        CompleteDiscovery();
                    break;
                case BehaviorState.Patrolling:
                    HandlePatrolArrival();
                    break;
                case BehaviorState.Traveling:
                    // Could be breach walk or debug command -- go Idle and let UpdatePatrolAI decide next
                    m_ai.SetState(BehaviorState.Idle);
                    break;
                case BehaviorState.Sleeping:
                    m_ai.Instance.StopMoving();
                    m_ai.SetState(BehaviorState.Sleeping);  // Clear target to stop re-triggering arrival
                    break;
                default:
                    m_ai.SetState(BehaviorState.Idle);
                    break;
            }
        }

        /// <summary>Send the patroller to the breach location ("Show me" button).</summary>
        public void WalkToBreach()
        {
            if (!m_breachPosition.HasValue) return;
            m_ai.SetState(BehaviorState.Traveling, m_breachPosition.Value);
        }

        /// <summary>Clear breach alarm and resume patrol.</summary>
        public void ClearBreach()
        {
            m_breachPosition = null;
            if (m_discoveryComplete) AdvanceToNextWaypoint();
        }

        private void HandlePatrolArrival()
        {
            // HNA routes are immutable -- skip deactivation and nudging
            if (!m_isHnaRoute)
            {
                // Quick arrival: waypoint is too close to the previous one, prune it
                // unless removal would create too large an angular gap in coverage
                if (m_refiner.IsQuickArrival() && m_patrolWaypoints != null && ActiveWaypointCount > 3
                    && !PatrolRefiner.WouldCreateLargeGap(
                        m_patrolWaypoints, m_currentWaypointIndex, m_ai.Memory.BedPosition))
                {
                    Plugin.Log?.LogInfo(
                        $"[Patrol:{m_ai.NpcName}] Quick arrival at waypoint {m_currentWaypointIndex}, deactivating redundant point");
                    DeactivateCurrentWaypoint();
                    return;
                }

                if (m_patrolWaypoints != null && m_currentWaypointIndex < m_patrolWaypoints.Count)
                {
                    // Nudge waypoint toward outer wall if there's a significant gap
                    var currentWp = m_patrolWaypoints[m_currentWaypointIndex];
                    if (PatrolRefiner.TryNudgeTowardWall(
                            currentWp.Position, m_ai.Memory.BedPosition, out var nudged))
                    {
                        m_patrolWaypoints[m_currentWaypointIndex] =
                            new VillagerWaypoint(nudged, VillagerWaypoint.DefaultStrategyId);
                        Plugin.Log?.LogInfo(
                            $"[Patrol:{m_ai.NpcName}] Nudged waypoint {m_currentWaypointIndex} toward wall" +
                            $" ({nudged.x:F0},{nudged.y:F1},{nudged.z:F0})");
                        RegisterVillageArea();
                        SaveState();
                    }
                }
            }

            if (m_patrolWaypoints != null && m_currentWaypointIndex < m_patrolWaypoints.Count)
            {
                var waypoint = m_patrolWaypoints[m_currentWaypointIndex];
                var pos = waypoint.Position;
                var bedPos = m_ai.Memory.BedPosition;
                int waypointIndex = m_currentWaypointIndex;

                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = "breach_check",
                    SourceId = $"{m_ai.UniqueId}:{waypointIndex}",
                    Priority = TaskPriority.High,
                    TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                    Attributes = new Dictionary<string, string>
                    {
                        { "waypoint_x", pos.x.ToString("F2", CultureInfo.InvariantCulture) },
                        { "waypoint_y", pos.y.ToString("F2", CultureInfo.InvariantCulture) },
                        { "waypoint_z", pos.z.ToString("F2", CultureInfo.InvariantCulture) },
                        { "bed_x", bedPos.x.ToString("F2", CultureInfo.InvariantCulture) },
                        { "bed_y", bedPos.y.ToString("F2", CultureInfo.InvariantCulture) },
                        { "bed_z", bedPos.z.ToString("F2", CultureInfo.InvariantCulture) },
                        { "guard_id", m_ai.UniqueId }
                    },
                    Callback = OnBreachCheckResult
                });
            }

            // Advance immediately; breach result arrives asynchronously
            AdvanceToNextWaypoint();
        }

        /// <summary>
        /// Callback invoked when a breach_check task completes.
        /// Sets the patroller to Alarmed state if a breach was detected.
        /// </summary>
        private void OnBreachCheckResult(TaskResult result)
        {
            if (!result.Success || result.Data == null) return;

            if (!result.Data.TryGetValue("breached", out var breachedStr)) return;
            if (breachedStr != "True") return;

            // Parse breach position from result data
            if (result.Data.TryGetValue("breach_x", out var bx) &&
                result.Data.TryGetValue("breach_y", out var by) &&
                result.Data.TryGetValue("breach_z", out var bz) &&
                float.TryParse(bx, NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                float.TryParse(by, NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                float.TryParse(bz, NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                m_breachPosition = new Vector3(x, y, z);
                m_ai.SetState(BehaviorState.Alarmed);
                Plugin.Log?.LogWarning(
                    $"[Patrol:{m_ai.NpcName}] BREACH detected (async) at ({x:F0},{z:F0})!");
            }
        }

        private void StartDiscovery()
        {
            // If HNA graph is not in memory, try restoring from ZDO
            if (!ValheimVillages.Villager.AI.Navigation.HnaRegionGraph.IsAvailable)
            {
                var zdo = m_ai?.NView?.GetZDO();
                if (zdo != null && PatrolPersistence.TryRestoreHnaGraph(zdo))
                    Plugin.Log?.LogInfo($"[Patrol:{m_ai.NpcName}] Restored HNA graph from ZDO");
            }

            // Try HNA boundary detection (instant, no physical walking)
            var hnaWaypoints = HnaBoundaryMapper.ComputeBoundaryWaypoints(m_ai.Memory.BedPosition);
            if (hnaWaypoints.Count >= 3)
            {
                m_patrolWaypoints = hnaWaypoints
                    .Select(p => new VillagerWaypoint(p, VillagerWaypoint.DefaultStrategyId))
                    .ToList();
                m_isHnaRoute = true;
                m_hnaPartitionRequested = false;
                CompleteHnaDiscovery();
                return;
            }

            // HNA graph unavailable: enqueue the partition task and wait one tick
            if (!m_hnaPartitionRequested)
            {
                m_hnaPartitionRequested = true;
                var bedPos = m_ai.Memory.BedPosition;
                Plugin.Log?.LogInfo($"[Patrol:{m_ai.NpcName}] HNA graph unavailable, requesting hna_partition build");
                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = "hna_partition",
                    SourceId = $"patrol:{m_ai.UniqueId}",
                    Priority = TaskPriority.Medium,
                    TimeoutSeconds = TaskSettings.DefaultTimeoutSeconds,
                    Attributes = new Dictionary<string, string>
                    {
                        { "anchor_x", bedPos.x.ToString("F2", CultureInfo.InvariantCulture) },
                        { "anchor_z", bedPos.z.ToString("F2", CultureInfo.InvariantCulture) }
                    }
                });
                return; // Stay in Idle; next tick will retry StartDiscovery()
            }

            // Already requested but graph still unavailable: fall back to raycast discovery
            Plugin.Log?.LogInfo($"[Patrol:{m_ai.NpcName}] HNA partition completed but graph insufficient, falling back to raycast discovery");
            m_isHnaRoute = false;
            m_hnaPartitionRequested = false;
            m_discovery = new PatrolDiscovery(m_ai, m_ai.Memory.BedPosition);
            m_discovery.BeginScouting();
        }

        private void CompleteHnaDiscovery()
        {
            m_currentWaypointIndex = 0;
            m_discoveryComplete = true;
            m_discovery = null;

            RegisterVillageArea();
            SaveState();

            // Persist the HNA graph to ZDO so other players don't need to recompute
            var zdo = m_ai?.NView?.GetZDO();
            if (zdo != null)
                PatrolPersistence.SaveHnaGraph(zdo);

            Plugin.Log?.LogInfo(
                $"[Patrol:{m_ai.NpcName}] HNA boundary discovery complete, {m_patrolWaypoints.Count} waypoints");
            AdvanceToNextWaypoint();
        }

        private void CompleteDiscovery()
        {
            if (m_discovery == null) return;
            m_patrolWaypoints = m_discovery.Waypoints
                .Select(p => new VillagerWaypoint(p, VillagerWaypoint.DefaultStrategyId))
                .ToList();
            m_currentWaypointIndex = 0;
            m_discoveryComplete = true;
            m_isHnaRoute = false;
            m_discovery = null;

            RegisterVillageArea();
            SaveState();
            Plugin.Log?.LogInfo($"[Patrol:{m_ai.NpcName}] Discovery complete, {m_patrolWaypoints.Count} waypoints");
            AdvanceToNextWaypoint();
        }

        private void RegisterVillageArea()
        {
            if (m_patrolWaypoints == null || ActiveWaypointCount == 0) return;
            var positions = m_patrolWaypoints.Where(w => w.Active).Select(w => w.Position).ToList();
            var area = new VillageArea(m_ai.UniqueId, m_ai.Memory.BedPosition, positions);
            VillageAreaManager.RegisterArea(area);
        }

        private void SaveState()
        {
            var zdo = m_ai?.NView?.GetZDO();
            if (zdo != null) PatrolPersistence.Save(this, zdo);
        }

        private void AdvanceToNextWaypoint()
        {
            if (m_patrolWaypoints == null || ActiveWaypointCount == 0)
            {
                m_ai.SetState(BehaviorState.Idle);
                return;
            }

            bool wrappedToZero = false;
            int total = m_patrolWaypoints.Count;

            // Advance to the next active waypoint, skipping inactive ones
            for (int i = 0; i < total; i++)
            {
                m_currentWaypointIndex = (m_currentWaypointIndex + 1) % total;
                if (m_currentWaypointIndex == 0)
                    wrappedToZero = true;
                if (m_patrolWaypoints[m_currentWaypointIndex].Active)
                    break;
            }

            // Full circuit completed: refine discovery-based routes (HNA routes are immutable)
            if (wrappedToZero && !m_isHnaRoute && ActiveWaypointCount >= 3)
            {
                var bed = m_ai.Memory.BedPosition;
                bool changed = false;

                // 1. Sort clockwise for a consistent loop
                PatrolRefiner.SortClockwise(m_patrolWaypoints, bed);

                // 2. Fill angular gaps to ensure full perimeter coverage
                int filled = PatrolRefiner.FillAngularGaps(m_patrolWaypoints, bed);
                if (filled > 0)
                {
                    Plugin.Log?.LogInfo(
                        $"[Patrol:{m_ai.NpcName}] Filled {filled} angular gaps ({m_patrolWaypoints.Count} total)");
                    changed = true;
                }

                // 3. Verify the patrol polygon encloses the bed
                if (!PatrolRefiner.PolygonContainsBed(m_patrolWaypoints, bed))
                    Plugin.Log?.LogWarning($"[Patrol:{m_ai.NpcName}] Patrol polygon does not enclose bed");

                // 4. Prune redundant collinear waypoints (with arc-length veto)
                if (m_patrolWaypoints.Count >= 4)
                {
                    int removed = PatrolRefiner.OptimizeCircuit(m_patrolWaypoints, bed);
                    if (removed > 0)
                    {
                        Plugin.Log?.LogInfo(
                            $"[Patrol:{m_ai.NpcName}] Optimized: removed {removed} redundant waypoints" +
                            $" ({m_patrolWaypoints.Count} remaining)");
                        changed = true;
                    }
                }

                if (changed)
                {
                    RegisterVillageArea();
                    SaveState();
                }
            }

            var wp = m_patrolWaypoints[m_currentWaypointIndex];
            Plugin.Log?.LogInfo(
                $"[Patrol:{m_ai.NpcName}] Patrol -> waypoint {m_currentWaypointIndex}/{m_patrolWaypoints.Count}" +
                $" at ({wp.Position.x:F0},{wp.Position.y:F1},{wp.Position.z:F0}), dist={Vector3.Distance(m_ai.Position, wp.Position):F1}m");
            m_ai.SetState(BehaviorState.Patrolling, wp);
            m_refiner.OnSegmentStart();
            ResetStallTracking();
        }

        #region Stall Recovery

        /// <summary>
        /// Check if the patroller is making progress toward its movement target.
        /// Called each behavior tick (~15s) for Patrolling and CircuitTracing states.
        /// Circuit tracing: uses consecutive stall ticks to skip arc points.
        /// Patrolling: uses the refiner to insert intermediate waypoints or relocate
        /// unreachable waypoints.
        /// </summary>
        private void CheckStallAndRecover(BehaviorState state)
        {
            float moved = Vector3.Distance(m_ai.Position, m_lastProgressPosition);
            m_lastProgressPosition = m_ai.Position;
            bool hasMoved = moved >= MinProgressDistance;

            if (hasMoved) m_progressStalls = 0;
            else m_progressStalls++;

            if (state == BehaviorState.CircuitTracing)
            {
                if (m_progressStalls >= 1)
                {
                    ResetStallTracking();
                    Plugin.Log?.LogWarning($"[Patrol:{m_ai.NpcName}] Stuck during circuit tracing (~5s no movement), skipping arc point");
                    m_discovery?.SkipToNextArcPoint();
                }
                return;
            }

            // HNA routes: find nearest reachable waypoint when stuck
            if (m_isHnaRoute)
            {
                if (!hasMoved)
                {
                    Plugin.Log?.LogWarning(
                        $"[Patrol:{m_ai.NpcName}] Stuck on HNA waypoint {m_currentWaypointIndex}, " +
                        $"searching for nearest reachable waypoint");
                    ResetStallTracking();
                    RecoverToNearestReachableWaypoint();
                }
                return;
            }

            // Discovery routes: refiner decides based on movement progress
            var action = m_refiner.CheckProgress(hasMoved);
            switch (action)
            {
                case PatrolRefinement.InsertWaypointHere:
                    InsertWaypointAtCurrentPosition();
                    break;
                case PatrolRefinement.RelocateWaypoint:
                    RelocateCurrentWaypoint();
                    break;
            }
        }

        /// <summary>
        /// When stuck on an HNA waypoint, find the nearest reachable waypoint AFTER the stuck
        /// one in route order (so the patroller advances forward, not backward into a loop).
        /// Uses CalculatePath which respects doors, bridges, stairs, and NavMesh links.
        /// Falls back to any reachable waypoint if none ahead are reachable.
        /// </summary>
        private void RecoverToNearestReachableWaypoint()
        {
            var currentPos = m_ai.Position;
            int stuckIdx = m_currentWaypointIndex;
            int count = m_patrolWaypoints.Count;
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = ValheimVillages.Villager.AI.Navigation.VillageNavMeshBake.ResolveValheimHumanoidAgentTypeID(),
                areaMask = NavMesh.AllAreas
            };

            if (!NavMesh.SamplePosition(currentPos, out NavMeshHit srcHit, 10f, filter))
            {
                Plugin.Log?.LogWarning(
                    $"[Patrol:{m_ai.NpcName}] Recovery failed: position not on NavMesh, skipping");
                m_currentWaypointIndex = (stuckIdx + 1) % count;
                AdvanceToNextWaypoint();
                return;
            }

            // Search forward from stuck+1, wrapping around the circuit.
            // Prefer the first reachable waypoint ahead in route order (closest by hops).
            int bestIndex = -1;
            float bestDist = float.MaxValue;

            for (int offset = 1; offset < count; offset++)
            {
                int i = (stuckIdx + offset) % count;
                if (!m_patrolWaypoints[i].Active) continue;

                var wpPos = m_patrolWaypoints[i].Position;
                if (!NavMesh.SamplePosition(wpPos, out NavMeshHit dstHit, 5f, filter))
                    continue;

                var path = new NavMeshPath();
                NavMesh.CalculatePath(srcHit.position, dstHit.position, filter, path);
                if (path.status != NavMeshPathStatus.PathComplete)
                    continue;

                float dist = PathLength(path);
                bestDist = dist;
                bestIndex = i;
                break; // Take the first reachable forward waypoint
            }

            if (bestIndex >= 0)
            {
                Plugin.Log?.LogInfo(
                    $"[Patrol:{m_ai.NpcName}] Recovery: skipping stuck wp {stuckIdx}, " +
                    $"routing forward to wp {bestIndex} (dist={bestDist:F1}m via path)");
                m_currentWaypointIndex = bestIndex;
                var wp = m_patrolWaypoints[m_currentWaypointIndex];
                m_ai.SetState(BehaviorState.Patrolling, wp);
                m_refiner.OnSegmentStart();
                ResetStallTracking();
            }
            else
            {
                Plugin.Log?.LogWarning(
                    $"[Patrol:{m_ai.NpcName}] Recovery failed: no reachable waypoint found, " +
                    $"skipping past stuck wp {stuckIdx}");
                m_currentWaypointIndex = (stuckIdx + 1) % count;
                AdvanceToNextWaypoint();
            }
        }

        /// <summary>Compute total length along a NavMeshPath's corners.</summary>
        private static float PathLength(NavMeshPath path)
        {
            var corners = path.corners;
            if (corners == null || corners.Length < 2) return 0f;
            float total = 0f;
            for (int i = 1; i < corners.Length; i++)
                total += Vector3.Distance(corners[i - 1], corners[i]);
            return total;
        }

        /// <summary>
        /// Deactivate the current patrol waypoint (mark it inactive for potential reactivation).
        /// The waypoint is retained in the list for debug display and future route recomputation.
        /// Only truly deletes if no active waypoints remain after deactivation.
        /// </summary>
        private void DeactivateCurrentWaypoint()
        {
            if (m_patrolWaypoints == null || m_patrolWaypoints.Count == 0) return;

            var wp = m_patrolWaypoints[m_currentWaypointIndex];
            wp.Active = false;
            Plugin.Log?.LogWarning(
                $"[Patrol:{m_ai.NpcName}] Deactivated waypoint {m_currentWaypointIndex}" +
                $" ({ActiveWaypointCount} active / {m_patrolWaypoints.Count} total)");

            if (ActiveWaypointCount == 0)
            {
                Plugin.Log?.LogWarning($"[Patrol:{m_ai.NpcName}] No active waypoints left, restarting discovery");
                m_discoveryComplete = false;
                VillageAreaManager.UnregisterArea(m_ai.UniqueId);
                SaveState();
                m_ai.SetState(BehaviorState.Idle);
                return;
            }

            RegisterVillageArea();
            SaveState();
            AdvanceToNextWaypoint();
        }

        /// <summary>
        /// Insert a new waypoint at the patroller's current position, before the current target.
        /// Continues moving toward the original target; the new point is for future laps.
        /// </summary>
        private void InsertWaypointAtCurrentPosition()
        {
            if (m_patrolWaypoints == null) return;

            var wp = new VillagerWaypoint(m_ai.Position, VillagerWaypoint.DefaultStrategyId);
            m_patrolWaypoints.Insert(m_currentWaypointIndex, wp);
            m_currentWaypointIndex++;
            RegisterVillageArea();
            SaveState();
            Plugin.Log?.LogInfo(
                $"[Patrol:{m_ai.NpcName}] Inserted mid-traverse waypoint at ({m_ai.Position.x:F0},{m_ai.Position.z:F0})" +
                $" ({m_patrolWaypoints.Count} total)");
        }

        /// <summary>
        /// Deactivate the unreachable target waypoint and insert a new active waypoint
        /// at the patroller's current position. The old waypoint is retained for potential reactivation.
        /// </summary>
        private void RelocateCurrentWaypoint()
        {
            if (m_patrolWaypoints == null || m_patrolWaypoints.Count == 0) return;

            var oldWp = m_patrolWaypoints[m_currentWaypointIndex];
            oldWp.Active = false;

            var newWp = new VillagerWaypoint(m_ai.Position, VillagerWaypoint.DefaultStrategyId);
            m_patrolWaypoints.Insert(m_currentWaypointIndex + 1, newWp);

            Plugin.Log?.LogWarning(
                $"[Patrol:{m_ai.NpcName}] Deactivated unreachable waypoint {m_currentWaypointIndex}," +
                $" inserted replacement at ({m_ai.Position.x:F0},{m_ai.Position.z:F0})");

            m_currentWaypointIndex++;
            RegisterVillageArea();
            SaveState();
            ResetStallTracking();
            AdvanceToNextWaypoint();
        }


        private void ResetStallTracking()
        {
            m_progressStalls = 0;
            m_lastProgressPosition = m_ai.Position;
        }

        #endregion
    }
}
