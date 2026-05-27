using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Behaviors.Patrol
{
    /// <summary>
    ///     Computes patrol waypoints from HNA region graph boundary cells.
    ///     Pipeline: edge snap -> clockwise sort -> Chaikin smooth -> NavMesh re-snap -> RDP -> sharp angle prune.
    ///     Pure geometry operations are delegated to <see cref="BoundaryGeometry" />.
    /// </summary>
    public static class BoundaryMapper
    {
        /// <summary>
        ///     One waypoint per HNA boundary cell, sorted clockwise. No filtering.
        /// </summary>
        public static List<Vector3> ComputeBoundaryWaypoints(Vector3 bedPosition)
        {
            var villageKey = RegionGraph.VillageKey(bedPosition);
            var graph = RegionGraph.Get(villageKey);
            return ComputeBoundaryWaypoints(graph, bedPosition);
        }

        /// <summary>
        ///     Overload that takes a <see cref="RegionGraph" /> directly so callers without a
        ///     bed position (e.g. <c>RegionPartitionHandler</c>) can build waypoints. Derives
        ///     the polygon center from the graph's boundary cells.
        /// </summary>
        public static List<Vector3> ComputeBoundaryWaypoints(RegionGraph graph)
        {
            if (graph == null || !graph.IsAvailable) return new List<Vector3>();

            var boundaryCells = graph.GetBoundaryCells();
            if (boundaryCells.Count < 3) return new List<Vector3>();

            // Centroid of boundary cells — the geometric center of the polygon,
            // matching the role bedPosition plays in the legacy overload.
            Vector3 sum = Vector3.zero;
            foreach (var (_, c, _) in boundaryCells) sum += c;
            var center = sum / boundaryCells.Count;
            return ComputeBoundaryWaypoints(graph, center);
        }

        private static List<Vector3> ComputeBoundaryWaypoints(RegionGraph graph, Vector3 center)
        {
            if (graph == null || !graph.IsAvailable) return new List<Vector3>();

            var boundaryCells = graph.GetBoundaryCells();
            if (boundaryCells.Count < 3)
                return new List<Vector3>();

            var waypoints = new List<Vector3>(boundaryCells.Count);
            foreach (var (_, cellCenter, _) in boundaryCells)
                waypoints.Add(cellCenter);

            LogYRange("raw centroids", waypoints);

            BoundaryGeometry.SortClockwise(waypoints, center);

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.ResolveValheimHumanoidAgentTypeID(),
                areaMask = NavMesh.AllAreas,
            };
            var preCount = waypoints.Count;
            waypoints = WaypointRelaxation.Refine(waypoints, center, filter, graph);
            LogYRange("after Refine", waypoints);

            var linkInserted = InsertLinkWaypoints(waypoints, filter, graph);
            LogYRange("after InsertLinks", waypoints);

            var unpathableDropped = RemoveUnpathableTransitions(waypoints, filter);
            LogYRange("after RemoveUnpathable", waypoints);

            Plugin.Log?.LogInfo(
                $"[Boundary] {preCount} boundary cells -> {waypoints.Count} waypoints " +
                $"({graph.RegionCount} HNA regions, refined, " +
                $"links={linkInserted}, dropped={unpathableDropped})");

            return waypoints;
        }

        private static void LogYRange(string stage, List<Vector3> waypoints)
        {
            if (waypoints.Count == 0) return;
            float yMin = float.MaxValue, yMax = float.MinValue;
            var lowCount = 0;
            var worstPt = Vector3.zero;
            var worstDelta = 0f;
            foreach (var p in waypoints)
            {
                if (p.y < yMin) yMin = p.y;
                if (p.y > yMax) yMax = p.y;
                var terrainY = ZoneSystem.instance != null
                    ? ZoneSystem.instance.GetGroundHeight(new Vector3(p.x, 0f, p.z))
                    : 0f;
                var delta = terrainY - p.y;
                if (delta > 1.5f)
                {
                    lowCount++;
                    if (delta > worstDelta)
                    {
                        worstDelta = delta;
                        worstPt = p;
                    }
                }
            }

            var extra = lowCount > 0
                ? $", {lowCount} below terrain (worst: ({worstPt.x:F1},{worstPt.y:F1},{worstPt.z:F1}) {worstDelta:F1}m under)"
                : "";
            Plugin.Log?.LogInfo($"[Boundary] {stage}: {waypoints.Count} pts, Y=[{yMin:F1},{yMax:F1}]{extra}");
        }

        #region Diagnostics

        /// <summary>
        ///     Run only the edge-snap step (Step 1) and return raw results for offline testing.
        /// </summary>
        public static List<(Vector3 cellCenter, Vector3 outwardDir, Vector3? edgeSnapped, bool elevated)>
            DiagnosticEdgeSnap(Vector3 bedPosition)
        {
            var results = new List<(Vector3, Vector3, Vector3?, bool)>();
            var graph = RegionGraph.Get(RegionGraph.VillageKey(bedPosition));
            if (graph == null || !graph.IsAvailable) return results;

            var boundaryCells = graph.GetBoundaryCells();
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.ResolveValheimHumanoidAgentTypeID(),
                areaMask = NavMesh.AllAreas,
            };

            foreach (var (_, cellCenter, outwardDir) in boundaryCells)
            {
                var halfCell = RegionGraph.CellSize * 0.5f;
                var probeOrigin = cellCenter + outwardDir * halfCell;

                if (BoundaryGeometry.TryFindBestEdge(probeOrigin, cellCenter, filter, out var edgePos,
                        out var isElevated))
                    results.Add((cellCenter, outwardDir, edgePos, isElevated));
                else
                    results.Add((cellCenter, outwardDir, null, false));
            }

            return results;
        }

        #endregion

        #region Unpathable Transition Removal

        /// <summary>
        ///     Walk the clockwise-sorted waypoint ring and check consecutive pathability.
        ///     Only drop a waypoint if BOTH prev→curr AND curr→next paths fail (fully isolated).
        /// </summary>
        private static int RemoveUnpathableTransitions(List<Vector3> waypoints, NavMeshQueryFilter filter)
        {
            if (waypoints.Count <= 4) return 0;

            var dropped = 0;
            for (var i = waypoints.Count - 1; i >= 0 && waypoints.Count > 4; i--)
            {
                var prev = (i - 1 + waypoints.Count) % waypoints.Count;
                var next = (i + 1) % waypoints.Count;

                var prevOk = BoundaryGeometry.IsNavMeshPathClear(waypoints[prev], waypoints[i], filter);
                var nextOk = BoundaryGeometry.IsNavMeshPathClear(waypoints[i], waypoints[next], filter);

                if (prevOk || nextOk) continue;

                // Both directions fail. We do NOT silently re-snap at neighbor-average Y —
                // that would mask a real disagreement between the HNA region graph Y
                // (which placed this waypoint) and what NavMesh considers walkable.
                // Surface the disagreement loudly and drop the waypoint when the
                // ring can stay connected via a direct prev→next path.
                var navY = float.NaN;
                if (NavMesh.SamplePosition(waypoints[i], out var navHit,
                        BoundaryGeometry.NavMeshProbeRadius, filter))
                    navY = navHit.position.y;
                var bakeY = RegionGraph.GetSolidHeightAt(waypoints[i].x, waypoints[i].z, out var by)
                    ? by
                    : float.NaN;

                if (BoundaryGeometry.IsNavMeshPathClear(waypoints[prev], waypoints[next], filter))
                {
                    Plugin.Log?.LogError(
                        "[BoundaryMapper] Unpathable waypoint at " +
                        $"({waypoints[i].x:F2}, {waypoints[i].y:F2}, {waypoints[i].z:F2}): " +
                        "both prev→curr and curr→next NavMesh paths blocked. " +
                        $"NavMesh Y={navY:F2}, bake Y={bakeY:F2}, " +
                        $"prev Y={waypoints[prev].y:F2}, next Y={waypoints[next].y:F2}. " +
                        "Dropping waypoint (no silent neighbor-average re-snap).");
                    waypoints.RemoveAt(i);
                    dropped++;
                }
                else
                {
                    Plugin.Log?.LogError(
                        "[BoundaryMapper] Isolated waypoint at " +
                        $"({waypoints[i].x:F2}, {waypoints[i].y:F2}, {waypoints[i].z:F2}): " +
                        "both prev→curr and curr→next NavMesh paths blocked AND " +
                        "prev→next direct bridge also blocked. " +
                        $"NavMesh Y={navY:F2}, bake Y={bakeY:F2}, " +
                        $"prev Y={waypoints[prev].y:F2}, next Y={waypoints[next].y:F2}. " +
                        "Keeping waypoint to preserve ring connectivity; patrol may snag here.");
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

        /// <summary>SphereCast radius for wall detection between consecutive waypoints.</summary>
        private const float WallCheckSphereRadius = 0.35f;

        /// <summary>Height above the higher waypoint Y to cast the wall-detection ray.</summary>
        private const float WallCheckHeight = 1f;

        private static readonly int WallCheckMask =
            LayerMask.GetMask("Default", "static_solid", "terrain", "piece");

        /// <summary>
        ///     For consecutive waypoint pairs separated by elevation change OR a wall,
        ///     find the nearest HNA link (stair/door) and insert its endpoints as
        ///     intermediate waypoints so the path routes through the link.
        /// </summary>
        private static int InsertLinkWaypoints(List<Vector3> waypoints, NavMeshQueryFilter filter,
            RegionGraph graph)
        {
            var links = graph.GetAllLinks();
            if (links == null || links.Count == 0) return 0;

            var mask = WallCheckMask != 0 ? WallCheckMask : ~0;
            var inserted = 0;

            for (var i = 0; i < waypoints.Count; i++)
            {
                var next = (i + 1) % waypoints.Count;
                var dy = Mathf.Abs(waypoints[next].y - waypoints[i].y);

                var elevationTrigger = dy >= LinkElevationThreshold;
                var wallTrigger = !elevationTrigger && IsWallBlocking(
                    waypoints[i], waypoints[next], mask);

                if (!elevationTrigger && !wallTrigger) continue;

                var midpoint = (waypoints[i] + waypoints[next]) * 0.5f;
                RegionLink? best = null;
                var bestDist = float.MaxValue;

                foreach (var link in links)
                {
                    if (wallTrigger && link.LinkType != RegionLinkType.Door)
                        continue;

                    var linkMid = (link.PositionStart + link.PositionEnd) * 0.5f;
                    var xzDist = Vector2.Distance(
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

                if (elevationTrigger)
                {
                    if (waypoints[i].y > waypoints[next].y)
                    {
                        linkA = linkVal.PositionStart.y > linkVal.PositionEnd.y
                            ? linkVal.PositionStart
                            : linkVal.PositionEnd;
                        linkB = linkVal.PositionStart.y > linkVal.PositionEnd.y
                            ? linkVal.PositionEnd
                            : linkVal.PositionStart;
                    }
                    else
                    {
                        linkA = linkVal.PositionStart.y < linkVal.PositionEnd.y
                            ? linkVal.PositionStart
                            : linkVal.PositionEnd;
                        linkB = linkVal.PositionStart.y < linkVal.PositionEnd.y
                            ? linkVal.PositionEnd
                            : linkVal.PositionStart;
                    }

                    if (!BoundaryGeometry.IsNavMeshPathClear(waypoints[i], linkA, filter)) continue;
                    if (!BoundaryGeometry.IsNavMeshPathClear(linkB, waypoints[next], filter)) continue;
                }
                else
                {
                    var dStartToI = Vector3.Distance(linkVal.PositionStart, waypoints[i]);
                    var dEndToI = Vector3.Distance(linkVal.PositionEnd, waypoints[i]);
                    linkA = dStartToI < dEndToI ? linkVal.PositionStart : linkVal.PositionEnd;
                    linkB = dStartToI < dEndToI ? linkVal.PositionEnd : linkVal.PositionStart;

                    if (IsWallBlocking(waypoints[i], linkA, mask)) continue;
                    if (IsWallBlocking(linkB, waypoints[next], mask)) continue;
                }

                waypoints.Insert(i + 1, linkA);
                waypoints.Insert(i + 2, linkB);
                inserted += 2;
                i += 2;
            }

            return inserted;
        }

        /// <summary>
        ///     Physics SphereCast to detect walls between two points.
        ///     Mirrors the per-edge capsule check RegionBuilder uses when
        ///     breaking terrain adjacency at walls (no BFS involved).
        /// </summary>
        private static bool IsWallBlocking(Vector3 from, Vector3 to, int wallMask)
        {
            var wallY = Mathf.Max(from.y, to.y) + WallCheckHeight;
            var wFrom = new Vector3(from.x, wallY, from.z);
            var wTo = new Vector3(to.x, wallY, to.z);
            var wDir = wTo - wFrom;
            var wDist = wDir.magnitude;
            if (wDist < 0.1f) return false;
            return Physics.SphereCast(wFrom, WallCheckSphereRadius, wDir / wDist,
                out _, wDist, wallMask, QueryTriggerInteraction.Ignore);
        }

        #endregion
    }
}