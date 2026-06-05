using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Settings;
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

        private static readonly List<NavMeshBuildMarkup> s_emptyMarkups = new();

        // --- Tile grid (WI-2 incremental bake) ---
        // 16m tiles, zone-aligned: Valheim's zone grid is 64m and both grids
        // share world origin 0, so 64 = 4x16 makes every 16m tile nest exactly
        // inside one zone (see VillageZoneLoadingPatch.ZoneSize).
        internal const float TileSize = 16f;

        // Per-tile bakes are separate NavMeshData; Unity neither stitches them
        // nor avoids eroding the walkable surface by ~agentRadius at each
        // bake-bounds edge. Baking each tile over its rect + this skirt makes
        // adjacent tiles overlap across the seam (skirt must exceed the erosion
        // distance = agentRadius + NavMeshBakeRadiusBuffer + a margin). TUNABLE:
        // validate in-game (vv_bake_audit / render_view) that open floors show
        // no 16m-grid gutters before trusting this value.
        internal const float TileSeamSkirt = 4f;

        // Cached by the last full BakeVillage so RebakeTiles can re-bake a
        // subset using the same village bounds / outside-cell flood / agent
        // settings. s_hasTileCache gates RebakeTiles (false → caller must run a
        // full BakeVillage first). Reset on Clear (hot reload / unload).
        private static Bounds s_lastVillageBounds;
        private static HashSet<long> s_lastOutsideCells;
        private static NavMeshBuildSettings s_lastSettings;
        private static bool s_hasTileCache;

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

            // Remove all previous bake tiles to avoid accumulating data across
            // partition runs. Routed through the holder so a hot-reloaded
            // assembly correctly clears the prior assembly's instances.
            Holder.RemoveAll();

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

            // Tiled bake: instead of one monolithic NavMeshData over the whole
            // village, bake a per-16m-tile NavMeshData so a single tile can be
            // re-baked incrementally (see RebakeTiles). The village-wide source
            // lists above remain the authority for triangle EXTRACTION
            // (RegionBuilder) — only the agent NavMesh is tiled, so region
            // building is byte-identical to the monolithic design.
            //
            // The outside-cell set was flooded ONCE over the village bounds
            // above (ComputeOutsideCellsForBake). Each tile carves only the
            // outside cells in its own area (filtered in BakeOneTile); a
            // per-tile flood would mis-classify an interior tile's whole
            // interior as outside (the tile edge is not the village hull).
            //
            // Tiles bake over their rect + TileSeamSkirt so adjacent tiles'
            // walkable areas OVERLAP across the shared seam: Unity does not
            // stitch separate NavMeshData instances and erodes the walkable
            // surface by ~agentRadius at every bake-bounds edge, so without the
            // skirt every internal seam would carve a false gutter.
            s_lastVillageBounds = bounds;
            s_lastOutsideCells = outsideCells;
            s_lastSettings = settings;
            s_hasTileCache = true;
            var candidateTiles = TilesOverlapping(bounds);
            var tilesBaked = 0;
            foreach (var tile in candidateTiles)
                if (BakeOneTile(tile, settings, bounds.center.y, bounds.size.y, outsideCells))
                    tilesBaked++;
            Plugin.Log?.LogInfo(
                $"[NavMeshBake] Tiled bake: {tilesBaked}/{candidateTiles.Count} tile(s) baked " +
                $"(tile={TileSize:F0}m, skirt={TileSeamSkirt:F0}m)");

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

        /// <summary>
        ///     A 16m bake tile (grid coords, zone-aligned). The incremental
        ///     reconcilers (WI-4/WI-5) pass these to <see cref="RebakeTiles" />.
        /// </summary>
        public readonly struct TileId : IEquatable<TileId>
        {
            public readonly int Tx, Tz;
            public TileId(int tx, int tz) { Tx = tx; Tz = tz; }
            public bool Equals(TileId o) => Tx == o.Tx && Tz == o.Tz;
            public override bool Equals(object o) => o is TileId t && Equals(t);
            public override int GetHashCode() => unchecked(Tx * 73856093 ^ Tz * 19349663);
            public override string ToString() => $"({Tx},{Tz})";
        }

        /// <summary>Tile containing a world position (XZ).</summary>
        public static TileId TileOf(Vector3 worldPos) => new(
            Mathf.FloorToInt(worldPos.x / TileSize),
            Mathf.FloorToInt(worldPos.z / TileSize));

        /// <summary>Every tile whose 16m rect overlaps <paramref name="b" />'s XZ extent.</summary>
        public static List<TileId> TilesOverlapping(Bounds b)
        {
            var txMin = Mathf.FloorToInt(b.min.x / TileSize);
            var txMax = Mathf.FloorToInt(b.max.x / TileSize);
            var tzMin = Mathf.FloorToInt(b.min.z / TileSize);
            var tzMax = Mathf.FloorToInt(b.max.z / TileSize);
            var list = new List<TileId>((txMax - txMin + 1) * (tzMax - tzMin + 1));
            for (var tx = txMin; tx <= txMax; tx++)
            for (var tz = tzMin; tz <= tzMax; tz++)
                list.Add(new TileId(tx, tz));
            return list;
        }

        /// <summary>
        ///     Re-bake only <paramref name="tiles" /> (incremental path), reusing
        ///     the village bounds / outside-cell flood / agent settings cached by
        ///     the last full <see cref="BakeVillage" />. Returns the number of
        ///     tiles that produced NavMesh data. No-op (returns 0) if no full bake
        ///     has run yet — the caller must fall back to a full BakeVillage.
        ///     Does NOT update the extraction source caches
        ///     (<see cref="s_terrainSources" />/<see cref="s_pieceSources" />); the
        ///     incremental reconciler owns region re-extraction over the changed
        ///     window.
        /// </summary>
        public static int RebakeTiles(IEnumerable<TileId> tiles)
        {
            if (!s_hasTileCache || tiles == null) return 0;
            if (!VillagerAgentType.IsRegistered) return 0;
            var rebaked = 0;
            foreach (var tile in tiles)
                if (BakeOneTile(tile, s_lastSettings,
                        s_lastVillageBounds.center.y, s_lastVillageBounds.size.y,
                        s_lastOutsideCells))
                    rebaked++;
            return rebaked;
        }

        /// <summary>
        ///     Bake one tile's NavMeshData over its rect + <see cref="TileSeamSkirt" />.
        ///     Collects terrain + piece sources locally, drops Door/Bed geometry,
        ///     re-adds bed + outside-cell phantom blockers scoped to the tile, and
        ///     installs the instance on the Holder (replacing any prior instance
        ///     for the tile). Removes the tile's instance and returns false when
        ///     the tile has no sources / the build fails.
        /// </summary>
        private static bool BakeOneTile(TileId tile, NavMeshBuildSettings settings,
            float bakeYCenter, float bakeYSize, HashSet<long> villageOutsideCells)
        {
            var tileBounds = TileSkirtBounds(tile, bakeYCenter, bakeYSize);

            var terrain = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(tileBounds, s_terrainMask,
                NavMeshCollectGeometry.PhysicsColliders, 0, s_emptyMarkups, terrain);

            var piece = new List<NavMeshBuildSource>();
            NavMeshBuilder.CollectSources(tileBounds, s_pieceMask,
                NavMeshCollectGeometry.PhysicsColliders, 0, s_emptyMarkups, piece);

            // Same filtering as the village-wide pass: doorways bake walkable
            // (drop the Door hierarchy), bed tops are carved by a phantom (drop
            // the Bed collider so the blocker wins).
            piece.RemoveAll(s =>
                s.component != null && s.component.GetComponentInParent<Door>() != null);
            piece.RemoveAll(s =>
                s.component != null && s.component.GetComponentInParent<Bed>() != null);
            AddBedBlockers(piece, tileBounds);

            // Carve only the outside cells inside this tile's (skirted) bounds.
            var tileOutside = FilterOutsideCellsToBounds(villageOutsideCells, tileBounds);
            AddOutsideCellBlockers(piece, tileOutside, tileBounds);

            var combined = new List<NavMeshBuildSource>(terrain.Count + piece.Count);
            combined.AddRange(terrain);
            combined.AddRange(piece);
            if (combined.Count == 0)
            {
                Holder.RemoveTile(tile);
                return false;
            }

            var data = NavMeshBuilder.BuildNavMeshData(
                settings, combined, tileBounds, Vector3.zero, Quaternion.identity);
            if (data == null)
            {
                Holder.RemoveTile(tile);
                return false;
            }

            Holder.SetTile(tile, NavMesh.AddNavMeshData(data));
            return true;
        }

        /// <summary>Tile rect expanded by <see cref="TileSeamSkirt" /> on each XZ side,
        ///     spanning the village bake Y band.</summary>
        private static Bounds TileSkirtBounds(TileId t, float yCenter, float ySize)
        {
            var minX = t.Tx * TileSize - TileSeamSkirt;
            var minZ = t.Tz * TileSize - TileSeamSkirt;
            var maxX = (t.Tx + 1) * TileSize + TileSeamSkirt;
            var maxZ = (t.Tz + 1) * TileSize + TileSeamSkirt;
            var b = new Bounds();
            b.SetMinMax(
                new Vector3(minX, yCenter - ySize * 0.5f, minZ),
                new Vector3(maxX, yCenter + ySize * 0.5f, maxZ));
            return b;
        }

        /// <summary>Subset of <paramref name="cells" /> (XZ keys) whose cell centre
        ///     falls within <paramref name="b" />'s XZ extent.</summary>
        private static HashSet<long> FilterOutsideCellsToBounds(HashSet<long> cells, Bounds b)
        {
            var result = new HashSet<long>();
            if (cells == null) return result;
            var cell = RegionGraph.LookupCellSize;
            foreach (var key in cells)
            {
                RegionGraph.UnpackXz(key, out var gx, out var gz);
                var wx = (gx + 0.5f) * cell;
                var wz = (gz + 0.5f) * cell;
                if (wx >= b.min.x && wx <= b.max.x && wz >= b.min.z && wz <= b.max.z)
                    result.Add(key);
            }

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

            // Grow each blocker rectangle by this much per XZ side so the carve
            // overlaps the cell boundary instead of ending exactly on it — keeps a
            // thin sliver of walkable navmesh from surviving along the seam between
            // an outside blocker and an adjacent kept cell. Small relative to the
            // 1m cell and the agent-radius erosion, so it only tightens the navmesh
            // against the wall.
            const float padXZ = 0.2f;

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
                    size = new Vector3(w + 2f * padXZ, ySize, d + 2f * padXZ),
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
            s_lastOutsideCells = null;
            s_hasTileCache = false;
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
        // One NavMeshDataInstance per 16m bake tile. Instances live here (on the
        // DontDestroyOnLoad holder) rather than in NavMeshBakeManager's statics
        // so a hot-reloaded assembly can find this holder by name and Remove the
        // PRIOR assembly's instances (via OnDestroy) before installing fresh
        // ones — otherwise stale NavMesh data stacks layer-on-layer across
        // reloads. The dict is private to each holder, so the prior assembly's
        // (different) TileId type is irrelevant to teardown.
        private readonly Dictionary<NavMeshBakeManager.TileId, NavMeshDataInstance> m_tiles = new();

        internal bool HasAny
        {
            get
            {
                foreach (var inst in m_tiles.Values)
                    if (inst.valid)
                        return true;
                return false;
            }
        }

        private void OnDestroy()
        {
            var removed = 0;
            foreach (var inst in m_tiles.Values)
                if (inst.valid)
                {
                    inst.Remove();
                    removed++;
                }

            m_tiles.Clear();
            if (removed > 0)
                Plugin.Log?.LogInfo(
                    $"[NavMeshBakeHolder] OnDestroy: removed {removed} orphaned NavMesh tile instances");
        }

        internal void SetTile(NavMeshBakeManager.TileId tile, NavMeshDataInstance instance)
        {
            RemoveTile(tile);
            m_tiles[tile] = instance;
        }

        internal void RemoveTile(NavMeshBakeManager.TileId tile)
        {
            if (!m_tiles.TryGetValue(tile, out var inst)) return;
            if (inst.valid) inst.Remove();
            m_tiles.Remove(tile);
        }

        internal void RemoveAll()
        {
            foreach (var inst in m_tiles.Values)
                if (inst.valid)
                    inst.Remove();
            m_tiles.Clear();
        }
    }
}