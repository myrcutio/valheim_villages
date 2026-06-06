using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villages;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    ///     Low-priority task that builds the region graph for village pathfinding.
    ///     Extracts NavMesh triangulation within village bounds, merges adjacent
    ///     triangles into regions, and adds door links.
    ///     Grid sampling, triangulation, and link detection are delegated to
    ///     <see cref="RegionBuilder" />.
    /// </summary>
    [RegisterTaskHandler]
    public class RegionPartitionHandler : ITaskHandlerWithLog
    {
        public const string RegionPartitionTaskName = "hna_partition";

        public const float BedVillageRadius = 15f;
        internal const float FloodFillRadius = 30f;
        private const float RegionBuildRadius = 30f;

        private const float VillageClusterRadius = 50f;

        /// <summary>
        ///     Seconds to hold villager movement after a partition runs, while the
        ///     freshly-baked navmesh settles and agents re-plan. Covers this
        ///     synchronous rebuild plus a margin; villagers stop and drop stale
        ///     paths for the window (see <see cref="VillageNavLock" />).
        /// </summary>
        private const float NavRebuildSettleSeconds = 3f;

        public string TaskName => RegionPartitionTaskName;

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            // Freeze villager movement across the rebuild + settle so a path
            // computed on the old navmesh can't run a villager off a ledge before
            // autoRepath catches up. Set here (the rebuild moment) rather than at
            // enqueue — the graph is stable while the task waits in the queue.
            VillageNavLock.RequestHold(NavRebuildSettleSeconds);

            var allBeds = VillagerAIManager.GetAllBedPositions();
            var beds = FilterBedsByAnchor(allBeds, task);
            // Snap each home/seed to a walkable, capsule-clear cell before it drives the
            // bake elevation and the Pass-1 reachability flood. A registry-anchored home
            // (or a bed) can sit inside its own colliders; flooding from there reaches a
            // couple of cells and the region graph/boundary degenerates. Resolved against
            // real geometry + the vanilla navmesh, so it works before slot 31 is (re)baked.
            beds = ResolveWalkableSeeds(beds);
            var hasPatrolBounds = VillageAreaManager.TryGetCombinedBounds(
                out var patrolMinX, out var patrolMinZ, out var patrolMaxX, out var patrolMaxZ);

            var village = ResolveVillage(task, beds);
            if (village == null)
            {
                Plugin.Log?.LogWarning(
                    "[Region] Partition skipped: no existing village resolved for this task " +
                    "(villages are created at registry placement; nothing to partition).");
                return TaskResult.Ok(new Dictionary<string, string>
                {
                    { "regions", "0" }, { "links", "0" }, { "reason", "no_village" },
                });
            }

            var villageKey = village.VillageId;

            float minX, minZ, maxX, maxZ;
            if (hasPatrolBounds && beds != null && beds.Count > 0)
            {
                minX = patrolMinX;
                minZ = patrolMinZ;
                maxX = patrolMaxX;
                maxZ = patrolMaxZ;
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
                minX = patrolMinX;
                minZ = patrolMinZ;
                maxX = patrolMaxX;
                maxZ = patrolMaxZ;
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
                    { "regions", "0" }, { "links", "0" }, { "reason", "no_beds_or_areas" },
                });
            }

            // Bake a fresh NavMesh surface for the villager agent (slot 31)
            // over this village's bounds. Without this, RegionBuilder's
            // NavMesh queries against slot 31 return no triangles because
            // Valheim only bakes the Humanoid agent (slot 1).
            if (beds == null || beds.Count == 0)
            {
                Plugin.Log?.LogError(
                    $"[Region] Partition aborted: no villager beds for village '{villageKey}'. " +
                    "Cannot determine bake elevation without beds — refusing to bake at sea-level fallback.");
                return TaskResult.Ok(new Dictionary<string, string>
                {
                    { "regions", "0" }, { "links", "0" }, { "reason", "no_beds_for_bake_y" },
                });
            }

            float bakeMinY = float.MaxValue, bakeMaxY = float.MinValue;
            foreach (var bed in beds)
            {
                if (bed.y < bakeMinY) bakeMinY = bed.y;
                if (bed.y > bakeMaxY) bakeMaxY = bed.y;
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
                ("door_pieces_dropped", bakeResult.DoorPiecesDropped),
                ("beds_blocked", bakeResult.BedsBlocked),
                ("outside_cells_blocked", bakeResult.OutsideCellsBlocked),
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
            //
            // Captured here (not persisted) so Pass 3's discovered piece-
            // step edges can be merged in before the BfsAdjacencyStore
            // gets its final snapshot — otherwise vv_bfs_trace would walk
            // a graph missing every piece-to-piece edge discovered by the
            // cell-level flood (and the trace would show "no path" for
            // any region only reachable via the piece chain).
            var crossKindAdj = BuildCrossKindAdjacency(terrainResult, pieceResult, beds);


            // Combine terrain + piece region sets (union + shadow suppression +
            // cascade) into the inputs the prune/graph stages consume.
            RegionBuilder.CombineTerrainAndPiece(
                terrainResult, pieceResult,
                out var combinedRegionIds, out var combinedCentroids, out var combinedLinks,
                out var combinedLookup, out var combinedBoundary, out var combinedTriangles,
                out var kindMap);

            // Rubber-band prune: drop regions whose footprint sits outside
            // the outermost layer of player-placed wall pieces.
            var rbStats = RubberBandPrune.Apply(
                combinedRegionIds, combinedCentroids, combinedLookup,
                combinedBoundary, combinedLinks, kindMap, combinedTriangles,
                beds,
                minX, minZ, maxX, maxZ,
                out var droppedRubberBand,
                out var pass3DiscoveredEdges,
                out var bedReachableCells,
                out var outsideCells,
                out var prunedPieceKeys,
                out var gateMarkers);

            // Merge Pass 3 discovered edges into the cross-kind adjacency
            // (if it was built) and publish the merged graph to
            // BfsAdjacencyStore for vv_bfs_trace. Pass 3 records each
            // piece-step transition during its cell-level flood — ground
            // truth for walkable adjacency, more reliable than vertex-
            // proximity heuristics. We add ONLY edges where both endpoints
            // survived the region cascade (others would point at dropped
            // nodes the trace can't render).
            if (crossKindAdj.HasValue)
            {
                var (combinedAdj, seeds, edgeMeta) = crossKindAdj.Value;
                var pass3EdgesMerged = 0;
                foreach (var (fromRid, toRid, _, _) in pass3DiscoveredEdges)
                {
                    if (droppedRubberBand.Contains(fromRid)
                        || droppedRubberBand.Contains(toRid)) continue;
                    if (!combinedAdj.TryGetValue(fromRid, out var fromAdj))
                    {
                        fromAdj = new HashSet<string>();
                        combinedAdj[fromRid] = fromAdj;
                    }
                    if (!combinedAdj.TryGetValue(toRid, out var toAdj))
                    {
                        toAdj = new HashSet<string>();
                        combinedAdj[toRid] = toAdj;
                    }
                    fromAdj.Add(toRid);
                    toAdj.Add(fromRid);

                    var key = BfsAdjacencyStore.EdgeKey(fromRid, toRid);
                    if (edgeMeta.TryGetValue(key, out var meta))
                    {
                        meta.Kinds |= BfsEdgeKind.Pass3Step;
                        edgeMeta[key] = meta;
                    }
                    else
                    {
                        edgeMeta[key] = new BfsEdgeMeta { Kinds = BfsEdgeKind.Pass3Step };
                        pass3EdgesMerged++;
                    }
                }
                BfsAdjacencyStore.Set(combinedAdj, seeds, edgeMeta);
                if (pass3EdgesMerged > 0)
                    DebugLog.Event("Region", "bfs_store_merged_pass3",
                        ("new_edges", pass3EdgesMerged),
                        ("total_pass3_pairs", pass3DiscoveredEdges.Count));
            }

            DebugLog.Event("RubberBandPrune", "applied",
                ("outside_terrain_cells", rbStats.OutsideTerrainCells),
                ("pass2_seeds", rbStats.Pass2Seeds),
                ("bed_reachable_terrain_cells", rbStats.BedReachableTerrainCells),
                ("pass3_seeds", rbStats.Pass3Seeds),
                ("bed_reachable_piece_keys", rbStats.BedReachablePieceKeys),
                ("pass3_piece_keys_dropped", rbStats.Pass3PieceKeysDropped),
                ("pass3_links_added", rbStats.Pass3LinksAdded),
                ("pass4_verts_snapped", rbStats.Pass4BoundaryVertsSnapped),
                ("pass4_snap_misses", rbStats.Pass4SnapMisses),
                ("pass4_links_snapped", rbStats.Pass4LinksSnapped),
                ("pass5_chains", rbStats.Pass5ChainsConsolidated),
                ("pass5_consumed", rbStats.Pass5RegionsConsumed),
                ("pass5_links_removed", rbStats.Pass5LinksRemoved),
                ("lookup_cells_dropped", rbStats.LookupCellsDropped),
                ("triangles_dropped", rbStats.TrianglesDropped),
                ("static_solid_dropped", rbStats.StaticSolidTrianglesDropped),
                ("regions_dropped", rbStats.RegionsDropped),
                ("seed_perimeter_cells", rbStats.PerimeterSeeds),
                ("regions_kept", combinedRegionIds.Count));
            if (droppedRubberBand.Count > 0)
                DebugLog.List("RubberBandPrune", "dropped_region_ids",
                    droppedRubberBand.Select(r => (object)r));

            // A partition that yielded 0 regions is a FAILED bake (its only seed is walled
            // off / outside the current footprint), NOT a genuinely empty village — the
            // no-beds case already returned earlier at no_beds_or_areas. If the seed sits
            // inside (or right against) an existing, non-empty graph, this is a MUTATION of
            // that same village, not a new one: committing the empty result would clobber a
            // working graph. (Hit by: rebuild the registry, then revive a villager whose
            // stale home re-seeds a degenerate partition over the good graph.) Keep the
            // existing graph instead of replacing it with nothing.
            if (combinedRegionIds.Count == 0)
            {
                var existing = FindContainingNonEmptyGraph(beds);
                if (existing != null)
                {
                    Plugin.Log?.LogWarning(
                        $"[Region] Partition for key={villageKey} produced 0 regions, but its seed is inside " +
                        $"existing graph '{existing.RegisteredVillageKey}' ({existing.RegionCount} regions, " +
                        $"{existing.LinkCount} links) — treating as a failed mutation of the same village; " +
                        "keeping the existing graph rather than clobbering it.");
                    return TaskResult.Ok(new Dictionary<string, string>
                    {
                        { "regions", existing.RegionCount.ToString() },
                        { "links", existing.LinkCount.ToString() },
                        { "village_key", existing.RegisteredVillageKey ?? villageKey },
                        { "reason", "degenerate_kept_existing" },
                    });
                }
            }

            var graph = village.GetOrCreateGraph();
            graph.SetGraph(combinedRegionIds, combinedLinks,
                combinedCentroids, combinedLookup, combinedBoundary, kindMap);
            graph.SetGates(gateMarkers);
            // Promote the perimeter classification to committed graph state so it
            // persists (v4 ZDO) and gives the incremental reconcilers a baseline
            // to diff against. Must run after SetGraph so HasClassification flips
            // only on a populated graph.
            graph.SetClassification(outsideCells, bedReachableCells, prunedPieceKeys);
            if (gateMarkers.Count > 0)
                Plugin.Log?.LogInfo(
                    $"[Region] Sealed {gateMarkers.Count} gate(s) into the village boundary");

            // (NavMesh carving for outside-the-wall cells is now done at
            // bake time by NavMeshBakeManager.BakeVillage via phantom
            // NotWalkable box sources — see ComputeOutsideCellsForBake +
            // AddOutsideCellBlockers. The earlier post-prune HNA-only
            // rebake is gone; the single bake produces a NavMesh that
            // already excludes outside surfaces and carves obstacles.)



            // Invalidate every active villager's cached BaseAI path.
            // The rebake replaced slot-31 NavMesh data and the link
            // sweep just cleared every NavMeshLink; any villager
            // mid-walk is holding waypoints that may now sit on
            // missing geometry or route through deleted links. The
            // re-pathfind branch in VillagerAI only fires when the
            // path is empty, so unreached-but-now-unreachable nodes
            // would otherwise lock the villager into a doomed path
            // indefinitely (step-jumping at thin air, no stuck-timer
            // safety net because jumps register as movement).
            var pathsInvalidated = VillagerAIManager.InvalidatePathsAfterRebake();
            if (pathsInvalidated > 0)
                Plugin.Log?.LogInfo(
                    $"[Region] Invalidated cached paths for {pathsInvalidated} villager(s) after rebake");

            // Auto-capture is opt-in: the orchestrated screenshot teleports the
            // player to a top-down anchor and can strand them in the sky if the
            // restore hiccups. Off by default (vv_capture still works on demand).
            if (Settings.VillagerSettings.AutoDiagnosticCaptureEnabled)
                DebugLog.Capture("repartition");

            // TODO: re-enable door links once the region graph is validated
            // RegionBuilder.CollectDoorLinks(graph, minX, minZ, maxX, maxZ, doorLinks);

            var regionCentersStr = BuildRegionCentersString(graph);
            var linksStr = BuildLinksSummaryString(combinedLinks);
            PathTelemetry.LogRegionGraph(combinedRegionIds.Count, combinedLinks.Count,
                minX, minZ, maxX, maxZ, regionCentersStr, linksStr);

            Plugin.Log?.LogInfo(
                $"[Region] Partition complete: {combinedRegionIds.Count} regions " +
                $"(terrain={terrainResult.RegionIds.Count}, piece={pieceResult.RegionIds.Count}), " +
                $"{combinedLinks.Count} links " +
                $"(bounds {minX:F0},{minZ:F0} to {maxX:F0},{maxZ:F0}, key={villageKey})");

            // Persist the freshly built graph onto the durable village ZDO (1-to-1),
            // then publish the area/caches from the village. This replaces the old
            // per-guard PatrolPersistence.SaveHnaGraph write.
            village.SaveGraph();
            VillageAreaManager.RefreshFromVillage(village);

            return TaskResult.Ok(new Dictionary<string, string>
            {
                { "regions", combinedRegionIds.Count.ToString() },
                { "regions_terrain", terrainResult.RegionIds.Count.ToString() },
                { "regions_piece", pieceResult.RegionIds.Count.ToString() },
                { "links", combinedLinks.Count.ToString() },
                { "village_key", villageKey },
            });
        }

        private static string BuildRegionCentersString(RegionGraph graph)
        {
            var sb = new StringBuilder();
            foreach (var id in graph.GetRegionIds())
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (!graph.GetCellWorldXZ(id, out var wx, out var wz)) continue;
                var wy = 0f;
                if (graph.TryGetCellHeight(id, out var bfsY)) wy = bfsY;
                else if (RegionGraph.GetSolidHeightAt(wx, wz, out var h)) wy = h;
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
                var typeStr = link.LinkType == RegionLinkType.Door ? "door"
                    : link.LinkType == RegionLinkType.Slope ? "slope" : "stair";
                sb.Append(link.FromRegionId).Append(',')
                    .Append(link.ToRegionId).Append(',').Append(typeStr);
            }

            return sb.ToString();
        }

        /// <summary>
        ///     Resolve the EXISTING durable <see cref="Village" /> this partition builds
        ///     for, by (1) an explicit <c>village_id</c> attribute, (2) an
        ///     <c>anchor_x/anchor_z</c> pair, or (3) the first seed bed — by id, graph
        ///     coverage, or registry-anchor proximity. NEVER mints: a partition runs for a
        ///     village that already exists (created at registry placement). Returns null
        ///     when none resolves, and the caller aborts rather than fabricating one.
        /// </summary>
        private static Village ResolveVillage(VillagerTask task, List<Vector3> beds)
        {
            if (task?.Attributes != null &&
                task.Attributes.TryGetValue("village_id", out var id) &&
                !string.IsNullOrEmpty(id))
            {
                var byId = VillageRegistry.FindById(id);
                if (byId != null) return byId;
            }

            if (task?.Attributes != null &&
                task.Attributes.TryGetValue("anchor_x", out var axStr) &&
                task.Attributes.TryGetValue("anchor_z", out var azStr) &&
                float.TryParse(axStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var ax) &&
                float.TryParse(azStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var az))
            {
                var y = beds != null && beds.Count > 0 ? beds[0].y : 0f;
                var anchor = new Vector3(ax, y, az);
                return VillageRegistry.GetVillageCovering(anchor) ?? VillageRegistry.FindNearAnchor(anchor);
            }

            if (beds != null && beds.Count > 0)
                return VillageRegistry.GetVillageCovering(beds[0]) ?? VillageRegistry.FindNearAnchor(beds[0]);

            return null;
        }

        /// <summary>
        ///     Find an existing, non-empty region graph that one of <paramref name="seeds" />
        ///     falls inside of — i.e. the village this partition is really a mutation of.
        ///     "Inside" means the seed resolves to a region of that graph, or (for a seed on
        ///     a carved-out bed/obstacle cell) sits within a few metres of one of its lookup
        ///     cells. Used to refuse committing a degenerate (0-region) re-derivation over a
        ///     working graph: a seed in an existing graph's interior is the same village.
        /// </summary>
        private static RegionGraph FindContainingNonEmptyGraph(List<Vector3> seeds)
        {
            if (seeds == null) return null;
            foreach (var seed in seeds)
            {
                var g = VillageRegistry.GetVillageAt(seed)?.Graph;
                if (g == null || g.RegionCount == 0) continue;
                if (!string.IsNullOrEmpty(g.PointToRegionId(seed))) return g;
                // The seed may sit on a bed/obstacle cell the prune carved out of an
                // otherwise-covered interior; accept a near lookup cell as "inside".
                if (g.TryFindNearestLookupCell(seed, null, out _, out _, 6f)) return g;
            }

            return null;
        }

        /// <summary>
        ///     Map each village seed (stored villager home / bed) to the nearest
        ///     walkable, agent-clear cell via <see cref="RegistrySeedResolver" />, so the
        ///     bake elevation and Pass-1 flood seed from ground the agent can stand on
        ///     rather than a point buried in the registry/bed colliders. Seeds that can't
        ///     be resolved are kept as-is and logged loudly (no silent substitution).
        /// </summary>
        private static List<Vector3> ResolveWalkableSeeds(List<Vector3> beds)
        {
            if (beds == null || beds.Count == 0) return beds;

            var snapped = new List<Vector3>(beds.Count);
            var moved = 0;
            foreach (var bed in beds)
            {
                if (RegistrySeedResolver.TryResolveWalkableSeed(bed, out var seed))
                {
                    if ((seed - bed).sqrMagnitude > 0.25f) moved++;
                    snapped.Add(seed);
                }
                else
                {
                    snapped.Add(bed);
                    Plugin.Log?.LogWarning(
                        $"[Region] No walkable seed near home ({bed.x:F1},{bed.y:F1},{bed.z:F1}); " +
                        "using it as-is (flood may degenerate).");
                }
            }

            if (moved > 0)
                Plugin.Log?.LogInfo(
                    $"[Region] Snapped {moved}/{beds.Count} village seed(s) onto walkable cells near their anchor.");
            return snapped;
        }

        private static List<Vector3> FilterBedsByAnchor(List<Vector3> allBeds, VillagerTask task)
        {
            if (allBeds == null || allBeds.Count == 0) return allBeds;
            if (task?.Attributes == null ||
                !task.Attributes.TryGetValue("anchor_x", out var axStr) ||
                !task.Attributes.TryGetValue("anchor_z", out var azStr))
                return allBeds;

            if (!float.TryParse(axStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var anchorX) ||
                !float.TryParse(azStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var anchorZ))
                return allBeds;

            var r2 = VillageClusterRadius * VillageClusterRadius;
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
        ///     Builds the cross-kind region adjacency graph (terrain↔terrain +
        ///     piece↔piece in-pass edges, plus terrain↔piece cross-kind edges via
        ///     shared quantized vertex positions and via vertex-to-vertex
        ///     proximity) and locates bed-anchored terrain seeds. Persists both
        ///     to <see cref="BfsAdjacencyStore" /> so the <c>vv_bfs_trace</c> dev
        ///     command can compute paths back to a bed without re-running the
        ///     partition. Read-only: does NOT mutate inputs and does NOT prune
        ///     regions; downstream <see cref="RubberBandPrune" /> handles cell-grid
        ///     reachability via the outermost wall layer.
        /// </summary>
        /// <summary>
        ///     Builds the cross-kind adjacency graph (BFS nodes / edges /
        ///     seeds) from per-pass results. Returns the tuple instead of
        ///     persisting to <see cref="BfsAdjacencyStore" /> directly so
        ///     the caller can merge in Pass 3's discovered piece-step
        ///     edges before publishing. Returns null on early abort
        ///     conditions (no terrain regions, no bed-mapped seeds).
        /// </summary>
        private static (Dictionary<string, HashSet<string>> adjacency,
                        HashSet<string> seeds,
                        Dictionary<string, BfsEdgeMeta> edgeMeta)?
            BuildCrossKindAdjacency(
            RegionBuilder.BuildResult terrainResult,
            RegionBuilder.BuildResult pieceResult,
            List<Vector3> beds)
        {
            if (terrainResult.RegionIds == null || terrainResult.RegionIds.Count == 0) return null;

            // --- Build combined adjacency ---
            var combinedAdj = new Dictionary<string, HashSet<string>>();
            // Per-edge metadata for vv_bfs_trace. Tracks which mechanism(s)
            // added each edge (in-pass shared edge / cross-kind vertex
            // coincidence / cross-kind 0.5m vertex proximity) and a
            // representative bridge position for cross-kind edges. Keyed by
            // BfsAdjacencyStore.EdgeKey(a, b) so undirected lookups are
            // canonical regardless of insertion order.
            var edgeMeta = new Dictionary<string, BfsEdgeMeta>();

            void EnsureNode(string id)
            {
                if (!combinedAdj.ContainsKey(id))
                    combinedAdj[id] = new HashSet<string>();
            }

            void AddEdgeBoth(string a, string b)
            {
                EnsureNode(a);
                EnsureNode(b);
                combinedAdj[a].Add(b);
                combinedAdj[b].Add(a);
            }

            // Adds the directed pair in both directions AND records / merges
            // the edge's kind + (optional) representative position into
            // edgeMeta. Multi-kind ORs together; first non-null RepresentativePos
            // wins; ProxMinDist tracks the min across CrossProx insertions.
            void RecordEdge(string a, string b,
                BfsEdgeKind kind,
                Vector3? repPos, float proxDist)
            {
                AddEdgeBoth(a, b);
                var key = BfsAdjacencyStore.EdgeKey(a, b);
                if (edgeMeta.TryGetValue(key, out var meta))
                {
                    meta.Kinds |= kind;
                    if (!meta.RepresentativePos.HasValue && repPos.HasValue)
                        meta.RepresentativePos = repPos;
                    if (kind == BfsEdgeKind.CrossProx &&
                        (meta.ProxMinDist == 0f || proxDist < meta.ProxMinDist))
                        meta.ProxMinDist = proxDist;
                    edgeMeta[key] = meta;
                }
                else
                {
                    edgeMeta[key] = new BfsEdgeMeta
                    {
                        Kinds = kind,
                        RepresentativePos = repPos,
                        ProxMinDist = kind == BfsEdgeKind.CrossProx ? proxDist : 0f,
                    };
                }
            }

            // In-kind adjacency from each pass.
            if (terrainResult.Adjacency != null)
                foreach (var kv in terrainResult.Adjacency)
                {
                    EnsureNode(kv.Key);
                    foreach (var n in kv.Value)
                        RecordEdge(kv.Key, n,
                            BfsEdgeKind.InPassEdge, null, 0f);
                }

            if (pieceResult.Adjacency != null)
                foreach (var kv in pieceResult.Adjacency)
                {
                    EnsureNode(kv.Key);
                    foreach (var n in kv.Value)
                        RecordEdge(kv.Key, n,
                            BfsEdgeKind.InPassEdge, null, 0f);
                }

            // --- Build quantized-vertex → world-position map ---
            // Used by the cross-kind CrossVert edge recording below to
            // attach a representative world position to each edge (where
            // the bridge geometrically sits). First vertex wins per bucket
            // — sufficient for the diagnostic; the actual matched vertex
            // is within 25cm of any other vertex in the same bucket.
            var quantPosToWorld = new Dictionary<long, Vector3>();
            if (terrainResult.RegionVertexList != null)
                foreach (var kv in terrainResult.RegionVertexList)
                foreach (var v in kv.Value)
                {
                    var q = RegionBuilder.PackQuantizedPos(v);
                    if (!quantPosToWorld.ContainsKey(q)) quantPosToWorld[q] = v;
                }

            if (pieceResult.RegionVertexList != null)
                foreach (var kv in pieceResult.RegionVertexList)
                foreach (var v in kv.Value)
                {
                    var q = RegionBuilder.PackQuantizedPos(v);
                    if (!quantPosToWorld.ContainsKey(q)) quantPosToWorld[q] = v;
                }

            // Cross-kind adjacency: terrain region T and piece region P are
            // adjacent iff they share any quantized vertex position. Build
            // an index from position → set of piece regions, then for each
            // terrain region scan its positions and union the matched piece
            // regions.
            var crossKindEdges = 0;
            var crossKindEdgesProx = 0;
            if (terrainResult.RegionVertexPositions != null &&
                pieceResult.RegionVertexPositions != null)
            {
                var posToPieces = new Dictionary<long, List<string>>();
                foreach (var kv in pieceResult.RegionVertexPositions)
                foreach (var q in kv.Value)
                {
                    if (!posToPieces.TryGetValue(q, out var list))
                    {
                        list = new List<string>();
                        posToPieces[q] = list;
                    }

                    list.Add(kv.Key);
                }

                foreach (var kv in terrainResult.RegionVertexPositions)
                {
                    // Track the FIRST matching quantized vertex per piece
                    // region so each CrossVert edge has a representative
                    // bridge position for the diagnostic.
                    var firstMatchPos = new Dictionary<string, long>();
                    foreach (var q in kv.Value)
                    {
                        if (!posToPieces.TryGetValue(q, out var list)) continue;
                        foreach (var pid in list)
                            if (!firstMatchPos.ContainsKey(pid))
                                firstMatchPos[pid] = q;
                    }

                    foreach (var pair in firstMatchPos)
                    {
                        var repPos = quantPosToWorld.TryGetValue(pair.Value, out var wp)
                            ? (Vector3?)wp
                            : null;
                        RecordEdge(kv.Key, pair.Key,
                            BfsEdgeKind.CrossVert,
                            repPos, 0f);
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
                foreach (var tkv in terrainResult.RegionVertexList)
                {
                    if (!terrainResult.RegionBounds.TryGetValue(tkv.Key, out var tb)) continue;
                    var tVerts = tkv.Value;
                    foreach (var pkv in pieceResult.RegionVertexList)
                    {
                        if (!pieceResult.RegionBounds.TryGetValue(pkv.Key, out var pb)) continue;
                        // Cheap AABB prefilter — if the boxes are further apart
                        // than the threshold, no vertex pair can be within it.
                        var adx = Mathf.Max(0f, Mathf.Max(tb.min.x - pb.max.x, pb.min.x - tb.max.x));
                        var ady = Mathf.Max(0f, Mathf.Max(tb.min.y - pb.max.y, pb.min.y - tb.max.y));
                        var adz = Mathf.Max(0f, Mathf.Max(tb.min.z - pb.max.z, pb.min.z - tb.max.z));
                        if (adx * adx + ady * ady + adz * adz > proxMaxDistSq) continue;

                        // Precise vertex-to-vertex min distance.
                        var pVerts = pkv.Value;
                        var matched = false;
                        Vector3 matchedMidpoint = default;
                        var matchedDistSq = 0f;
                        for (var i = 0; i < tVerts.Count && !matched; i++)
                        {
                            var ta = tVerts[i];
                            for (var j = 0; j < pVerts.Count; j++)
                            {
                                var pa = pVerts[j];
                                float dx = ta.x - pa.x, dy = ta.y - pa.y, dz = ta.z - pa.z;
                                var dSq = dx * dx + dy * dy + dz * dz;
                                if (dSq <= proxMaxDistSq)
                                {
                                    matched = true;
                                    matchedMidpoint = (ta + pa) * 0.5f;
                                    matchedDistSq = dSq;
                                    break;
                                }
                            }
                        }

                        if (matched)
                        {
                            RecordEdge(tkv.Key, pkv.Key,
                                BfsEdgeKind.CrossProx,
                                matchedMidpoint, Mathf.Sqrt(matchedDistSq));
                            crossKindEdgesProx++;
                        }
                    }
                }

            // --- Find seeds: closest terrain region centroid to each bed ---
            var seeds = new HashSet<string>();
            const float bedYTol = 3f;
            const float bedXZTol = 30f;
            const float bedXZTolSq = bedXZTol * bedXZTol;
            if (beds != null)
                foreach (var bed in beds)
                {
                    string closest = null;
                    var closestDistSq = float.MaxValue;
                    foreach (var rid in terrainResult.RegionIds)
                    {
                        if (!terrainResult.Centroids.TryGetValue(rid, out var c)) continue;
                        if (Mathf.Abs(c.y - bed.y) > bedYTol) continue;
                        float dx = c.x - bed.x, dz = c.z - bed.z;
                        var dSq = dx * dx + dz * dz;
                        if (dSq > bedXZTolSq) continue;
                        if (dSq < closestDistSq)
                        {
                            closestDistSq = dSq;
                            closest = rid;
                        }
                    }

                    if (closest != null) seeds.Add(closest);
                }

            if (seeds.Count == 0)
            {
                var bedCount = beds?.Count ?? 0;
                var regionCount = terrainResult.RegionIds?.Count ?? 0;
                Plugin.Log?.LogError(
                    "[Region] CrossKind adjacency aborted: no bed mapped to any terrain region " +
                    $"(beds={bedCount}, terrain_regions={regionCount}). " +
                    "Refusing to seed BFS from a synthetic largest-region fallback; vv_bfs_trace will report no data.");
                return null;
            }

            // Defer persistence to the caller — they merge Pass 3's
            // discovered piece-step edges in first, then call
            // BfsAdjacencyStore.Set once with the merged adjacency.
            Plugin.Log?.LogInfo(
                $"[Region] CrossKind adjacency built: {combinedAdj.Count} nodes, " +
                $"seeds={seeds.Count}, edges={edgeMeta.Count}, " +
                $"cross_vert={crossKindEdges}, cross_prox={crossKindEdgesProx} " +
                "(piece-step edges added downstream by RubberBandPrune)");
            return (combinedAdj, seeds, edgeMeta);
        }
    }
}