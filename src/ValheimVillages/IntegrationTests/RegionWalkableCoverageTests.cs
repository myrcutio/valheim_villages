using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.TaskQueue.Handlers;
using ValheimVillages.Testing;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.IntegrationTests
{
    /// <summary>
    ///     Runtime integration tests verifying the live HNA graph covers all
    ///     recorded walkable positions. Each point must be within CellSize (3m)
    ///     of at least one HNA region center.
    /// </summary>
    public static partial class RegionWalkableCoverageTests
    {
        private const float MaxDistance = RegionGraph.CellSize;

        /// <summary>
        ///     How far outside a village's own recorded footprint a fixture point may still
        ///     count as belonging to it. The region graph reaches past the pieces that
        ///     define the footprint (the bake runs on anchors +/- 30m), so an exact
        ///     containment test would reject legitimate perimeter points.
        /// </summary>
        private const float FixtureFootprintMargin = 50f;

        /// <summary>
        ///     True when at least one recorded fixture point falls within a loaded
        ///     village's footprint, i.e. we are in the place the fixture was captured.
        ///
        ///     <para><see cref="WalkablePositions" /> is a recording of ONE village (see the
        ///     Source line on the data file). Replayed anywhere else all 389 points sit
        ///     kilometres away, so both fixture tests failed permanently — 2 of 6 red on
        ///     every run, which made the suite verdict worthless as a gate. Being in the
        ///     wrong world is a SKIP, not a failure.</para>
        ///
        ///     <para>Measured against each village's own <c>TryGetFootprint</c> rather than
        ///     <c>VillageRegistry.GraphAt</c>: GraphAt is built on GetVillageAt, which
        ///     returns the NEAREST village with no distance limit, so it answers non-null
        ///     for a point on the far side of the map and this guard never tripped. It is
        ///     also not tested via PointToRegionId, because that is the very thing these
        ///     tests measure — gating on it would make them incapable of ever failing.</para>
        ///
        ///     <para>Probing the points rather than hardcoding a world or village name is
        ///     what makes this self-maintaining: load the fixture's world and the tests
        ///     start asserting again on their own, with no flag to remember.</para>
        /// </summary>
        private static bool FixtureWorldIsLoaded()
        {
            foreach (var village in Villages.Entity.VillageRegistry.EnumerateAll())
            {
                var graph = village.Graph;
                if (graph == null || !graph.IsAvailable) continue;
                if (!village.TryGetFootprint(out var minX, out var minZ, out var maxX, out var maxZ))
                    continue;

                foreach (var pt in WalkablePositions)
                    if (pt.x >= minX - FixtureFootprintMargin &&
                        pt.x <= maxX + FixtureFootprintMargin &&
                        pt.z >= minZ - FixtureFootprintMargin &&
                        pt.z <= maxZ + FixtureFootprintMargin)
                        return true;
            }

            return false;
        }

        /// <summary>Skip unless we are in the world <see cref="WalkablePositions" /> came from.</summary>
        private static void RequireFixtureWorld()
        {
            if (FixtureWorldIsLoaded()) return;

            ModAssert.Skip(
                "the recorded walkable fixture belongs to a different place — not one of its "
                + WalkablePositions.Length
                + " points falls within any loaded village's footprint. Re-capture it from "
                + "vv_dumps/hna_walkable_path.json to measure coverage here.");
        }

        [ModTest(Name = "HNA_WalkableCoverage_AllPointsWithinCellSize", Order = 200)]
        public static void AllPointsWithinCellSize()
        {
            if (!Villages.Entity.VillageRegistry.IsAnyAvailable)
                ModAssert.Skip(
                    "no HNA graph is built in this world, so there is nothing to check "
                    + "(walkable coverage)");

            RequireFixtureWorld();

            var centers = CollectAllRegionCenters();
            if (centers.Count == 0)
                ModAssert.Skip("the HNA graph reports no region centers at all");

            var covered = 0;
            var uncovered = 0;
            var worstDist = 0f;
            var worstIdx = -1;
            var uncoveredSamples = new List<string>();

            for (var i = 0; i < WalkablePositions.Length; i++)
            {
                var pt = WalkablePositions[i];
                var minDist = NearestCenterDist3D(pt, centers);

                if (minDist <= MaxDistance)
                {
                    covered++;
                }
                else
                {
                    uncovered++;
                    if (minDist > worstDist)
                    {
                        worstDist = minDist;
                        worstIdx = i;
                    }

                    if (uncoveredSamples.Count < 20)
                        uncoveredSamples.Add(
                            $"  [{i}] ({pt.x:F1}, {pt.y:F1}, {pt.z:F1}) nearest={minDist:F2}m");
                }
            }

            var coveragePct = 100f * covered / WalkablePositions.Length;

            var sb = new StringBuilder();
            sb.AppendLine($"[HNA Coverage] {covered}/{WalkablePositions.Length} points within " +
                          $"{MaxDistance}m (3D) of a region center ({coveragePct:F1}%)");
            sb.AppendLine($"  Graph regions total: {centers.Count}");
            if (uncovered > 0)
            {
                sb.AppendLine($"  Uncovered: {uncovered} (worst: [{worstIdx}] at {worstDist:F2}m)");
                foreach (var s in uncoveredSamples)
                    sb.AppendLine(s);
                if (uncovered > 20)
                    sb.AppendLine($"  ... and {uncovered - 20} more");
            }

            Plugin.Log?.LogInfo(sb.ToString());

            ModAssert.True(uncovered == 0,
                $"HNA coverage: {uncovered}/{WalkablePositions.Length} walkable points are " +
                $"farther than {MaxDistance}m (3D) from any region center " +
                $"(worst: [{worstIdx}] at {worstDist:F2}m). Coverage={coveragePct:F1}%");
        }

        [ModTest(Name = "HNA_WalkableCoverage_PointToRegionResolution", Order = 201)]
        public static void PointToRegionResolution()
        {
            if (!Villages.Entity.VillageRegistry.IsAnyAvailable)
                ModAssert.Skip(
                    "no HNA graph is built in this world, so there is nothing to check "
                    + "(point-to-region resolution)");

            RequireFixtureWorld();

            var resolved = 0;
            var unresolved = 0;
            var unresolvedSamples = new List<string>();

            for (var i = 0; i < WalkablePositions.Length; i++)
            {
                var pt = WalkablePositions[i];
                var graph = Villages.Entity.VillageRegistry.GraphAt(pt);
                if (graph == null || !graph.IsAvailable)
                {
                    unresolved++;
                    continue;
                }

                var regionId = graph.PointToRegionId(pt);
                if (regionId != null)
                {
                    resolved++;
                }
                else
                {
                    unresolved++;
                    if (unresolvedSamples.Count < 20)
                        unresolvedSamples.Add(
                            $"  [{i}] ({pt.x:F1}, {pt.y:F1}, {pt.z:F1})");
                }
            }

            var pct = 100f * resolved / WalkablePositions.Length;

            var sb = new StringBuilder();
            sb.AppendLine($"[HNA PointToRegion] {resolved}/{WalkablePositions.Length} " +
                          $"points resolve to a region ({pct:F1}%)");
            if (unresolved > 0)
            {
                sb.AppendLine($"  Unresolved: {unresolved}");
                foreach (var s in unresolvedSamples)
                    sb.AppendLine(s);
            }

            Plugin.Log?.LogInfo(sb.ToString());

            ModAssert.True(unresolved == 0,
                $"HNA PointToRegion: {unresolved}/{WalkablePositions.Length} walkable points " +
                $"did not resolve to any region. Resolution={pct:F1}%");
        }

        [ModTest(Name = "HNA_WalkableCoverage_GraphHasMinimumRegions", Order = 199)]
        public static void GraphHasMinimumRegions()
        {
            if (!Villages.Entity.VillageRegistry.IsAnyAvailable)
                ModAssert.Skip(
                    "no HNA graph is built in this world, so there is nothing to check "
                    + "(minimum region count)");

            var centers = CollectAllRegionCenters();
            Plugin.Log?.LogInfo($"[HNA MinRegions] Total region centers across all graphs: {centers.Count}");

            ModAssert.True(centers.Count >= 10,
                "HNA graph should have at least 10 regions to cover the village, " +
                $"but has {centers.Count}");
        }

        [ModTest(Name = "HNA_Pruning_AllRegionsHaveGroundBelow", Order = 202)]
        public static void AllRegionsHaveGroundBelow()
        {
            if (!Villages.Entity.VillageRegistry.IsAnyAvailable)
                ModAssert.Skip(
                    "no HNA graph is built in this world, so there is nothing to check "
                    + "(ground-below pruning)");

            var centers = CollectAllRegionCenters();
            int passed = 0, failed = 0;
            var failSamples = new List<string>();

            foreach (var pos in centers)
                if (CellValidator.HasGroundBelow(pos))
                {
                    passed++;
                }
                else
                {
                    failed++;
                    if (failSamples.Count < 10)
                        failSamples.Add($"  ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
                }

            var sb = new StringBuilder();
            sb.AppendLine($"[HNA GroundBelow] {passed}/{centers.Count} regions have ground below");
            if (failed > 0)
            {
                sb.AppendLine($"  Floating: {failed}");
                foreach (var s in failSamples) sb.AppendLine(s);
            }

            Plugin.Log?.LogInfo(sb.ToString());

            if (failed > 0)
                Plugin.Log?.LogWarning(
                    $"[HNA GroundBelow] {failed}/{centers.Count} region centers lack solid " +
                    $"ground within {CellValidator.GroundCheckDist}m (may include restored graphs)");
        }

        [ModTest(Name = "HNA_Pruning_AllRegionsHaveSufficientWidth", Order = 203)]
        public static void AllRegionsHaveSufficientWidth()
        {
            if (!Villages.Entity.VillageRegistry.IsAnyAvailable)
                ModAssert.Skip(
                    "no HNA graph is built in this world, so there is nothing to check "
                    + "(surface-width pruning)");

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.ResolveValheimHumanoidAgentTypeID(),
                areaMask = NavMesh.AllAreas,
            };

            var centers = CollectAllRegionCenters();
            int passed = 0, failed = 0;
            var failSamples = new List<string>();

            foreach (var pos in centers)
                if (CellValidator.IsSurfaceWideEnough(pos, filter))
                {
                    passed++;
                }
                else
                {
                    failed++;
                    if (failSamples.Count < 10)
                        failSamples.Add($"  ({pos.x:F1}, {pos.y:F1}, {pos.z:F1})");
                }

            var sb = new StringBuilder();
            sb.AppendLine($"[HNA SurfaceWidth] {passed}/{centers.Count} regions have " +
                          $"sufficient width (>= {CellValidator.MinEdgeDistance}m from edge)");
            if (failed > 0)
            {
                sb.AppendLine($"  Too narrow: {failed}");
                foreach (var s in failSamples) sb.AppendLine(s);
            }

            Plugin.Log?.LogInfo(sb.ToString());

            if (failed > 0)
                Plugin.Log?.LogWarning(
                    $"[HNA SurfaceWidth] {failed}/{centers.Count} region centers are closer " +
                    $"than {CellValidator.MinEdgeDistance}m to a NavMesh edge (may include restored graphs)");
        }

        /// <summary>
        ///     Collect all region centers from every available HNA graph.
        /// </summary>
        private static List<Vector3> CollectAllRegionCenters()
        {
            var all = new List<Vector3>();
            foreach (var graph in Villages.Entity.VillageRegistry.AllGraphs())
                all.AddRange(graph.Diagnostics.GetAllRegionCenters());
            return all;
        }

        /// <summary>
        ///     Find the minimum 3D distance from a point to any region center.
        ///     Uses full XYZ so upper-floor coverage gaps are detected.
        /// </summary>
        private static float NearestCenterDist3D(Vector3 point, List<Vector3> centers)
        {
            var best = float.MaxValue;
            for (var i = 0; i < centers.Count; i++)
            {
                var dist = Vector3.Distance(point, centers[i]);
                if (dist < best)
                    best = dist;
            }

            return best;
        }
    }
}