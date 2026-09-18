using System;
using System.Collections;
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
    public class RegionPartitionHandler : ITaskHandlerWithLog, ITaskPrecondition
    {
        public const string RegionPartitionTaskName = "hna_partition";

        // The piece layers NavMeshBakeManager collects PhysicsColliders from. The
        // readiness gate (IsReady) requires every object on these layers in the village
        // sector to be instantiated before the bake runs — kept in sync with
        // NavMeshBakeManager's s_pieceMask so "ready" means exactly "the bake will see
        // it". (Terrain is not a sector ZDO; IsZoneLoaded covers the heightmap.)
        private static readonly int s_pieceLayerMask =
            LayerMask.GetMask("Default", "static_solid", "piece");
        private static readonly List<ZDO> s_readinessScratch = new();

        public const float AnchorVillageRadius = 15f;
        internal const float FloodFillRadius = 30f;
        private const float RegionBuildRadius = 30f;

        /// <summary>How far out to look for player-built pieces when sizing the footprint.</summary>
        private const float FootprintScanRadius = 100f;

        /// <summary>
        ///     Hard cap on footprint AREA, in 1m flood cells. A runaway build (a fence line
        ///     running off across the map) must not balloon the grid — repartition cost scales
        ///     with cell count — but the limit has to be on area, not on a symmetric radius:
        ///     real bases are lopsided (the test base runs 93m west of its anchor centroid and
        ///     44m east), and a symmetric radius clamps the long side while wasting budget on
        ///     the short one. Clamping is logged loudly because it means the flood seeds inside
        ///     the build again and those cells mis-classify as outside.
        /// </summary>
        private const int MaxFootprintCells = 40000;

        /// <summary>
        ///     Clearance kept between the outermost village piece and the footprint border, so
        ///     the seed ring sits demonstrably OUTSIDE the perimeter wall rather than on it.
        /// </summary>
        private const float SeedRingMargin = 4f;

        private const float VillageClusterRadius = 50f;

        /// <summary>
        ///     Seconds to hold villager movement after a partition runs, while the
        ///     freshly-baked navmesh settles and agents re-plan. Covers this
        ///     synchronous rebuild plus a margin; villagers stop and drop stale
        ///     paths for the window (see <see cref="VillageNavLock" />).
        /// </summary>
        private const float NavRebuildSettleSeconds = 3f;

        public string TaskName => RegionPartitionTaskName;

        /// <summary>
        ///     Gate dequeue until the world is loaded AND settled within the village
        ///     footprint, so <see cref="NavMeshBakeManager.BakeVillage" /> reads real
        ///     terrain + piece colliders rather than a half-streamed world. The boot
        ///     partition previously ran ~5s after load against 0 bake sources (zone not
        ///     yet loaded / objects not yet instantiated) and committed a graph that
        ///     didn't cover the village. Criteria are deterministic — no magnitude
        ///     thresholds and no spatial assumptions about where the village sits:
        ///     every anchor's zone must be loaded and every bake-relevant object in it
        ///     instantiated.
        /// </summary>
        public bool IsReady(VillagerTask task)
        {
            if (ZNet.instance == null || ZNetScene.instance == null ||
                ZoneSystem.instance == null || ZDOMan.instance == null)
            {
                ReadyDiag("engine singletons not ready");
                return false;
            }

            // Self-bootstrap the villager agent slot (31). EnsureRegistered only needs the
            // Pathfinding singleton — not a spawned villager — and is idempotent, so a
            // registry-only village (no villager to trigger registration via
            // VillagerAI.Awake) can still bake. Without this the partition a fresh registry
            // needs defers forever: IsRegistered stays false with zero villagers, yet the
            // graph is itself the prerequisite for spawning the first one.
            if (!VillagerAgentType.EnsureRegistered())
            {
                ReadyDiag("villager agent slot (31) not registered");
                return false;
            }

            var anchors = FilterAnchorsByTask(CollectSeedAnchors(), task);
            // No anchors yet => nothing to partition. Let Handle() run and no-op (it
            // returns no_anchors and never bakes) rather than defer indefinitely.
            if (anchors == null || anchors.Count == 0) return true;

            // m_activeArea is gone; the synced SimulationDistance carries near/far now.
            var area = ZNet.instance.GetSyncedSimulationDistance();
            foreach (var anchor in anchors)
            {
                var zone = ZoneSystem.GetZone(anchor);
                if (!ZoneSystem.instance.IsZoneLoaded(zone))
                {
                    ReadyDiag($"zone {zone.x},{zone.y} NOT LOADED for anchor " +
                              $"({anchor.x:F0},{anchor.y:F0},{anchor.z:F0}) of {anchors.Count} total");
                    return false;
                }

                // Every piece-layer object in the sector must be instantiated, i.e.
                // object streaming for the village footprint is complete (guards the
                // observed PieceSources=0 / partially-streamed case). Terrain rides
                // along with the loaded zone (heightmap), so no separate probe — which
                // also avoids assuming the village sits at terrain height.
                if (!SectorBakeGeometryInstantiated(zone, area, anchor, out var missing))
                {
                    ReadyDiag($"piece not instantiated in zone {zone.x},{zone.y} near anchor " +
                              $"({anchor.x:F0},{anchor.y:F0},{anchor.z:F0}): prefab={missing}");
                    return false;
                }
            }

            return true;
        }

        // DIAGNOSTIC: throttled log of the gate that's deferring the partition. IsReady is
        // polled ~every 1.5s per pending task, so throttle to keep the log readable.
        private static float s_lastReadyDiag;

        private static void ReadyDiag(string reason)
        {
            var now = Time.realtimeSinceStartup;
            if (now - s_lastReadyDiag < 2f) return;
            s_lastReadyDiag = now;
            Plugin.Log?.LogInfo($"[PartitionReady] deferring hna_partition: {reason}");
        }

        /// <summary>
        ///     True when every bake-relevant (piece-layer) ZDO <i>within the village's bake
        ///     footprint</i> (XZ box of ±<see cref="RegionBuildRadius" /> around
        ///     <paramref name="anchor" />) has a live <see cref="ZNetView" /> — the
        ///     deterministic "the geometry the bake will read is done streaming" signal.
        ///     On false, <paramref name="missingPrefab" /> names the first un-instantiated
        ///     piece prefab (for diagnostics).
        ///
        ///     <para>
        ///     The footprint scope is deliberate: <see cref="NavMeshBakeManager.BakeVillage" />
        ///     only reads colliders within the anchor bounds, and the sector (zone +
        ///     simulation-distance neighbours) is far larger. Natural world objects on these
        ///     layers (rocks/trees) at the sector periphery may never instantiate unless the
        ///     player walks to them — gating on those blocked the partition forever
        ///     (observed: a dormant <c>Rock_4</c> at the zone edge). We only wait on what the
        ///     bake actually covers.
        ///     </para>
        /// </summary>
        private static bool SectorBakeGeometryInstantiated(
            Vector2s zone, SimulationDistance area, Vector3 anchor, out string missingPrefab)
        {
            missingPrefab = null;
            s_readinessScratch.Clear();
            ZDOMan.instance.FindSectorObjects(zone, area, s_readinessScratch);
            foreach (var zdo in s_readinessScratch)
            {
                if (zdo == null) continue;
                var prefab = ZNetScene.instance.GetPrefab(zdo.GetPrefab());
                if (prefab == null) continue;
                if (((1 << prefab.layer) & s_pieceLayerMask) == 0) continue;

                // Only gate on objects inside the bake footprint (anchor ± RegionBuildRadius,
                // XZ). Distant natural geometry at the sector edge isn't baked and won't all
                // instantiate, so it must not block readiness.
                var p = zdo.GetPosition();
                if (Mathf.Abs(p.x - anchor.x) > RegionBuildRadius ||
                    Mathf.Abs(p.z - anchor.z) > RegionBuildRadius)
                    continue;

                if (ZNetScene.instance.FindInstance(zdo) == null)
                {
                    missingPrefab = prefab.name;
                    return false;
                }
            }

            return true;
        }

        /// <summary>Attribute names carrying an incremental partition's changed-area rect.</summary>
        private static readonly string[] DirtyRectKeys =
            { "dirty_min_x", "dirty_min_z", "dirty_max_x", "dirty_max_z" };

        /// <summary>
        ///     Read the changed-area rect off the task. All four bounds or none: a task that
        ///     carries some of them is malformed, and silently treating it as a full rebuild
        ///     would turn an enqueuer bug into a permanent, invisible performance regression.
        /// </summary>
        private static DirtyRect? ParseDirtyRect(VillagerTask task)
        {
            if (task?.Attributes == null) return null;

            var present = 0;
            var values = new float[DirtyRectKeys.Length];
            for (var i = 0; i < DirtyRectKeys.Length; i++)
            {
                if (!task.Attributes.TryGetValue(DirtyRectKeys[i], out var raw)) continue;
                if (!float.TryParse(raw, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out values[i]))
                    throw new FormatException(
                        $"hna_partition attribute {DirtyRectKeys[i]}='{raw}' is not a number.");
                present++;
            }

            if (present == 0) return null;
            if (present != DirtyRectKeys.Length)
                throw new ArgumentException(
                    $"hna_partition carries {present}/{DirtyRectKeys.Length} dirty-rect bounds; " +
                    "an incremental partition needs all four or none.");

            return new DirtyRect(values[0], values[1], values[2], values[3]);
        }

        /// <summary>
        ///     Start this village's partition and return immediately. The work itself is a
        ///     coroutine on <see cref="PartitionRunner" /> that yields whenever the frame
        ///     budget is spent — see that class for why a task that runs to completion inside
        ///     one Update is the wrong shape for 700ms of work.
        /// </summary>
        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            if (task?.Attributes == null
                || !task.Attributes.TryGetValue("village_id", out var villageId)
                || string.IsNullOrEmpty(villageId))
                throw new ArgumentException(
                    "hna_partition requires a village_id attribute; every enqueue site sets " +
                    "one, and the in-flight guard and every cache are keyed by it.");

            // A partition is already in flight — for THIS village or any other. Defer:
            // partitions are one-at-a-time by contract (see PartitionRunner.s_inFlight for
            // the shared statics that depend on it). The task must be re-queued rather than
            // dropped, since it may carry a newer dirty rect than the one running.
            if (PartitionRunner.IsAnyRunning)
            {
                task.NotBefore = Time.time + InFlightRetrySeconds;
                var requeued = GlobalTaskQueue.Enqueue(task);
                // Logged, not just returned: GlobalTaskQueue never surfaces a handler's
                // TaskResult.Data anywhere, so without this the guard that keeps two
                // partitions from running concurrently — the one whose absence corrupted a
                // village down to 10 regions — has no observable evidence that it fired.
                DebugLog.Event("Region", "partition_deferred",
                    ("village_key", villageId),
                    ("running", PartitionRunner.RunningVillage),
                    ("requeued", requeued));
                return TaskResult.Ok(new Dictionary<string, string>
                {
                    { "village_key", villageId }, { "reason", "deferred_in_flight" },
                });
            }

            PartitionRunner.Run(villageId, PartitionRoutine(task));
            return TaskResult.Ok(new Dictionary<string, string>
            {
                { "village_key", villageId }, { "reason", "started" },
            });
        }

        /// <summary>How long a task waits before retrying when this village is mid-partition.</summary>
        private const float InFlightRetrySeconds = 1f;

        /// <summary>
        ///     The partition itself. Yields to <see cref="PartitionRunner.ShouldYield" />
        ///     between stages so no single frame absorbs more than the budget. Its outcome is
        ///     logged rather than returned: the task it came from completed the moment this
        ///     coroutine started.
        /// </summary>
        private IEnumerator PartitionRoutine(VillagerTask task)
        {
            // Freeze villager movement across the rebuild + settle so a path
            // computed on the old navmesh can't run a villager off a ledge before
            // autoRepath catches up. Set here (the rebuild moment) rather than at
            // enqueue — the graph is stable while the task waits in the queue.
            VillageNavLock.RequestHold(NavRebuildSettleSeconds);

            // Per-partition profiler: per-stage *_ms + native-query counters, flushed as a
            // single "PartitionProfile" event before the success return (see PartitionProfile).
            PartitionProfile.Begin();

            var allAnchors = CollectSeedAnchors();
            var anchors = FilterAnchorsByTask(allAnchors, task);
            // Snap each home/seed to a walkable, capsule-clear cell before it drives the
            // bake elevation and the Pass-1 reachability flood. A registry-anchored home
            // (or a anchor) can sit inside its own colliders; flooding from there reaches a
            // couple of cells and the region graph/boundary degenerates. Resolved against
            // real geometry + the vanilla navmesh, so it works before slot 31 is (re)baked.
            var seedsMark = PartitionProfile.Mark();
            anchors = ResolveWalkableSeeds(anchors);
            PartitionProfile.Since("seeds", seedsMark);
            if (PartitionRunner.ShouldYield()) yield return null;
            var village = ResolveVillage(task, anchors);
            if (village == null)
            {
                Plugin.Log?.LogWarning(
                    "[Region] Partition skipped: no existing village resolved for this task " +
                    "(villages are created at registry placement; nothing to partition).");
                DebugLog.Event("Region", "partition_done",
                    ("regions", 0), ("links", 0), ("reason", "no_village"));
                yield break;
            }

            var villageKey = village.VillageId;

            // A task carrying a dirty rect is an INCREMENTAL partition: only geometry near
            // that rect is re-probed. A task without one is a full rebuild. There is no
            // middle ground and no recovery mode — a half-specified rect is a bug in the
            // enqueuer, so ParseDirtyRect throws rather than quietly rebuilding everything.
            var dirtyRect = ParseDirtyRect(task);

            // Seed the box from THIS village's patrol ring only. Resolved after the village
            // (not before, as the combined-bounds version was) because the lookup is keyed by
            // village id — see VillageAreaManager.TryGetAreaBounds for what the combined form
            // did to a two-village world.
            var hasPatrolBounds = VillageAreaManager.TryGetAreaBounds(
                villageKey, out var patrolMinX, out var patrolMinZ, out var patrolMaxX, out var patrolMaxZ);

            float minX, minZ, maxX, maxZ;
            if (hasPatrolBounds && anchors != null && anchors.Count > 0)
            {
                minX = patrolMinX;
                minZ = patrolMinZ;
                maxX = patrolMaxX;
                maxZ = patrolMaxZ;
                foreach (var anchor in anchors)
                {
                    if (anchor.x - RegionBuildRadius < minX) minX = anchor.x - RegionBuildRadius;
                    if (anchor.z - RegionBuildRadius < minZ) minZ = anchor.z - RegionBuildRadius;
                    if (anchor.x + RegionBuildRadius > maxX) maxX = anchor.x + RegionBuildRadius;
                    if (anchor.z + RegionBuildRadius > maxZ) maxZ = anchor.z + RegionBuildRadius;
                }
            }
            else if (hasPatrolBounds)
            {
                minX = patrolMinX;
                minZ = patrolMinZ;
                maxX = patrolMaxX;
                maxZ = patrolMaxZ;
            }
            else if (anchors != null && anchors.Count > 0)
            {
                minX = maxX = anchors[0].x;
                minZ = maxZ = anchors[0].z;
                foreach (var anchor in anchors)
                {
                    if (anchor.x - RegionBuildRadius < minX) minX = anchor.x - RegionBuildRadius;
                    if (anchor.z - RegionBuildRadius < minZ) minZ = anchor.z - RegionBuildRadius;
                    if (anchor.x + RegionBuildRadius > maxX) maxX = anchor.x + RegionBuildRadius;
                    if (anchor.z + RegionBuildRadius > maxZ) maxZ = anchor.z + RegionBuildRadius;
                }
            }
            else
            {
                Plugin.Log?.LogInfo("[Region] Partition skipped: no village areas and no villager anchors.");
                DebugLog.Event("Region", "partition_done",
                    ("regions", 0), ("links", 0), ("reason", "no_anchors_or_areas"));
                yield break;
            }

            // The footprint border is the outside-flood's SEED RING. Anchors +/- RegionBuildRadius
            // knows nothing about where the player actually walled, so a perimeter beyond that
            // radius leaves the ring INSIDE the village — the flood is seeded inside its own walls
            // and classifies the whole settlement as outside (measured: 88% of the footprint carved
            // NotWalkable, villagers stranded, registry stuck on "needs a perimeter wall"). Grow
            // the box until every player-built piece is inside it.
            var footprintMark = PartitionProfile.Mark();
            ExpandFootprintToVillagePieces(villageKey, anchors, ref minX, ref minZ, ref maxX, ref maxZ);
            PartitionProfile.Since("footprint", footprintMark);
            if (PartitionRunner.ShouldYield()) yield return null;

            // Bake a fresh NavMesh surface for the villager agent (slot 31)
            // over this village's bounds. Without this, RegionBuilder's
            // NavMesh queries against slot 31 return no triangles because
            // Valheim only bakes the Humanoid agent (slot 1).
            if (anchors == null || anchors.Count == 0)
            {
                Plugin.Log?.LogError(
                    $"[Region] Partition aborted: no villager anchors for village '{villageKey}'. " +
                    "Cannot determine bake elevation without anchors — refusing to bake at sea-level fallback.");
                DebugLog.Event("Region", "partition_done",
                    ("regions", 0), ("links", 0), ("reason", "no_anchors_for_bake_y"));
                yield break;
            }

            float bakeMinY = float.MaxValue, bakeMaxY = float.MinValue;
            foreach (var anchor in anchors)
            {
                if (anchor.y < bakeMinY) bakeMinY = anchor.y;
                if (anchor.y > bakeMaxY) bakeMaxY = anchor.y;
            }

            const float bakeYPadding = 30f;
            var bakeBounds = new Bounds();
            bakeBounds.SetMinMax(
                new Vector3(minX, bakeMinY - bakeYPadding, minZ),
                new Vector3(maxX, bakeMaxY + bakeYPadding, maxZ));

            var bakeMark = PartitionProfile.Mark();
            var bakeHolder = new NavMeshBakeManager.BakeResultHolder();
            yield return NavMeshBakeManager.BakeVillage(
                bakeBounds, villageKey, dirtyRect, bakeHolder);
            var bakeResult = bakeHolder.Result;
            PartitionProfile.Since("bake", bakeMark);
            if (PartitionRunner.ShouldYield()) yield return null;
            DebugLog.Event("NavMeshBake", "village_bake",
                ("success", bakeResult.Success),
                ("sources", bakeResult.SourceCount),
                ("terrain_sources", bakeResult.TerrainSourceCount),
                ("piece_sources", bakeResult.PieceSourceCount),
                ("doors_blocked", bakeResult.DoorsBlocked),
                ("door_pieces_dropped", bakeResult.DoorPiecesDropped),
                ("beds_blocked", bakeResult.BedsBlocked),
                ("outside_cells", bakeResult.OutsideCellsCount),
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
            var triTerrainMark = PartitionProfile.Mark();
            var terrainHolder = new RegionBuilder.BuildResultHolder();
            yield return RegionBuilder.BuildFromTriangulation(
                villageKey, SurfaceKind.Terrain, minX, minZ, maxX, maxZ, anchors, dirtyRect,
                terrainHolder);
            var terrainResult = terrainHolder.Result;
            PartitionProfile.Since("tri_terrain", triTerrainMark);
            if (PartitionRunner.ShouldYield()) yield return null;
            var triPieceMark = PartitionProfile.Mark();
            var pieceHolder = new RegionBuilder.BuildResultHolder();
            yield return RegionBuilder.BuildFromTriangulation(
                villageKey, SurfaceKind.Piece, minX, minZ, maxX, maxZ, anchors, dirtyRect,
                pieceHolder);
            var pieceResult = pieceHolder.Result;
            PartitionProfile.Since("tri_piece", triPieceMark);
            if (PartitionRunner.ShouldYield()) yield return null;

            // Cross-kind BFS reachability: prune terrain regions not
            // reachable from anchors through the combined terrain↔piece
            // adjacency graph. Pieces (stone tiles, floors, paths) act as
            // legitimate bridges between terrain regions; without this,
            // terrain pieced over by walkable floor pieces appears
            // disconnected and gets falsely pruned.
            //
            // Captured here (not persisted) so Pass 3's discovered piece-
            // step edges can be merged in before the BfsAdjacencyStore
            // gets its final snapshot — otherwise vv_graph bfs would walk
            // a graph missing every piece-to-piece edge discovered by the
            // cell-level flood (and the trace would show "no path" for
            // any region only reachable via the piece chain).
            var crossKindMark = PartitionProfile.Mark();
            var crossKindAdj = BuildCrossKindAdjacency(terrainResult, pieceResult, anchors);
            PartitionProfile.Since("crosskind", crossKindMark);
            if (PartitionRunner.ShouldYield()) yield return null;


            // Combine terrain + piece region sets (union + shadow suppression +
            // cascade) into the inputs the prune/graph stages consume.
            var combineMark = PartitionProfile.Mark();
            RegionBuilder.CombineTerrainAndPiece(
                villageKey,
                terrainResult, pieceResult,
                out var combinedRegionIds, out var combinedCentroids, out var combinedLinks,
                out var combinedLookup, out var combinedBoundary, out var combinedTriangles,
                out var kindMap);
            PartitionProfile.Since("combine", combineMark);
            if (PartitionRunner.ShouldYield()) yield return null;

            // Rubber-band prune: drop regions whose footprint sits outside
            // the outermost layer of player-placed wall pieces.
            var pruneMark = PartitionProfile.Mark();
            var pruneResult = new RubberBandPrune.PruneResult();
            yield return RubberBandPrune.Apply(
                combinedRegionIds, combinedCentroids, combinedLookup,
                combinedBoundary, combinedLinks, kindMap, combinedTriangles,
                anchors,
                minX, minZ, maxX, maxZ,
                villageKey,
                dirtyRect,
                pruneResult);
            var rbStats = pruneResult.Stats;
            var droppedRubberBand = pruneResult.DroppedRegionIds;
            var pass3DiscoveredEdges = pruneResult.Pass3DiscoveredEdges;
            var anchorReachableCells = pruneResult.AnchorReachableCells;
            var outsideCells = pruneResult.OutsideCells;
            var prunedPieceKeys = pruneResult.PrunedPieceKeys;
            var gateMarkers = pruneResult.GateMarkers;
            PartitionProfile.Since("prune", pruneMark);
            if (PartitionRunner.ShouldYield()) yield return null;

            // Merge Pass 3 discovered edges into the cross-kind adjacency
            // (if it was built) and publish the merged graph to
            // BfsAdjacencyStore for vv_graph bfs. Pass 3 records each
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
                ("anchor_reachable_terrain_cells", rbStats.AnchorReachableTerrainCells),
                ("pass3_seeds", rbStats.Pass3Seeds),
                ("anchor_reachable_piece_keys", rbStats.AnchorReachablePieceKeys),
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
            // no-anchors case already returned earlier at no_anchors_or_areas. If the seed sits
            // inside (or right against) an existing, non-empty graph, this is a MUTATION of
            // that same village, not a new one: committing the empty result would clobber a
            // working graph. (Hit by: rebuild the registry, then revive a villager whose
            // stale home re-seeds a degenerate partition over the good graph.) Keep the
            // existing graph instead of replacing it with nothing.
            if (combinedRegionIds.Count == 0)
            {
                var existing = FindContainingNonEmptyGraph(anchors);
                if (existing != null)
                {
                    Plugin.Log?.LogWarning(
                        $"[Region] Partition for key={villageKey} produced 0 regions, but its seed is inside " +
                        $"existing graph '{existing.RegisteredVillageKey}' ({existing.RegionCount} regions, " +
                        $"{existing.LinkCount} links) — treating as a failed mutation of the same village; " +
                        "keeping the existing graph rather than clobbering it.");
                    DebugLog.Event("Region", "partition_done",
                        ("regions", existing.RegionCount),
                        ("links", existing.LinkCount),
                        ("village_key", existing.RegisteredVillageKey ?? villageKey),
                        ("reason", "degenerate_kept_existing"));
                    yield break;
                }
            }

            var commitGraphMark = PartitionProfile.Mark();
            var graph = village.GetOrCreateGraph();
            graph.SetGraph(combinedRegionIds, combinedLinks,
                combinedCentroids, combinedLookup, combinedBoundary, kindMap);
            graph.SetGates(gateMarkers);
            // Promote the perimeter classification to committed graph state so it
            // persists (v4 ZDO) and gives the incremental reconcilers a baseline
            // to diff against. Must run after SetGraph so HasClassification flips
            // only on a populated graph.
            graph.SetClassification(outsideCells, anchorReachableCells, prunedPieceKeys);
            PartitionProfile.Since("commit_graph", commitGraphMark);
            if (PartitionRunner.ShouldYield()) yield return null;

            // Perimeter-wall requirement: if the bake's raw outside flood reaches the
            // registry cell, the village isn't sealed by a wall. Persist the flag so the
            // registry station can refuse to open (workbench-style) until it's walled in.
            // Uses the bake's RAW set (whole-footprint when unsealed) — NOT the committed
            // classification, which is empty on a degenerate 0-region partition. A rebake
            // fires on the next piece change, so the flag self-clears once the wall closes.
            // Publish the footprint so chest/station lookups can ask "is this inside the
            // village?" instead of guessing with a radius around one villager's anchor.
            village.SetFootprint(minX, minZ, maxX, maxZ);

            if (bakeResult.OutsideCells != null)
            {
                // Test the registry AND the anchor triad, not the registry alone.
                //
                // The registry pivot sits inside the registry building's own stone shell —
                // measured on a deliberately breached village: all four of its neighbouring
                // cells report WallBlocks=TRUE, so no outside flood can reach it whatever
                // happens to the perimeter. The flag therefore only ever fired for a village
                // whose registry still stood in the open, i.e. a brand new one, and read
                // False for an established village with a hole in its wall — which is
                // precisely the state it claims to describe.
                //
                // The triad is by construction walkable ground inside the village, so the
                // flood reaching any of it means the village genuinely is not sealed. On a
                // village's very first partition the triad does not exist yet (
                // EnsureAnchorTriad runs later in this method), and the registry test alone
                // is the right answer for that case anyway.
                var unsealed = false;
                if (village.TryGetAnchor(VillageAnchor.Registry, out var registryPos))
                    unsealed = RubberBandPrune.IsOutsideCell(registryPos, bakeResult.OutsideCells);

                if (!unsealed)
                    foreach (var triad in village.TriadAnchors)
                        if (RubberBandPrune.IsOutsideCell(triad, bakeResult.OutsideCells))
                        {
                            unsealed = true;
                            break;
                        }

                if (unsealed != village.NeedsPerimeterWall)
                    DebugLog.Event("Region", "perimeter_wall_changed",
                        ("village_key", villageKey), ("needs_wall", unsealed));
                village.NeedsPerimeterWall = unsealed;
            }

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
            var invalidateMark = PartitionProfile.Mark();
            var pathsInvalidated = VillagerAIManager.InvalidatePathsAfterRebake();
            PartitionProfile.Since("invalidate_paths", invalidateMark);
            if (PartitionRunner.ShouldYield()) yield return null;
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

            // Only build the (per-region, per-link) summary strings if something will read
            // them — they are pure input to a telemetry line that is off by default.
            if (Settings.DevSettings.WritePathTelemetry)
                PathTelemetry.LogRegionGraph(combinedRegionIds.Count, combinedLinks.Count,
                    minX, minZ, maxX, maxZ,
                    BuildRegionCentersString(graph), BuildLinksSummaryString(combinedLinks));

            Plugin.Log?.LogInfo(
                $"[Region] Partition complete: {combinedRegionIds.Count} regions " +
                $"(terrain={terrainResult.RegionIds.Count}, piece={pieceResult.RegionIds.Count}), " +
                $"{combinedLinks.Count} links " +
                $"(bounds {minX:F0},{minZ:F0} to {maxX:F0},{maxZ:F0}, key={villageKey})");

            // Persist the freshly built graph onto the durable village ZDO (1-to-1),
            // then publish the area/caches from the village. This replaces the old
            // per-guard PatrolPersistence.SaveHnaGraph write.
            var saveMark = PartitionProfile.Mark();
            village.SaveGraph();
            PartitionProfile.Since("save_graph", saveMark);
            if (PartitionRunner.ShouldYield()) yield return null;

            // Now that the graph is rebuilt + saved, create/repair/validate the village's
            // self-healing anchor triad (3 walkable, mutually connected, founder-reachable
            // points near the registry). Idempotent and cheap on the already-valid path.
            // Must run AFTER SaveGraph (so the navmesh/graph reflect the committed state)
            // and BEFORE RefreshFromVillage (so the area/caches publish from a valid triad).
            var triadMark = PartitionProfile.Mark();
            VillageRegistry.EnsureAnchorTriad(village);
            PartitionProfile.Since("anchor_triad", triadMark);
            if (PartitionRunner.ShouldYield()) yield return null;

            // Re-seat every village's orders on the tokens physically inside it. All
            // villages, not just this one: a token moved OUT of another village has to be
            // released there, and that village may not repartition for a long time. Each
            // village only writes its own orders, so doing them together is safe.
            // Runs here because the footprint it scopes by was just published above.
            var reseated = Villages.WorkOrderAdoption.ReconcileAll();
            if (reseated > 0)
                DebugLog.Event("Region", "work_orders_reseated",
                    ("triggered_by", villageKey), ("changed", reseated));
            if (PartitionRunner.ShouldYield()) yield return null;

            var areaMark = PartitionProfile.Mark();
            VillageAreaManager.RefreshFromVillage(village);
            PartitionProfile.Since("area_refresh", areaMark);
            if (PartitionRunner.ShouldYield()) yield return null;

            // The boundary may have grown or shrunk (e.g. the player walled off a
            // section). InvalidatePathsAfterRebake above only cleared the low-level
            // NavMeshAgent path; each patroller's cached waypoint list still traces
            // the PRE-change boundary. Re-derive every patrol route from the fresh
            // graph so a guard stops marching into a now-sealed area. (Done here,
            // after SetGraph + SaveGraph, so StartDiscovery reads the committed graph.)
            var patrolMark = PartitionProfile.Mark();
            var routesRebuilt = VillagerAIManager.ResetPatrolRoutesAfterRepartition();
            PartitionProfile.Since("patrol_routes", patrolMark);
            if (PartitionRunner.ShouldYield()) yield return null;
            if (routesRebuilt > 0)
                Plugin.Log?.LogInfo(
                    $"[Region] Rebuilt patrol routes for {routesRebuilt} patroller(s) after repartition");

            PartitionProfile.Emit(villageKey);

            DebugLog.Event("Region", "partition_done",
                ("regions", combinedRegionIds.Count),
                ("regions_terrain", terrainResult.RegionIds.Count),
                ("regions_piece", pieceResult.RegionIds.Count),
                ("links", combinedLinks.Count),
                ("village_key", villageKey),
                ("reason", "ok"));
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
        ///     <c>anchor_x/anchor_z</c> pair, or (3) the first seed anchor — by id, graph
        ///     coverage, or registry-anchor proximity. NEVER mints: a partition runs for a
        ///     village that already exists (created at registry placement). Returns null
        ///     when none resolves, and the caller aborts rather than fabricating one.
        /// </summary>
        private static Village ResolveVillage(VillagerTask task, List<Vector3> anchors)
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
                var y = anchors != null && anchors.Count > 0 ? anchors[0].y : 0f;
                var anchor = new Vector3(ax, y, az);
                return VillageRegistry.GetVillageCovering(anchor) ?? VillageRegistry.FindNearAnchor(anchor);
            }

            if (anchors != null && anchors.Count > 0)
                return VillageRegistry.GetVillageCovering(anchors[0]) ?? VillageRegistry.FindNearAnchor(anchors[0]);

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
        ///     Map each village seed (stored villager home / anchor) to the nearest
        ///     walkable, agent-clear cell via <see cref="RegistrySeedResolver" />, so the
        ///     bake elevation and Pass-1 flood seed from ground the agent can stand on
        ///     rather than a point buried in the registry/anchor colliders. Seeds that can't
        ///     be resolved are kept as-is and logged loudly (no silent substitution).
        /// </summary>
        private static List<Vector3> ResolveWalkableSeeds(List<Vector3> anchors)
        {
            if (anchors == null || anchors.Count == 0) return anchors;

            var snapped = new List<Vector3>(anchors.Count);
            var moved = 0;
            foreach (var anchor in anchors)
            {
                if (RegistrySeedResolver.TryResolveWalkableSeed(anchor, out var seed))
                {
                    if ((seed - anchor).sqrMagnitude > 0.25f) moved++;
                    snapped.Add(seed);
                }
                else
                {
                    snapped.Add(anchor);
                    Plugin.Log?.LogWarning(
                        $"[Region] No walkable seed near home ({anchor.x:F1},{anchor.y:F1},{anchor.z:F1}); " +
                        "using it as-is (flood may degenerate).");
                }
            }

            if (moved > 0)
                Plugin.Log?.LogInfo(
                    $"[Region] Snapped {moved}/{anchors.Count} village seed(s) onto walkable cells near their anchor.");
            return snapped;
        }

        /// <summary>
        ///     Seed anchors the partition builds around: every villager home position
        ///     (<see cref="VillagerAIManager.GetAllAnchorPositions" />) PLUS every durable
        ///     village's registry anchor (<see cref="Village.Anchor" />). The registry
        ///     anchors are what let a freshly-placed registry — a village with no villagers
        ///     yet — bake its region graph, which is the prerequisite for spawning its
        ///     first villager. Without them the anchor list is empty for a registry-only
        ///     village and the partition no-ops at no_anchors_or_areas. Deduped against
        ///     villager homes (1 m²) so a registry co-located with a villager home isn't
        ///     counted twice. Returns a fresh list (GetAllAnchorPositions already does).
        /// </summary>
        /// <summary>
        ///     Grow the partition footprint until every player-built piece near the anchors lies
        ///     inside it, with <see cref="SeedRingMargin" /> of clearance.
        ///     <para>
        ///         WHY: the footprint border doubles as the outside-flood's seed ring. Sizing it
        ///         from anchors alone (+/- <see cref="RegionBuildRadius" />) means a village whose
        ///         perimeter wall sits further out than that radius gets its flood seeded INSIDE
        ///         the wall — the flood then fills the settlement from within and every open cell
        ///         is classified outside, so the bake carves it NotWalkable and villagers strand.
        ///         Only pieces count (a <see cref="Piece" /> component): terrain, rocks and trees
        ///         must NOT drag the footprint outward.
        ///     </para>
        /// </summary>
        /// <summary>
        ///     True when <paramref name="pos" /> sits strictly closer to some other village's
        ///     anchor than to this village's anchor centroid. A plain Voronoi split on XZ —
        ///     it needs no graph, which matters because this runs while deciding the bounds
        ///     the graph will be built from.
        /// </summary>
        private static bool IsNearerToAnotherVillage(
            Vector3 pos, float cx, float cz, List<Vector3> rivalAnchors)
        {
            if (rivalAnchors.Count == 0) return false;

            var dx = pos.x - cx;
            var dz = pos.z - cz;
            var ownSq = dx * dx + dz * dz;
            for (var i = 0; i < rivalAnchors.Count; i++)
            {
                var rdx = pos.x - rivalAnchors[i].x;
                var rdz = pos.z - rivalAnchors[i].z;
                if (rdx * rdx + rdz * rdz < ownSq) return true;
            }

            return false;
        }

        private static void ExpandFootprintToVillagePieces(
            string villageKey,
            List<Vector3> anchors, ref float minX, ref float minZ, ref float maxX, ref float maxZ)
        {
            if (anchors == null || anchors.Count == 0) return;

            float cx = 0f, cz = 0f;
            foreach (var a in anchors)
            {
                cx += a.x;
                cz += a.z;
            }

            cx /= anchors.Count;
            cz /= anchors.Count;

            var mask = Villager.AI.Navigation.NavMeshBakeManager.SolidMask;
            if (mask == 0) return;

            var hits = Physics.OverlapBox(
                new Vector3(cx, 0f, cz),
                new Vector3(FootprintScanRadius, 5000f, FootprintScanRadius),
                Quaternion.identity, mask, QueryTriggerInteraction.Ignore);

            // Other villages' anchors, so a piece can be attributed to the nearest village
            // rather than swallowed by whichever one happens to be partitioning. The scan box
            // is FootprintScanRadius (100m) per side, so with two settlements closer than
            // 200m this village's footprint would otherwise grow to enclose the neighbour's
            // build — the same class of cross-village bleed that TryGetCombinedBounds caused.
            var rivalAnchors = new List<Vector3>();
            foreach (var other in VillageRegistry.EnumerateAll())
            {
                if (other == null || other.VillageId == villageKey) continue;
                var a = other.Anchor;
                if (a != Vector3.zero) rivalAnchors.Add(a);
            }

            float pMinX = float.MaxValue, pMinZ = float.MaxValue;
            float pMaxX = float.MinValue, pMaxZ = float.MinValue;
            var pieces = 0;
            var foreignPieces = 0;
            foreach (var col in hits)
            {
                if (col == null) continue;
                if (col.GetComponentInParent<Piece>() == null) continue;
                if (IsNearerToAnotherVillage(col.transform.position, cx, cz, rivalAnchors))
                {
                    foreignPieces++;
                    continue;
                }

                var b = col.bounds;
                if (b.min.x < pMinX) pMinX = b.min.x;
                if (b.min.z < pMinZ) pMinZ = b.min.z;
                if (b.max.x > pMaxX) pMaxX = b.max.x;
                if (b.max.z > pMaxZ) pMaxZ = b.max.z;
                pieces++;
            }

            if (foreignPieces > 0)
                Plugin.Log?.LogInfo(
                    $"[Region] Footprint scan for {villageKey} ignored {foreignPieces} piece(s) " +
                    "nearer to another village");

            if (pieces == 0) return;

            var wantMinX = Mathf.Min(minX, pMinX - SeedRingMargin);
            var wantMinZ = Mathf.Min(minZ, pMinZ - SeedRingMargin);
            var wantMaxX = Mathf.Max(maxX, pMaxX + SeedRingMargin);
            var wantMaxZ = Mathf.Max(maxZ, pMaxZ + SeedRingMargin);

            // Clamp on AREA, shrinking both axes by the same factor about the anchor centroid
            // so a lopsided build keeps its long side instead of losing it to a symmetric cap.
            var wantW = wantMaxX - wantMinX;
            var wantH = wantMaxZ - wantMinZ;
            float clampMinX = wantMinX, clampMinZ = wantMinZ;
            float clampMaxX = wantMaxX, clampMaxZ = wantMaxZ;

            var wantCells = (double)wantW * wantH;
            if (wantCells > MaxFootprintCells)
            {
                var scale = Mathf.Sqrt(MaxFootprintCells / (float)wantCells);
                clampMinX = cx - (cx - wantMinX) * scale;
                clampMaxX = cx + (wantMaxX - cx) * scale;
                clampMinZ = cz - (cz - wantMinZ) * scale;
                clampMaxZ = cz + (wantMaxZ - cz) * scale;

                Plugin.Log?.LogWarning(
                    $"[Region] Footprint clamped to {MaxFootprintCells} cells around " +
                    $"({cx:F0},{cz:F0}): village pieces span x[{pMinX:F0}..{pMaxX:F0}] " +
                    $"z[{pMinZ:F0}..{pMaxZ:F0}] ({wantCells:F0} cells wanted). The flood will " +
                    "seed INSIDE the build on every side and those cells will mis-classify as " +
                    "outside. Split this into separate villages.");
            }

            var grewX = clampMaxX - clampMinX - (maxX - minX);
            var grewZ = clampMaxZ - clampMinZ - (maxZ - minZ);
            minX = clampMinX;
            minZ = clampMinZ;
            maxX = clampMaxX;
            maxZ = clampMaxZ;

            if (grewX > 0.01f || grewZ > 0.01f)
                Plugin.Log?.LogInfo(
                    $"[Region] Footprint grown to enclose {pieces} village piece collider(s): " +
                    $"+{grewX:F1}m x, +{grewZ:F1}m z -> x[{minX:F0}..{maxX:F0}] z[{minZ:F0}..{maxZ:F0}]");
        }

        private static List<Vector3> CollectSeedAnchors()
        {
            var anchors = VillagerAIManager.GetAllAnchorPositions();
            foreach (var village in VillageRegistry.EnumerateAll())
            {
                var a = village.Anchor;
                if (a == Vector3.zero) continue;
                var duplicate = false;
                foreach (var existing in anchors)
                    if ((existing - a).sqrMagnitude < 1f) { duplicate = true; break; }
                if (!duplicate) anchors.Add(a);
            }

            return anchors;
        }

        /// <summary>
        ///     Scope the world-wide seed-anchor list down to a SINGLE village's cluster
        ///     so a partition never spans more than one village. Critical for multi-
        ///     village worlds: <see cref="IsReady" /> requires every scoped anchor's
        ///     zone loaded, and <see cref="CollectSeedAnchors" /> returns ALL villages'
        ///     anchors. A second village 350 m away can never have its zone loaded while
        ///     the player/server is at the first, so an unscoped (whole-world) partition
        ///     deferred on that distant zone forever and was eventually dropped — no
        ///     graph ever built for ANY village. Scoping to one village removes that
        ///     coupling: each village bakes/builds/persists its own graph independently.
        ///     <para>The scope center is the task's explicit anchor (patrol-requested
        ///     partitions stamp anchor_x/anchor_z); for an anchor-less global task
        ///     (structure-change rebuild, <c>vv_repartition</c>) we resolve the village
        ///     at the first seed so the rebuild still targets exactly one village. A
        ///     single-village world is unaffected — every anchor is inside the one
        ///     cluster.</para>
        /// </summary>
        private static List<Vector3> FilterAnchorsByTask(List<Vector3> allAnchors, VillagerTask task)
        {
            if (allAnchors == null || allAnchors.Count == 0) return allAnchors;

            float anchorX, anchorZ;
            if (task?.Attributes != null &&
                task.Attributes.TryGetValue("anchor_x", out var axStr) &&
                task.Attributes.TryGetValue("anchor_z", out var azStr) &&
                float.TryParse(axStr, NumberStyles.Float, CultureInfo.InvariantCulture, out anchorX) &&
                float.TryParse(azStr, NumberStyles.Float, CultureInfo.InvariantCulture, out anchorZ))
            {
                // Explicit scope center from the requesting villager's anchor.
            }
            else
            {
                // No explicit anchor (global rebuild): still scope to ONE village so a
                // distant village's unloaded zone can't block this partition. Center on
                // the resolved village's anchor, falling back to the first seed.
                var target = ResolveVillage(task, allAnchors);
                var center = target != null && target.Anchor != Vector3.zero
                    ? target.Anchor
                    : allAnchors[0];
                anchorX = center.x;
                anchorZ = center.z;
            }

            var r2 = VillageClusterRadius * VillageClusterRadius;
            var filtered = new List<Vector3>();
            foreach (var anchor in allAnchors)
            {
                float dx = anchor.x - anchorX, dz = anchor.z - anchorZ;
                if (dx * dx + dz * dz <= r2) filtered.Add(anchor);
            }

            Plugin.Log?.LogInfo(
                $"[Region] Scoped anchors to village near ({anchorX:F0},{anchorZ:F0}): " +
                $"{filtered.Count}/{allAnchors.Count} within {VillageClusterRadius}m");
            if (filtered.Count == 0)
                Plugin.Log?.LogWarning(
                    $"[Region] No anchors within {VillageClusterRadius}m of ({anchorX:F0},{anchorZ:F0})");
            return filtered;
        }

        /// <summary>
        ///     Builds the cross-kind region adjacency graph (terrain↔terrain +
        ///     piece↔piece in-pass edges, plus terrain↔piece cross-kind edges via
        ///     shared quantized vertex positions and via vertex-to-vertex
        ///     proximity) and locates anchor-anchored terrain seeds. Persists both
        ///     to <see cref="BfsAdjacencyStore" /> so the <c>vv_graph bfs</c> dev
        ///     command can compute paths back to a anchor without re-running the
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
        ///     conditions (no terrain regions, no anchor-mapped seeds).
        /// </summary>
        private static (Dictionary<string, HashSet<string>> adjacency,
                        HashSet<string> seeds,
                        Dictionary<string, BfsEdgeMeta> edgeMeta)?
            BuildCrossKindAdjacency(
            RegionBuilder.BuildResult terrainResult,
            RegionBuilder.BuildResult pieceResult,
            List<Vector3> anchors)
        {
            if (terrainResult.RegionIds == null || terrainResult.RegionIds.Count == 0) return null;

            // --- Build combined adjacency ---
            var combinedAdj = new Dictionary<string, HashSet<string>>();
            // Per-edge metadata for vv_graph bfs. Tracks which mechanism(s)
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

            // --- Find seeds: closest terrain region centroid to each anchor ---
            var seeds = new HashSet<string>();
            const float anchorYTol = 3f;
            const float anchorXZTol = 30f;
            const float anchorXZTolSq = anchorXZTol * anchorXZTol;
            if (anchors != null)
                foreach (var anchor in anchors)
                {
                    string closest = null;
                    var closestDistSq = float.MaxValue;
                    foreach (var rid in terrainResult.RegionIds)
                    {
                        if (!terrainResult.Centroids.TryGetValue(rid, out var c)) continue;
                        if (Mathf.Abs(c.y - anchor.y) > anchorYTol) continue;
                        float dx = c.x - anchor.x, dz = c.z - anchor.z;
                        var dSq = dx * dx + dz * dz;
                        if (dSq > anchorXZTolSq) continue;
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
                var anchorCount = anchors?.Count ?? 0;
                var regionCount = terrainResult.RegionIds?.Count ?? 0;
                Plugin.Log?.LogError(
                    "[Region] CrossKind adjacency aborted: no anchor mapped to any terrain region " +
                    $"(anchors={anchorCount}, terrain_regions={regionCount}). " +
                    "Refusing to seed BFS from a synthetic largest-region fallback; vv_graph bfs will report no data.");
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