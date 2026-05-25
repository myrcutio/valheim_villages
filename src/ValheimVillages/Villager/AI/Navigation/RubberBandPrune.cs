using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.Handlers;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    /// "Rubber-band" prune: three sequential passes on a 4-connected XZ grid at
    /// <see cref="RegionGraph.LookupCellSize"/> resolution. Each pass is
    /// authoritative on its own scope and runs strictly on its target layer
    /// (terrain or piece) so cross-kind geometry can never bridge across walls:
    /// <list type="number">
    /// <item><b>Pass 1</b> (terrain): outside-in flood from every perimeter
    /// cell of the inflated bake bounds. Cell→cell traversal is gated by a
    /// symmetric <see cref="Physics.CheckCapsule"/> against the <c>piece</c>
    /// layer. Output: <c>outsideCells</c> (terrain XZ keys outside the
    /// outermost wall ring). Handles layered walls correctly — flood stops at
    /// the OUTER ring, so secondary courtyards are not marked outside.</item>
    /// <item><b>Pass 2</b> (terrain): inside-out flood seeded from each bed's
    /// XZ column (the terrain cell directly under/over the bed). Same
    /// <c>WallBlocks</c> primitive, refuses to expand into <c>outsideCells</c>.
    /// Output: <c>bedReachableCells</c> (terrain XZ keys reachable from at
    /// least one bed). Catches internal cliffs, pits, locked-off rooms.</item>
    /// <item><b>Pass 3</b> (piece): keep iff the piece cell's XZ is NOT in
    /// <c>outsideCells</c>. No piece adjacency, no overlap chasing — purely
    /// XZ-strict. Trades off rampart pieces sitting on top of walls that
    /// straddle the inside/outside boundary (those half-keep). Overhangs and
    /// piece-to-piece reachability deferred to a follow-up.</item>
    /// </list>
    /// Cell-level apply (per kind):
    /// <list type="bullet">
    /// <item>Terrain <c>LookupGrid</c> entries / tris dropped iff XZ NOT in
    /// <c>bedReachableCells</c>.</item>
    /// <item>Piece <c>LookupGrid</c> entries / tris dropped iff XZ in
    /// <c>outsideCells</c>.</item>
    /// <item><c>static_solid</c> tris dropped iff centroid XZ in
    /// <c>outsideCells</c> (unchanged from prior PR).</item>
    /// </list>
    /// Region-level cascade: a region is dropped wholesale iff every one of
    /// its XZ cells failed the per-kind keep rule above.
    /// </summary>
    internal static class RubberBandPrune
    {
        public struct Stats
        {
            /// <summary>Populated terrain XZ cells inside <c>outsideCells</c> (Pass 1).</summary>
            public int OutsideTerrainCells;
            /// <summary>Bed seeds that successfully resolved to a non-outside terrain cell (Pass 2).</summary>
            public int Pass2Seeds;
            /// <summary>Terrain XZ cells reached by Pass 2's bed flood (or the fallback set).</summary>
            public int BedReachableTerrainCells;
            /// <summary>Piece XZ cells dropped by Pass 3 (cells inside <c>outsideCells</c>).</summary>
            public int Pass3PieceCellsDropped;
            public int RegionsDropped;
            public int PerimeterSeeds;
            public int LookupCellsDropped;
            public int TrianglesDropped;
            public int StaticSolidTrianglesDropped;
        }

        private const float CapsuleRadius = 0.35f;
        private const float CastHeightOffset = 1f;

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

        // Mirrors RegionGraph.PackLookup's K constant. Used for the inverse
        // unpacker below — must stay in sync if PackLookup is ever retuned.
        private const long PackK = 1_000_003L;
        private const long PackKHalf = PackK / 2;

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

            int pieceMask = LayerMask.GetMask("piece");
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
            int staticSolidLayer = LayerMask.NameToLayer("static_solid");

            float cell = RegionGraph.LookupCellSize;
            int gxMin = Mathf.FloorToInt(minX / cell) - 1;
            int gzMin = Mathf.FloorToInt(minZ / cell) - 1;
            int gxMax = Mathf.FloorToInt(maxX / cell) + 1;
            int gzMax = Mathf.FloorToInt(maxZ / cell) + 1;

            // --- Flatten LookupGrid by XZ, split by SurfaceKind ---
            // xzMaxY{Terrain,Piece}: max region-centroid Y per XZ cell, per
            //   kind. Pass 1 / Pass 2 use the terrain map for cast-height
            //   anchoring; the piece map is informational only (joined into
            //   the diagnostic snapshot at the bottom).
            // ridToXz: combined region → XZ cells (the region cascade still
            //   operates on the combined set).
            // xzToLookupKeys{Terrain,Piece}: per-kind reverse index used by
            //   the cell-level apply below to drop entries without rescanning
            //   the full lookupGrid.
            var xzMaxYTerrain = new Dictionary<long, float>();
            var xzMaxYPiece = new Dictionary<long, float>();
            var ridToXz = new Dictionary<string, List<long>>();
            var xzToLookupKeysTerrain = new Dictionary<long, List<long>>();
            var xzToLookupKeysPiece = new Dictionary<long, List<long>>();
            foreach (var kv in lookupGrid)
            {
                UnpackLookup(kv.Key, out int gx, out int gz, out int _);
                if (gx < gxMin || gx > gxMax || gz < gzMin || gz > gzMax)
                {
                    // Out-of-bounds LookupGrid entry — shouldn't happen if the
                    // partition stays inside the bake bounds, but defend anyway.
                    continue;
                }
                long xz = XzKey(gx, gz);
                SurfaceKind kind = (kindMap != null && kindMap.TryGetValue(kv.Value, out var k))
                    ? k : SurfaceKind.Terrain;
                bool isPiece = kind == SurfaceKind.Piece;
                Dictionary<long, float> xzMaxY = isPiece ? xzMaxYPiece : xzMaxYTerrain;
                Dictionary<long, List<long>> xzToLookupKeys = isPiece
                    ? xzToLookupKeysPiece : xzToLookupKeysTerrain;

                if (centroids.TryGetValue(kv.Value, out Vector3 c))
                {
                    if (!xzMaxY.TryGetValue(xz, out float yExisting) || c.y > yExisting)
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
            }

            // --- Pass 1: outside-in terrain flood ---
            // Floods inward from every perimeter cell on the terrain layer.
            // Pieces don't participate as cells or stepping stones — a wall
            // piece is never a bridge between outside and inside terrain.
            // outsideCells contains every cell (populated or not) the flood
            // could reach without tripping WallBlocks on the piece layer.
            var outsideCells = new HashSet<long>();
            var queue = new Queue<long>();
            for (int gx = gxMin; gx <= gxMax; gx++)
            {
                EnqueuePerimeterSeed(gx, gzMin, outsideCells, queue);
                EnqueuePerimeterSeed(gx, gzMax, outsideCells, queue);
            }
            for (int gz = gzMin + 1; gz <= gzMax - 1; gz++)
            {
                EnqueuePerimeterSeed(gxMin, gz, outsideCells, queue);
                EnqueuePerimeterSeed(gxMax, gz, outsideCells, queue);
            }
            stats.PerimeterSeeds = outsideCells.Count;

            int[] dx = { 1, -1, 0, 0 };
            int[] dz = { 0, 0, 1, -1 };

            while (queue.Count > 0)
            {
                long curKey = queue.Dequeue();
                UnpackXz(curKey, out int gx, out int gz);
                float curY = GetCellY(gx, gz, xzMaxYTerrain, cell);

                for (int d = 0; d < 4; d++)
                {
                    int ngx = gx + dx[d], ngz = gz + dz[d];
                    if (ngx < gxMin || ngx > gxMax || ngz < gzMin || ngz > gzMax) continue;
                    long nKey = XzKey(ngx, ngz);
                    if (outsideCells.Contains(nKey)) continue;

                    float nY = GetCellY(ngx, ngz, xzMaxYTerrain, cell);
                    if (WallBlocks(gx, gz, ngx, ngz, curY, nY, cell, pieceMask)) continue;

                    outsideCells.Add(nKey);
                    queue.Enqueue(nKey);
                }
            }
            foreach (var kv in xzMaxYTerrain)
                if (outsideCells.Contains(kv.Key)) stats.OutsideTerrainCells++;

            // --- Pass 2: inside-out terrain flood from beds ---
            // For each bed, find the terrain cell at its XZ column (a bed at
            // (x, y, z) seeds the terrain cell at (Floor(x/cell), Floor(z/cell))
            // regardless of the bed's y — terrain is single-surface in Valheim
            // so vertical infinity collapses to one cell). Skip beds whose XZ
            // has no terrain (bed on a piece floor with no terrain below) or
            // whose seed cell is in outsideCells (bed outside the perimeter —
            // pathological). BFS expands via 4-connected terrain cells, same
            // WallBlocks primitive as Pass 1, refusing to enter outsideCells.
            // Fallback if no bed seeded: all non-outside terrain cells (matches
            // prior no-seeds branch — keeps the partition usable when beds
            // don't resolve to terrain).
            var bedReachableCells = new HashSet<long>();
            int pass2Seeds = 0;
            if (beds != null && beds.Count > 0)
            {
                var bedQueue = new Queue<long>();
                // Bed→terrain seeding: a bed inside a building sits over floor
                // pieces that shadow terrain in the combined LookupGrid at the
                // bed's exact XZ cell, so an exact-XZ rule misses every bed
                // deeper into a building than its outer wall thickness. The
                // user's "vertical infinity" intent maps to "find the closest
                // terrain cell to the bed's XZ column" — implemented as a
                // direct scan over xzMaxYTerrain, picking the closest
                // non-outside cell within MaxBedSeedDist. 30m is generous
                // enough to span any typical building / multi-room hall while
                // still being village-local.
                const float MaxBedSeedDist = 30f;
                const float MaxBedSeedDistSq = MaxBedSeedDist * MaxBedSeedDist;
                foreach (var bed in beds)
                {
                    int bgx = Mathf.FloorToInt(bed.x / cell);
                    int bgz = Mathf.FloorToInt(bed.z / cell);
                    long exactKey = XzKey(bgx, bgz);
                    bool exactHasTerrain = xzMaxYTerrain.ContainsKey(exactKey);
                    bool exactOutside = outsideCells.Contains(exactKey);
                    long bedKey;
                    if (exactHasTerrain && !exactOutside)
                    {
                        bedKey = exactKey;
                    }
                    else
                    {
                        // Closest non-outside terrain cell in xzMaxYTerrain
                        // within MaxBedSeedDist of the bed's XZ. Iterate the
                        // map directly — cheap (few hundred entries) and
                        // guaranteed to find any reachable cell regardless of
                        // how far the bed is from terrain.
                        long best = 0;
                        float bestDistSq = float.MaxValue;
                        foreach (var kv in xzMaxYTerrain)
                        {
                            if (outsideCells.Contains(kv.Key)) continue;
                            UnpackXz(kv.Key, out int kgx, out int kgz);
                            float cx = kgx * cell + cell * 0.5f;
                            float cz = kgz * cell + cell * 0.5f;
                            float ddx = cx - bed.x, ddz = cz - bed.z;
                            float dSq = ddx * ddx + ddz * ddz;
                            if (dSq > MaxBedSeedDistSq) continue;
                            if (dSq < bestDistSq) { bestDistSq = dSq; best = kv.Key; }
                        }
                        if (bestDistSq == float.MaxValue)
                        {
                            Plugin.Log?.LogInfo(
                                $"[RubberBand] Pass 2 bed skipped: bed=({bed.x:F1},{bed.z:F1}) " +
                                $"cell=({bgx},{bgz}) exact_terrain={exactHasTerrain} " +
                                $"exact_outside={exactOutside} no_seed_within_{MaxBedSeedDist:F0}m");
                            continue;
                        }
                        bedKey = best;
                    }
                    if (bedReachableCells.Add(bedKey))
                    {
                        bedQueue.Enqueue(bedKey);
                        pass2Seeds++;
                    }
                }
                while (bedQueue.Count > 0)
                {
                    long curKey = bedQueue.Dequeue();
                    UnpackXz(curKey, out int gx, out int gz);
                    float curY = GetCellY(gx, gz, xzMaxYTerrain, cell);
                    for (int d = 0; d < 4; d++)
                    {
                        int ngx = gx + dx[d], ngz = gz + dz[d];
                        if (ngx < gxMin || ngx > gxMax || ngz < gzMin || ngz > gzMax) continue;
                        long nKey = XzKey(ngx, ngz);
                        if (bedReachableCells.Contains(nKey)) continue;
                        if (outsideCells.Contains(nKey)) continue;
                        if (!xzMaxYTerrain.ContainsKey(nKey)) continue;

                        float nY = GetCellY(ngx, ngz, xzMaxYTerrain, cell);
                        if (WallBlocks(gx, gz, ngx, ngz, curY, nY, cell, pieceMask)) continue;

                        bedReachableCells.Add(nKey);
                        bedQueue.Enqueue(nKey);
                    }
                }
            }
            stats.Pass2Seeds = pass2Seeds;
            if (pass2Seeds == 0)
            {
                Plugin.Log?.LogWarning(
                    "[RubberBand] Pass 2 had no bed seeds (no beds in scene, or all " +
                    "beds resolved to non-terrain / outside-perimeter cells). Falling " +
                    "back to 'all non-outside terrain' as bed-reachable.");
                foreach (var kv in xzMaxYTerrain)
                    if (!outsideCells.Contains(kv.Key)) bedReachableCells.Add(kv.Key);
            }
            stats.BedReachableTerrainCells = bedReachableCells.Count;

            // --- Cell-level apply ---
            // Static_solid sweep runs first so its tris are counted under
            // StaticSolidTrianglesDropped, not TrianglesDropped. Uses the full
            // outsideCells set (catches unpopulated outside cells the
            // populated-only main sweeps below skip).
            if (staticSolidLayer >= 0 && outsideCells.Count > 0 &&
                triangles != null && triangles.Count > 0)
            {
                int beforeCount = triangles.Count;
                triangles.RemoveAll(t =>
                {
                    if (t.Layer != staticSolidLayer) return false;
                    float cx = (t.V0.x + t.V1.x + t.V2.x) / 3f;
                    float cz = (t.V0.z + t.V1.z + t.V2.z) / 3f;
                    int tgx = Mathf.FloorToInt(cx / cell);
                    int tgz = Mathf.FloorToInt(cz / cell);
                    return outsideCells.Contains(XzKey(tgx, tgz));
                });
                stats.StaticSolidTrianglesDropped = beforeCount - triangles.Count;
            }

            // Terrain LookupGrid: drop entry iff its XZ is NOT in bedReachable.
            int terrainLookupDropped = 0;
            foreach (var kv in xzToLookupKeysTerrain)
            {
                if (bedReachableCells.Contains(kv.Key)) continue;
                foreach (long lookupKey in kv.Value)
                {
                    if (lookupGrid.Remove(lookupKey)) terrainLookupDropped++;
                }
            }

            // Piece LookupGrid (Pass 3): drop entry iff its XZ is in outsideCells.
            int pieceLookupDropped = 0;
            int pass3PieceCellsDropped = 0;
            foreach (var kv in xzToLookupKeysPiece)
            {
                if (!outsideCells.Contains(kv.Key)) continue;
                pass3PieceCellsDropped++;
                foreach (long lookupKey in kv.Value)
                {
                    if (lookupGrid.Remove(lookupKey)) pieceLookupDropped++;
                }
            }
            stats.LookupCellsDropped = terrainLookupDropped + pieceLookupDropped;
            stats.Pass3PieceCellsDropped = pass3PieceCellsDropped;

            // Triangle sweep: terrain tris drop iff centroid XZ NOT in
            // bedReachable; piece tris drop iff centroid XZ in outsideCells.
            // Triangle kind comes from kindMap[t.RegionId]; missing kind
            // defaults to Terrain (safe — terrain rule is stricter).
            if (triangles != null && triangles.Count > 0)
            {
                int beforeCount = triangles.Count;
                triangles.RemoveAll(t =>
                {
                    float cx = (t.V0.x + t.V1.x + t.V2.x) / 3f;
                    float cz = (t.V0.z + t.V1.z + t.V2.z) / 3f;
                    int tgx = Mathf.FloorToInt(cx / cell);
                    int tgz = Mathf.FloorToInt(cz / cell);
                    long xz = XzKey(tgx, tgz);
                    SurfaceKind k = (kindMap != null && !string.IsNullOrEmpty(t.RegionId)
                                     && kindMap.TryGetValue(t.RegionId, out var sk))
                        ? sk : SurfaceKind.Terrain;
                    return k == SurfaceKind.Piece
                        ? outsideCells.Contains(xz)
                        : !bedReachableCells.Contains(xz);
                });
                stats.TrianglesDropped = beforeCount - triangles.Count;
            }

            // --- Region-level cascade ---
            // A region is dropped wholesale iff none of its XZ cells survived
            // the per-kind keep rule. Terrain regions need ≥1 cell in
            // bedReachableCells; piece regions need ≥1 cell not in
            // outsideCells. A region with no recorded XZ cells (centroid
            // lookup failed during flatten) is dropped as a no-op safety.
            foreach (string rid in regionIds)
            {
                bool anyKept = false;
                SurfaceKind kind = (kindMap != null && kindMap.TryGetValue(rid, out var k))
                    ? k : SurfaceKind.Terrain;
                if (ridToXz.TryGetValue(rid, out var cells))
                {
                    foreach (long xz in cells)
                    {
                        bool keptCell = kind == SurfaceKind.Piece
                            ? !outsideCells.Contains(xz)
                            : bedReachableCells.Contains(xz);
                        if (keptCell) { anyKept = true; break; }
                    }
                }
                if (!anyKept) dropped.Add(rid);
            }

            if (dropped.Count > 0)
            {
                regionIds.RemoveWhere(r => dropped.Contains(r));
                foreach (string r in dropped)
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
            {
                if (!combinedXzMaxY.TryGetValue(kv.Key, out float existing) || kv.Value > existing)
                    combinedXzMaxY[kv.Key] = kv.Value;
            }
            LastXzMaxY = combinedXzMaxY;
            LastGxMin = gxMin; LastGzMin = gzMin;
            LastGxMax = gxMax; LastGzMax = gzMax;
            LastCell = cell;
            LastPieceMask = pieceMask;
            HasSnapshot = true;

            return stats;
        }

        /// <summary>
        /// Diagnostic helper: combined 64-bit XZ key (matches the internal
        /// encoding used by <c>LastOutsideCells</c> / <c>LastXzMaxY</c>).
        /// </summary>
        internal static long DiagnoseXzKey(int gx, int gz) => XzKey(gx, gz);

        /// <summary>
        /// Diagnostic helper: per-cell Y used by <see cref="Diagnose"/>.
        /// Falls back to <c>ZoneSystem.GetGroundHeight</c> when the cell
        /// wasn't populated by the last partition.
        /// </summary>
        internal static float DiagnoseCellY(int gx, int gz)
        {
            if (LastXzMaxY == null) return 0f;
            return GetCellY(gx, gz, LastXzMaxY, LastCell > 0f ? LastCell : RegionGraph.LookupCellSize);
        }

        private static void EnqueuePerimeterSeed(int gx, int gz,
            HashSet<long> outsideCells, Queue<long> queue)
        {
            long key = XzKey(gx, gz);
            if (outsideCells.Add(key)) queue.Enqueue(key);
        }

        private static float GetCellY(int gx, int gz,
            Dictionary<long, float> xzMaxY, float cell)
        {
            long key = XzKey(gx, gz);
            if (xzMaxY.TryGetValue(key, out float y)) return y;
            if (ZoneSystem.instance == null) return 0f;
            float wx = gx * cell + cell * 0.5f;
            float wz = gz * cell + cell * 0.5f;
            return ZoneSystem.instance.GetGroundHeight(new Vector3(wx, 0f, wz));
        }

        private static bool WallBlocks(int gxA, int gzA, int gxB, int gzB,
            float yA, float yB, float cell, int mask)
        {
            // Cast at min(yA, yB) + 1m so the capsule sits INSIDE the wall
            // column when stepping between cells of different elevation
            // (e.g. ground → wall-top). The max-Y form let the flood walk
            // over walls because the cast was anchored above the wall.
            float castY = Mathf.Min(yA, yB) + CastHeightOffset;
            float half = cell * 0.5f;
            var a = new Vector3(gxA * cell + half, castY, gzA * cell + half);
            var b = new Vector3(gxB * cell + half, castY, gzB * cell + half);
            return Physics.CheckCapsule(a, b, CapsuleRadius, mask,
                QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Diagnostic mirror of <see cref="WallBlocks"/> that also returns
        /// the names of every collider the capsule hit (semicolon-joined,
        /// truncated). Uses <c>OverlapCapsule</c> so we can name the
        /// blockers — slightly slower than CheckCapsule, only called by
        /// vv_probe.
        /// </summary>
        internal static bool Diagnose(int gxA, int gzA, int gxB, int gzB,
            float yA, float yB, float cell, int mask, out string hitNames)
        {
            float castY = Mathf.Min(yA, yB) + CastHeightOffset;
            float half = cell * 0.5f;
            var a = new Vector3(gxA * cell + half, castY, gzA * cell + half);
            var b = new Vector3(gxB * cell + half, castY, gzB * cell + half);
            var hits = Physics.OverlapCapsule(a, b, CapsuleRadius, mask,
                QueryTriggerInteraction.Ignore);
            if (hits == null || hits.Length == 0)
            {
                hitNames = "";
                return false;
            }
            var names = new List<string>(hits.Length);
            for (int i = 0; i < hits.Length && i < 4; i++)
                names.Add(hits[i] != null ? hits[i].name : "<null>");
            hitNames = string.Join(";", names);
            if (hits.Length > 4) hitNames += $";+{hits.Length - 4}";
            return true;
        }

        // --- Encoding helpers ---

        // 2D XZ key — independent of RegionGraph's 3D lookup key. Symmetric
        // sign handling: arithmetic right shift preserves sign for the high
        // half; int cast on the low half truncates uint→int with wrap.
        private static long XzKey(int gx, int gz) =>
            ((long)(uint)gx << 32) | (uint)gz;

        private static void UnpackXz(long key, out int gx, out int gz)
        {
            gx = (int)(key >> 32);
            gz = (int)(key & 0xFFFFFFFFL);
        }

        // Inverse of RegionGraph.PackLookup. Uses symmetric mod centred on
        // zero so negative gx/gz/hb round-trip correctly.
        private static void UnpackLookup(long key, out int gx, out int gz, out int hb)
        {
            long h = key % PackK;
            if (h > PackKHalf) h -= PackK;
            else if (h < -PackKHalf) h += PackK;
            long r1 = (key - h) / PackK;

            long g = r1 % PackK;
            if (g > PackKHalf) g -= PackK;
            else if (g < -PackKHalf) g += PackK;
            long r2 = (r1 - g) / PackK;

            hb = (int)h;
            gz = (int)g;
            gx = (int)r2;
        }
    }
}
