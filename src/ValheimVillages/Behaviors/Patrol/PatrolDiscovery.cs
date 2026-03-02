using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Enums;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villages;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    /// Manages patrol route discovery.
    /// Phase 1 (Scouting): Detect perimeter instantly via exterior-inward raycasts, then walk toward wall.
    /// Phase 2 (CircuitTracing): Trace circle around bed creating waypoints, raycasting from exterior
    /// inward at each angle so the first wall hit is always the outermost perimeter wall.
    /// </summary>
    public class PatrolDiscovery
    {
        public const float MaxScoutDistance = 15f;
        public const float MinPatrolRadius = 15f;
        public const float WaypointSpacing = 7f;
        public const float WallInsetDistance = 2f;
        public const float CircuitCloseThreshold = 7f;
        private const float NavMeshProbeHeight = 20f;
        private const float NavMeshProbeRadius = 20f;
        private const int MaxAngleSkips = 5;
        private const float ExteriorProbeOffset = 8f;
        private const float PerimeterProbeDistance = 12f;

        private readonly VillagerAI m_ai;
        private readonly Vector3 m_bedPosition;
        private Vector3 m_scoutDirection;
        private bool m_perimeterDetected;
        private float m_patrolRadius;
        private float m_currentAngle;
        private float m_startAngle;
        private readonly List<Vector3> m_waypoints = new();
        private bool m_circuitStarted;

        public PatrolDiscovery(VillagerAI ai, Vector3 bedPosition)
        {
            m_ai = ai;
            m_bedPosition = bedPosition;
        }

        public IReadOnlyList<Vector3> Waypoints => m_waypoints;
        public bool IsCircuitComplete { get; private set; }

        #region Phase 1: Scouting

        public void BeginScouting()
        {
            float angle = Random.Range(0f, 360f);
            m_scoutDirection = new Vector3(Mathf.Cos(angle * Mathf.Deg2Rad), 0f, Mathf.Sin(angle * Mathf.Deg2Rad)).normalized;

            // Instant perimeter detection: raycast from exterior inward
            if (RaycastPerimeterFromExterior(m_scoutDirection, PerimeterProbeDistance, out var hit))
            {
                var wallXZ = new Vector3(hit.point.x, 0f, hit.point.z);
                var bedXZ = new Vector3(m_bedPosition.x, 0f, m_bedPosition.z);
                m_patrolRadius = Mathf.Max(Vector3.Distance(bedXZ, wallXZ), MinPatrolRadius);
                Plugin.Log?.LogInfo($"[Patrol:{m_ai.NpcName}] Outer wall at {hit.point}, radius={m_patrolRadius:F1}m");
            }
            else
            {
                m_patrolRadius = MaxScoutDistance;
                Plugin.Log?.LogInfo($"[Patrol:{m_ai.NpcName}] No outer wall found, using max radius {m_patrolRadius:F1}m");
            }

            m_perimeterDetected = true;

            // Still send patroller walking outward so they move away from the bed
            m_ai.SetState(BehaviorState.Scouting, m_bedPosition + m_scoutDirection * m_patrolRadius);
            Plugin.Log?.LogInfo($"[Patrol:{m_ai.NpcName}] Scouting in direction {m_scoutDirection}");
        }

        /// <summary>Returns true when scouting is complete. Perimeter is detected instantly
        /// in BeginScouting, so this returns true on first call to transition to circuit tracing.</summary>
        public bool UpdateScouting()
        {
            return m_perimeterDetected;
        }

        #endregion

        #region Phase 2: Circuit Tracing

        public void BeginCircuitTracing()
        {
            var offset = m_ai.Position - m_bedPosition;
            offset.y = 0f;

            // Safeguard against arrival before UpdateScouting had a tick to set the radius
            if (m_patrolRadius < MinPatrolRadius)
                m_patrolRadius = Mathf.Max(offset.magnitude, MinPatrolRadius);

            m_startAngle = Mathf.Atan2(offset.z, offset.x);
            m_currentAngle = m_startAngle;
            m_circuitStarted = false;
            m_waypoints.Clear();
            IsCircuitComplete = false;

            CreateWaypointAtCurrentAngle();
            AdvanceToNextArcPoint();
            Plugin.Log?.LogInfo($"[Patrol:{m_ai.NpcName}] Circuit tracing at radius {m_patrolRadius:F1}m");
        }

        /// <summary>Returns true when the circuit is complete.</summary>
        public bool OnCircuitWaypointArrived()
        {
            CreateWaypointAtCurrentAngle();

            // Validate the just-added waypoint is reachable from the patroller
            if (m_waypoints.Count > 0 && !IsReachableFromPatroller(m_waypoints[m_waypoints.Count - 1]))
                m_waypoints.RemoveAt(m_waypoints.Count - 1);

            if (m_circuitStarted && m_waypoints.Count >= 3)
            {
                float distToFirst = Vector2.Distance(
                    new Vector2(m_ai.Position.x, m_ai.Position.z),
                    new Vector2(m_waypoints[0].x, m_waypoints[0].z));

                if (distToFirst < CircuitCloseThreshold)
                {
                    IsCircuitComplete = true;
                    Plugin.Log?.LogInfo($"[Patrol:{m_ai.NpcName}] Circuit complete, {m_waypoints.Count} waypoints");
                    return true;
                }
            }

            m_circuitStarted = true;
            AdvanceToNextArcPoint();
            return false;
        }

        /// <summary>
        /// Skip to the next arc point without creating a waypoint.
        /// Used for stall recovery when the patroller can't reach the current target.
        /// </summary>
        public void SkipToNextArcPoint()
        {
            // The current position is known to be pathable — record it as a waypoint
            m_waypoints.Add(m_ai.Position);
            Plugin.Log?.LogWarning(
                $"[Patrol:{m_ai.NpcName}] Unreachable arc point, added waypoint at current position" +
                $" ({m_ai.Position.x:F0},{m_ai.Position.z:F0}), advancing angle");
            m_circuitStarted = true;
            AdvanceToNextArcPoint();
        }

        private void CreateWaypointAtCurrentAngle()
        {
            var direction = new Vector3(Mathf.Cos(m_currentAngle), 0f, Mathf.Sin(m_currentAngle));
            var currentPos = m_ai.Position;

            Vector3 candidate;
            bool nearPerimeter = false;

            // Raycast from exterior inward to find the outermost wall at this angle
            float probeDist = m_patrolRadius + ExteriorProbeOffset;
            if (RaycastPerimeterFromExterior(direction, probeDist, out var hit))
            {
                // Place waypoint just inside the outer wall
                var inward = (m_bedPosition - hit.point);
                inward.y = 0f;
                inward = inward.normalized;
                candidate = hit.point + inward * WallInsetDistance;
                candidate.y = currentPos.y;
                nearPerimeter = true;
            }
            else
            {
                candidate = m_bedPosition + direction * m_patrolRadius;
                candidate.y = currentPos.y;
            }

            if (SnapToNavMesh(candidate, nearPerimeter, out var snapped))
                m_waypoints.Add(snapped);
        }

        private void AdvanceToNextArcPoint()
        {
            float angleStep = WaypointSpacing / Mathf.Max(m_patrolRadius, 1f);
            float probeDist = m_patrolRadius + ExteriorProbeOffset;

            for (int i = 0; i < MaxAngleSkips; i++)
            {
                m_currentAngle += angleStep;
                var dir = new Vector3(Mathf.Cos(m_currentAngle), 0f, Mathf.Sin(m_currentAngle));

                // Raycast from exterior inward to find the wall at this angle
                Vector3 target;
                if (RaycastPerimeterFromExterior(dir, probeDist, out var hit))
                {
                    var inward = (m_bedPosition - hit.point);
                    inward.y = 0f;
                    inward = inward.normalized;
                    target = hit.point + inward * WallInsetDistance;
                }
                else
                {
                    target = m_bedPosition + dir * m_patrolRadius;
                }

                if (SnapToNavMesh(target, false, out var snapped))
                {
                    m_ai.SetState(BehaviorState.CircuitTracing,
                        new VillagerWaypoint(snapped, VillagerWaypoint.DefaultStrategyId));
                    return;
                }
            }

            // Fallback: use last geometric position with ground-level Y
            var fallbackDir = new Vector3(Mathf.Cos(m_currentAngle), 0f, Mathf.Sin(m_currentAngle));
            var fallback = m_bedPosition + fallbackDir * m_patrolRadius;
            if (ZoneSystem.instance != null &&
                ZoneSystem.instance.GetSolidHeight(fallback, out float height, 1000))
                fallback.y = height;

            m_ai.SetState(BehaviorState.CircuitTracing,
                new VillagerWaypoint(fallback, VillagerWaypoint.DefaultStrategyId));
        }

        /// <summary>
        /// Raycast from a point far outside the village inward toward the bed center.
        /// The nearest wall hit from the exterior is always the outermost perimeter wall.
        /// </summary>
        private bool RaycastPerimeterFromExterior(Vector3 direction, float probeDistance, out RaycastHit wallHit)
        {
            wallHit = default;
            var origin = m_bedPosition + direction * probeDistance;

            // Get terrain height at the exterior probe point, 1m above ground
            float height = m_bedPosition.y;
            if (ZoneSystem.instance != null &&
                ZoneSystem.instance.GetSolidHeight(origin, out float h, 1000))
                height = h;
            origin.y = height + 1f;

            // Raycast horizontally inward toward the bed center
            var inward = (m_bedPosition - origin);
            inward.y = 0f;
            inward = inward.normalized;

            return WallDetection.RaycastForWall(origin, inward, probeDistance + ExteriorProbeOffset, out wallHit);
        }

        /// <summary>
        /// Snap a candidate position to the NavMesh. When near the village perimeter (wall-adjacent),
        /// probes from above to prefer elevated surfaces like wall tops. Otherwise probes at
        /// candidate height to stay at ground level.
        /// </summary>
        private static bool SnapToNavMesh(Vector3 candidate, bool preferElevated, out Vector3 snapped)
        {
            snapped = candidate;
            var filter = new NavMeshQueryFilter();
            filter.agentTypeID = ValheimVillages.Villager.AI.Navigation.VillageNavMeshBake.ResolveValheimHumanoidAgentTypeID();
            filter.areaMask = NavMesh.AllAreas;

            float probeY = preferElevated ? candidate.y + NavMeshProbeHeight : candidate.y;
            var probe = new Vector3(candidate.x, probeY, candidate.z);
            if (NavMesh.SamplePosition(probe, out NavMeshHit hit, NavMeshProbeRadius, filter))
            {
                snapped = hit.position;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Check if a target position is reachable from the patroller via a complete NavMesh path.
        /// </summary>
        private bool IsReachableFromPatroller(Vector3 target)
        {
            var filter = new NavMeshQueryFilter();
            filter.agentTypeID = ValheimVillages.Villager.AI.Navigation.VillageNavMeshBake.ResolveValheimHumanoidAgentTypeID();
            filter.areaMask = NavMesh.AllAreas;

            if (!NavMesh.SamplePosition(m_ai.Position, out NavMeshHit srcHit, 5f, filter)) return false;
            if (!NavMesh.SamplePosition(target, out NavMeshHit dstHit, 5f, filter)) return false;

            var path = new NavMeshPath();
            NavMesh.CalculatePath(srcHit.position, dstHit.position, filter, path);
            return path.status == NavMeshPathStatus.PathComplete;
        }

        #endregion
    }
}
