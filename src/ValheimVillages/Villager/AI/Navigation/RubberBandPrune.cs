using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.TaskQueue.Handlers;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     "Rubber-band" prune: three sequential passes on a 4-connected XZ grid at
    ///     <see cref="RegionGraph.LookupCellSize" /> resolution. Each pass is
    ///     authoritative on its own scope and runs strictly on its target layer
    ///     (terrain or piece) so cross-kind geometry can never bridge across walls:
    ///     <list type="number">
    ///         <item>
    ///             <b>Pass 1</b> (terrain): outside-in flood from every perimeter
    ///             cell of the inflated bake bounds. Cell→cell traversal is gated by a
    ///             symmetric <see cref="Physics.CheckCapsule" /> against the <c>piece</c>
    ///             layer. Output: <c>outsideCells</c> (terrain XZ keys outside the
    ///             outermost wall ring). Handles layered walls correctly — flood stops at
    ///             the OUTER ring, so secondary courtyards are not marked outside.
    ///         </item>
    ///         <item>
    ///             <b>Pass 2</b> (terrain): inside-out flood seeded from each bed's
    ///             XZ column (the terrain cell directly under/over the bed). Same
    ///             <c>WallBlocks</c> primitive, refuses to expand into <c>outsideCells</c>.
    ///             Output: <c>bedReachableCells</c> (terrain XZ keys reachable from at
    ///             least one bed). Catches internal cliffs, pits, locked-off rooms.
    ///         </item>
    ///         <item>
    ///             <b>Pass 3</b> (piece): bed-reachable flood on the piece layer.
    ///             Seeds are piece lookup keys within <c>MaxSeedStep</c> Y of a
    ///             <c>bedReachableCells</c> terrain cell (i.e. "a villager could step
    ///             from terrain onto this piece"). BFS expands via 4-connected horizontal
    ///             neighbour XZs across all height buckets present at the neighbour
    ///             (stair pieces naturally bridge floors at different Ys), gated by
    ///             <c>|Δy| ≤ MaxClimb</c> on region-centroid Y. Step-height naturally
    ///             severs rooftops from floors in the same XZ column and severs floors
    ///             from wall-top pieces. Output: <c>pieceReachableKeys</c>. Fallback if
    ///             zero seeds: today's XZ-outside rule (keep piece iff its XZ is not in
    ///             <c>outsideCells</c>) with a warning log.
    ///         </item>
    ///     </list>
    ///     Cell-level apply (per kind):
    ///     <list type="bullet">
    ///         <item>
    ///             Terrain <c>LookupGrid</c> entries / tris dropped iff XZ NOT in
    ///             <c>bedReachableCells</c>.
    ///         </item>
    ///         <item>
    ///             Piece <c>LookupGrid</c> entries / tris dropped iff lookup key NOT
    ///             in <c>pieceReachableKeys</c>.
    ///         </item>
    ///         <item>
    ///             <c>static_solid</c> tris dropped iff centroid XZ in
    ///             <c>outsideCells</c> (unchanged from prior PR).
    ///         </item>
    ///     </list>
    ///     Region-level cascade: a region is dropped wholesale iff every one of
    ///     its cells failed the per-kind keep rule above (terrain by XZ,
    ///     piece by lookup key).
    /// </summary>
    internal static class RubberBandPrune
    {
        // Waist-height probe band, relative to a cell's ground Y.
        //
        // The probe is a thin Y-slab at walker waist height that fully
        // spans the cell pair in the step direction. Tuned to:
        //   - CATCH walls (extend from ground to 2m+, body crosses waist)
        //   - MISS beds (top at ~0.6m, below WaistMin)
        //   - MISS overhead arches / vaults / ceilings (start at ~2m,
        //     above WaistMax)
        //
        // WaistMin 0.7m: above typical bed top (~0.6m) and above the
        // villager's max climb (~0.3m). Things shorter than this the
        // walker can step over, so they shouldn't block.
        // WaistMax 1.3m: walker chest height. Above this we'd start
        // catching low arches / overhanging eaves.
        //
        // WallBlocks calls this twice — once at each cell's ground Y —
        // so a wall on a plateau (uphill step) is caught by the higher
        // probe while a wall on the low side is caught by the lower one.
        // The OLD single-height probe at min+1m missed plateau walls
        // (probe sat below them); max+1m missed low-side walls.
        private const float WaistMin = 0.7f;
        private const float WaistMax = 1.3f;

        // Mirrors RegionGraph.PackLookup's K constant. Used for the inverse
        // unpacker below — must stay in sync if PackLookup is ever retuned.
        private const long PackK = 1_000_003L;
        private const long PackKHalf = PackK / 2;

        // --- Diagnostic snapshot (for vv_probe) ---
        // Populated at end of every Apply(). Cleared on hot reload via
        // [RegisterCleanup]. MeshProbe reads these to answer "could Pass 1 have
        // reached this cell, and if not, what blocked it?".
        internal static HashSet<long> LastOutsideCells;
        internal static Dictionary<long, float> LastXzMaxY;
        internal static int LastGxMin, LastGzMin, LastGxMax, LastGzMax;
        internal static float LastCell;
        internal static int LastPieceMask;
        internal static bool HasSnapshot;

        [RegisterCleanup]
        public static void ClearDiagnosticState()
        {
            LastOutsideCells = null;
            LastXzMaxY = null;
            LastGxMin = LastGzMin = LastGxMax = LastGzMax = 0;
            LastCell = 0f;
            LastPieceMask = 0;
            HasSnapshot = false;
        }

        public static Stats Apply(
            HashSet<string> regionIds,
            Dictionary<string, Vector3> centroids,
            Dictionary<long, string> lookupGrid,
            List<(string id, Vector3 center, Vector3 outwardDir)> boundaryCells,
            List<RegionLink> links,
            Dictionary<string, SurfaceKind> kindMap,
            List<RegionBuilder.CachedTriangle> triangles,
            List<Vector3> beds,
            float minX, float minZ, float maxX, float maxZ,
            out HashSet<string> droppedRegionIds)
        {
            var dropped = new HashSet<string>();
            droppedRegionIds = dropped;
            var stats = new Stats();
            if (regionIds == null || regionIds.Count == 0) return stats;
            if (lookupGrid == null || lookupGrid.Count == 0) return stats;

            var pieceMask = LayerMask.GetMask("piece");
            if (pieceMask == 0)
            {
                Plugin.Log?.LogWarning("[RubberBand] Skipped: 'piece' layer not found");
                return stats;
            }

            // static_solid layer carries cliffs, rocks, and world props. These
            // bake as Piece-kind sources but the piece filter path in
            // RegionBuilder has no wall-aware primitive — outside-wall
            // static_solid artifacts persist through the existing prunes. We
            // sweep tris whose source layer == this and whose centroid XZ is
            // in outsideCells. Sentinel -1 disables the sweep if the layer
            // isn't found in the project.
            var staticSolidLayer = LayerMask.NameToLayer("static_solid");

            var cell = RegionGraph.LookupCellSize;
            var gxMin = Mathf.FloorToInt(minX / cell) - 1;
            var gzMin = Mathf.FloorToInt(minZ / cell) - 1;
            var gxMax = Mathf.FloorToInt(maxX / cell) + 1;
            var gzMax = Mathf.FloorToInt(maxZ / cell) + 1;

            // --- Flatten LookupGrid by XZ, split by SurfaceKind ---
            // xzMaxY{Terrain,Piece}: max region-centroid Y per XZ cell, per
            //   kind. Pass 1 / Pass 2 use the terrain map for cast-height
            //   anchoring; the piece map is informational only (joined into
            //   the diagnostic snapshot at the bottom).
            // ridToXz: combined region → XZ cells (terrain region cascade
            //   still operates on this).
            // ridToPieceKeys: per-piece-region → lookup keys. Piece region
            //   cascade uses this so we can ask "any of my keys reachable?"
            //   at lookup-key granularity (XZ would lose Y-bucket info).
            // xzToLookupKeys{Terrain,Piece}: per-kind reverse index used by
            //   the cell-level apply below to drop entries without rescanning
            //   the full lookupGrid.
            var xzMaxYTerrain = new Dictionary<long, float>();
            var xzMaxYPiece = new Dictionary<long, float>();
            var ridToXz = new Dictionary<string, List<long>>();
            var ridToPieceKeys = new Dictionary<string, HashSet<long>>();
            var xzToLookupKeysTerrain = new Dictionary<long, List<long>>();
            var xzToLookupKeysPiece = new Dictionary<long, List<long>>();
            foreach (var kv in lookupGrid)
            {
                UnpackLookup(kv.Key, out var gx, out var gz, out var _);
                if (gx < gxMin || gx > gxMax || gz < gzMin || gz > gzMax)
                    // Out-of-bounds LookupGrid entry — shouldn't happen if the
                    // partition stays inside the bake bounds, but defend anyway.
                    continue;
                var xz = XzKey(gx, gz);
                var kind = kindMap != null && kindMap.TryGetValue(kv.Value, out var k)
                    ? k
                    : SurfaceKind.Terrain;
                var isPiece = kind == SurfaceKind.Piece;
                var xzMaxY = isPiece ? xzMaxYPiece : xzMaxYTerrain;
                var xzToLookupKeys = isPiece
                    ? xzToLookupKeysPiece
                    : xzToLookupKeysTerrain;

                if (centroids.TryGetValue(kv.Value, out var c))
                {
                    if (!xzMaxY.TryGetValue(xz, out var yExisting) || c.y > yExisting)
                        xzMaxY[xz] = c.y;
                }
                else if (!xzMaxY.ContainsKey(xz))
                {
                    xzMaxY[xz] = 0f;
                }

                if (!ridToXz.TryGetValue(kv.Value, out var ridList))
                {
                    ridList = new List<long>(4);
                    ridToXz[kv.Value] = ridList;
                }

                ridList.Add(xz);
                if (!xzToLookupKeys.TryGetValue(xz, out var keyList))
                {
                    keyList = new List<long>(4);
                    xzToLookupKeys[xz] = keyList;
                }

                keyList.Add(kv.Key);
                if (isPiece)
                {
                    if (!ridToPieceKeys.TryGetValue(kv.Value, out var ridKeys))
                    {
                        ridKeys = new HashSet<long>();
                        ridToPieceKeys[kv.Value] = ridKeys;
                    }

                    ridKeys.Add(kv.Key);
                }
            }

            // --- Pass 1: outside-in terrain flood ---
            // Floods inward from every perimeter cell on the terrain layer.
            // Pieces don't participate as cells or stepping stones — a wall
            // piece is never a bridge between outside and inside terrain.
            // outsideCells contains every cell (populated or not) the flood
            // could reach without tripping WallBlocks on the piece layer.
            var outsideCells = new HashSet<long>();
            var queue = new Queue<long>();
            for (var gx = gxMin; gx <= gxMax; gx++)
            {
                EnqueuePerimeterSeed(gx, gzMin, outsideCells, queue);
                EnqueuePerimeterSeed(gx, gzMax, outsideCells, queue);
            }

            for (var gz = gzMin + 1; gz <= gzMax - 1; gz++)
            {
                EnqueuePerimeterSeed(gxMin, gz, outsideCells, queue);
                EnqueuePerimeterSeed(gxMax, gz, outsideCells, queue);
            }

            stats.PerimeterSeeds = outsideCells.Count;

            int[] dx = { 1, -1, 0, 0 };
            int[] dz = { 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                var curKey = queue.Dequeue();
                UnpackXz(curKey, out var gx, out var gz);
                var curY = GetCellY(gx, gz, xzMaxYTerrain, cell);

                for (var d = 0; d < 4; d++)
                {
                    int ngx = gx + dx[d], ngz = gz + dz[d];
                    if (ngx < gxMin || ngx > gxMax || ngz < gzMin || ngz > gzMax) continue;
                    var nKey = XzKey(ngx, ngz);
                    if (outsideCells.Contains(nKey)) continue;

                    var nY = GetCellY(ngx, ngz, xzMaxYTerrain, cell);
                    if (WallBlocks(gx, gz, ngx, ngz, curY, nY, cell, pieceMask)) continue;

                    outsideCells.Add(nKey);
                    queue.Enqueue(nKey);
                }
            }

            foreach (var kv in xzMaxYTerrain)
                if (outsideCells.Contains(kv.Key))
                    stats.OutsideTerrainCells++;

            // --- Pass 2: inside-out flood from beds ---
            // Walks any non-outside cell in the bake bounds (NOT only cells in
            // xzMaxYTerrain). The prior xzMaxYTerrain.ContainsKey gate made the
            // flood depend on the terrain bake's region coverage: in villages
            // heavily covered by piece floors / foundations / paving the
            // terrain under those pieces gets rejBlocked'd by RegionBuilder,
            // so xzMaxYTerrain has no entry there, and Pass 2 couldn't bridge
            // across them — fragmented inside-village walkable area into tiny
            // islands. A villager can legitimately walk on any non-outside
            // cell that isn't wall-blocked from its neighbour, regardless of
            // whether the terrain region centroid landed on this XZ.
            //
            // Bed seeding: bed's exact XZ cell, period. No 30m direct-scan
            // fallback (that was masking the real seeding semantics: a bed
            // outside the perimeter, or out of bake bounds, is a real bug
            // worth surfacing, not silently snapping to a nearby cell).
            //
            // Cell Y comes from ZoneSystem.GetGroundHeight (per-cell heightmap),
            // not xzMaxYTerrain (region-centroid Y can drift up to ~0.75m).
            var bedReachableCells = new HashSet<long>();
            var pass2Seeds = 0;
            if (beds != null && beds.Count > 0 && ZoneSystem.instance != null)
            {
                var bedQueue = new Queue<long>();
                foreach (var bed in beds)
                {
                    var bgx = Mathf.FloorToInt(bed.x / cell);
                    var bgz = Mathf.FloorToInt(bed.z / cell);
                    if (bgx < gxMin || bgx > gxMax || bgz < gzMin || bgz > gzMax)
                    {
                        Plugin.Log?.LogWarning(
                            $"[RubberBand] Pass 2 bed skipped: bed=({bed.x:F1},{bed.z:F1}) " +
                            $"cell=({bgx},{bgz}) out_of_bake_bounds " +
                            $"[{gxMin}..{gxMax}, {gzMin}..{gzMax}]");
                        continue;
                    }

                    var bedKey = XzKey(bgx, bgz);
                    if (outsideCells.Contains(bedKey))
                    {
                        Plugin.Log?.LogWarning(
                            $"[RubberBand] Pass 2 bed skipped: bed=({bed.x:F1},{bed.z:F1}) " +
                            $"cell=({bgx},{bgz}) lies in outsideCells " +
                            "(perimeter flood reached the bed's cell — bed outside walls?)");
                        continue;
                    }

                    if (bedReachableCells.Add(bedKey))
                    {
                        bedQueue.Enqueue(bedKey);
                        pass2Seeds++;
                    }
                }

                var halfCell = cell * 0.5f;
                while (bedQueue.Count > 0)
                {
                    var curKey = bedQueue.Dequeue();
                    UnpackXz(curKey, out var gx, out var gz);
                    var curWx = gx * cell + halfCell;
                    var curWz = gz * cell + halfCell;
                    var curY = ZoneSystem.instance.GetGroundHeight(new Vector3(curWx, 0f, curWz));
                    for (var d = 0; d < 4; d++)
                    {
                        int ngx = gx + dx[d], ngz = gz + dz[d];
                        if (ngx < gxMin || ngx > gxMax || ngz < gzMin || ngz > gzMax) continue;
                        var nKey = XzKey(ngx, ngz);
                        if (bedReachableCells.Contains(nKey)) continue;
                        if (outsideCells.Contains(nKey)) continue;

                        var nwx = ngx * cell + halfCell;
                        var nwz = ngz * cell + halfCell;
                        var nY = ZoneSystem.instance.GetGroundHeight(new Vector3(nwx, 0f, nwz));
                        if (WallBlocks(gx, gz, ngx, ngz, curY, nY, cell, pieceMask)) continue;

                        bedReachableCells.Add(nKey);
                        bedQueue.Enqueue(nKey);
                    }
                }
            }

            stats.Pass2Seeds = pass2Seeds;
            stats.BedReachableTerrainCells = bedReachableCells.Count;

            // --- Pass 3: bed-reachable piece flood (bridges through terrain) ---
            // Unified BFS over (XZ, Y) nodes. Seeded from every bed-reachable
            // terrain cell at its true ground height (ZoneSystem heightmap, not
            // region-centroid Y — region centroids drift up to ~0.75m and that
            // matters at the MaxClimb threshold). At each visit, the flood
            // tries stepping to:
            //   (a) the neighbour XZ's terrain — but ONLY if that XZ is itself
            //       bed-reachable. Terrain visits are pure CONNECTIVITY BRIDGES
            //       (let the flood span two piece islands separated by a strip
            //       of open ground). They never add anything to
            //       pieceReachableKeys — terrain decisions are Pass 2's alone.
            //   (b) any piece at the neighbour XZ (regardless of hb). Piece
            //       visits are gated by |c.y - curY| ≤ MaxClimb on region-
            //       centroid Y and are added to pieceReachableKeys.
            // Y-aware via centroid gating naturally severs roof apexes from
            // floors in the same XZ column (no 4-neighbour stair piece linking
            // them), and severs floor → wall-top (Δy typically 2m+).
            //
            // MaxClimb 1.0m: tight enough to drop the rampart → roof-base
            // bridge (typically ≥2m), still allows 1m-per-cell stair pieces.
            // Bump to 1.25–1.5m if stair connectivity breaks empirically.
            const float MaxClimb = 1.0f;
            var pieceReachableKeys = new HashSet<long>();
            var pass3Seeds = 0;
            if (xzToLookupKeysPiece.Count > 0 && bedReachableCells.Count > 0
                                              && ZoneSystem.instance != null)
            {
                var half = cell * 0.5f;
                var visitedTerrainXz = new HashSet<long>();
                // Queue entries are tagged isTerrain so dedup is kind-aware:
                //   - Terrain visits (seed + bridge) dedup by XZ via
                //     visitedTerrainXz on dequeue. Each bed-reachable XZ is
                //     processed once at ground altitude.
                //   - Piece visits (piece-step) skip the XZ gate so a column
                //     with stacked pieces can be re-entered at multiple
                //     altitudes; pieceReachableKeys dedups per lookup key.
                var pieceQueue = new Queue<(long xz, float y, bool isTerrain)>();
                foreach (var xz in bedReachableCells)
                {
                    UnpackXz(xz, out var gx, out var gz);
                    var wx = gx * cell + half;
                    var wz = gz * cell + half;
                    var ty = ZoneSystem.instance.GetGroundHeight(new Vector3(wx, 0f, wz));
                    // Do NOT pre-mark visitedTerrainXz here. Marking on
                    // seed-enqueue was the dead-code bug — it pre-flagged
                    // every bed-reachable XZ so the bridge step (a) below
                    // could never fire from a piece visit. Dedup at dequeue
                    // instead. (bedReachableCells is a HashSet so seeds are
                    // already unique; the queue is safe.)
                    pieceQueue.Enqueue((xz, ty, true));
                }

                while (pieceQueue.Count > 0)
                {
                    var (curXz, curY, isTerrain) = pieceQueue.Dequeue();
                    // Terrain-altitude dedup: each bed-reachable XZ runs its
                    // 4-neighbour scan at most once at ground height. Piece
                    // visits bypass this gate — pieceReachableKeys already
                    // prevents redundant per-piece work.
                    if (isTerrain && !visitedTerrainXz.Add(curXz)) continue;
                    UnpackXz(curXz, out var gx, out var gz);
                    for (var d = 0; d < 4; d++)
                    {
                        int ngx = gx + dx[d], ngz = gz + dz[d];
                        var nXz = XzKey(ngx, ngz);
                        // (a) Terrain bridge: enqueue as a terrain visit at the
                        //     neighbour's ground height if it's bed-reachable and
                        //     the climb from curY fits within MaxClimb. Fires
                        //     from both terrain visits AND piece visits — the
                        //     latter is "step off this piece onto the adjacent
                        //     ground". The pre-check against visitedTerrainXz is
                        //     an optimization; the authoritative dedup is the
                        //     dequeue gate above.
                        if (bedReachableCells.Contains(nXz) && !visitedTerrainXz.Contains(nXz))
                        {
                            var nwx = ngx * cell + half;
                            var nwz = ngz * cell + half;
                            var tnY = ZoneSystem.instance.GetGroundHeight(new Vector3(nwx, 0f, nwz));
                            if (Mathf.Abs(tnY - curY) <= MaxClimb) pieceQueue.Enqueue((nXz, tnY, true));
                        }

                        // (b) Piece step: walk every piece at neighbour XZ within
                        //     climb of curY. Dedup via pieceReachableKeys.
                        if (xzToLookupKeysPiece.TryGetValue(nXz, out var nPieceKeys))
                            foreach (var pKey in nPieceKeys)
                            {
                                if (pieceReachableKeys.Contains(pKey)) continue;
                                if (!lookupGrid.TryGetValue(pKey, out var rid)) continue;
                                if (!centroids.TryGetValue(rid, out var c)) continue;
                                if (Mathf.Abs(c.y - curY) > MaxClimb) continue;
                                // Don't chain into pieces in outsideCells.
                                // Pass 1's perimeter flood is the authoritative
                                // "what's inside the village" signal; pieces
                                // whose XZ Pass 1 reached are outside the wall
                                // ring (rogue floors, far-away structures,
                                // decorations). Without this gate, piece
                                // chains hop over walls into outside pieces
                                // via 4-neighbour adjacency. A wall-blocking
                                // primitive can't distinguish stair risers
                                // from walls reliably; this perimeter-based
                                // check sidesteps that ambiguity entirely.
                                if (outsideCells.Contains(nXz)) continue;
                                pieceReachableKeys.Add(pKey);
                                pieceQueue.Enqueue((nXz, c.y, false));
                                pass3Seeds++;
                            }
                    }
                }
            }

            stats.Pass3Seeds = pass3Seeds;
            stats.BedReachablePieceKeys = pieceReachableKeys.Count;

            // --- Cell-level apply ---
            // Static_solid sweep runs first so its tris are counted under
            // StaticSolidTrianglesDropped, not TrianglesDropped. Uses the full
            // outsideCells set (catches unpopulated outside cells the
            // populated-only main sweeps below skip).
            if (staticSolidLayer >= 0 && outsideCells.Count > 0 &&
                triangles != null && triangles.Count > 0)
            {
                var beforeCount = triangles.Count;
                triangles.RemoveAll(t =>
                {
                    if (t.Layer != staticSolidLayer) return false;
                    var cx = (t.V0.x + t.V1.x + t.V2.x) / 3f;
                    var cz = (t.V0.z + t.V1.z + t.V2.z) / 3f;
                    var tgx = Mathf.FloorToInt(cx / cell);
                    var tgz = Mathf.FloorToInt(cz / cell);
                    return outsideCells.Contains(XzKey(tgx, tgz));
                });
                stats.StaticSolidTrianglesDropped = beforeCount - triangles.Count;
            }

            // Terrain LookupGrid: drop entry iff its XZ is NOT in bedReachable.
            var terrainLookupDropped = 0;
            foreach (var kv in xzToLookupKeysTerrain)
            {
                if (bedReachableCells.Contains(kv.Key)) continue;
                foreach (var lookupKey in kv.Value)
                    if (lookupGrid.Remove(lookupKey))
                        terrainLookupDropped++;
            }

            // Piece LookupGrid (Pass 3): drop entry iff its lookup key is not
            // in pieceReachableKeys (bed-reachable piece flood result).
            var pieceLookupDropped = 0;
            var pass3PieceKeysDropped = 0;
            foreach (var kv in xzToLookupKeysPiece)
            foreach (var lookupKey in kv.Value)
            {
                if (pieceReachableKeys.Contains(lookupKey)) continue;
                pass3PieceKeysDropped++;
                if (lookupGrid.Remove(lookupKey)) pieceLookupDropped++;
            }

            stats.LookupCellsDropped = terrainLookupDropped + pieceLookupDropped;
            stats.Pass3PieceKeysDropped = pass3PieceKeysDropped;

            // Triangle sweep: terrain tris drop iff centroid XZ NOT in
            // bedReachable; piece tris drop iff their lookup key (gx, gz,
            // hb derived from centroid Y) is NOT in pieceReachableKeys.
            // Triangle kind comes from kindMap[t.RegionId]; missing kind
            // defaults to Terrain (safe — terrain rule is stricter).
            if (triangles != null && triangles.Count > 0)
            {
                var beforeCount = triangles.Count;
                triangles.RemoveAll(t =>
                {
                    var cx = (t.V0.x + t.V1.x + t.V2.x) / 3f;
                    var cz = (t.V0.z + t.V1.z + t.V2.z) / 3f;
                    var tgx = Mathf.FloorToInt(cx / cell);
                    var tgz = Mathf.FloorToInt(cz / cell);
                    var k = kindMap != null && !string.IsNullOrEmpty(t.RegionId)
                                            && kindMap.TryGetValue(t.RegionId, out var sk)
                        ? sk
                        : SurfaceKind.Terrain;
                    if (k == SurfaceKind.Piece)
                    {
                        var cy = (t.V0.y + t.V1.y + t.V2.y) / 3f;
                        var hb = RegionGraph.HeightBucket(cy);
                        var pKey = RegionGraph.PackLookup(tgx, tgz, hb);
                        return !pieceReachableKeys.Contains(pKey);
                    }

                    return !bedReachableCells.Contains(XzKey(tgx, tgz));
                });
                stats.TrianglesDropped = beforeCount - triangles.Count;
            }

            // --- Region-level cascade ---
            // A region is dropped wholesale iff none of its cells survived
            // the per-kind keep rule:
            //   terrain region: ≥1 XZ cell in bedReachableCells (uses ridToXz)
            //   piece region:   ≥1 lookup key in pieceReachableKeys (uses
            //                   ridToPieceKeys, which preserves hb info that
            //                   ridToXz collapses away)
            // A region with no recorded cells (centroid lookup failed during
            // flatten) is dropped as a no-op safety.
            foreach (var rid in regionIds)
            {
                var anyKept = false;
                var kind = kindMap != null && kindMap.TryGetValue(rid, out var k)
                    ? k
                    : SurfaceKind.Terrain;
                if (kind == SurfaceKind.Piece)
                {
                    if (ridToPieceKeys.TryGetValue(rid, out var pieceKeys))
                        foreach (var pKey in pieceKeys)
                            if (pieceReachableKeys.Contains(pKey))
                            {
                                anyKept = true;
                                break;
                            }
                }
                else
                {
                    if (ridToXz.TryGetValue(rid, out var cells))
                        foreach (var xz in cells)
                            if (bedReachableCells.Contains(xz))
                            {
                                anyKept = true;
                                break;
                            }
                }

                if (!anyKept) dropped.Add(rid);
            }

            if (dropped.Count > 0)
            {
                regionIds.RemoveWhere(r => dropped.Contains(r));
                foreach (var r in dropped)
                {
                    centroids?.Remove(r);
                    kindMap?.Remove(r);
                }

                links?.RemoveAll(l =>
                    dropped.Contains(l.FromRegionId) ||
                    dropped.Contains(l.ToRegionId));
                boundaryCells?.RemoveAll(b => dropped.Contains(b.id));
            }

            stats.RegionsDropped = dropped.Count;

            // --- Snapshot for diagnostics ---
            // Combine terrain + piece xzMaxY into a single map for vv_probe;
            // the probe reports cell Y as one number regardless of kind.
            LastOutsideCells = new HashSet<long>(outsideCells);
            var combinedXzMaxY = new Dictionary<long, float>(xzMaxYTerrain.Count + xzMaxYPiece.Count);
            foreach (var kv in xzMaxYTerrain) combinedXzMaxY[kv.Key] = kv.Value;
            foreach (var kv in xzMaxYPiece)
                if (!combinedXzMaxY.TryGetValue(kv.Key, out var existing) || kv.Value > existing)
                    combinedXzMaxY[kv.Key] = kv.Value;
            LastXzMaxY = combinedXzMaxY;
            LastGxMin = gxMin;
            LastGzMin = gzMin;
            LastGxMax = gxMax;
            LastGzMax = gzMax;
            LastCell = cell;
            LastPieceMask = pieceMask;
            HasSnapshot = true;

            return stats;
        }

        /// <summary>
        ///     Diagnostic helper: combined 64-bit XZ key (matches the internal
        ///     encoding used by <c>LastOutsideCells</c> / <c>LastXzMaxY</c>).
        /// </summary>
        internal static long DiagnoseXzKey(int gx, int gz)
        {
            return XzKey(gx, gz);
        }

        /// <summary>
        ///     Diagnostic helper: per-cell Y used by <see cref="Diagnose" />.
        ///     Falls back to <c>ZoneSystem.GetGroundHeight</c> when the cell
        ///     wasn't populated by the last partition.
        /// </summary>
        internal static float DiagnoseCellY(int gx, int gz)
        {
            if (LastXzMaxY == null) return 0f;
            return GetCellY(gx, gz, LastXzMaxY, LastCell > 0f ? LastCell : RegionGraph.LookupCellSize);
        }

        private static void EnqueuePerimeterSeed(int gx, int gz,
            HashSet<long> outsideCells, Queue<long> queue)
        {
            var key = XzKey(gx, gz);
            if (outsideCells.Add(key)) queue.Enqueue(key);
        }

        private static float GetCellY(int gx, int gz,
            Dictionary<long, float> xzMaxY, float cell)
        {
            var key = XzKey(gx, gz);
            if (xzMaxY.TryGetValue(key, out var y)) return y;
            if (ZoneSystem.instance == null) return 0f;
            var wx = gx * cell + cell * 0.5f;
            var wz = gz * cell + cell * 0.5f;
            return ZoneSystem.instance.GetGroundHeight(new Vector3(wx, 0f, wz));
        }

        private static bool WallBlocks(int gxA, int gzA, int gxB, int gzB,
            float yA, float yB, float cell, int mask)
        {
            // Probe at walker waist of EACH cell. Two probes handle
            // elevation steps: a wall on the higher cell's plateau sits
            // at plateauY+0.7..1.3 in world coords — a single probe at
            // min(yA,yB)+waist would sit below it. Probing at both cells'
            // waists catches walls at either elevation end. Short-circuits
            // on first hit.
            return ProbeAtWaist(gxA, gzA, gxB, gzB, yA, cell, mask)
                   || (yA != yB && ProbeAtWaist(gxA, gzA, gxB, gzB, yB, cell, mask));
        }

        private static bool ProbeAtWaist(int gxA, int gzA, int gxB, int gzB,
            float floorY, float cell, int mask)
        {
            ComputeWaistProbeBox(gxA, gzA, gxB, gzB, floorY, cell,
                out var center, out var halfExtents);
            return Physics.CheckBox(center, halfExtents, Quaternion.identity,
                mask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        ///     Diagnostic mirror of <see cref="WallBlocks" /> that also returns
        ///     the names of every collider the probe box hit (semicolon-joined,
        ///     truncated to 8 across both waist probes). Uses <c>OverlapBox</c>
        ///     so we can enumerate the blockers — slightly slower than CheckBox,
        ///     only called by <c>vv_probe</c>.
        /// </summary>
        internal static bool Diagnose(int gxA, int gzA, int gxB, int gzB,
            float yA, float yB, float cell, int mask, out string hitNames)
        {
            ComputeWaistProbeBox(gxA, gzA, gxB, gzB, yA, cell,
                out var centerA, out var halfExtentsA);
            var hitsA = Physics.OverlapBox(centerA, halfExtentsA, Quaternion.identity,
                mask, QueryTriggerInteraction.Ignore);
            Collider[] hitsB = null;
            if (yA != yB)
            {
                ComputeWaistProbeBox(gxA, gzA, gxB, gzB, yB, cell,
                    out var centerB, out var halfExtentsB);
                hitsB = Physics.OverlapBox(centerB, halfExtentsB, Quaternion.identity,
                    mask, QueryTriggerInteraction.Ignore);
            }

            var total = (hitsA?.Length ?? 0) + (hitsB?.Length ?? 0);
            if (total == 0)
            {
                hitNames = "";
                return false;
            }

            var names = new List<string>(Mathf.Min(total, 8));

            void AddNames(Collider[] arr, int max)
            {
                if (arr == null) return;
                var limit = Mathf.Min(arr.Length, max);
                for (var i = 0; i < limit; i++)
                    names.Add(arr[i] != null ? arr[i].name : "<null>");
            }

            AddNames(hitsA, 4);
            AddNames(hitsB, 4);
            hitNames = string.Join(";", names);
            if (total > names.Count) hitNames += $";+{total - names.Count}";
            return true;
        }

        // Build the waist-height probe box for a 4-neighbour step between
        // cells A and B, anchored at the given cell's ground Y. The box:
        //   - Spans the full cell pair in the step direction (catches walls
        //     anywhere along the segment in any orientation — including
        //     off-axis like hex / diagonal wall pieces).
        //   - Spans the full cell width perpendicular to the step (catches
        //     walls overlapping the shared edge anywhere across its 1m).
        //   - Y range floorY+WaistMin to floorY+WaistMax: thin horizontal
        //     slice at walker waist height, above beds and below arches.
        // Diagonal steps aren't used by the 4-neighbour BFS; non-cardinal
        // input is logged as a contract violation.
        private static void ComputeWaistProbeBox(int gxA, int gzA, int gxB, int gzB,
            float floorY, float cell,
            out Vector3 center, out Vector3 halfExtents)
        {
            var half = cell * 0.5f;
            var lowY = floorY + WaistMin;
            var highY = floorY + WaistMax;
            var centerY = (lowY + highY) * 0.5f;
            var halfY = (highY - lowY) * 0.5f;

            var midX = (gxA + gxB) * 0.5f * cell + half;
            var midZ = (gzA + gzB) * 0.5f * cell + half;
            center = new Vector3(midX, centerY, midZ);

            var dgx = gxB - gxA;
            var dgz = gzB - gzA;
            if ((dgx != 0 && dgz == 0) || (dgz != 0 && dgx == 0))
            {
                halfExtents = new Vector3(half, halfY, half);
            }
            else
            {
                Plugin.Log?.LogError(
                    "[RubberBand] ComputeWaistProbeBox called with non-cardinal step " +
                    $"({gxA},{gzA})->({gxB},{gzB}); 4-neighbour BFS contract violated.");
                halfExtents = new Vector3(half, halfY, half);
            }
        }

        // --- Encoding helpers ---

        // 2D XZ key — independent of RegionGraph's 3D lookup key. Symmetric
        // sign handling: arithmetic right shift preserves sign for the high
        // half; int cast on the low half truncates uint→int with wrap.
        private static long XzKey(int gx, int gz)
        {
            return ((long)(uint)gx << 32) | (uint)gz;
        }

        private static void UnpackXz(long key, out int gx, out int gz)
        {
            gx = (int)(key >> 32);
            gz = (int)(key & 0xFFFFFFFFL);
        }

        // Inverse of RegionGraph.PackLookup. Uses symmetric mod centred on
        // zero so negative gx/gz/hb round-trip correctly.
        private static void UnpackLookup(long key, out int gx, out int gz, out int hb)
        {
            var h = key % PackK;
            if (h > PackKHalf) h -= PackK;
            else if (h < -PackKHalf) h += PackK;
            var r1 = (key - h) / PackK;

            var g = r1 % PackK;
            if (g > PackKHalf) g -= PackK;
            else if (g < -PackKHalf) g += PackK;
            var r2 = (r1 - g) / PackK;

            hb = (int)h;
            gz = (int)g;
            gx = (int)r2;
        }

        public struct Stats
        {
            /// <summary>Populated terrain XZ cells inside <c>outsideCells</c> (Pass 1).</summary>
            public int OutsideTerrainCells;

            /// <summary>Bed seeds that successfully resolved to a non-outside terrain cell (Pass 2).</summary>
            public int Pass2Seeds;

            /// <summary>Terrain XZ cells reached by Pass 2's bed flood (or the fallback set).</summary>
            public int BedReachableTerrainCells;

            /// <summary>Piece lookup keys seeded for Pass 3 (piece cells within step-height of a bed-reachable terrain cell).</summary>
            public int Pass3Seeds;

            /// <summary>Piece lookup keys reached by Pass 3's piece flood (or the fallback set).</summary>
            public int BedReachablePieceKeys;

            /// <summary>Piece lookup keys dropped by Pass 3 (not reached by the piece flood).</summary>
            public int Pass3PieceKeysDropped;

            public int RegionsDropped;
            public int PerimeterSeeds;
            public int LookupCellsDropped;
            public int TrianglesDropped;
            public int StaticSolidTrianglesDropped;
        }
    }
}