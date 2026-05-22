using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.Handlers;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    /// "Rubber-band" prune: drops HNA regions whose footprint falls outside
    /// the outermost layer of player-placed wall pieces. Two-pass algorithm
    /// over a 4-connected XZ grid at <see cref="RegionGraph.LookupCellSize"/>
    /// resolution (height-bucket-flattened):
    /// <list type="number">
    /// <item>Outside-in flood seeded from every perimeter cell of the inflated
    /// bake bounds. Cell→cell traversal is gated by a symmetric
    /// <see cref="Physics.CheckCapsule"/> against the <c>piece</c> layer.</item>
    /// <item>Bed-anchored island cleanup BFS on the survivors using plain grid
    /// adjacency.</item>
    /// </list>
    /// Region-level apply: a region is dropped iff <em>all</em> of its
    /// LookupGrid entries land on outside ∪ island XZ cells. Mutates the
    /// passed-in combined collections in place, mirroring how
    /// <see cref="RegionPartitionHandler"/> handles its earlier cross-kind
    /// reachability prune.
    /// </summary>
    internal static class RubberBandPrune
    {
        public struct Stats
        {
            public int OutsideCells;
            public int IslandCells;
            public int RegionsDropped;
            public int PerimeterSeeds;
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
            List<Vector3> beds,
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
            // xzMaxY: max region-centroid Y per XZ cell (used as cast-height
            // anchor). ridToXz: reverse index region → its XZ cells (used by
            // the region-level apply at the end).
            var xzMaxY = new Dictionary<long, float>();
            var ridToXz = new Dictionary<string, List<long>>();
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
                if (!ridToXz.TryGetValue(kv.Value, out var list))
                {
                    list = new List<long>(4);
                    ridToXz[kv.Value] = list;
                }
                list.Add(xz);
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

            // --- Pass 2: bed-anchored island cleanup ---
            var insideCells = new HashSet<long>();
            queue.Clear();
            if (beds != null)
            {
                foreach (var bed in beds)
                {
                    int gx = Mathf.FloorToInt(bed.x / cell);
                    int gz = Mathf.FloorToInt(bed.z / cell);
                    long key = XzKey(gx, gz);
                    if (!xzMaxY.ContainsKey(key)) continue;
                    if (outsideCells.Contains(key)) continue;
                    if (insideCells.Add(key)) queue.Enqueue(key);
                }
            }
            while (queue.Count > 0)
            {
                long curKey = queue.Dequeue();
                UnpackXz(curKey, out int gx, out int gz);
                for (int d = 0; d < 4; d++)
                {
                    int ngx = gx + dx[d], ngz = gz + dz[d];
                    long nKey = XzKey(ngx, ngz);
                    if (!xzMaxY.ContainsKey(nKey)) continue;
                    if (outsideCells.Contains(nKey)) continue;
                    if (!insideCells.Add(nKey)) continue;
                    queue.Enqueue(nKey);
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

            // --- Region-level apply ---
            // A region is dropped iff every one of its XZ cells is in
            // outside ∪ island. A region with no recorded XZ cells (e.g. its
            // centroid lookup failed earlier) is also dropped as a no-op
            // safety. Survivors keep all their entries — we don't fragment.
            foreach (string rid in regionIds)
            {
                bool anyKept = false;
                if (ridToXz.TryGetValue(rid, out var cells))
                {
                    foreach (long xz in cells)
                    {
                        if (outsideCells.Contains(xz)) continue;
                        if (islandCells.Contains(xz)) continue;
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
                if (lookupGrid != null)
                {
                    var keysToDrop = new List<long>();
                    foreach (var kv in lookupGrid)
                        if (dropped.Contains(kv.Value)) keysToDrop.Add(kv.Key);
                    foreach (long k in keysToDrop) lookupGrid.Remove(k);
                }
                triangles?.RemoveAll(t => dropped.Contains(t.RegionId));
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
