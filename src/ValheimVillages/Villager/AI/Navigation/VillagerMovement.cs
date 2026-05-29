using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Navigation helpers for VillagerAI.
    /// </summary>
    public static class VillagerMovement
    {
        /// <summary>
        ///     Single source of truth for "is the villager close enough to this
        ///     position to be considered there?" 3D distance against an
        ///     explicit threshold. Callers pass
        ///     <see cref="VillagerSettings.ArrivalThreshold"/> for final
        ///     arrival at stations / chests (generous, ~2m) and
        ///     <see cref="VillagerSettings.PathNodePopThreshold"/> for
        ///     intermediate path-node popping (tight, ~0.5m). Same shape, two
        ///     distinct semantics — using the generous arrival radius for
        ///     intermediate pops eats routing corners and strands the agent
        ///     against obstacles those corners were supposed to detour around.
        /// </summary>
        public static bool IsAtPosition(Vector3 villagerPos, Vector3 target, float threshold)
        {
            return Vector3.Distance(villagerPos, target) < threshold;
        }

        /// <summary>
        ///     Attempt to compute a COMPLETE path against the villager NavMesh
        ///     (slot 31) that ALSO lies entirely on the HNA region graph.
        ///     This is the load-bearing invariant that prevents villagers
        ///     walking into piece-layer colliders the HNA prune correctly
        ///     excluded but Unity's voxelizer re-introduced as sliver/orphan
        ///     polygons. Returns true only when all of:
        ///     <list type="bullet">
        ///       <item>RegionGraph A* over the LookupGrid resolves both endpoints to cells and finds a connecting cell sequence,</item>
        ///       <item>each per-segment <c>NavMesh.CalculatePath</c> between consecutive corridor waypoints returns <see cref="NavMeshPathStatus.PathComplete"/>,</item>
        ///       <item>every returned corner satisfies <see cref="RegionGraph.PointToRegionId"/> ≠ null.</item>
        ///     </list>
        ///     Failure → outPath empty, caller enters PathUnreachable recovery.
        ///     No silent fallback to unconstrained CalculatePath: a
        ///     same-shape-but-degraded path here would mask the bake/HNA
        ///     drift the corridor planner exists to catch.
        ///     <para>If no RegionGraph is available for either endpoint
        ///     (graph not yet built, villager outside any registered
        ///     village), the planner falls through to the legacy
        ///     unconstrained NavMesh.CalculatePath — pre-graph behavior is
        ///     preserved for villagers who haven't entered a partition yet.
        ///     The fallthrough is logged so cases that *should* be
        ///     corridor-planned but slip through are visible.</para>
        /// </summary>
        public static bool TryFindCompletePath(Vector3 start, Vector3 end, List<Vector3> outPath)
        {
            outPath?.Clear();

            if (!VillagerAgentType.IsRegistered) return false;

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            // Pick the HNA graph that covers either endpoint. Prefer the
            // start-side graph (where the villager actually lives) so
            // cross-village trips are planned in the source village's
            // frame; the corridor will hand off to NavMesh-only routing
            // for the inter-village segment naturally if the end cell
            // resolves in a different graph.
            var graph = ResolvePlanningGraph(start, end);
            if (graph == null)
            {
                // Pre-graph villagers: pre-existing behavior, single
                // unconstrained query. Surface that we slipped through.
                DebugLog.Event("PathPlan", "fallthrough_no_graph",
                    ("from_x", start.x), ("from_y", start.y), ("from_z", start.z),
                    ("to_x", end.x), ("to_y", end.y), ("to_z", end.z));
                return TryFindUnconstrainedPath(start, end, filter, outPath);
            }

            return TryFindCorridorPath(graph, filter, start, end, outPath);
        }

        /// <summary>
        ///     Legacy single-shot CalculatePath retained for the
        ///     no-graph-available fallthrough. Behaviour matches the
        ///     pre-corridor implementation exactly.
        /// </summary>
        private static bool TryFindUnconstrainedPath(
            Vector3 start, Vector3 end, NavMeshQueryFilter filter, List<Vector3> outPath)
        {
            if (!NavMesh.SamplePosition(start, out var startHit, 1f, filter)) return false;
            if (!NavMesh.SamplePosition(end, out var endHit, 1f, filter)) return false;
            var navPath = new NavMeshPath();
            if (!NavMesh.CalculatePath(startHit.position, endHit.position, filter, navPath))
                return false;
            if (navPath.status != NavMeshPathStatus.PathComplete) return false;
            if (outPath != null)
            {
                var corners = navPath.corners;
                for (var i = 0; i < corners.Length; i++) outPath.Add(corners[i]);
            }
            return true;
        }

        /// <summary>
        ///     HNA-constrained planner: A* over <paramref name="graph"/>'s
        ///     cells → string-pull → per-segment NavMesh.CalculatePath +
        ///     per-corner PointToRegionId validation. Fail-closed on every
        ///     failure mode — never returns a degraded path.
        /// </summary>
        private static bool TryFindCorridorPath(
            RegionGraph graph, NavMeshQueryFilter filter,
            Vector3 start, Vector3 end, List<Vector3> outPath)
        {
            var cells = RegionGraphAStar.PlanCells(graph, start, end, out var failReason);
            if (cells == null || cells.Count == 0)
            {
                // Throttle by (failReason, from-cell, to-cell): identical
                // repeated failures (Farmer tries the same target 191k
                // times) collapse to one event instead of flooding.
                var sc = CellCoord.FromWorld(start);
                var gc = CellCoord.FromWorld(end);
                DebugLog.Throttled(
                    $"pathplan_unreach_{(int)failReason}_{sc.Gx}_{sc.Gz}_{sc.Hb}_{gc.Gx}_{gc.Gz}_{gc.Hb}",
                    "PathPlan", "astar_unreachable",
                    ("why", failReason.ToString()),
                    ("from_cell", $"({sc.Gx},{sc.Gz},{sc.Hb})"),
                    ("to_cell", $"({gc.Gx},{gc.Gz},{gc.Hb})"),
                    ("from_x", start.x), ("from_y", start.y), ("from_z", start.z),
                    ("to_x", end.x), ("to_y", end.y), ("to_z", end.z));
                return false;
            }

            // VillagerAgentType.TryGetRadius isn't a thing today; the
            // settings table only exposes slope/climb. Use a conservative
            // 0.4m matching the bake-buffer-inflated agent body — this
            // is the same radius the NavMeshLinkPlacer capsule check uses.
            const float agentRadius = 0.4f;
            var waypoints = RegionGraphWaypoints.StringPull(graph, cells, agentRadius);

            // Single waypoint (start == goal cell) — trivially reached.
            if (waypoints.Count <= 1)
            {
                outPath?.Add(waypoints.Count == 1 ? waypoints[0] : end);
                DebugLog.Event("PathPlan", "trivial",
                    ("cells", cells.Count), ("waypoints", waypoints.Count));
                return true;
            }

            // Per-segment NavMesh validation. Each consecutive pair must:
            //   1. Snap both endpoints onto the agent NavMesh,
            //   2. NavMesh.CalculatePath returns PathComplete,
            //   3. Every corner of the returned path resolves under
            //      graph.PointToRegionId (off-graph corners would mean
            //      NavMesh routed the segment through a sliver polygon
            //      the HNA prune correctly excluded — exactly the case
            //      we're catching).
            // Concatenate corners across segments; dedup the seam so each
            // intermediate waypoint isn't double-added.
            var totalCorners = 0;
            for (var i = 0; i < waypoints.Count - 1; i++)
            {
                var segStart = waypoints[i];
                var segEnd = waypoints[i + 1];
                if (!NavMesh.SamplePosition(segStart, out var sHit, 2f, filter))
                {
                    DebugLog.Event("PathPlan", "seg_snap_fail",
                        ("seg", i), ("side", "start"),
                        ("x", segStart.x), ("y", segStart.y), ("z", segStart.z));
                    outPath?.Clear();
                    return false;
                }
                if (!NavMesh.SamplePosition(segEnd, out var eHit, 2f, filter))
                {
                    DebugLog.Event("PathPlan", "seg_snap_fail",
                        ("seg", i), ("side", "end"),
                        ("x", segEnd.x), ("y", segEnd.y), ("z", segEnd.z));
                    outPath?.Clear();
                    return false;
                }

                var segPath = new NavMeshPath();
                if (!NavMesh.CalculatePath(sHit.position, eHit.position, filter, segPath) ||
                    segPath.status != NavMeshPathStatus.PathComplete)
                {
                    DebugLog.Event("PathPlan", "seg_navmesh_incomplete",
                        ("seg", i), ("status", segPath.status.ToString()),
                        ("from_x", sHit.position.x), ("from_z", sHit.position.z),
                        ("to_x", eHit.position.x), ("to_z", eHit.position.z));
                    outPath?.Clear();
                    return false;
                }

                var corners = segPath.corners;
                for (var k = 0; k < corners.Length; k++)
                {
                    var c = corners[k];
                    // Off-graph corner: the planner cut through a sliver
                    // polygon HNA correctly excluded. Fail closed — no
                    // partial path returned.
                    if (string.IsNullOrEmpty(graph.PointToRegionId(c)))
                    {
                        DebugLog.Event("PathPlan", "off_graph_corner",
                            ("seg", i), ("corner", k),
                            ("x", c.x), ("y", c.y), ("z", c.z));
                        outPath?.Clear();
                        return false;
                    }

                    // Skip the seam corner: every segment except the
                    // first should drop its first corner (== prior
                    // segment's last corner).
                    if (i > 0 && k == 0) continue;
                    outPath?.Add(c);
                    totalCorners++;
                }
            }

            DebugLog.Event("PathPlan", "ok",
                ("cells", cells.Count),
                ("waypoints", waypoints.Count),
                ("corners", totalCorners));
            return true;
        }

        /// <summary>
        ///     Pick the RegionGraph that should plan a path from
        ///     <paramref name="start"/> to <paramref name="end"/>. Preference:
        ///     graph whose PointToRegionId resolves the start; failing that,
        ///     graph whose PointToRegionId resolves the end; failing that,
        ///     null (caller falls through to unconstrained CalculatePath).
        ///     The asymmetry matters because villagers are typically inside
        ///     a village when they pathfind out toward something, so the
        ///     start-side graph is the relevant authority.
        /// </summary>
        private static RegionGraph ResolvePlanningGraph(Vector3 start, Vector3 end)
        {
            if (!RegionGraph.IsAnyAvailable) return null;
            RegionGraph endSideGraph = null;
            foreach (var g in RegionGraph.GetAll())
            {
                if (!string.IsNullOrEmpty(g.PointToRegionId(start)))
                    return g;
                if (endSideGraph == null && !string.IsNullOrEmpty(g.PointToRegionId(end)))
                    endSideGraph = g;
            }

            return endSideGraph;
        }

        /// <summary>
        ///     Snap a world position to the walkable surface below it, if close.
        ///     Avoids snapping to roofs/ceilings in multi-story buildings.
        ///  </summary>
        public static Vector3 GetWalkableDestination(Vector3 worldPosition)
        {
            if (ZoneSystem.instance == null) return worldPosition;
            if (!ZoneSystem.instance.GetSolidHeight(worldPosition, out var h))
                return worldPosition;
            if (Mathf.Abs(h - worldPosition.y) < 2f)
                return new Vector3(worldPosition.x, h, worldPosition.z);
            return worldPosition;
        }

        /// <summary>
        ///     Find the nearest reachable point on the villager NavMesh within
        ///     <paramref name="maxRadius"/> of <paramref name="target"/>. Use this for stations
        ///     whose own transform sits on a non-walkable obstacle (e.g. Smelter, CharcoalKiln):
        ///     the villager can't path TO the obstacle, but they CAN path to a walkable cell
        ///     adjacent to it, which is close enough for RPC interaction (RPC_AddOre, RPC_AddFuel, …).
        ///     Returns true and writes the approach point on success; returns false (and leaves
        ///     <paramref name="approachPoint"/> = target) when nothing walkable is found within radius.
        /// </summary>
        public static bool TryFindReachableApproach(Vector3 target, float maxRadius, out Vector3 approachPoint)
        {
            approachPoint = target;
            if (!VillagerAgentType.IsRegistered) return false;

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            if (!NavMesh.SamplePosition(target, out var hit, maxRadius, filter)) return false;
            approachPoint = hit.position;
            return true;
        }

        /// <summary>
        ///     Resolve a navigable approach point for a target whose own centroid may sit on a
        ///     non-walkable obstacle (smelter on a foundation, chest on a shelf, etc). Probes the
        ///     centroid first, then expanding compass-offset rings, sampling the NavMesh at each
        ///     and validating with a complete-path check from <paramref name="pathSource"/>.
        ///     <para>Pass an optional <paramref name="hullPredicate"/> that returns false for
        ///     candidates outside the village hull — used by station lookup to reject points on
        ///     the wrong side of an outer wall. Pass null when hull filtering isn't applicable
        ///     (e.g. container targets — no hull data available at the workflow layer).</para>
        ///     Returns true and writes the approach on success. Returns false (writes
        ///     <paramref name="approach"/> = target) when no probe in the ring is both walkable
        ///     AND reachable from <paramref name="pathSource"/>.
        /// </summary>
        public static bool TryResolveApproach(
            Vector3 target,
            Vector3 pathSource,
            System.Func<Vector3, bool> hullPredicate,
            out Vector3 approach)
        {
            approach = target;
            var probes = s_probeOffsets;
            var pathBuffer = new List<Vector3>();
            for (var i = 0; i < probes.Length; i++)
            {
                var probe = target + probes[i];
                if (!TryFindReachableApproach(probe, ApproachProbeRadius, out var hit)) continue;
                if (hullPredicate != null && !hullPredicate(hit)) continue;
                if (!TryFindCompletePath(pathSource, hit, pathBuffer)) continue;
                approach = hit;
                return true;
            }
            return false;
        }

        private const float ApproachProbeRadius = 1.5f;

        private static readonly Vector3[] s_probeOffsets = BuildProbeOffsets();

        private static Vector3[] BuildProbeOffsets()
        {
            var list = new List<Vector3> { Vector3.zero };
            float[] rings = { 2f, 4f };
            foreach (var r in rings)
            {
                list.Add(new Vector3(0f, 0f, r));
                list.Add(new Vector3(r, 0f, 0f));
                list.Add(new Vector3(0f, 0f, -r));
                list.Add(new Vector3(-r, 0f, 0f));
                list.Add(new Vector3(r, 0f, r));
                list.Add(new Vector3(r, 0f, -r));
                list.Add(new Vector3(-r, 0f, -r));
                list.Add(new Vector3(-r, 0f, r));
            }
            return list.ToArray();
        }
    }
}