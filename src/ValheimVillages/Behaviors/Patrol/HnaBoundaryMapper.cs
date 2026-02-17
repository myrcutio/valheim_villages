using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.NPCs.AI;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    /// Computes guard patrol waypoints from HNA region graph boundary cells.
    /// Pipeline: edge snap -> clockwise sort -> Chaikin smooth -> NavMesh re-snap -> RDP -> sharp angle prune.
    /// Pure geometry operations are delegated to <see cref="BoundaryGeometry"/>.
    /// </summary>
    public static class HnaBoundaryMapper
    {
        /// <summary>
        /// Compute patrol waypoints from HNA boundary cells, sorted clockwise around the bed.
        /// Returns an empty list if the HNA graph is unavailable or has fewer than 3 boundary cells.
        /// </summary>
        public static List<Vector3> ComputeBoundaryWaypoints(Vector3 bedPosition)
        {
            if (!HnaRegionGraph.IsAvailable)
                return new List<Vector3>();

            var boundaryCells = HnaRegionGraph.GetBoundaryCells();
            if (boundaryCells.Count < 3)
                return new List<Vector3>();

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillageNavMeshBake.ResolveValheimHumanoidAgentTypeID(),
                areaMask = NavMesh.AllAreas
            };

            // Step 1: Edge-snap each boundary cell to the nearest NavMesh edge.
            var waypoints = new List<Vector3>();
            int elevated = 0;
            foreach (var (_, cellCenter, outwardDir) in boundaryCells)
            {
                float halfCell = HnaRegionGraph.CellSize * 0.5f;
                var probeOrigin = cellCenter + outwardDir * halfCell;

                if (BoundaryGeometry.TryFindBestEdge(probeOrigin, cellCenter, filter, out var edgePos, out bool isElevated))
                {
                    waypoints.Add(edgePos);
                    if (isElevated) elevated++;
                }
            }

            if (waypoints.Count < 3)
            {
                Plugin.Log?.LogWarning(
                    $"[HnaBoundary] Edge snap produced only {waypoints.Count} waypoints, " +
                    $"falling back to raw boundary cells");
                return BoundaryGeometry.FallbackFromCells(boundaryCells.ConvertAll(c => (c.cellId, c.worldCenter)),
                    bedPosition);
            }

            // Step 1b: Deduplicate waypoints close in XZ
            int preDedupe = waypoints.Count;
            waypoints = BoundaryGeometry.DeduplicateByXZ(waypoints);
            int postDedupe = waypoints.Count;

            // Step 2: Sort clockwise
            BoundaryGeometry.SortClockwise(waypoints, bedPosition);
            int edgeCount = waypoints.Count;

            // Step 3: Light Chaikin smoothing
            waypoints = BoundaryGeometry.ChaikinSmooth(waypoints);

            // Step 4: Re-snap Chaikin points to NavMesh
            waypoints = BoundaryGeometry.NavMeshReSnap(waypoints, filter);
            int smoothedCount = waypoints.Count;

            // Step 5: RDP to prune grid noise
            waypoints = BoundaryGeometry.SimplifyRDP(waypoints, BoundaryGeometry.RdpEpsilon);
            int rdpCount = waypoints.Count;

            // Step 6: Prune sharp reflex angles
            int pruned = BoundaryGeometry.PruneSharpAngles(waypoints, filter);

            // Step 7: Final XZ dedup
            int preFinalDedup = waypoints.Count;
            waypoints = BoundaryGeometry.DeduplicateByXZ(waypoints);

            // Step 8: Enforce monotonic clockwise angle
            int preMonotonic = waypoints.Count;
            waypoints = BoundaryGeometry.EnforceMonotonicAngle(waypoints, bedPosition);
            int monotonicDrops = preMonotonic - waypoints.Count;

            // Step 9: Remove fully-isolated waypoints (both connections fail)
            int transitionDrops = RemoveUnpathableTransitions(waypoints, filter);

            // Step 10: Snap elevation transitions to nearest HNA link endpoints
            int linksInserted = InsertLinkWaypoints(waypoints, filter);

            Plugin.Log?.LogInfo(
                $"[HnaBoundary] {preDedupe} edge -> {postDedupe} dedup -> " +
                $"{edgeCount} sorted -> {smoothedCount} Chaikin -> {rdpCount} RDP -> " +
                $"{preFinalDedup} pruned -> {preMonotonic} dedup -> {waypoints.Count} final " +
                $"({pruned} sharp, {monotonicDrops} backtrack, {transitionDrops} unpathable, " +
                $"{linksInserted} links, {elevated} elevated, {HnaRegionGraph.RegionCount} HNA regions)");

            return waypoints;
        }

        #region Diagnostics

        /// <summary>
        /// Run only the edge-snap step (Step 1) and return raw results for offline testing.
        /// </summary>
        public static List<(Vector3 cellCenter, Vector3 outwardDir, Vector3? edgeSnapped, bool elevated)>
            DiagnosticEdgeSnap()
        {
            var results = new List<(Vector3, Vector3, Vector3?, bool)>();
            if (!HnaRegionGraph.IsAvailable) return results;

            var boundaryCells = HnaRegionGraph.GetBoundaryCells();
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillageNavMeshBake.ResolveValheimHumanoidAgentTypeID(),
                areaMask = NavMesh.AllAreas
            };

            foreach (var (_, cellCenter, outwardDir) in boundaryCells)
            {
                float halfCell = HnaRegionGraph.CellSize * 0.5f;
                var probeOrigin = cellCenter + outwardDir * halfCell;

                if (BoundaryGeometry.TryFindBestEdge(probeOrigin, cellCenter, filter, out var edgePos, out bool isElevated))
                    results.Add((cellCenter, outwardDir, edgePos, isElevated));
                else
                    results.Add((cellCenter, outwardDir, null, false));
            }

            return results;
        }

        #endregion

        #region Unpathable Transition Removal

        /// <summary>
        /// Walk the clockwise-sorted waypoint ring and check consecutive pathability.
        /// Only drop a waypoint if BOTH prev→curr AND curr→next paths fail (fully isolated).
        /// </summary>
        private static int RemoveUnpathableTransitions(List<Vector3> waypoints, NavMeshQueryFilter filter)
        {
            if (waypoints.Count <= 4) return 0;

            int dropped = 0;
            for (int i = waypoints.Count - 1; i >= 0 && waypoints.Count > 4; i--)
            {
                int prev = (i - 1 + waypoints.Count) % waypoints.Count;
                int next = (i + 1) % waypoints.Count;

                bool prevOk = BoundaryGeometry.IsNavMeshPathClear(waypoints[prev], waypoints[i], filter);
                bool nextOk = BoundaryGeometry.IsNavMeshPathClear(waypoints[i], waypoints[next], filter);

                if (prevOk || nextOk) continue;

                // Both directions fail — try re-snapping at the average Y of neighbors
                float neighborY = (waypoints[prev].y + waypoints[next].y) * 0.5f;
                var resnap = new Vector3(waypoints[i].x, neighborY, waypoints[i].z);
                if (NavMesh.SamplePosition(resnap, out NavMeshHit hit, BoundaryGeometry.NavMeshProbeRadius, filter))
                {
                    bool prevOk2 = BoundaryGeometry.IsNavMeshPathClear(waypoints[prev], hit.position, filter);
                    bool nextOk2 = BoundaryGeometry.IsNavMeshPathClear(hit.position, waypoints[next], filter);
                    if (prevOk2 || nextOk2)
                    {
                        waypoints[i] = hit.position;
                        continue;
                    }
                }

                if (BoundaryGeometry.IsNavMeshPathClear(waypoints[prev], waypoints[next], filter))
                {
                    waypoints.RemoveAt(i);
                    dropped++;
                }
            }

            return dropped;
        }

        #endregion

        #region Link Snapping

        /// <summary>Minimum height difference between consecutive waypoints to trigger link insertion.</summary>
        private const float LinkElevationThreshold = 2f;

        /// <summary>Max XZ distance from a waypoint midpoint to consider an HNA link relevant.</summary>
        private const float LinkSearchRadius = 10f;

        /// <summary>
        /// For consecutive waypoint pairs with significant elevation change, find the nearest
        /// HNA stair/door link and insert its start/end positions between the waypoints.
        /// </summary>
        private static int InsertLinkWaypoints(List<Vector3> waypoints, NavMeshQueryFilter filter)
        {
            var links = HnaRegionGraph.GetAllLinks();
            if (links == null || links.Count == 0) return 0;

            int inserted = 0;
            for (int i = 0; i < waypoints.Count; i++)
            {
                int next = (i + 1) % waypoints.Count;
                float dy = Mathf.Abs(waypoints[next].y - waypoints[i].y);
                if (dy < LinkElevationThreshold) continue;

                var midpoint = (waypoints[i] + waypoints[next]) * 0.5f;
                HnaLink? best = null;
                float bestDist = float.MaxValue;

                foreach (var link in links)
                {
                    var linkMid = (link.PositionStart + link.PositionEnd) * 0.5f;
                    float xzDist = Vector2.Distance(
                        new Vector2(midpoint.x, midpoint.z),
                        new Vector2(linkMid.x, linkMid.z));

                    if (xzDist > LinkSearchRadius) continue;
                    if (xzDist < bestDist)
                    {
                        bestDist = xzDist;
                        best = link;
                    }
                }

                if (best == null) continue;

                var linkVal = best.Value;
                Vector3 linkA, linkB;
                if (waypoints[i].y > waypoints[next].y)
                {
                    linkA = linkVal.PositionStart.y > linkVal.PositionEnd.y
                        ? linkVal.PositionStart : linkVal.PositionEnd;
                    linkB = linkVal.PositionStart.y > linkVal.PositionEnd.y
                        ? linkVal.PositionEnd : linkVal.PositionStart;
                }
                else
                {
                    linkA = linkVal.PositionStart.y < linkVal.PositionEnd.y
                        ? linkVal.PositionStart : linkVal.PositionEnd;
                    linkB = linkVal.PositionStart.y < linkVal.PositionEnd.y
                        ? linkVal.PositionEnd : linkVal.PositionStart;
                }

                if (!BoundaryGeometry.IsNavMeshPathClear(waypoints[i], linkA, filter)) continue;
                if (!BoundaryGeometry.IsNavMeshPathClear(linkB, waypoints[next], filter)) continue;

                waypoints.Insert(i + 1, linkA);
                waypoints.Insert(i + 2, linkB);
                inserted += 2;
                i += 2;
            }

            return inserted;
        }

        #endregion
    }
}
