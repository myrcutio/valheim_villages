using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.Handlers;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    /// "Rubber-band" prune: drops HNA cells and regions that fall outside the
    /// outermost layer of player-placed wall pieces. Two-pass flood over a
    /// 4-connected XZ grid at <see cref="RegionGraph.LookupCellSize"/>
    /// resolution (height-bucket flattened):
    /// <list type="number">
    /// <item>Outside-in flood seeded from every perimeter cell of the inflated
    /// bake bounds. Cell→cell traversal is gated by a symmetric
    /// <see cref="Physics.CheckCapsule"/> against the <c>piece</c> layer.</item>
    /// <item>Bed-anchored island cleanup BFS on the survivors using plain grid
    /// adjacency.</item>
    /// </list>
    /// Cell-level apply: outside ∪ island XZ cells are dropped individually
    /// from <c>LookupGrid</c> (all height buckets at that XZ) and from
    /// <c>CachedTriangles</c> (filtered by tri-centroid XZ). Regions that span
    /// a wall keep their inside half. A region is dropped wholesale (from
    /// <c>RegionIds</c>, <c>Centroids</c>, <c>kindMap</c>, <c>BoundaryCells</c>,
    /// <c>Links</c>) only when every one of its XZ cells got pruned.
    /// <c>BoundaryCells</c> entries for partial regions are left untouched —
    /// downstream consumers (e.g. <see cref="Behaviors.Patrol.BoundaryMapper"/>)
    /// accept slightly stale centroid markers for partial regions in v1.
    /// </summary>
    internal static class RubberBandPrune
    {
        public struct Stats
        {
            public int OutsideCells;
            public int IslandCells;
            public int RegionsDropped;
            public int PerimeterSeeds;
            public int LookupCellsDropped;
            public int TrianglesDropped;
            public int RegionsReached;
        }

        private const float CapsuleRadius = 0.35f;
        private const float CastHeightOffset = 1f;

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

            float cell = RegionGraph.LookupCellSize;
            int gxMin = Mathf.FloorToInt(minX / cell) - 1;
            int gzMin = Mathf.FloorToInt(minZ / cell) - 1;
            int gxMax = Mathf.FloorToInt(maxX / cell) + 1;
            int gzMax = Mathf.FloorToInt(maxZ / cell) + 1;

            // --- Flatten LookupGrid by XZ ---
            // xzMaxY: max region-centroid Y per XZ cell (cast-height anchor).
            // ridToXz: region → its XZ cells (used to detect fully-emptied
            //   regions for the region-level cascade).
            // xzToLookupKeys: XZ → list of LookupGrid keys at that XZ across
            //   all height buckets (used by the cell-level apply below to
            //   drop entries without rescanning the full lookupGrid).
            var xzMaxY = new Dictionary<long, float>();
            var ridToXz = new Dictionary<string, List<long>>();
            var xzToLookupKeys = new Dictionary<long, List<long>>();
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

            // --- Pass 1: outside-in flood ---
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
                float curY = GetCellY(gx, gz, xzMaxY, cell);

                for (int d = 0; d < 4; d++)
                {
                    int ngx = gx + dx[d], ngz = gz + dz[d];
                    if (ngx < gxMin || ngx > gxMax || ngz < gzMin || ngz > gzMax) continue;
                    long nKey = XzKey(ngx, ngz);
                    if (outsideCells.Contains(nKey)) continue;

                    float nY = GetCellY(ngx, ngz, xzMaxY, cell);
                    if (WallBlocks(gx, gz, ngx, ngz, curY, nY, cell, pieceMask)) continue;

                    outsideCells.Add(nKey);
                    queue.Enqueue(nKey);
                }
            }
            foreach (var kv in xzMaxY)
                if (outsideCells.Contains(kv.Key)) stats.OutsideCells++;

            // --- Pass 2: region-aware bed-flood ---
            // Walk BfsAdjacencyStore.Adjacency from BfsAdjacencyStore.Seeds
            // (populated upstream by RecordCrossKindAdjacency) to find the
            // bed-reachable region IDs. Expand to XZ cells via ridToXz.
            // Region-aware so the build-time terrain edge-midpoint capsule
            // break is respected — bed-flood can't cross from inside-terrain
            // to outside-terrain via raw XZ adjacency because the two halves
            // are different regions with no edge in combinedAdj.
            var insideCells = new HashSet<long>();
            var seeds = BfsAdjacencyStore.Seeds;
            var adjacency = BfsAdjacencyStore.Adjacency;
            if (seeds == null || seeds.Count == 0)
            {
                Plugin.Log?.LogWarning(
                    "[RubberBand] Pass 2 skipped: BfsAdjacencyStore.Seeds is empty " +
                    "(no terrain regions, or RecordCrossKindAdjacency wasn't called). " +
                    "All non-outside populated cells kept (no island marking).");
                foreach (var kv in xzMaxY)
                    if (!outsideCells.Contains(kv.Key)) insideCells.Add(kv.Key);
            }
            else
            {
                var reachedRegions = new HashSet<string>(seeds);
                var regionQueue = new Queue<string>(seeds);
                while (regionQueue.Count > 0)
                {
                    string cur = regionQueue.Dequeue();
                    if (!adjacency.TryGetValue(cur, out var neighbors)) continue;
                    foreach (var n in neighbors)
                        if (reachedRegions.Add(n)) regionQueue.Enqueue(n);
                }
                stats.RegionsReached = reachedRegions.Count;

                foreach (string rid in reachedRegions)
                {
                    if (!ridToXz.TryGetValue(rid, out var cells)) continue;
                    foreach (long xz in cells)
                    {
                        if (outsideCells.Contains(xz)) continue;
                        insideCells.Add(xz);
                    }
                }
            }

            var islandCells = new HashSet<long>();
            foreach (var kv in xzMaxY)
            {
                long k = kv.Key;
                if (outsideCells.Contains(k)) continue;
                if (insideCells.Contains(k)) continue;
                islandCells.Add(k);
            }
            stats.IslandCells = islandCells.Count;

            // --- Cell-level apply ---
            // Build the union of XZ cells to prune (populated outside cells +
            // all island cells). Drop matching LookupGrid entries via the
            // xzToLookupKeys reverse index, and drop matching triangles by
            // tri-centroid XZ. Surviving regions (those with at least one
            // un-pruned cell) keep their reduced footprint — handles the
            // "region spans a wall" case without fragmenting region identity.
            var prunedXz = new HashSet<long>(outsideCells.Count + islandCells.Count);
            foreach (long key in outsideCells)
                if (xzMaxY.ContainsKey(key)) prunedXz.Add(key);
            foreach (long key in islandCells) prunedXz.Add(key);

            if (prunedXz.Count > 0)
            {
                foreach (long xz in prunedXz)
                {
                    if (!xzToLookupKeys.TryGetValue(xz, out var keys)) continue;
                    foreach (long lookupKey in keys)
                    {
                        if (lookupGrid.Remove(lookupKey)) stats.LookupCellsDropped++;
                    }
                }

                if (triangles != null && triangles.Count > 0)
                {
                    int beforeCount = triangles.Count;
                    triangles.RemoveAll(t =>
                    {
                        float cx = (t.V0.x + t.V1.x + t.V2.x) / 3f;
                        float cz = (t.V0.z + t.V1.z + t.V2.z) / 3f;
                        int tgx = Mathf.FloorToInt(cx / cell);
                        int tgz = Mathf.FloorToInt(cz / cell);
                        return prunedXz.Contains(XzKey(tgx, tgz));
                    });
                    stats.TrianglesDropped = beforeCount - triangles.Count;
                }
            }

            // --- Region-level cascade ---
            // A region is dropped wholesale iff none of its XZ cells survived
            // the cell-level apply above. A region with no recorded XZ cells
            // (e.g. centroid lookup failed during flatten) is also dropped as
            // a no-op safety. LookupGrid + triangles for fully-dropped regions
            // were already removed by the cell-level pass; no duplicate sweep
            // needed here.
            foreach (string rid in regionIds)
            {
                bool anyKept = false;
                if (ridToXz.TryGetValue(rid, out var cells))
                {
                    foreach (long xz in cells)
                    {
                        if (prunedXz.Contains(xz)) continue;
                        anyKept = true;
                        break;
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
            return stats;
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
