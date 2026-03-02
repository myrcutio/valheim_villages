using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villages;
using VillagerWaypoint = ValheimVillages.Villager.AI.Pathfinding.VillagerWaypoint;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    /// Refines a patrol route over successive laps using timing heuristics:
    /// - Quick arrival (less than 2s): waypoint is redundant, remove it
    /// - Mid-traverse (15s+ and still moving): insert waypoint at current position
    /// - Stuck (15s+ and no movement): relocate unreachable waypoint to current position
    /// - Post-circuit: prune nearly-collinear waypoints that don't add meaningful perimeter coverage
    /// </summary>
    public class PatrolRefiner
    {
        /// <summary>Arrivals faster than this indicate the waypoint is redundant.</summary>
        public const float QuickArrivalThreshold = 2f;

        /// <summary>
        /// Minimum detour ratio to keep a waypoint during optimization.
        /// If (dist(A,B) + dist(B,C)) / dist(A,C) is below this, B is nearly collinear
        /// and can be pruned without losing meaningful perimeter coverage.
        /// </summary>
        private const float MinDetourRatio = 1.15f;

        /// <summary>Minimum waypoints to keep after optimization (need at least 3 for a circuit).</summary>
        private const int MinWaypointsAfterOptimize = 3;

        /// <summary>
        /// Maximum allowed arc-length multiplier relative to WaypointSpacing.
        /// If removing a waypoint would create a gap larger than WaypointSpacing * this,
        /// the removal is vetoed to maintain angular coverage around the perimeter.
        /// </summary>
        private const float MaxArcGapMultiplier = 2f;

        /// <summary>Minimum XZ gap (meters) between waypoint and outer wall before nudging outward.</summary>
        private const float MinNudgeGap = 3f;

        /// <summary>How far beyond the waypoint to place the exterior raycast origin (meters).</summary>
        private const float NudgeProbeOffset = 8f;

        /// <summary>How high above the candidate to probe NavMesh for elevated surfaces.</summary>
        private const float ElevatedProbeHeight = 20f;

        /// <summary>NavMesh sample radius when probing for elevated surfaces.</summary>
        private const float ElevatedProbeRadius = 20f;

        private float m_segmentStartTime;
        private bool m_midTraverseInserted;

        /// <summary>Call when the patroller begins moving toward a new waypoint.</summary>
        public void OnSegmentStart()
        {
            m_segmentStartTime = Time.time;
            m_midTraverseInserted = false;
        }

        /// <summary>Returns true if the patroller arrived faster than the quick arrival threshold.</summary>
        public bool IsQuickArrival()
        {
            return (Time.time - m_segmentStartTime) < QuickArrivalThreshold;
        }

        /// <summary>
        /// Evaluate patrol progress at each behavior tick.
        /// Called once per tick (~15s) while the patroller is patrolling.
        /// </summary>
        /// <param name="hasMoved">True if the patroller moved significantly since the last tick.</param>
        public PatrolRefinement CheckProgress(bool hasMoved)
        {
            if (!hasMoved)
                return PatrolRefinement.RelocateWaypoint;

            if (!m_midTraverseInserted)
            {
                m_midTraverseInserted = true;
                return PatrolRefinement.InsertWaypointHere;
            }

            return PatrolRefinement.None;
        }

        /// <summary>
        /// Check if a waypoint has a significant gap to the outer wall and nudge it outward.
        /// Probes NavMesh from above to prefer elevated surfaces like wall tops, so the
        /// patrol route gravitates toward the actual village perimeter over successive laps.
        /// </summary>
        /// <returns>True if the waypoint was nudged, with the new position in <paramref name="nudged"/>.</returns>
        public static bool TryNudgeTowardWall(Vector3 waypointPos, Vector3 bedPosition, out Vector3 nudged)
        {
            nudged = waypointPos;

            var outward = waypointPos - bedPosition;
            outward.y = 0f;
            float currentDist = outward.magnitude;
            if (currentDist < 0.1f) return false;
            outward = outward.normalized;

            // Raycast from exterior inward to find the outer wall at this angle
            float probeDist = currentDist + NudgeProbeOffset;
            var origin = bedPosition + outward * probeDist;

            float height = bedPosition.y;
            if (ZoneSystem.instance != null &&
                ZoneSystem.instance.GetSolidHeight(origin, out float h, 1000))
                height = h;
            origin.y = height + 1f;

            var inward = -outward;
            if (!WallDetection.RaycastForWall(origin, inward, probeDist + NudgeProbeOffset, out var wallHit))
                return false;

            // Measure the gap: how much closer to the bed is the waypoint vs the wall?
            var bedXZ = new Vector3(bedPosition.x, 0f, bedPosition.z);
            var wallXZ = new Vector3(wallHit.point.x, 0f, wallHit.point.z);
            float wallDist = Vector3.Distance(bedXZ, wallXZ);
            float gap = wallDist - currentDist;

            if (gap < MinNudgeGap) return false;

            // Nudge toward the wall, staying WallInsetDistance inside
            var candidate = wallHit.point + inward * PatrolDiscovery.WallInsetDistance;
            candidate.y = waypointPos.y;

            // Snap to NavMesh probing from above to prefer wall tops / elevated paths
            var filter = new NavMeshQueryFilter();
            filter.agentTypeID = VillageNavMeshBake.ResolveValheimHumanoidAgentTypeID();
            filter.areaMask = NavMesh.AllAreas;

            var probe = new Vector3(candidate.x, candidate.y + ElevatedProbeHeight, candidate.z);
            if (!NavMesh.SamplePosition(probe, out NavMeshHit hit, ElevatedProbeRadius, filter))
                return false;

            nudged = hit.position;
            return true;
        }

        /// <summary>
        /// Check if deactivating a waypoint at the given index would create an angular gap
        /// that's too large relative to the patrol perimeter.
        /// Only considers active neighbors when measuring the gap.
        /// </summary>
        public static bool WouldCreateLargeGap(
            List<VillagerWaypoint> waypoints, int index, Vector3 bedPosition)
        {
            int activeCount = 0;
            foreach (var w in waypoints) if (w.Active) activeCount++;
            if (activeCount <= MinWaypointsAfterOptimize) return true;

            int n = waypoints.Count;
            int prev = FindActiveNeighbor(waypoints, index, -1);
            int next = FindActiveNeighbor(waypoints, index, +1);
            if (prev < 0 || next < 0 || prev == next) return true;

            var dirPrev = waypoints[prev].Position - bedPosition;
            dirPrev.y = 0f;
            var dirNext = waypoints[next].Position - bedPosition;
            dirNext.y = 0f;

            float avgRadius = (dirPrev.magnitude + dirNext.magnitude) * 0.5f;
            if (avgRadius < 1f) return false;

            float angleDeg = Vector3.Angle(dirPrev, dirNext);
            float arcLength = angleDeg * Mathf.Deg2Rad * avgRadius;
            float maxArc = PatrolDiscovery.WaypointSpacing * MaxArcGapMultiplier;

            return arcLength > maxArc;
        }

        /// <summary>
        /// Find the nearest active waypoint in a given direction from the specified index.
        /// Direction should be +1 (forward) or -1 (backward). Returns -1 if none found.
        /// </summary>
        private static int FindActiveNeighbor(List<VillagerWaypoint> waypoints, int fromIndex, int direction)
        {
            int n = waypoints.Count;
            for (int i = 1; i < n; i++)
            {
                int idx = (fromIndex + direction * i + n * i) % n;
                if (waypoints[idx].Active) return idx;
            }
            return -1;
        }

        /// <summary>
        /// Sort waypoints clockwise (top-down) around the bed position by their XZ angle.
        /// This ensures the patroller walks a consistent perimeter loop instead of doubling back.
        /// </summary>
        public static void SortClockwise(List<VillagerWaypoint> waypoints, Vector3 bedPosition)
        {
            waypoints.Sort((a, b) =>
            {
                var da = a.Position - bedPosition; da.y = 0f;
                var db = b.Position - bedPosition; db.y = 0f;
                float angleA = Mathf.Atan2(da.z, da.x);
                float angleB = Mathf.Atan2(db.z, db.x);
                return angleB.CompareTo(angleA);
            });
        }

        /// <summary>
        /// Check whether the active patrol polygon (XZ projection) encloses the bed position.
        /// Uses the ray-casting point-in-polygon algorithm. Only considers active waypoints.
        /// </summary>
        public static bool PolygonContainsBed(List<VillagerWaypoint> waypoints, Vector3 bedPosition)
        {
            var active = GetActivePositions(waypoints);
            int n = active.Count;
            if (n < 3) return false;

            float bx = bedPosition.x, bz = bedPosition.z;
            bool inside = false;

            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                float iz = active[i].z, jz = active[j].z;
                float ix = active[i].x, jx = active[j].x;

                if (((iz > bz) != (jz > bz)) &&
                    (bx < (jx - ix) * (bz - iz) / (jz - iz) + ix))
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        /// <summary>Extract positions of active waypoints only.</summary>
        private static List<Vector3> GetActivePositions(List<VillagerWaypoint> waypoints)
        {
            var result = new List<Vector3>();
            foreach (var wp in waypoints)
                if (wp.Active) result.Add(wp.Position);
            return result;
        }

        /// <summary>
        /// After a clockwise sort, find angular gaps larger than the max arc among active
        /// waypoints and insert new active waypoints at the patrol radius to fill them.
        /// Only considers active waypoints when measuring gaps.
        /// </summary>
        /// <returns>Number of waypoints inserted.</returns>
        public static int FillAngularGaps(List<VillagerWaypoint> waypoints, Vector3 bedPosition)
        {
            var activeIndices = GetActiveIndices(waypoints);
            if (activeIndices.Count < 3) return 0;

            float totalDist = 0f;
            foreach (int ai in activeIndices)
            {
                var offset = waypoints[ai].Position - bedPosition;
                offset.y = 0f;
                totalDist += offset.magnitude;
            }
            float patrolRadius = totalDist / activeIndices.Count;
            float maxArc = PatrolDiscovery.WaypointSpacing * MaxArcGapMultiplier;
            float maxAngle = maxArc / Mathf.Max(patrolRadius, 1f);

            int added = 0;

            for (int a = 0; a < activeIndices.Count; a++)
            {
                int nextA = (a + 1) % activeIndices.Count;
                int listIdx = activeIndices[a];
                int listNext = activeIndices[nextA];

                var di = waypoints[listIdx].Position - bedPosition; di.y = 0f;
                var dn = waypoints[listNext].Position - bedPosition; dn.y = 0f;

                float angleI = Mathf.Atan2(di.z, di.x);
                float angleN = Mathf.Atan2(dn.z, dn.x);

                float gap = angleI - angleN;
                if (gap < 0f) gap += 2f * Mathf.PI;

                if (gap > maxAngle && gap < 2f * Mathf.PI - 0.1f)
                {
                    int numInserts = Mathf.CeilToInt(gap / maxAngle) - 1;
                    numInserts = Mathf.Clamp(numInserts, 1, 10);
                    float step = gap / (numInserts + 1);

                    int insertPos = listIdx + 1;
                    int actualInserts = 0;
                    for (int j = 1; j <= numInserts; j++)
                    {
                        float newAngle = angleI - step * j;
                        var dir = new Vector3(Mathf.Cos(newAngle), 0f, Mathf.Sin(newAngle));

                        float probeDist = patrolRadius + NudgeProbeOffset;
                        var origin = bedPosition + dir * probeDist;

                        float originY = bedPosition.y;
                        if (ZoneSystem.instance != null &&
                            ZoneSystem.instance.GetSolidHeight(origin, out float oh, 1000))
                            originY = oh;
                        origin.y = originY + 1f;

                        var inward = -dir;
                        if (!WallDetection.RaycastForWall(origin, inward, probeDist + NudgeProbeOffset, out var wallHit))
                            continue;

                        var pos = wallHit.point + inward * PatrolDiscovery.WallInsetDistance;
                        pos.y = bedPosition.y;

                        var filter = new NavMeshQueryFilter();
                        filter.agentTypeID = VillageNavMeshBake.ResolveValheimHumanoidAgentTypeID();
                        filter.areaMask = NavMesh.AllAreas;

                        if (!NavMesh.SamplePosition(pos, out NavMeshHit navHit, ElevatedProbeRadius, filter))
                            continue;

                        waypoints.Insert(insertPos + actualInserts, new VillagerWaypoint(
                            navHit.position, VillagerWaypoint.DefaultStrategyId));
                        actualInserts++;
                        added++;
                    }

                    // Shift active indices after our insert position
                    for (int k = a + 1; k < activeIndices.Count; k++)
                        activeIndices[k] += actualInserts;
                }

                if (waypoints.Count > 80) break;
            }

            return added;
        }

        /// <summary>Returns the list indices of all active waypoints.</summary>
        private static List<int> GetActiveIndices(List<VillagerWaypoint> waypoints)
        {
            var result = new List<int>();
            for (int i = 0; i < waypoints.Count; i++)
                if (waypoints[i].Active) result.Add(i);
            return result;
        }

        /// <summary>
        /// Post-circuit optimization: deactivate waypoints that create negligible detours
        /// without leaving large angular gaps in perimeter coverage.
        /// A waypoint is only deactivated if it's nearly collinear (low detour ratio) AND
        /// deactivation wouldn't create an arc gap larger than 2x WaypointSpacing.
        /// Deactivated waypoints are retained for potential reactivation and debug display.
        /// </summary>
        /// <returns>Number of waypoints deactivated.</returns>
        public static int OptimizeCircuit(List<VillagerWaypoint> waypoints, Vector3 bedPosition)
        {
            var activeIndices = GetActiveIndices(waypoints);
            if (activeIndices.Count <= MinWaypointsAfterOptimize) return 0;

            var toDeactivate = new HashSet<int>();

            for (int a = 0; a < activeIndices.Count; a++)
            {
                int prevA = (a - 1 + activeIndices.Count) % activeIndices.Count;
                int nextA = (a + 1) % activeIndices.Count;

                // Don't evaluate if an active neighbor is already marked
                if (toDeactivate.Contains(activeIndices[prevA]) ||
                    toDeactivate.Contains(activeIndices[nextA])) continue;

                int idx = activeIndices[a];
                var aPos = waypoints[activeIndices[prevA]].Position;
                var bPos = waypoints[idx].Position;
                var cPos = waypoints[activeIndices[nextA]].Position;

                float directDist = Vector3.Distance(aPos, cPos);
                if (directDist < 0.01f) continue;

                float detourDist = Vector3.Distance(aPos, bPos) + Vector3.Distance(bPos, cPos);
                float detourRatio = detourDist / directDist;

                if (detourRatio >= MinDetourRatio) continue;

                // Arc-length veto: don't deactivate if it would leave too large a gap
                var dirPrev = (aPos - bedPosition); dirPrev.y = 0f;
                var dirNext = (cPos - bedPosition); dirNext.y = 0f;
                float avgRadius = (dirPrev.magnitude + dirNext.magnitude) * 0.5f;

                if (avgRadius >= 1f)
                {
                    float angleDeg = Vector3.Angle(dirPrev, dirNext);
                    float arcLength = angleDeg * Mathf.Deg2Rad * avgRadius;
                    float maxArc = PatrolDiscovery.WaypointSpacing * MaxArcGapMultiplier;
                    if (arcLength > maxArc) continue;
                }

                toDeactivate.Add(idx);
            }

            // Respect minimum active waypoint count
            if (activeIndices.Count - toDeactivate.Count < MinWaypointsAfterOptimize)
                return 0;

            foreach (int idx in toDeactivate)
                waypoints[idx].Active = false;

            return toDeactivate.Count;
        }
    }

    public enum PatrolRefinement
    {
        None,
        InsertWaypointHere,
        RelocateWaypoint,
    }
}
