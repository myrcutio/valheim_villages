using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villages;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    /// Low-priority task that builds the region graph for village pathfinding.
    /// Extracts NavMesh triangulation within village bounds, merges adjacent
    /// triangles into regions, and adds door links.
    /// Grid sampling, triangulation, and link detection are delegated to
    /// <see cref="RegionBuilder"/>.
    /// </summary>
    [RegisterTaskHandler]
    public class RegionPartitionHandler : ITaskHandlerWithLog
    {
        public const string RegionPartitionTaskName = "hna_partition";

        public const float BedVillageRadius = 15f;
        internal const float FloodFillRadius = 30f;
        private const float RegionBuildRadius = 30f;

        public string TaskName => RegionPartitionTaskName;

        private const float VillageClusterRadius = 50f;

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            var allBeds = ValheimVillages.Villager.AI.VillagerAIManager.GetAllBedPositions();
            var beds = FilterBedsByAnchor(allBeds, task);
            bool hasPatrolBounds = VillageAreaManager.TryGetCombinedBounds(
                out float patrolMinX, out float patrolMinZ, out float patrolMaxX, out float patrolMaxZ);

            string villageKey = ExtractVillageKey(task);

            float minX, minZ, maxX, maxZ;
            if (hasPatrolBounds && beds != null && beds.Count > 0)
            {
                minX = patrolMinX; minZ = patrolMinZ;
                maxX = patrolMaxX; maxZ = patrolMaxZ;
                foreach (var bed in beds)
                {
                    if (bed.x - RegionBuildRadius < minX) minX = bed.x - RegionBuildRadius;
                    if (bed.z - RegionBuildRadius < minZ) minZ = bed.z - RegionBuildRadius;
                    if (bed.x + RegionBuildRadius > maxX) maxX = bed.x + RegionBuildRadius;
                    if (bed.z + RegionBuildRadius > maxZ) maxZ = bed.z + RegionBuildRadius;
                }
            }
            else if (hasPatrolBounds)
            {
                minX = patrolMinX; minZ = patrolMinZ;
                maxX = patrolMaxX; maxZ = patrolMaxZ;
            }
            else if (beds != null && beds.Count > 0)
            {
                minX = maxX = beds[0].x;
                minZ = maxZ = beds[0].z;
                foreach (var bed in beds)
                {
                    if (bed.x - RegionBuildRadius < minX) minX = bed.x - RegionBuildRadius;
                    if (bed.z - RegionBuildRadius < minZ) minZ = bed.z - RegionBuildRadius;
                    if (bed.x + RegionBuildRadius > maxX) maxX = bed.x + RegionBuildRadius;
                    if (bed.z + RegionBuildRadius > maxZ) maxZ = bed.z + RegionBuildRadius;
                }
            }
            else
            {
                Plugin.Log?.LogInfo("[Region] Partition skipped: no village areas and no villager beds.");
                return TaskResult.Ok(new Dictionary<string, string>
                {
                    { "regions", "0" }, { "links", "0" }, { "reason", "no_beds_or_areas" }
                });
            }

            // Bake a fresh NavMesh surface for the villager agent (slot 31)
            // over this village's bounds. Without this, RegionBuilder's
            // NavMesh queries against slot 31 return no triangles because
            // Valheim only bakes the Humanoid agent (slot 1).
            float bakeMinY = float.MaxValue, bakeMaxY = float.MinValue;
            if (beds != null && beds.Count > 0)
            {
                foreach (var bed in beds)
                {
                    if (bed.y < bakeMinY) bakeMinY = bed.y;
                    if (bed.y > bakeMaxY) bakeMaxY = bed.y;
                }
            }
            else
            {
                // Fall back to a sensible vertical range around sea level.
                bakeMinY = 0f; bakeMaxY = 0f;
            }
            const float bakeYPadding = 30f;
            var bakeBounds = new Bounds();
            bakeBounds.SetMinMax(
                new Vector3(minX, bakeMinY - bakeYPadding, minZ),
                new Vector3(maxX, bakeMaxY + bakeYPadding, maxZ));

            var bakeResult = NavMeshBakeManager.BakeVillage(bakeBounds);
            DebugLog.Event("NavMeshBake", "village_bake",
                ("success", bakeResult.Success),
                ("sources", bakeResult.SourceCount),
                ("terrain_sources", bakeResult.TerrainSourceCount),
                ("piece_sources", bakeResult.PieceSourceCount),
                ("doors_blocked", bakeResult.DoorsBlocked),
                ("duration_ms", bakeResult.DurationMs),
                ("terrain_ms", bakeResult.TerrainDurationMs),
                ("piece_ms", bakeResult.PieceDurationMs),
                ("agent_slot", VillagerAgentType.UnityAgentTypeID),
                ("bounds_x", $"{minX:F0}..{maxX:F0}"),
                ("bounds_z", $"{minZ:F0}..{maxZ:F0}"),
                ("bounds_y", $"{bakeMinY - bakeYPadding:F0}..{bakeMaxY + bakeYPadding:F0}"),
                ("reason", bakeResult.FailureReason ?? ""));

            // Two-pass build: terrain first (loose tuning), piece second
            // (strict tuning). Region IDs are prefix-disjoint (t* vs p*) so
            // the two result sets concatenate without collision. Piece runs
            // second so on lookup-grid overlap it wins — player-placed
            // surfaces should override raw terrain at the same XZ + height
            // bucket.
            var terrainResult = RegionBuilder.BuildFromTriangulation(
                SurfaceKind.Terrain, minX, minZ, maxX, maxZ, beds);
            var pieceResult = RegionBuilder.BuildFromTriangulation(
                SurfaceKind.Piece, minX, minZ, maxX, maxZ, beds);

            // Cross-kind BFS reachability: prune terrain regions not
            // reachable from beds through the combined terrain↔piece
            // adjacency graph. Pieces (stone tiles, floors, paths) act as
            // legitimate bridges between terrain regions; without this,
            // terrain pieced over by walkable floor pieces appears
            // disconnected and gets falsely pruned.
            RecordCrossKindAdjacency(terrainResult, pieceResult, beds);

            var combinedRegionIds = new HashSet<string>(terrainResult.RegionIds);
            combinedRegionIds.UnionWith(pieceResult.RegionIds);

            var combinedCentroids = new Dictionary<string, Vector3>(terrainResult.Centroids);
            foreach (var kv in pieceResult.Centroids) combinedCentroids[kv.Key] = kv.Value;

            var combinedLinks = new List<RegionLink>(terrainResult.Links.Count + pieceResult.Links.Count);
            combinedLinks.AddRange(terrainResult.Links);
            combinedLinks.AddRange(pieceResult.Links);

            var combinedLookup = new Dictionary<long, string>(terrainResult.LookupGrid);
            foreach (var kv in pieceResult.LookupGrid) combinedLookup[kv.Key] = kv.Value;

            var combinedBoundary = new List<(string, Vector3, Vector3)>(
                terrainResult.BoundaryCells.Count + pieceResult.BoundaryCells.Count);
            combinedBoundary.AddRange(terrainResult.BoundaryCells);
            combinedBoundary.AddRange(pieceResult.BoundaryCells);

            var kindMap = new Dictionary<string, SurfaceKind>(combinedRegionIds.Count);
            foreach (var rid in terrainResult.RegionIds) kindMap[rid] = SurfaceKind.Terrain;
            foreach (var rid in pieceResult.RegionIds) kindMap[rid] = SurfaceKind.Piece;

            // Concatenate per-kind cached triangles into the shared static
            // list that vv_tri_debug reads. Order doesn't matter for the
            // wireframe; both kinds render in the same pass.
            var combinedTriangles = new List<RegionBuilder.CachedTriangle>(
                terrainResult.Triangles.Count + pieceResult.Triangles.Count);
            combinedTriangles.AddRange(terrainResult.Triangles);
            combinedTriangles.AddRange(pieceResult.Triangles);
            RegionBuilder.CachedTriangles = combinedTriangles;

            // Rubber-band prune: drop regions whose footprint sits outside
            // the outermost layer of player-placed wall pieces.
            var rbStats = RubberBandPrune.Apply(
                combinedRegionIds, combinedCentroids, combinedLookup,
                combinedBoundary, combinedLinks, kindMap, combinedTriangles,
                minX, minZ, maxX, maxZ,
                out var droppedRubberBand);
            DebugLog.Event("RubberBandPrune", "applied",
                ("cells_outside", rbStats.OutsideCells),
                ("cells_island", rbStats.IslandCells),
                ("lookup_cells_dropped", rbStats.LookupCellsDropped),
                ("triangles_dropped", rbStats.TrianglesDropped),
                ("regions_dropped", rbStats.RegionsDropped),
                ("regions_reached", rbStats.RegionsReached),
                ("seed_perimeter_cells", rbStats.PerimeterSeeds),
                ("regions_kept", combinedRegionIds.Count));
            if (droppedRubberBand.Count > 0)
            {
                DebugLog.List("RubberBandPrune", "dropped_region_ids",
                    droppedRubberBand.Select(r => (object)r));
            }

            var graph = RegionGraph.GetOrCreate(villageKey);
            graph.SetGraph(combinedRegionIds, combinedLinks,
                combinedCentroids, combinedLookup, combinedBoundary, kindMap);

            DebugLog.Capture("repartition");

            // TODO: re-enable door links once the region graph is validated
            // RegionBuilder.CollectDoorLinks(graph, minX, minZ, maxX, maxZ, doorLinks);

            string regionCentersStr = BuildRegionCentersString(graph);
            string linksStr = BuildLinksSummaryString(combinedLinks);
            PathTelemetry.LogRegionGraph(combinedRegionIds.Count, combinedLinks.Count,
                minX, minZ, maxX, maxZ, regionCentersStr, linksStr);

            Plugin.Log?.LogInfo(
                $"[Region] Partition complete: {combinedRegionIds.Count} regions " +
                $"(terrain={terrainResult.RegionIds.Count}, piece={pieceResult.RegionIds.Count}), " +
                $"{combinedLinks.Count} links " +
                $"(bounds {minX:F0},{minZ:F0} to {maxX:F0},{maxZ:F0}, key={villageKey})");

            return TaskResult.Ok(new Dictionary<string, string>
            {
                { "regions", combinedRegionIds.Count.ToString() },
                { "regions_terrain", terrainResult.RegionIds.Count.ToString() },
                { "regions_piece", pieceResult.RegionIds.Count.ToString() },
                { "links", combinedLinks.Count.ToString() },
                { "village_key", villageKey }
            });
        }

        private static string BuildRegionCentersString(RegionGraph graph)
        {
            var sb = new StringBuilder();
            foreach (string id in graph.GetRegionIds())
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (!graph.GetCellWorldXZ(id, out float wx, out float wz)) continue;
                float wy = 0f;
                if (graph.TryGetCellHeight(id, out float bfsY)) wy = bfsY;
                else if (RegionGraph.GetSolidHeightAt(wx, wz, out float h)) wy = h;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(id).Append(',')
                    .Append(wx.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                    .Append(wy.ToString("F1", CultureInfo.InvariantCulture)).Append(',')
                    .Append(wz.ToString("F1", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string BuildLinksSummaryString(List<RegionLink> links)
        {
            var sb = new StringBuilder();
            foreach (var link in links)
            {
                if (sb.Length > 0) sb.Append(';');
                string typeStr = link.LinkType == RegionLinkType.Door ? "door"
                    : link.LinkType == RegionLinkType.Slope ? "slope" : "stair";
                sb.Append(link.FromRegionId).Append(',')
                    .Append(link.ToRegionId).Append(',').Append(typeStr);
            }
            return sb.ToString();
        }

        private static string ExtractVillageKey(VillagerTask task)
        {
            if (task?.Attributes != null &&
                task.Attributes.TryGetValue("anchor_x", out string axStr) &&
                task.Attributes.TryGetValue("anchor_z", out string azStr) &&
                float.TryParse(axStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float ax) &&
                float.TryParse(azStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float az))
                return RegionGraph.VillageKey(ax, az);
            return "_default";
        }

        private static List<Vector3> FilterBedsByAnchor(List<Vector3> allBeds, VillagerTask task)
        {
            if (allBeds == null || allBeds.Count == 0) return allBeds;
            if (task?.Attributes == null ||
                !task.Attributes.TryGetValue("anchor_x", out string axStr) ||
                !task.Attributes.TryGetValue("anchor_z", out string azStr))
                return allBeds;

            if (!float.TryParse(axStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float anchorX) ||
                !float.TryParse(azStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float anchorZ))
                return allBeds;

            float r2 = VillageClusterRadius * VillageClusterRadius;
            var filtered = new List<Vector3>();
            foreach (var bed in allBeds)
            {
                float dx = bed.x - anchorX, dz = bed.z - anchorZ;
                if (dx * dx + dz * dz <= r2) filtered.Add(bed);
            }

            Plugin.Log?.LogInfo(
                $"[Region] Filtered beds by anchor ({anchorX:F0},{anchorZ:F0}): " +
                $"{filtered.Count}/{allBeds.Count} within {VillageClusterRadius}m");
            if (filtered.Count == 0)
                Plugin.Log?.LogWarning(
                    $"[Region] No beds within {VillageClusterRadius}m of anchor ({anchorX:F0},{anchorZ:F0})");
            return filtered;
        }

        /// <summary>
        /// Builds the cross-kind region adjacency graph (terrain↔terrain +
        /// piece↔piece in-pass edges, plus terrain↔piece cross-kind edges via
        /// shared quantized vertex positions and via vertex-to-vertex
        /// proximity) and locates bed-anchored terrain seeds. Persists both
        /// to <see cref="BfsAdjacencyStore"/> so the <c>vv_bfs_trace</c> dev
        /// command can compute paths back to a bed without re-running the
        /// partition. Read-only: does NOT mutate inputs and does NOT prune
        /// regions; downstream <see cref="RubberBandPrune"/> handles cell-grid
        /// reachability via the outermost wall layer.
        /// </summary>
        private static void RecordCrossKindAdjacency(
            RegionBuilder.BuildResult terrainResult,
            RegionBuilder.BuildResult pieceResult,
            List<Vector3> beds)
        {
            if (terrainResult.RegionIds == null || terrainResult.RegionIds.Count == 0) return;

            // --- Build combined adjacency ---
            var combinedAdj = new Dictionary<string, HashSet<string>>();
            void EnsureNode(string id)
            {
                if (!combinedAdj.ContainsKey(id))
                    combinedAdj[id] = new HashSet<string>();
            }
            void AddEdgeBoth(string a, string b)
            {
                EnsureNode(a); EnsureNode(b);
                combinedAdj[a].Add(b);
                combinedAdj[b].Add(a);
            }

            // In-kind adjacency from each pass.
            if (terrainResult.Adjacency != null)
                foreach (var kv in terrainResult.Adjacency)
                {
                    EnsureNode(kv.Key);
                    foreach (var n in kv.Value) AddEdgeBoth(kv.Key, n);
                }
            if (pieceResult.Adjacency != null)
                foreach (var kv in pieceResult.Adjacency)
                {
                    EnsureNode(kv.Key);
                    foreach (var n in kv.Value) AddEdgeBoth(kv.Key, n);
                }

            // Cross-kind adjacency: terrain region T and piece region P are
            // adjacent iff they share any quantized vertex position. Build
            // an index from position → set of piece regions, then for each
            // terrain region scan its positions and union the matched piece
            // regions.
            int crossKindEdges = 0;
            int crossKindEdgesProx = 0;
            if (terrainResult.RegionVertexPositions != null &&
                pieceResult.RegionVertexPositions != null)
            {
                var posToPieces = new Dictionary<long, List<string>>();
                foreach (var kv in pieceResult.RegionVertexPositions)
                {
                    foreach (long q in kv.Value)
                    {
                        if (!posToPieces.TryGetValue(q, out var list))
                        {
                            list = new List<string>();
                            posToPieces[q] = list;
                        }
                        list.Add(kv.Key);
                    }
                }
                foreach (var kv in terrainResult.RegionVertexPositions)
                {
                    var matched = new HashSet<string>();
                    foreach (long q in kv.Value)
                    {
                        if (!posToPieces.TryGetValue(q, out var list)) continue;
                        foreach (var pid in list) matched.Add(pid);
                    }
                    foreach (var pid in matched)
                    {
                        AddEdgeBoth(kv.Key, pid);
                        crossKindEdges++;
                    }
                }
            }

            // Cross-kind adjacency (proximity): vertex-to-vertex minimum
            // distance between each terrain and piece region. Required because
            // AABB distance can falsely match a piece sitting inside a
            // sprawling terrain region's bounding box (the AABBs intersect
            // even though no actual vertex pair is close). Vertex-to-vertex
            // is the geometrically honest "physically touching" measure.
            // AABB prefilter (cheap) skips pairs that can't possibly be
            // close to avoid the O(M*N) inner loop on every pair.
            const float proxMaxDist = 0.5f; // metres, vertex-to-vertex
            const float proxMaxDistSq = proxMaxDist * proxMaxDist;
            if (terrainResult.RegionVertexList != null &&
                pieceResult.RegionVertexList != null &&
                terrainResult.RegionBounds != null &&
                pieceResult.RegionBounds != null)
            {
                foreach (var tkv in terrainResult.RegionVertexList)
                {
                    if (!terrainResult.RegionBounds.TryGetValue(tkv.Key, out var tb)) continue;
                    var tVerts = tkv.Value;
                    foreach (var pkv in pieceResult.RegionVertexList)
                    {
                        if (!pieceResult.RegionBounds.TryGetValue(pkv.Key, out var pb)) continue;
                        // Cheap AABB prefilter — if the boxes are further apart
                        // than the threshold, no vertex pair can be within it.
                        float adx = Mathf.Max(0f, Mathf.Max(tb.min.x - pb.max.x, pb.min.x - tb.max.x));
                        float ady = Mathf.Max(0f, Mathf.Max(tb.min.y - pb.max.y, pb.min.y - tb.max.y));
                        float adz = Mathf.Max(0f, Mathf.Max(tb.min.z - pb.max.z, pb.min.z - tb.max.z));
                        if (adx * adx + ady * ady + adz * adz > proxMaxDistSq) continue;

                        // Precise vertex-to-vertex min distance.
                        var pVerts = pkv.Value;
                        bool matched = false;
                        for (int i = 0; i < tVerts.Count && !matched; i++)
                        {
                            Vector3 ta = tVerts[i];
                            for (int j = 0; j < pVerts.Count; j++)
                            {
                                Vector3 pa = pVerts[j];
                                float dx = ta.x - pa.x, dy = ta.y - pa.y, dz = ta.z - pa.z;
                                if (dx * dx + dy * dy + dz * dz <= proxMaxDistSq)
                                { matched = true; break; }
                            }
                        }
                        if (matched)
                        {
                            AddEdgeBoth(tkv.Key, pkv.Key);
                            crossKindEdgesProx++;
                        }
                    }
                }
            }

            // --- Find seeds: closest terrain region centroid to each bed ---
            var seeds = new HashSet<string>();
            const float bedYTol = 3f;
            const float bedXZTol = 30f;
            const float bedXZTolSq = bedXZTol * bedXZTol;
            if (beds != null)
            {
                foreach (var bed in beds)
                {
                    string closest = null;
                    float closestDistSq = float.MaxValue;
                    foreach (var rid in terrainResult.RegionIds)
                    {
                        if (!terrainResult.Centroids.TryGetValue(rid, out Vector3 c)) continue;
                        if (Mathf.Abs(c.y - bed.y) > bedYTol) continue;
                        float dx = c.x - bed.x, dz = c.z - bed.z;
                        float dSq = dx * dx + dz * dz;
                        if (dSq > bedXZTolSq) continue;
                        if (dSq < closestDistSq) { closestDistSq = dSq; closest = rid; }
                    }
                    if (closest != null) seeds.Add(closest);
                }
            }

            // Fallback: largest terrain region by triangle count from Triangles list.
            if (seeds.Count == 0)
            {
                var triCounts = new Dictionary<string, int>();
                if (terrainResult.Triangles != null)
                    foreach (var ct in terrainResult.Triangles)
                    {
                        if (string.IsNullOrEmpty(ct.RegionId)) continue;
                        triCounts[ct.RegionId] = triCounts.TryGetValue(ct.RegionId, out int c0) ? c0 + 1 : 1;
                    }
                string largest = null;
                int largestCount = 0;
                foreach (var kv in triCounts)
                    if (kv.Value > largestCount) { largestCount = kv.Value; largest = kv.Key; }
                if (largest != null)
                {
                    seeds.Add(largest);
                    Plugin.Log?.LogInfo(
                        $"[Region] CrossKind BFS: no bed mapped to terrain region; " +
                        $"falling back to largest terrain region {largest} ({largestCount} tris)");
                }
            }

            if (seeds.Count == 0)
            {
                Plugin.Log?.LogWarning("[Region] CrossKind adjacency: no seeds found (vv_bfs_trace will be empty)");
                return;
            }

            // Persist for vv_bfs_trace diagnostic.
            ValheimVillages.Villager.AI.Navigation.BfsAdjacencyStore.Set(combinedAdj, seeds);

            Plugin.Log?.LogInfo(
                $"[Region] CrossKind adjacency built: {combinedAdj.Count} nodes, " +
                $"seeds={seeds.Count}, cross_vert={crossKindEdges}, cross_prox={crossKindEdgesProx} " +
                $"(prune handled downstream by RubberBandPrune)");
        }
    }
}
