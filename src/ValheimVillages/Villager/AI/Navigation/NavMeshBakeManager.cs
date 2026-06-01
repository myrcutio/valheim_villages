using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Settings;
using ValheimVillages.TaskQueue.Handlers;
using ValheimVillages.Villager.AI.Pathfinding;
using Object = UnityEngine.Object;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Runtime NavMesh bake for the custom villager agent (slot 31).
    ///     Valheim bakes the Humanoid agent (slot 1) for its own NPCs but the
    ///     custom slot 31 has no baked surface — `EnsureRegistered()` only adds
    ///     the settings, it does not bake. This manager owns the per-village bake
    ///     lifecycle: collect sources from colliders in the village bounds,
    ///     `BuildNavMeshData` for slot 31, and `AddNavMeshData` so that queries
    ///     against slot 31 (RegionBuilder, NavMeshLinkPlacer, patrol) return real
    ///     triangles instead of misses.
    ///     One bake per `hna_partition` run; the previous instance is removed
    ///     before the next add so NavMesh data does not accumulate. Hot reload
    ///     cleanup via `[RegisterCleanup]`.
    /// </summary>
    public static class NavMeshBakeManager
    {
        // Two separate bakes, both into agent slot 31. Unity's NavMesh allows
        // multiple NavMeshData instances to coexist for the same agent type;
        // queries union them. Splitting the bake by source kind lets
        // RegionBuilder run two filter/build passes with per-kind tuning
        // (terrain has gentler edge cases than buildable pieces).
        //
        // Instances are stored on a DontDestroyOnLoad MonoBehaviour found by
        // name so that a hot-reloaded assembly inherits the prior assembly's
        // active bakes and can Remove() them before adding fresh data. Plain
        // static fields would leak: on reload the static resets to default,
        // .valid becomes false, the Remove guard skips, and the prior
        // instance stays orphaned inside Unity's NavMesh — stacking layer
        // after layer of village geometry across reloads.
        private const string HolderName = "VV_NavMeshBakeHolder";
        private static NavMeshBakeHolder s_holder;

        private static readonly List<NavMeshBuildSource> s_terrainSources = new();
        private static readonly List<NavMeshBuildSource> s_pieceSources = new();

        /// <summary>
        ///     Count of phantom door-blocker sources appended at the END of
        ///     <see cref="s_pieceSources" /> for the most recent bake. Excluded
        ///     from triangle extraction (they're synthetic obstacles, not
        ///     extractable walkable geometry). Door blockers attach to the piece
        ///     bake because doors are piece-adjacent.
        /// </summary>
        private static int s_piecePhantomCount;

        // Per-phantom-category counts from the most recent bake. The phantoms
        // are appended to s_pieceSources in this order: door first, then bed,
        // then outside-cell. With these counts vv_bake_audit can label each
        // phantom by category from its sequential index.
        private static int s_lastDoorPhantoms;
        private static int s_lastBedPhantoms;
        private static int s_lastOutsidePhantoms;

        /// <summary>Read-only view of the terrain bake's NavMeshBuildSource list (diagnostic use).</summary>
        public static IReadOnlyList<NavMeshBuildSource> TerrainSources => s_terrainSources;

        /// <summary>Read-only view of the piece bake's NavMeshBuildSource list, including phantom tail (diagnostic use).</summary>
        public static IReadOnlyList<NavMeshBuildSource> PieceSources => s_pieceSources;

        /// <summary>Phantom-blocker count appended at the END of <see cref="PieceSources" />. Sum of door + bed + outside-cell phantoms.</summary>
        public static int PiecePhantomCount => s_piecePhantomCount;

        /// <summary>Phantom door blockers from the most recent bake (first slice of phantom tail).</summary>
        public static int LastDoorPhantoms => s_lastDoorPhantoms;

        /// <summary>Phantom bed blockers from the most recent bake (second slice of phantom tail).</summary>
        public static int LastBedPhantoms => s_lastBedPhantoms;

        /// <summary>Phantom outside-cell blockers from the most recent bake (third slice of phantom tail).</summary>
        public static int LastOutsidePhantoms => s_lastOutsidePhantoms;

        private static readonly int s_terrainMask = LayerMask.GetMask("terrain");

        private static readonly int s_pieceMask =
            LayerMask.GetMask("Default", "static_solid", "piece");

        private static NavMeshBakeHolder Holder
        {
            get
            {
                if (s_holder != null) return s_holder;
                // Find by name first — a holder created by a prior assembly
                // survives via DontDestroyOnLoad and still owns its instances.
                var existing = GameObject.Find(HolderName);
                if (existing != null)
                {
                    var current = existing.GetComponent<NavMeshBakeHolder>();
                    if (current == null)
                    {
                        // HOT-RELOAD STALENESS FIX: after a script reload the
                        // holder's component is the PRIOR assembly's
                        // NavMeshBakeHolder TYPE, so GetComponent<>() (this
                        // assembly's type) misses it. The old code then
                        // AddComponent'd a second, EMPTY holder — orphaning the
                        // old one's still-installed NavMeshDataInstances. Those
                        // stale instances stay added to Unity's NavMesh (only
                        // OnDestroy removes them, which never fired), so
                        // SamplePosition keeps unioning the previous bake and
                        // rebakes appear to do nothing (bed/obstacle cells
                        // survive; only a full game restart clears it). Destroy
                        // any prior-assembly holder component by name match:
                        // DestroyImmediate fires its OnDestroy (old assembly
                        // code is still loaded) which removes its orphaned
                        // NavMesh instances before we install a fresh holder.
                        foreach (var mb in existing.GetComponents<MonoBehaviour>())
                            if (mb != null && mb.GetType().Name == nameof(NavMeshBakeHolder))
                                Object.DestroyImmediate(mb);
                        current = existing.AddComponent<NavMeshBakeHolder>();
                    }

                    s_holder = current;
                    return s_holder;
                }

                var go = new GameObject(HolderName);
                Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideInHierarchy;
                s_holder = go.AddComponent<NavMeshBakeHolder>();
                return s_holder;
            }
        }

        /// <summary>Whether any bake is currently active.</summary>
        public static bool HasBakedData => Holder.HasAny;

        /// <summary>
        ///     Snapshot of the source list used by the most recent bake (terrain
        ///     + piece concatenated). Diagnostics-only; per-kind callers should
        ///     use <see cref="GetSources" />.
        /// </summary>
        public static IReadOnlyList<NavMeshBuildSource> LastSources
        {
            get
            {
                var combined = new List<NavMeshBuildSource>(s_terrainSources.Count + s_pieceSources.Count);
                combined.AddRange(s_terrainSources);
                combined.AddRange(s_pieceSources);
                return combined;
            }
        }

        /// <summary>
        ///     Max walkable slope (degrees) for the villager NavMesh bake. Matches
        ///     the HNA region builder's walkable cutoff (cos 27°) so the baked
        ///     navmesh and the region graph agree on what's traversable — the
        ///     navmesh must not bridge slopes the region graph (and the humanoid)
        ///     reject, or the agent mover commits to un-traversable routes.
        /// </summary>
        private const float NavMeshBakeMaxSlopeDegrees = 27f;

        /// <summary>
        ///     Max step height (m) the villager NavMesh bake will bridge. Capped
        ///     at a realistic humanoid step so the voxelizer doesn't stitch
        ///     walkable navmesh over tall stair risers / ledges the character
        ///     can't actually step up (which the agent mover would then commit to
        ///     and wedge on). Lower than Valheim's permissive Humanoid-cloned
        ///     value.
        /// </summary>
        private const float NavMeshBakeMaxClimb = 0.2f;

        /// <summary>
        ///     Bake a fresh NavMesh for the villager agent over <paramref name="bounds" />.
        ///     Two passes (terrain + piece) both register into slot 31; queries
        ///     union the results. Removes any previous bakes first.
        /// </summary>
        public static BakeResult BakeVillage(Bounds bounds)
        {
            var result = new BakeResult();
            var sw = Stopwatch.StartNew();

            if (!VillagerAgentType.IsRegistered)
            {
                result.FailureReason = "agent_not_registered";
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }

            // Remove previous bakes to avoid accumulating data across
            // partition runs. Routed through the holder so a hot-reloaded
            // assembly correctly clears the prior assembly's instances.
            // Also clear any HNA-rebake data from a prior partition —
            // RebakeFromHnaTriangles (called after RubberBandPrune) adds
            // a separate HNA slot via Holder.SetHna that would otherwise
            // accumulate on top of the fresh terrain + piece bakes.
            Holder.RemoveTerrain();
            Holder.RemovePiece();
            Holder.RemoveHna();

            // Settings is a local struct copy; modifying it here shapes the
            // baked topology without affecting slot 31's registered agent
            // settings (NavMesh queries, SamplePosition etc. still use the
            // originals).
            var settings = NavMesh.GetSettingsByID(VillagerAgentType.UnityAgentTypeID);
            settings.agentRadius += VillagerSettings.NavMeshBakeRadiusBuffer;

            // Enforce the SAME max walkable slope the HNA region builder uses
            // (RegionBuilder rejects normals steeper than cos(27°)). The slot-31
            // agent is cloned from Valheim's Humanoid verbatim, whose slope
            // tolerance is more permissive — so the voxelizer bridged steep
            // ledges (e.g. a 0.66m rise over 1m ≈ 33°) that the region graph
            // correctly excludes and a humanoid can't actually traverse. The
            // agent mover rides the navmesh (not the HNA graph), so it committed
            // to those un-traversable routes and wedged. Matching the bake slope
            // to the region builder makes the navmesh stop bridging them, so the
            // planner routes around (or reports unreachable) instead of stalling.
            settings.agentSlope = NavMeshBakeMaxSlopeDegrees;

            // Lower the step-climb the voxelizer will bridge. Stairs become
            // walkable navmesh when each riser is <= agentClimb, regardless of
            // overall slope — so the permissive Humanoid-cloned climb let the
            // bake stitch a continuous walkable surface over tall stone-stair
            // risers (~0.5m) the character can't actually step up, and the agent
            // mover then committed to them and wedged. Capping climb at a
            // realistic humanoid step (0.2m) means risers taller than that are
            // NOT bridged: the navmesh ends at the base of an un-stepable stair,
            // so the planner routes around or reports unreachable instead of
            // wedging. Local copy — bake topology only, not query settings.
            settings.agentClimb = NavMeshBakeMaxClimb;

            // --- Terrain geometry collection (combined into the single bake
            //     below, NOT baked into its own slot) ---
            // The old design baked terrain into a separate slot from the terrain
            // mask only — no piece colliders. SamplePosition/CalculatePath union
            // every slot, so a column standing on terrain at ~floor height stayed
            // walkable in the un-eroded terrain slot even though the piece slot
            // carved it. Baking terrain + piece TOGETHER lets Unity's voxelizer
            // erode the walkable surface (terrain included) around real obstacles
            // by agent radius, so columns/walls carve precisely with no cell-grid
            // hacks and no over-carving of reachable-but-un-flooded areas.
            var terrainSw = Stopwatch.StartNew();
            var terrainSources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(
                bounds, s_terrainMask, NavMeshCollectGeometry.PhysicsColliders,
                0, new List<NavMeshBuildMarkup>(), terrainSources);

            s_terrainSources.Clear();
            s_terrainSources.AddRange(terrainSources);
            result.TerrainSourceCount = terrainSources.Count;
            Holder.RemoveTerrain(); // no separate terrain slot in the combined design

            terrainSw.Stop();
            result.TerrainDurationMs = (float)terrainSw.Elapsed.TotalMilliseconds;

            // --- Piece bake (includes phantom door blockers at the tail) ---
            var pieceSw = Stopwatch.StartNew();
            var pieceSources = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(
                bounds, s_pieceMask, NavMeshCollectGeometry.PhysicsColliders,
                0, new List<NavMeshBuildMarkup>(), pieceSources);

            // Drop the real, movable Door/Gate geometry before baking, and
            // leave the doorway as plain WALKABLE navmesh — no phantom blocker,
            // no off-mesh link. A door PANEL rotates with open/close state, so
            // baking its collider makes the navmesh depend on a moment-to-moment
            // piece state (open-inward carves the room, open-outward carves the
            // exterior, closed carves the doorway shut). Excluding the whole Door
            // hierarchy (panel + frame) is the ONE load-bearing step: the doorway
            // then bakes as a clean walkable gap between the flanking wall pieces,
            // independent of door state. Villagers walk straight through on
            // continuous navmesh and open the physical door on approach
            // (UpdateAgentMovement → DoorHandler.GetBlockingDoor). This replaces
            // the old phantom-blocker + door-link pipeline (fewer moving parts:
            // one bake filter + one proximity check, vs. exclude + phantom +
            // link placement + manual link traversal). Valheim uses the Door
            // component for both doors and gates, so this catches both.
            var doorGeometryDropped = pieceSources.RemoveAll(s =>
                s.component != null && s.component.GetComponentInParent<Door>() != null);
            result.DoorPiecesDropped = doorGeometryDropped;
            result.DoorsBlocked = 0;

            // Drop bed geometry from the bake too. A bed's flat top is a
            // walkable surface to the voxelizer, so it generates navmesh ON the
            // bed — and because that walkable source sits at the SAME location
            // as the bed NotWalkable blocker below, the walkable source wins and
            // the blocker never carves (verified: navmesh persisted on the bed
            // top at 37.81 even with a ModifierBox over it). Removing the bed
            // collider eliminates the competing walkable source; the blocker
            // then carves the bed-footprint column cleanly (same reason outside-
            // cell ModifierBoxes work — nothing walkable layered on them).
            pieceSources.RemoveAll(s =>
                s.component != null && s.component.GetComponentInParent<Bed>() != null);

            var phantomBeds = AddBedBlockers(pieceSources, bounds);
            result.BedsBlocked = phantomBeds;

            // Compute outside cells via a pre-bake perimeter flood (uses
            // Physics.CheckBox on the piece layer + ZoneSystem.GetGroundHeight,
            // doesn't require any NavMesh data), then append phantom
            // NotWalkable box sources covering each outside cell. The bake's
            // voxelizer marks those cells as unwalkable, so the agent NavMesh
            // excludes outside-the-wall surface up front — no need for a
            // second HNA-only rebake afterwards. The bake also keeps the
            // real piece colliders, so wall corners and small interior
            // obstacles (chairs, decorations) get carved properly.
            var outsideCells = ValheimVillages.Villager.AI.Navigation
                .RubberBandPrune.ComputeOutsideCellsForBake(bounds);
            var phantomOutside = AddOutsideCellBlockers(pieceSources, outsideCells, bounds);
            result.OutsideCellsBlocked = phantomOutside;
            result.OutsideCellsCount = outsideCells.Count;

            s_pieceSources.Clear();
            s_pieceSources.AddRange(pieceSources);
            // Doors are no longer phantom-blocked (doorways bake walkable); only
            // bed + outside-cell phantoms remain at the tail of pieceSources.
            s_piecePhantomCount = phantomBeds + phantomOutside;
            s_lastDoorPhantoms = 0;
            s_lastBedPhantoms = phantomBeds;
            s_lastOutsidePhantoms = phantomOutside;
            result.PieceSourceCount = pieceSources.Count;

            // Single combined bake: terrain + piece geometry + phantom blockers
            // (door/bed/outside). Unity erodes the walkable surface around every
            // obstacle by the inflated agentRadius, so columns and walls carve
            // precisely and the terrain beneath them carves too.
            var combinedSources = new List<NavMeshBuildSource>(
                terrainSources.Count + pieceSources.Count);
            combinedSources.AddRange(terrainSources);
            combinedSources.AddRange(pieceSources);
            if (combinedSources.Count > 0)
            {
                var data = NavMeshBuilder.BuildNavMeshData(
                    settings, combinedSources, bounds, Vector3.zero, Quaternion.identity);
                if (data != null) Holder.SetPiece(NavMesh.AddNavMeshData(data));
            }

            pieceSw.Stop();
            result.PieceDurationMs = (float)pieceSw.Elapsed.TotalMilliseconds;

            result.SourceCount = result.TerrainSourceCount + result.PieceSourceCount;

            if (result.SourceCount == 0)
            {
                result.FailureReason = "no_sources_collected";
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }

            result.Success = Holder.HasAny;
            if (!result.Success) result.FailureReason = "build_returned_null";
            sw.Stop();
            result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
            return result;
        }

        public static IReadOnlyList<NavMeshBuildSource> GetSources(SurfaceKind kind)
        {
            return kind == SurfaceKind.Terrain ? (IReadOnlyList<NavMeshBuildSource>)s_terrainSources : s_pieceSources;
        }

        /// <summary>
        ///     Result of a <see cref="RebakeFromHnaTriangles" /> call.
        /// </summary>
        public struct HnaRebakeResult
        {
            public bool Success;
            public int TriangleCount;
            public int VertexCount;
            public float DurationMs;
            public string FailureReason;
        }

        /// <summary>
        ///     Result of a <see cref="RebakeWithPruneComplement" /> call.
        /// </summary>
        public struct PruneComplementRebakeResult
        {
            public bool Success;
            public int PruneComplementCells;
            public int PruneComplementRectangles;
            public int PrunedPieceCells;
            public int PrunedPieceBuckets;
            public int PrunedPieceRectangles;
            public int TotalSources;
            public float DurationMs;
            public string FailureReason;
        }

        /// <summary>
        ///     Result of a <see cref="PruneOrphanTriangles" /> call.
        /// </summary>
        public struct OrphanPruneResult
        {
            public int OrphanTriangles;
            public int OrphanCellsBlocked;
            public int Rectangles;
            public int HeightBuckets;
            public float DurationMs;
            public bool DidRebake;
        }

        /// <summary>
        ///     Human-readable summary of the most recent partition's bake
        ///     prune passes (prune-complement + orphan iterations). Populated by
        ///     RegionPartitionHandler; surfaced by the vv_orphan_status command
        ///     so the carve counts are visible without reading BepInEx logs.
        /// </summary>
        public static string LastPrunePassSummary = "(no partition yet)";

        /// <summary>
        ///     When false (the default), the post-bake graph-prune passes
        ///     (RebakeWithPruneComplement + PruneOrphanTriangles) are skipped.
        ///     They rebuilt the piece slot from piece-only sources to carve
        ///     cells the HNA bed-flood marked unreachable — but that (a) discards
        ///     the combined bake's terrain geometry and (b) over-carved
        ///     physically-walkable surface the flood simply didn't reach (e.g.
        ///     the smelter platform). With the single combined bake, Unity's
        ///     voxelizer already carves real obstacles by agent radius and the
        ///     perimeter phantom blockers handle confinement, so these passes are
        ///     redundant. Confinement to the village is the A* corridor's job.
        /// </summary>
        public static bool EnableGraphPrunePasses = false;

        /// <summary>
        ///     Second bake: append <c>NotWalkable</c> ModifierBox volumes
        ///     covering every cell HNA pruning dropped but the first bake
        ///     left walkable, then re-bake the piece NavMesh. The first
        ///     bake already excludes <paramref name="outsideCells" /> via
        ///     <see cref="AddOutsideCellBlockers" />; this pass closes the
        ///     remaining gap (Pass 2 cliffs, locked-off rooms, anything
        ///     inside the perimeter but unreachable from a bed).
        ///
        ///     Why ModifierBox + greedy rectangles instead of rebuilding
        ///     from HNA triangles: the original piece sources include
        ///     every real collider, which the voxelizer carves correctly
        ///     at sub-voxel accuracy. Replacing them with a HNA-triangle
        ///     mesh re-introduces the gap between triangle data and the
        ///     bake's voxel grid (small obstacles lose carving precision).
        ///     ModifierBox preserves the original sources' carving and
        ///     just stamps area=1 over the prune-complement footprint.
        ///
        ///     Iff <paramref name="bedReachableCells" /> is null/empty
        ///     OR every cell is already accounted for, this is a no-op.
        /// </summary>
        internal static PruneComplementRebakeResult RebakeWithPruneComplement(
            HashSet<long> bedReachableCells,
            HashSet<long> outsideCells,
            HashSet<long> prunedPieceKeys,
            Bounds bounds)
        {
            var result = new PruneComplementRebakeResult();
            var sw = Stopwatch.StartNew();

            if (!VillagerAgentType.IsRegistered)
            {
                result.FailureReason = "agent_not_registered";
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }
            if (bedReachableCells == null)
            {
                result.FailureReason = "no_bed_reachable_set";
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }

            // Compute the prune-complement cell set: every cell in the
            // inflated bake-bounds XZ range that is NEITHER bed-reachable
            // NOR already accounted for by the first bake's outside-cell
            // blockers. These are the cells HNA's bed-flood determined are
            // unreachable from any bed but the first bake's voxelizer
            // still produced walkable polygons for.
            var cell = ValheimVillages.Villager.AI.Navigation.RegionGraph.LookupCellSize;
            var gxMin = Mathf.FloorToInt(bounds.min.x / cell) - 1;
            var gzMin = Mathf.FloorToInt(bounds.min.z / cell) - 1;
            var gxMax = Mathf.FloorToInt(bounds.max.x / cell) + 1;
            var gzMax = Mathf.FloorToInt(bounds.max.z / cell) + 1;

            var pruneComplement = new HashSet<long>();
            for (var gz = gzMin; gz <= gzMax; gz++)
            for (var gx = gxMin; gx <= gxMax; gx++)
            {
                var key = ValheimVillages.Villager.AI.Navigation
                    .RubberBandPrune.PackXzKey(gx, gz);
                if (bedReachableCells.Contains(key)) continue;
                if (outsideCells != null && outsideCells.Contains(key)) continue;
                pruneComplement.Add(key);
            }
            result.PruneComplementCells = pruneComplement.Count;

            // Decompose terrain prune-complement to rectangles.
            var rects = ValheimVillages.Villager.AI.Navigation
                .RubberBandPrune.DecomposeToRectangles(pruneComplement);
            result.PruneComplementRectangles = rects.Count;

            // Piece-layer prune-complement: bucket the HNA-rejected piece keys
            // by height (hb), decompose each bucket's XZ footprint into greedy
            // rectangles, and emit one HeightBucketSize-tall ModifierBox per
            // rectangle per bucket. Tight Y slices instead of full-Y boxes:
            // piece polygons sit at specific elevations (walkway top, smelter
            // side, second-floor) and full-Y boxes would over-block neighbour
            // buckets and risk severing terrain underneath.
            var pieceBucketed = new Dictionary<int, HashSet<long>>();
            if (prunedPieceKeys != null)
            {
                foreach (var key in prunedPieceKeys)
                {
                    ValheimVillages.Villager.AI.Navigation
                        .RubberBandPrune.UnpackLookup(key, out var pgx, out var pgz, out var phb);
                    if (!pieceBucketed.TryGetValue(phb, out var set))
                    {
                        set = new HashSet<long>();
                        pieceBucketed[phb] = set;
                    }
                    set.Add(ValheimVillages.Villager.AI.Navigation
                        .RubberBandPrune.PackXzKey(pgx, pgz));
                }
            }
            result.PrunedPieceCells = prunedPieceKeys?.Count ?? 0;
            result.PrunedPieceBuckets = pieceBucketed.Count;

            if (pruneComplement.Count == 0 && pieceBucketed.Count == 0)
            {
                result.Success = true;
                result.TotalSources = s_pieceSources.Count;
                Plugin.Log?.LogInfo(
                    "[NavMeshBake] Second bake skipped: NavMesh already matches HNA " +
                    "(0 prune-complement cells, 0 pruned piece cells)");
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }

            var ySize = bounds.size.y + 4f;
            var yCenter = bounds.center.y;

            // Build the second-bake source list: start from the first
            // piece-bake source set (real colliders + door/bed/outside-cell
            // phantoms already in place) and append prune-complement
            // ModifierBox rectangles at the tail.
            var sources = new List<NavMeshBuildSource>(s_pieceSources.Count + rects.Count);
            sources.AddRange(s_pieceSources);
            foreach (var r in rects)
            {
                var w = (r.Gx1 - r.Gx0 + 1) * cell;
                var d = (r.Gz1 - r.Gz0 + 1) * cell;
                var cx = (r.Gx0 + r.Gx1 + 1) * 0.5f * cell;
                var cz = (r.Gz0 + r.Gz1 + 1) * 0.5f * cell;
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.ModifierBox,
                    size = new Vector3(w, ySize, d),
                    transform = Matrix4x4.TRS(
                        new Vector3(cx, yCenter, cz),
                        Quaternion.identity, Vector3.one),
                    area = 1, // NotWalkable
                });
            }

            var pieceRectsTotal = 0;
            foreach (var bucketKv in pieceBucketed)
            {
                var hb = bucketKv.Key;
                var bucketCenterY = (hb + 0.5f) * ValheimVillages.Villager.AI.Navigation
                    .RegionGraph.HeightBucketSize;
                var bucketRects = ValheimVillages.Villager.AI.Navigation
                    .RubberBandPrune.DecomposeToRectangles(bucketKv.Value);
                foreach (var r in bucketRects)
                {
                    var w = (r.Gx1 - r.Gx0 + 1) * cell;
                    var d = (r.Gz1 - r.Gz0 + 1) * cell;
                    var cx = (r.Gx0 + r.Gx1 + 1) * 0.5f * cell;
                    var cz = (r.Gz0 + r.Gz1 + 1) * 0.5f * cell;
                    sources.Add(new NavMeshBuildSource
                    {
                        shape = NavMeshBuildSourceShape.ModifierBox,
                        size = new Vector3(w,
                            ValheimVillages.Villager.AI.Navigation.RegionGraph.HeightBucketSize,
                            d),
                        transform = Matrix4x4.TRS(
                            new Vector3(cx, bucketCenterY, cz),
                            Quaternion.identity, Vector3.one),
                        area = 1, // NotWalkable
                    });
                    pieceRectsTotal++;
                }
            }
            result.PrunedPieceRectangles = pieceRectsTotal;

            if (prunedPieceKeys != null && prunedPieceKeys.Count > 0)
                Plugin.Log?.LogInfo(
                    $"[NavMeshBake] Piece-layer prune: {prunedPieceKeys.Count} pruned cells across " +
                    $"{pieceBucketed.Count} height buckets -> {pieceRectsTotal} ModifierBox rectangles");

            result.TotalSources = sources.Count;

            // Same agentRadius buffer treatment as BakeVillage so the
            // second bake's obstacle clearance is consistent with the
            // first (path corners land in the same place).
            var settings = NavMesh.GetSettingsByID(VillagerAgentType.UnityAgentTypeID);
            settings.agentRadius += VillagerSettings.NavMeshBakeRadiusBuffer;
            var data = NavMeshBuilder.BuildNavMeshData(
                settings, sources, bounds, Vector3.zero, Quaternion.identity);
            if (data == null)
            {
                result.FailureReason = "build_returned_null";
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }

            // Swap: remove the first piece bake + any prior HNA-slot bake,
            // install the new prune-complement-aware bake in the HNA slot.
            // Terrain bake stays as-is (its sources don't change).
            Holder.RemovePiece();
            Holder.RemoveHna();
            Holder.SetHna(NavMesh.AddNavMeshData(data));

            // Persist the new source list so audit/diagnostic tools see the
            // bake state that's actually live. The new tail is the prune-
            // complement rectangles; door/bed/outside-cell phantoms are
            // still in the same relative positions from the first bake.
            s_pieceSources.Clear();
            s_pieceSources.AddRange(sources);

            result.Success = true;
            sw.Stop();
            result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
            Plugin.Log?.LogInfo(
                $"[NavMeshBake] Second bake: {pruneComplement.Count} prune-complement " +
                $"cells -> {rects.Count} ModifierBox rectangles; " +
                $"piece-layer: {result.PrunedPieceCells} cells / {result.PrunedPieceBuckets} buckets / " +
                $"{result.PrunedPieceRectangles} rects; duration {result.DurationMs:F1}ms");
            return result;
        }

        /// <summary>
        ///     Third (and any subsequent) bake: walk an XZ × height-bucket
        ///     grid covering the bake bounds, ask Unity's NavMesh for the
        ///     nearest slot-31 sample at each cell, and stamp every cell
        ///     whose sample lands on walkable surface the HNA graph does
        ///     not recognize. Re-bake with the new ModifierBox tail.
        ///     Idempotent: a clean run returns
        ///     <see cref="OrphanPruneResult.DidRebake"/> = false so the
        ///     caller can stop iterating once NavMesh ⊆ HNA holds.
        ///
        ///     Why grid-sample instead of <c>NavMesh.CalculateTriangulation</c>:
        ///     <c>CalculateTriangulation</c> only returns STATIC scene-baked
        ///     data (Valheim's pre-baked slot-1 navmesh) — it misses every
        ///     runtime <c>AddNavMeshData</c> instance, including the entire
        ///     slot-31 bake we just installed. We'd be blocking phantoms
        ///     that aren't in our bake at all and never converging.
        ///     <c>SamplePosition</c> with an agent-typed filter queries the
        ///     actual installed slot-31 navmesh, so each ModifierBox we add
        ///     genuinely shrinks the orphan set the next iteration sees.
        ///
        ///     Why this exists on top of the two prune passes: Unity's
        ///     voxelizer can produce walkable polygons from sliver triangles
        ///     or edge artifacts the region builder never tracked; the two
        ///     prior prunes only cover cells HNA explicitly rejected. This
        ///     pass closes whatever's left.
        ///
        ///     Sources accumulate across iterations: each call extends
        ///     <see cref="s_pieceSources"/> with new ModifierBoxes and
        ///     installs the rebake into the HNA slot via <c>Holder.SetHna</c>,
        ///     replacing the prior HNA-slot instance.
        /// </summary>
        internal static OrphanPruneResult PruneOrphanTriangles(
            RegionGraph graph,
            Bounds bounds)
        {
            var result = new OrphanPruneResult();
            var sw = Stopwatch.StartNew();

            if (graph == null || !graph.IsAvailable || !VillagerAgentType.IsRegistered)
            {
                result.DidRebake = false;
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }

            var cell = ValheimVillages.Villager.AI.Navigation.RegionGraph.LookupCellSize;
            var hbSize = ValheimVillages.Villager.AI.Navigation.RegionGraph.HeightBucketSize;

            var gxMin = Mathf.FloorToInt(bounds.min.x / cell);
            var gxMax = Mathf.FloorToInt(bounds.max.x / cell);
            var gzMin = Mathf.FloorToInt(bounds.min.z / cell);
            var gzMax = Mathf.FloorToInt(bounds.max.z / cell);
            var hbMin = ValheimVillages.Villager.AI.Navigation.RegionGraph.HeightBucket(bounds.min.y);
            var hbMax = ValheimVillages.Villager.AI.Navigation.RegionGraph.HeightBucket(bounds.max.y);

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            // SamplePosition probes from the cell-center column. Max
            // distance = half the bucket height so a sample at bucket
            // center can't snap to a polygon in a neighbouring bucket
            // (that would mis-attribute an orphan to the wrong hb).
            var sampleRadius = hbSize * 0.5f;
            var halfCell = cell * 0.5f;

            var bucketedCells = new Dictionary<int, HashSet<long>>();
            var samples = 0;
            var orphanHits = 0;
            for (var hb = hbMin; hb <= hbMax; hb++)
            {
                var py = (hb + 0.5f) * hbSize;
                for (var gz = gzMin; gz <= gzMax; gz++)
                for (var gx = gxMin; gx <= gxMax; gx++)
                {
                    var px = gx * cell + halfCell;
                    var pz = gz * cell + halfCell;
                    samples++;
                    if (!NavMesh.SamplePosition(
                            new Vector3(px, py, pz), out var hit, sampleRadius, filter))
                        continue;

                    // Only count this sample as an orphan IN THIS CELL if the
                    // hit didn't snap out of the cell. Without this guard the
                    // same walkable polygon at the edge of an orphan region
                    // gets discovered from every neighbour cell's probe, and
                    // each iteration's ModifierBox just shifts the snap by
                    // one cell — orphan count plateaus instead of converging.
                    var hitGx = Mathf.FloorToInt(hit.position.x / cell);
                    var hitGz = Mathf.FloorToInt(hit.position.z / cell);
                    if (hitGx != gx || hitGz != gz) continue;

                    var hitHb = ValheimVillages.Villager.AI.Navigation
                        .RegionGraph.HeightBucket(hit.position.y);
                    // Carve this cell ONLY if the agent's torso is physically
                    // blocked here (a pillar/wall through a 1m cell the navmesh
                    // still calls walkable). We deliberately do NOT carve merely
                    // off-HNA-graph cells: the navmesh feeds the local agent and
                    // should keep every physically-walkable surface (e.g. the
                    // smelter platform, which the bed-flood may not reach), with
                    // confinement left to the A* corridor planner. The body
                    // probe is a waist band, so stairs/ramps and beds aren't
                    // carved. (graph is unused now but kept in the signature for
                    // diagnostics / future graph-aware passes.)
                    if (!IsAgentBodyBlocked(hit.position)) continue;

                    orphanHits++;
                    if (!bucketedCells.TryGetValue(hitHb, out var set))
                    {
                        set = new HashSet<long>();
                        bucketedCells[hitHb] = set;
                    }
                    set.Add(ValheimVillages.Villager.AI.Navigation
                        .RubberBandPrune.PackXzKey(hitGx, hitGz));
                }
            }

            result.OrphanTriangles = orphanHits; // grid samples that hit walkable AND had no HNA region
            result.HeightBuckets = bucketedCells.Count;
            if (bucketedCells.Count == 0)
            {
                result.OrphanCellsBlocked = 0;
                result.DidRebake = false;
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }

            // Accumulate: start from the current piece-source list (which
            // RebakeWithPruneComplement extended on its run) and append the
            // new orphan-bucket ModifierBoxes at the tail. Each iteration
            // adds more boxes on top of the previous run's, never replacing
            // — so a 2nd iteration's SamplePosition probes see the effect of
            // the 1st iteration's ModifierBoxes and only flag NEW orphan
            // cells.
            var sources = new List<NavMeshBuildSource>(s_pieceSources.Count + bucketedCells.Count * 8);
            sources.AddRange(s_pieceSources);

            var totalCells = 0;
            var totalRects = 0;
            foreach (var bucketKv in bucketedCells)
            {
                var hb = bucketKv.Key;
                totalCells += bucketKv.Value.Count;
                var bucketCenterY = (hb + 0.5f) * hbSize;
                var bucketRects = ValheimVillages.Villager.AI.Navigation
                    .RubberBandPrune.DecomposeToRectangles(bucketKv.Value);
                foreach (var r in bucketRects)
                {
                    var w = (r.Gx1 - r.Gx0 + 1) * cell;
                    var d = (r.Gz1 - r.Gz0 + 1) * cell;
                    var cxR = (r.Gx0 + r.Gx1 + 1) * 0.5f * cell;
                    var czR = (r.Gz0 + r.Gz1 + 1) * 0.5f * cell;
                    sources.Add(new NavMeshBuildSource
                    {
                        shape = NavMeshBuildSourceShape.ModifierBox,
                        size = new Vector3(w, hbSize, d),
                        transform = Matrix4x4.TRS(
                            new Vector3(cxR, bucketCenterY, czR),
                            Quaternion.identity, Vector3.one),
                        area = 1, // NotWalkable
                    });
                    totalRects++;
                }
            }
            result.OrphanCellsBlocked = totalCells;
            result.Rectangles = totalRects;

            var settings = NavMesh.GetSettingsByID(VillagerAgentType.UnityAgentTypeID);
            settings.agentRadius += VillagerSettings.NavMeshBakeRadiusBuffer;

            // Combine terrain + piece sources + all accumulated NotWalkable
            // boxes into ONE bake. The terrain slot is built blocker-free in
            // BakeVillage and never carved, so a column / outside cell sitting
            // on terrain at ~floor height stays walkable in the terrain slot
            // even after the piece slot is carved (SamplePosition unions the
            // slots, so the terrain surface wins). Merging into a single slot —
            // and removing the separate terrain/piece slots — makes queries see
            // exactly the carved union. s_terrainSources is NOT folded into
            // s_pieceSources (kept separate) so the next iteration re-prepends
            // it once rather than duplicating it.
            var bakeSources = new List<NavMeshBuildSource>(s_terrainSources.Count + sources.Count);
            bakeSources.AddRange(s_terrainSources);
            bakeSources.AddRange(sources);
            var data = NavMeshBuilder.BuildNavMeshData(
                settings, bakeSources, bounds, Vector3.zero, Quaternion.identity);
            if (data == null)
            {
                result.DidRebake = false;
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                Plugin.Log?.LogWarning(
                    $"[NavMeshBake] Orphan triangle prune: BuildNavMeshData returned null after " +
                    $"{totalCells} cells / {totalRects} rects");
                return result;
            }

            Holder.RemoveTerrain();
            Holder.RemovePiece();
            Holder.RemoveHna();
            Holder.SetHna(NavMesh.AddNavMeshData(data));

            // Persist the accumulated piece source list (piece + phantom blockers
            // + prune/orphan boxes, NO terrain) so subsequent orphan-prune
            // iterations and audit/diagnostic tools see the live bake state.
            s_pieceSources.Clear();
            s_pieceSources.AddRange(sources);

            result.DidRebake = true;
            sw.Stop();
            result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
            Plugin.Log?.LogInfo(
                $"[NavMeshBake] Orphan triangle prune: {orphanHits} orphan hits / {samples} samples -> " +
                $"{totalCells} cells / {bucketedCells.Count} buckets / {totalRects} rects in " +
                $"{result.DurationMs:F1}ms");
            return result;
        }

        /// <summary>
        ///     REPLACE the slot-31 agent NavMesh with one baked solely from
        ///     the HNA-accepted triangle set. Call this after
        ///     <see cref="RubberBandPrune.Apply" /> finishes and
        ///     <see cref="RegionBuilder.CachedTriangles" /> reflects the
        ///     final inside-village geometry.
        ///
        ///     Removes the existing terrain + piece bakes (they include
        ///     outside-the-wall walkable surface that the prune excluded
        ///     but the bake doesn't know about) and replaces them with a
        ///     single NavMesh built from the HNA tris only.
        ///
        ///     Why: <c>NavMesh.SamplePosition</c> and Valheim's
        ///     <c>Pathfinding.GetPath</c> query whatever NavMesh data is
        ///     registered for the agent type. If the bake covers the open
        ///     terrain around the village, link endpoint snaps can land
        ///     outside the perimeter and path queries can route through
        ///     non-village surface — both observed in practice. Carving
        ///     the bake down to HNA-only surfaces makes the agent NavMesh
        ///     and the HNA RegionGraph represent the same walkable set.
        ///
        ///     The bake is voxelized at slot-31's voxel size (~0.166m), so
        ///     adjacent HNA triangles whose vertices are sub-voxel apart
        ///     should remain connected. Pass 4 border snapping is what
        ///     keeps this true; if snap misses leave gaps, the rebaked
        ///     NavMesh will reflect those gaps as real discontinuities
        ///     (then NavMeshLinkPlacer fills them with explicit links).
        /// </summary>
        internal static HnaRebakeResult RebakeFromHnaTriangles(
            IReadOnlyList<RegionBuilder.CachedTriangle> triangles,
            Bounds bounds)
        {
            var result = new HnaRebakeResult();
            var sw = Stopwatch.StartNew();

            if (!VillagerAgentType.IsRegistered)
            {
                result.FailureReason = "agent_not_registered";
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }
            if (triangles == null || triangles.Count == 0)
            {
                result.FailureReason = "no_triangles";
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }

            // Pack HNA triangles into a single Mesh. Each cached triangle
            // contributes 3 unique vertices (no dedup — Unity's voxelizer
            // handles the redundancy and the bake's voxel size is much
            // larger than typical vertex coincidence, so dedup wouldn't
            // change the output).
            var triCount = triangles.Count;
            var verts = new Vector3[triCount * 3];
            var idx = new int[triCount * 3];
            for (var i = 0; i < triCount; i++)
            {
                var t = triangles[i];
                var i0 = i * 3;
                verts[i0] = t.V0;
                verts[i0 + 1] = t.V1;
                verts[i0 + 2] = t.V2;
                idx[i0] = i0;
                idx[i0 + 1] = i0 + 1;
                idx[i0 + 2] = i0 + 2;
            }

            var mesh = new Mesh
            {
                indexFormat = triCount * 3 > ushort.MaxValue
                    ? UnityEngine.Rendering.IndexFormat.UInt32
                    : UnityEngine.Rendering.IndexFormat.UInt16,
                vertices = verts,
                triangles = idx,
            };
            mesh.RecalculateBounds();

            var sources = new List<NavMeshBuildSource>(1)
            {
                new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Mesh,
                    sourceObject = mesh,
                    transform = Matrix4x4.identity,
                    area = 0, // Walkable
                },
            };

            // Same agentRadius buffer treatment as BakeVillage — keeps the
            // HNA-rebaked NavMesh consistent with the initial bake's
            // obstacle clearance, so path corners land in the same place.
            var settings = NavMesh.GetSettingsByID(VillagerAgentType.UnityAgentTypeID);
            settings.agentRadius += VillagerSettings.NavMeshBakeRadiusBuffer;
            var data = NavMeshBuilder.BuildNavMeshData(
                settings, sources, bounds, Vector3.zero, Quaternion.identity);
            if (data == null)
            {
                Object.DestroyImmediate(mesh);
                result.FailureReason = "build_returned_null";
                sw.Stop();
                result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
                return result;
            }

            // REPLACE the previous bakes. Remove terrain + piece (they
            // included outside-the-wall surface) so SamplePosition and
            // Pathfinding queries see ONLY the HNA-derived navmesh.
            Holder.RemoveTerrain();
            Holder.RemovePiece();
            Holder.SetHna(NavMesh.AddNavMeshData(data));

            result.Success = true;
            result.TriangleCount = triCount;
            result.VertexCount = verts.Length;
            sw.Stop();
            result.DurationMs = (float)sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>
        ///     Extract a triangulation (vertices + indices, world space) from
        ///     every cached source list (terrain + piece concatenated). Diagnostics
        ///     path only; per-kind callers should use the
        ///     <see cref="ExtractBakedTriangles(SurfaceKind)" /> overload so they
        ///     can run separate filter passes.
        /// </summary>
        public static (Vector3[] vertices, int[] indices, int[] triangleLayers) ExtractBakedTriangles()
        {
            var (tv, ti, tl) = ExtractBakedTriangles(SurfaceKind.Terrain);
            var (pv, pi, pl) = ExtractBakedTriangles(SurfaceKind.Piece);
            if (tv.Length == 0) return (pv, pi, pl);
            if (pv.Length == 0) return (tv, ti, tl);

            var verts = new Vector3[tv.Length + pv.Length];
            Array.Copy(tv, 0, verts, 0, tv.Length);
            Array.Copy(pv, 0, verts, tv.Length, pv.Length);

            var idx = new int[ti.Length + pi.Length];
            Array.Copy(ti, 0, idx, 0, ti.Length);
            for (var i = 0; i < pi.Length; i++) idx[ti.Length + i] = pi[i] + tv.Length;

            var layers = new int[tl.Length + pl.Length];
            Array.Copy(tl, 0, layers, 0, tl.Length);
            Array.Copy(pl, 0, layers, tl.Length, pl.Length);

            return (verts, idx, layers);
        }

        /// <summary>
        ///     Extract a triangulation (vertices + indices, world space) from the
        ///     source list of the requested kind. This is the canonical way to get
        ///     triangles for runtime-baked data: <c>NavMesh.CalculateTriangulation()</c>
        ///     returns only static scene-baked data, missing both Valheim's runtime
        ///     bakes and ours. Handles Mesh and Box source shapes; other shapes
        ///     (Sphere, Capsule, ModifierBox) are skipped — they're rare in Valheim
        ///     piece geometry. Phantom door blockers at the tail of the piece
        ///     sources are skipped (they're synthetic obstacles, not extractable
        ///     walkable geometry).
        /// </summary>
        public static (Vector3[] vertices, int[] indices, int[] triangleLayers) ExtractBakedTriangles(SurfaceKind kind)
        {
            // Sentinel bounds = no filter. Use the overload to skip
            // out-of-bounds triangles at extraction time.
            return ExtractBakedTriangles(kind, float.NegativeInfinity, float.NegativeInfinity,
                float.PositiveInfinity, float.PositiveInfinity);
        }

        /// <summary>
        ///     Same as <see cref="ExtractBakedTriangles(SurfaceKind)" /> but drops
        ///     triangles whose centroid is outside the XZ bounds. Terrain meshes
        ///     are full Valheim TerrainComp tiles (~32m × ~32m each, ~8k tris
        ///     each); a village spanning 60m × 60m typically pulls in 4 tiles ≈
        ///     32k tris of which ~80% are outside village bounds. Filtering at
        ///     extraction skips the per-triangle work RegionBuilder would have
        ///     otherwise wasted on rej_bounds.
        /// </summary>
        public static (Vector3[] vertices, int[] indices, int[] triangleLayers) ExtractBakedTriangles(
            SurfaceKind kind, float minX, float minZ, float maxX, float maxZ)
        {
            var sources = kind == SurfaceKind.Terrain ? s_terrainSources : s_pieceSources;
            var phantom = kind == SurfaceKind.Piece ? s_piecePhantomCount : 0;
            if (sources.Count == 0)
                return (Array.Empty<Vector3>(), Array.Empty<int>(), Array.Empty<int>());

            var verts = new List<Vector3>(sources.Count * 32);
            var idx = new List<int>(sources.Count * 96);
            var triLayers = new List<int>(sources.Count * 32);
            var skippedUnreadable = 0;
            var skippedShape = 0;

            var realCount = sources.Count - phantom;
            for (var i = 0; i < realCount; i++)
            {
                var src = sources[i];
                // Layer is read from the source Collider's GameObject. Box
                // sources we synthesize (door blockers) have no component
                // and get -1; they're phantom obstacles and excluded from
                // extraction by realCount anyway.
                var layer = src.component != null ? src.component.gameObject.layer : -1;
                switch (src.shape)
                {
                    case NavMeshBuildSourceShape.Mesh:
                        var mesh = src.sourceObject as Mesh;
                        if (mesh == null)
                        {
                            skippedShape++;
                            continue;
                        }

                        if (!mesh.isReadable)
                        {
                            skippedUnreadable++;
                            continue;
                        }

                        AppendMesh(verts, idx, triLayers, layer, src.transform, mesh, minX, minZ, maxX, maxZ);
                        break;
                    case NavMeshBuildSourceShape.Box:
                        // Subdivide walkable faces on the piece pass only: piece
                        // box floors arrive as 4m faces the centroid-based filter
                        // fails wholesale; terrain is already finely tessellated
                        // and welded, and over-tessellating it would bloat tri
                        // count for no gain.
                        AppendBox(verts, idx, triLayers, layer, src.transform, src.size,
                            minX, minZ, maxX, maxZ, kind == SurfaceKind.Piece);
                        break;
                    default:
                        skippedShape++;
                        break;
                }
            }

            if (skippedUnreadable > 0 || skippedShape > 0)
                Plugin.Log?.LogDebug(
                    $"[NavMeshBake] extract({kind}): {skippedUnreadable} unreadable mesh(es) skipped, " +
                    $"{skippedShape} non-Mesh/Box source(s) skipped");

            // Terrain-only: weld coincident vertices across source meshes
            // (heightmap + cultivated_ground + paved_road + other terrain-
            // layer pieces). Each source produces its own vertex indices;
            // without welding, tris from different sources at the same
            // physical position have different indices, no shared edges,
            // and end up in separate union-find components — even though
            // they're physically continuous. Welding makes shared-position
            // vertices share an index, so RegionBuilder's adjacency works
            // naturally across the cultivated/natural boundary.
            // Piece pass is not welded (its prefabs are intentionally
            // distinct walkable surfaces; we don't want adjacent piece
            // prefabs to silently bridge their regions).
            // Weld remaps indices but preserves triangle count, so
            // triLayers stays parallel to idx (length = idx.Count / 3).
            if (kind == SurfaceKind.Terrain)
                WeldCoincidentVertices(verts, idx);

            return (verts.ToArray(), idx.ToArray(), triLayers.ToArray());
        }

        /// <summary>
        ///     In-place vertex welding via spatial hash. Vertices within the
        ///     same 5cm quantum bucket are unified to a single index. Triangle
        ///     indices are remapped accordingly. Unused vertices are NOT pruned
        ///     (they just become orphans in the vertex array — cheap to leave
        ///     since downstream only iterates triangles).
        /// </summary>
        private static void WeldCoincidentVertices(List<Vector3> verts, List<int> idx)
        {
            if (verts.Count == 0 || idx.Count == 0) return;
            const float weldQuantum = 0.05f; // 5cm, matches MergeCoplanarRegions
            const long bias = 1L << 20;
            const long mask = (1L << 21) - 1L;

            var posToIndex = new Dictionary<long, int>(verts.Count);
            var remap = new int[verts.Count];
            for (var i = 0; i < verts.Count; i++)
            {
                var v = verts[i];
                long qx = Mathf.RoundToInt(v.x / weldQuantum);
                long qy = Mathf.RoundToInt(v.y / weldQuantum);
                long qz = Mathf.RoundToInt(v.z / weldQuantum);
                var key = (((qx + bias) & mask) << 42)
                          | (((qy + bias) & mask) << 21)
                          | ((qz + bias) & mask);
                if (posToIndex.TryGetValue(key, out var existing))
                {
                    remap[i] = existing;
                }
                else
                {
                    posToIndex[key] = i;
                    remap[i] = i;
                }
            }

            var welded = 0;
            for (var j = 0; j < idx.Count; j++)
            {
                var oldIdx = idx[j];
                var newIdx = remap[oldIdx];
                if (newIdx != oldIdx)
                {
                    idx[j] = newIdx;
                    welded++;
                }
            }

            Plugin.Log?.LogInfo(
                $"[NavMeshBake] terrain weld: {welded} index refs re-pointed across " +
                $"{verts.Count - posToIndex.Count} merged vertex positions " +
                $"({verts.Count} total verts, {posToIndex.Count} unique positions)");
        }

        /// <summary>
        ///     Append a phantom solid box source for every <see cref="Door" /> in
        ///     bounds. These block the bake from producing NavMesh through
        ///     doorways regardless of the door's current open/closed orientation.
        /// </summary>
        private static int AddDoorBlockers(List<NavMeshBuildSource> sources, Bounds bounds)
        {
            var added = 0;
            var allDoors = Object.FindObjectsByType<Door>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var door in allDoors)
            {
                if (door == null || door.transform == null) continue;
                if (!bounds.Contains(door.transform.position)) continue;

                // Phantom centered at the door's transform position with no
                // vertical offset. Valheim's Door prefab pivots at the door
                // PANEL CENTER (chest height, ~1m above the floor for a
                // standard 2m-tall door), so a 2m-tall phantom centered
                // here spans floor to top-of-door — covering the doorway
                // opening at the agent's footstep elevation. The earlier
                // "+1m up" offset assumed door.transform was at the floor
                // and pushed the phantom to 1m-3m above the floor, leaving
                // the doorway unblocked at NavMesh height and causing
                // PlaceDoorLinks to think the doorway was already
                // traversable on the NavMesh (no link needed).
                var transform = Matrix4x4.TRS(
                    door.transform.position,
                    door.transform.rotation,
                    Vector3.one);
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.Box,
                    size = new Vector3(1.2f, 2.0f, 0.3f),
                    transform = transform,
                    area = 1, // NotWalkable — block but don't make walkable surface on top
                });
                added++;
            }

            return added;
        }

        /// <summary>
        ///     Append a phantom <c>NotWalkable</c> box over every column/pillar
        ///     piece in bounds. Their convex mesh colliders are collected by
        ///     CollectSources but bake as area=0 (Walkable) and leave a walkable
        ///     sliver straight through the column — the navmesh runs through it
        ///     while physics blocks the agent, so paths drive into the column.
        ///     A NotWalkable box over the collider bounds forces a real hole so
        ///     CalculatePath routes around (e.g. up the adjacent walkable stairs).
        /// </summary>
        private static readonly int s_bodyBlockMask =
            LayerMask.GetMask("Default", "static_solid", "piece");

        private static readonly Collider[] s_bodyBlockBuf = new Collider[16];

        /// <summary>
        ///     True if the agent's torso is physically blocked at this walkable
        ///     surface point — i.e. a solid piece/rock occupies a waist-height
        ///     band over the cell. Uses the same waist band as
        ///     RubberBandPrune.WallBlocks (0.7–1.3m), so it catches walls and
        ///     pillars (which span the band) but NOT stairs/ramps (risers sit
        ///     below it) or low furniture. Beds are excluded (carved separately).
        ///     The orphan-prune pass carves cells that test true so the navmesh
        ///     stops claiming a pillar-occupied cell is walkable.
        /// </summary>
        internal static bool IsAgentBodyBlocked(Vector3 surfacePos)
        {
            // Box at waist height (0.7–1.3m above the surface) sized so it
            // catches any solid the agent's BODY would intersect while standing
            // anywhere in this 1m cell: half-extent = cell-half (0.5) + agent
            // radius (~0.4) ≈ 0.9m. The orphan loop probes one point per cell
            // (the cell centre), so a tight box missed columns/walls sitting at
            // a cell CORNER — the agent stands in the adjacent walkable cell and
            // clips the column even though the column's own (un-navmeshed) cell
            // is never sampled. The waist band means stairs/ramps (risers below
            // the band) don't trip it; beds are excluded (carved separately).
            var center = surfacePos + Vector3.up * 1.0f;     // waist-band centre
            var halfExtents = new Vector3(0.9f, 0.3f, 0.9f); // cell-half + agent radius
            var count = Physics.OverlapBoxNonAlloc(
                center, halfExtents, s_bodyBlockBuf, Quaternion.identity,
                s_bodyBlockMask, QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var c = s_bodyBlockBuf[i];
                if (c == null) continue;
                if (c.GetComponentInParent<Bed>() != null) continue;
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Append a phantom <c>NotWalkable</c> box source for each
        ///     outside cell computed by the pre-bake perimeter flood. The
        ///     box is a 1m × bake-height × 1m column centered on the cell —
        ///     tall enough to carve the cell out of the agent NavMesh at
        ///     every altitude (terrain ground, piece tops, rampart tops),
        ///     so the bake doesn't produce walkable surface anywhere in an
        ///     outside cell.
        ///
        ///     Effect: NavMesh.SamplePosition snaps inside the village
        ///     stay inside; Pathfinding.GetPath queries can't route through
        ///     outside-the-wall surfaces; NavMeshLink endpoints can't snap
        ///     outside. The HNA RegionGraph and the agent NavMesh end up
        ///     representing the same walkable set in a single bake.
        ///
        ///     area=1 (NotWalkable) — same approach as AddDoorBlockers.
        /// </summary>
        /// <summary>
        ///     Append a phantom <c>NotWalkable</c> box source over every
        ///     <c>Bed</c> in bake bounds. Without this, the bake produces
        ///     walkable mesh on the bed's flat top face: villagers
        ///     pathfinding past a bed can route OVER it (treating the bed
        ///     top as a shortcut, then getting stuck because the bed's
        ///     height exceeds the agent's climb), and the agent's
        ///     CapsuleCollider catches on the bed's frame when trying to
        ///     pass at floor level.
        ///
        ///     The blocker is generously sized (2.5m × 1.2m × 1.5m, raised
        ///     0.6m off the bed pivot) so it covers the bed's footprint
        ///     plus a small clearance margin that pushes the agent's
        ///     navmesh further away from the bed's collider. Beds remain
        ///     valid Pass-2 seed POSITIONS — Pass 2 uses GetGroundHeight,
        ///     not the navmesh, so the seed cell's "walkability" in the
        ///     agent navmesh doesn't matter for seeding. (Villagers that
        ///     need to physically reach the bed for sleep can still get
        ///     close — the blocker is above floor, so the floor next to
        ///     the bed remains walkable.)
        /// </summary>
        private static int AddBedBlockers(List<NavMeshBuildSource> sources, Bounds bounds)
        {
            var added = 0;
            var allBeds = Object.FindObjectsByType<Bed>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var bed in allBeds)
            {
                if (bed == null || bed.transform == null) continue;
                if (!bounds.Contains(bed.transform.position)) continue;

                // Size the phantom from the bed's ACTUAL collider footprint, not
                // a hardcoded box. The old fixed (2.5,1.2,1.5) assumed a bed
                // oriented length-along-local-X; real beds vary in size and
                // orientation (e.g. a 2.8m bed whose long axis is the OTHER way),
                // so the fixed box left ~0.65m of each bed end uncarved —
                // walkable navmesh sitting on a solid bed collider, and the agent
                // caught on the bed corner. Collider.bounds is the world AABB, so
                // an axis-aligned phantom covering it carves the bed at ANY
                // rotation (no rotation/size mismatch possible).
                var bedCol = bed.GetComponentInChildren<Collider>();
                if (bedCol == null) continue;
                var bb = bedCol.bounds;
                const float xzMargin = 0.2f; // cover the exact edge; agent-radius erosion adds the rest
                // Vertical span: carve from BELOW the bed's base (down past the
                // floor the bed rests on) up to above the bed top. The walkable
                // navmesh under a bed is generated at FLOOR level (~bed base),
                // not the bed-top — so a phantom that only covers the bed top
                // leaves the floor-level navmesh under the bed footprint
                // walkable, and the agent still walks into the bed. botPad
                // reaches below the floor; topPad clears the bed top.
                const float botPad = 0.6f;
                const float topPad = 0.4f;
                var botY = bb.min.y - botPad;
                var topY = bb.max.y + topPad;
                // ModifierBox, NOT Box: a regular Box source produces walkable
                // surface on its own top and does NOT override the walkable
                // navmesh already generated by the floor/bed-top colliders under
                // it — so a Box phantom never actually carved the bed (verified:
                // navmesh persisted on the bed top at 37.81). ModifierBox is a
                // pure area-override volume that stamps area=1 (NotWalkable)
                // across its column, removing the bed-footprint navmesh. Same
                // approach AddOutsideCellBlockers uses.
                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.ModifierBox,
                    size = new Vector3(bb.size.x + xzMargin, topY - botY, bb.size.z + xzMargin),
                    transform = Matrix4x4.TRS(
                        new Vector3(bb.center.x, (botY + topY) * 0.5f, bb.center.z),
                        Quaternion.identity,
                        Vector3.one),
                    area = 1, // NotWalkable
                });
                added++;
            }
            return added;
        }

        private static int AddOutsideCellBlockers(
            List<NavMeshBuildSource> sources,
            HashSet<long> outsideCells,
            Bounds bounds)
        {
            if (outsideCells == null || outsideCells.Count == 0) return 0;

            var cell = ValheimVillages.Villager.AI.Navigation.RegionGraph.LookupCellSize;
            var ySize = bounds.size.y + 4f;
            var yCenter = bounds.center.y;

            // Greedy-decompose contiguous outside-cell blocks into rectangles.
            // For a village with ~3000 outside cells this typically drops to
            // tens of ModifierBox sources — ~50x fewer voxelizer inputs. We
            // use ModifierBox (not Box) so the bake treats this as a pure
            // area-override volume: it stamps area=1 (NotWalkable) across the
            // covered XZ column without ALSO producing walkable surface on
            // top of the box (which a regular Box source would do, defeating
            // the carve at rampart-top altitude).
            var rects = ValheimVillages.Villager.AI.Navigation
                .RubberBandPrune.DecomposeToRectangles(outsideCells);
            foreach (var r in rects)
            {
                var w = (r.Gx1 - r.Gx0 + 1) * cell;
                var d = (r.Gz1 - r.Gz0 + 1) * cell;
                var cx = (r.Gx0 + r.Gx1 + 1) * 0.5f * cell;
                var cz = (r.Gz0 + r.Gz1 + 1) * 0.5f * cell;

                sources.Add(new NavMeshBuildSource
                {
                    shape = NavMeshBuildSourceShape.ModifierBox,
                    size = new Vector3(w, ySize, d),
                    transform = Matrix4x4.TRS(
                        new Vector3(cx, yCenter, cz),
                        Quaternion.identity, Vector3.one),
                    area = 1, // NotWalkable
                });
            }

            Plugin.Log?.LogInfo(
                $"[NavMeshBake] OutsideCellBlockers: {outsideCells.Count} cells -> " +
                $"{rects.Count} ModifierBox rectangles");
            return rects.Count;
        }

        private static void AppendMesh(List<Vector3> verts, List<int> idx, List<int> triLayers, int layer,
            Matrix4x4 t, Mesh mesh,
            float minX, float minZ, float maxX, float maxZ)
        {
            Vector3[] localVerts;
            int[] localTris;
            try
            {
                localVerts = mesh.vertices;
                localTris = mesh.triangles;
            }
            catch
            {
                // Defensive: even isReadable=true meshes can throw on some builds.
                return;
            }

            // Transform once into a scratch array so we can per-triangle
            // bounds-check using world-space centroids without re-transforming.
            // Triangles whose centroid is outside [minX..maxX, minZ..maxZ] are
            // dropped here; unused vertices are pruned by tracking which
            // local indices actually get used.
            var worldVerts = new Vector3[localVerts.Length];
            for (var i = 0; i < localVerts.Length; i++)
                worldVerts[i] = t.MultiplyPoint3x4(localVerts[i]);

            // Map local-index -> output-index (lazy, only for used vertices).
            var remap = new int[localVerts.Length];
            for (var i = 0; i < remap.Length; i++) remap[i] = -1;

            for (var i = 0; i < localTris.Length; i += 3)
            {
                int li0 = localTris[i], li1 = localTris[i + 1], li2 = localTris[i + 2];
                var v0 = worldVerts[li0];
                var v1 = worldVerts[li1];
                var v2 = worldVerts[li2];
                var cx = (v0.x + v1.x + v2.x) * (1f / 3f);
                var cz = (v0.z + v1.z + v2.z) * (1f / 3f);
                if (cx < minX || cx > maxX || cz < minZ || cz > maxZ) continue;

                if (remap[li0] < 0)
                {
                    remap[li0] = verts.Count;
                    verts.Add(v0);
                }

                if (remap[li1] < 0)
                {
                    remap[li1] = verts.Count;
                    verts.Add(v1);
                }

                if (remap[li2] < 0)
                {
                    remap[li2] = verts.Count;
                    verts.Add(v2);
                }

                idx.Add(remap[li0]);
                idx.Add(remap[li1]);
                idx.Add(remap[li2]);
                triLayers.Add(layer);
            }
        }

        // Minimum world-space normal.y for a box face to be finely
        // tessellated. Mirrors RegionBuilder's MinNormalY pre-filter (cos 60°):
        // only faces that could plausibly be walkable get the lattice; vertical
        // wall/side faces stay 2 triangles (they're normal-rejected downstream
        // anyway), keeping the triangle-count increase focused on floors.
        private const float SubdivFaceMinNormalY = 0.5f;

        private static void AppendBox(List<Vector3> verts, List<int> idx, List<int> triLayers, int layer,
            Matrix4x4 t, Vector3 size,
            float minX, float minZ, float maxX, float maxZ, bool subdivide)
        {
            // Box bounds check via the world-space transform origin; boxes
            // are typically small (door blockers, piece colliders) so a
            // single-point center test is sufficient — partial-overlap boxes
            // would have been rejected by CollectSources' bounds anyway.
            var worldCenter = t.MultiplyPoint3x4(Vector3.zero);
            if (worldCenter.x < minX || worldCenter.x > maxX ||
                worldCenter.z < minZ || worldCenter.z > maxZ) return;

            // Each face is an origin corner + two edge vectors (u, v) spanning
            // it, plus the local outward axis. Walkable (upward-facing) faces
            // are tessellated into a shared-vertex lattice at <= LookupCellSize
            // edges so the per-triangle walkability filter rejects only the
            // cells a piece collider (chest, anvil) actually intersects, instead
            // of failing a whole 4m floor at its single centroid. The lattice is
            // conforming (shared verts within a face) so the floor stays one
            // connected region rather than shattering into pruned slivers.
            var h = size * 0.5f;
            var ux = new Vector3(2f * h.x, 0f, 0f);
            var uy = new Vector3(0f, 2f * h.y, 0f);
            var uz = new Vector3(0f, 0f, 2f * h.z);

            // +Y top / -Y bottom span X×Z.
            AppendBoxFace(verts, idx, triLayers, layer, t,
                new Vector3(-h.x, h.y, -h.z), ux, uz, Vector3.up, subdivide);
            AppendBoxFace(verts, idx, triLayers, layer, t,
                new Vector3(-h.x, -h.y, -h.z), ux, uz, Vector3.down, subdivide);
            // +Z front / -Z back span X×Y.
            AppendBoxFace(verts, idx, triLayers, layer, t,
                new Vector3(-h.x, -h.y, h.z), ux, uy, Vector3.forward, subdivide);
            AppendBoxFace(verts, idx, triLayers, layer, t,
                new Vector3(-h.x, -h.y, -h.z), ux, uy, Vector3.back, subdivide);
            // +X right / -X left span Z×Y.
            AppendBoxFace(verts, idx, triLayers, layer, t,
                new Vector3(h.x, -h.y, -h.z), uz, uy, Vector3.right, subdivide);
            AppendBoxFace(verts, idx, triLayers, layer, t,
                new Vector3(-h.x, -h.y, -h.z), uz, uy, Vector3.left, subdivide);
        }

        /// <summary>
        ///     Emit one box face (origin corner + edge vectors u, v) as either a
        ///     single quad (2 tris) or, when <paramref name="subdivide" /> and the
        ///     face points up, a shared-vertex lattice at &lt;= LookupCellSize
        ///     edges. Per-cell winding is corrected so each triangle's normal
        ///     points along the face's outward axis — the walkability filter keys
        ///     on normal.y, so winding must be outward.
        /// </summary>
        private static void AppendBoxFace(List<Vector3> verts, List<int> idx, List<int> triLayers,
            int layer, Matrix4x4 t, Vector3 originLocal, Vector3 uLocal, Vector3 vLocal,
            Vector3 axisLocal, bool subdivide)
        {
            var worldOut = t.MultiplyVector(axisLocal).normalized;

            var nu = 1;
            var nv = 1;
            if (subdivide && worldOut.y >= SubdivFaceMinNormalY)
            {
                var maxEdge = RegionGraph.LookupCellSize;
                var uLen = t.MultiplyVector(uLocal).magnitude;
                var vLen = t.MultiplyVector(vLocal).magnitude;
                nu = Mathf.Max(1, Mathf.CeilToInt(uLen / maxEdge));
                nv = Mathf.Max(1, Mathf.CeilToInt(vLen / maxEdge));
            }

            // (nu+1)*(nv+1) world-space lattice, shared across cells.
            var baseIdx = verts.Count;
            for (var j = 0; j <= nv; j++)
            {
                var tv = (float)j / nv;
                for (var i = 0; i <= nu; i++)
                {
                    var su = (float)i / nu;
                    verts.Add(t.MultiplyPoint3x4(originLocal + uLocal * su + vLocal * tv));
                }
            }

            var stride = nu + 1;
            for (var j = 0; j < nv; j++)
            for (var i = 0; i < nu; i++)
            {
                var p00 = baseIdx + j * stride + i;
                var p10 = p00 + 1;
                var p01 = p00 + stride;
                var p11 = p01 + 1;

                // Orient winding so the cell normal faces outward.
                var n = Vector3.Cross(verts[p10] - verts[p00], verts[p11] - verts[p00]);
                if (Vector3.Dot(n, worldOut) >= 0f)
                {
                    idx.Add(p00); idx.Add(p10); idx.Add(p11);
                    idx.Add(p00); idx.Add(p11); idx.Add(p01);
                }
                else
                {
                    idx.Add(p00); idx.Add(p11); idx.Add(p10);
                    idx.Add(p00); idx.Add(p01); idx.Add(p11);
                }

                triLayers.Add(layer);
                triLayers.Add(layer);
            }
        }

        /// <summary>
        ///     Remove the active bake's NavMesh data. Invoked automatically on
        ///     hot reload via <see cref="RegisterCleanupAttribute" />; safe to call
        ///     when no bake is active.
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            // Route through holder so a fresh assembly cleanup also clears
            // instances inherited from the prior assembly.
            Holder.RemoveAll();
            s_terrainSources.Clear();
            s_pieceSources.Clear();
            s_piecePhantomCount = 0;
            s_lastDoorPhantoms = 0;
            s_lastBedPhantoms = 0;
            s_lastOutsidePhantoms = 0;
        }

        public struct BakeResult
        {
            public bool Success;
            public int SourceCount;
            public int DoorsBlocked;
            public int DoorPiecesDropped;
            public int BedsBlocked;
            public int OutsideCellsBlocked;
            public int OutsideCellsCount;
            public float DurationMs;
            public string FailureReason;

            // Per-kind sub-counts so the caller can log each bake separately
            // while still seeing the rollup totals in SourceCount / DurationMs.
            public int TerrainSourceCount;
            public int PieceSourceCount;
            public float TerrainDurationMs;
            public float PieceDurationMs;
        }
    }

    /// <summary>
    ///     Owns the active <see cref="NavMeshDataInstance" /> handles for the
    ///     villager bake on a <see cref="Object.DontDestroyOnLoad" /> GameObject
    ///     named <c>VV_NavMeshBakeHolder</c>. New assemblies find the existing
    ///     holder by name so they inherit and can clean up the prior assembly's
    ///     bake instances. <see cref="OnDestroy" /> is a safety net for full
    ///     teardown (game exit, scene unload).
    /// </summary>
    internal class NavMeshBakeHolder : MonoBehaviour
    {
        private NavMeshDataInstance m_piece;
        private NavMeshDataInstance m_terrain;
        private NavMeshDataInstance m_hna;

        internal bool HasAny => m_terrain.valid || m_piece.valid || m_hna.valid;

        private void OnDestroy()
        {
            var removed = 0;
            if (m_terrain.valid)
            {
                m_terrain.Remove();
                m_terrain = default;
                removed++;
            }

            if (m_piece.valid)
            {
                m_piece.Remove();
                m_piece = default;
                removed++;
            }

            if (m_hna.valid)
            {
                m_hna.Remove();
                m_hna = default;
                removed++;
            }

            if (removed > 0)
                Plugin.Log?.LogInfo(
                    $"[NavMeshBakeHolder] OnDestroy: removed {removed} orphaned NavMesh data instances");
        }

        internal void SetTerrain(NavMeshDataInstance instance)
        {
            RemoveTerrain();
            m_terrain = instance;
        }

        internal void SetPiece(NavMeshDataInstance instance)
        {
            RemovePiece();
            m_piece = instance;
        }

        internal void SetHna(NavMeshDataInstance instance)
        {
            RemoveHna();
            m_hna = instance;
        }

        internal void RemoveTerrain()
        {
            if (m_terrain.valid)
            {
                m_terrain.Remove();
                m_terrain = default;
            }
        }

        internal void RemovePiece()
        {
            if (m_piece.valid)
            {
                m_piece.Remove();
                m_piece = default;
            }
        }

        internal void RemoveHna()
        {
            if (m_hna.valid)
            {
                m_hna.Remove();
                m_hna = default;
            }
        }

        internal void RemoveAll()
        {
            RemoveTerrain();
            RemovePiece();
            RemoveHna();
        }
    }
}