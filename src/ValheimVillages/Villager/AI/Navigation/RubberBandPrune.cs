using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Pathfinding;

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
    ///             <b>Pass 2</b> (terrain): inside-out flood seeded from each bed,
    ///             snapped to the nearest populated cell (the floor the villager
    ///             stands on, not the terrain hole a metre below a raised
    ///             foundation). Flood surface Y is the region-centroid surface
    ///             (max of terrain/piece) so the waist probe sits above the floor
    ///             and bed; <c>WallBlocks</c> ignores bed colliders; refuses to
    ///             expand into <c>outsideCells</c>.
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
            out HashSet<string> droppedRegionIds,
            out List<(string fromRid, string toRid,
                      Vector3 startPos, Vector3 endPos)> pass3DiscoveredEdgeList,
            out HashSet<long> bedReachableCellsOut,
            out HashSet<long> outsideCellsOut,
            out HashSet<long> prunedPieceKeysOut,
            out List<Vector3> gateMarkersOut)
        {
            var dropped = new HashSet<string>();
            droppedRegionIds = dropped;
            // Pre-assign the out so all early-exit paths satisfy the
            // definite-assignment rule. Pass 3 will overwrite later if it runs.
            pass3DiscoveredEdgeList = new List<(string fromRid, string toRid,
                                                Vector3 startPos, Vector3 endPos)>();
            bedReachableCellsOut = new HashSet<long>();
            outsideCellsOut = new HashSet<long>();
            prunedPieceKeysOut = new HashSet<long>();
            gateMarkersOut = new List<Vector3>();
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

            // Gather door/gate seals so the Pass 1 outside flood treats an
            // OPEN gate as a sealed boundary. WallBlocks probes physical piece
            // colliders at waist height, but an open gate's leaf has swung out
            // of the doorway, so the flood would pour through the gap — the
            // whole village then floods "outside" and the region graph
            // collapses (3 regions -> 1, village deregistered). A Door's
            // transform.position/forward stay fixed at the closed reference
            // frame regardless of open state, so we seal the doorway plane
            // geometrically. Published to the caller for the village map.
            var gateSeals = GatherGateSeals(minX, minZ, maxX, maxZ);
            foreach (var g in gateSeals) gateMarkersOut.Add(g.Mid);

            // --- Pass 1: outside-in terrain flood ---
            // Floods inward from every perimeter cell on the terrain layer.
            // Pieces don't participate as cells or stepping stones — a wall
            // piece is never a bridge between outside and inside terrain.
            // outsideCells contains every cell (populated or not) the flood
            // could reach without tripping WallBlocks on the piece layer.
            // Pass 1 flood now lives in the pure PerimeterOutsideFlood helper;
            // here we inject terrain cell heights and the piece-layer wall gate
            // (augmented with the geometric gate seal above).
            var outsideCells = PerimeterOutsideFlood(gxMin, gzMin, gxMax, gzMax,
                (gx, gz) => GetCellY(gx, gz, xzMaxYTerrain, cell),
                (ax, az, bx, bz, ya, yb) =>
                    WallBlocks(ax, az, bx, bz, ya, yb, cell, pieceMask)
                    || GateBlocksStep(ax, az, bx, bz, cell, gateSeals),
                out var perimeterSeeds);
            stats.PerimeterSeeds = perimeterSeeds;

            // dx/dz are reused by Passes 2 and 3 below.
            int[] dx = { 1, -1, 0, 0 };
            int[] dz = { 0, 0, 1, -1 };

            foreach (var kv in xzMaxYTerrain)
                if (outsideCells.Contains(kv.Key))
                    stats.OutsideTerrainCells++;

            // --- Pass 2: inside-out flood from beds ---
            // Walks any reachable cell in the bake bounds. A villager can
            // legitimately walk on any non-outside cell that isn't
            // wall-blocked from its neighbour, regardless of whether a terrain
            // region centroid landed on this XZ.
            //
            // Flood surface (cell Y): the partition's own region surface — the
            // max region-centroid Y across the terrain AND piece regions at the
            // cell (SurfaceY below). In a floored village the walkable surface
            // is the floor PIECES (≈1m above the terrain heightmap), so the old
            // GetGroundHeight anchor put the waist probe a metre too low, where
            // it clipped the floor pieces (and the bed) and walled every cell
            // off from its neighbour. Anchoring on the region surface puts the
            // probe band above the floor/bed and below walls — exactly the
            // height Pass 1's diagnostic uses to report WallBlocks=false across
            // a floor. GetGroundHeight is the fallback only for cells with no
            // region at all (unpopulated).
            //
            // Bed seeding (PRIMARY = snap to the nearest populated cell): a bed
            // commonly sits on a floor whose own XZ cell is carved out of the
            // partition (the bed blocker shadows it), leaving it unpopulated.
            // Seeding the literal bed cell put the flood on the terrain hole
            // under the bed. We snap each bed to the nearest cell that actually
            // carries a region — the floor the villager stands on. This is the
            // main position marker, not a fallback.
            //
            // bedReachableCellY records each reached cell's surface Y so Pass 3
            // seeds the piece flood at the same height (not the terrain a metre
            // below, which would exceed MaxClimb and orphan the floor pieces).
            var bedReachableCells = new HashSet<long>();
            var bedReachableCellY = new Dictionary<long, float>();
            var pass2Seeds = 0;

            // Region surface Y at a cell: the highest region-centroid Y present
            // there across both kinds (floor pieces win over the terrain they
            // shadow). Falls back to the terrain heightmap for cells with no
            // region. `populated` reports whether any region covers the cell.
            float SurfaceY(long xzKey, int gx, int gz, out bool populated)
            {
                var best = float.MinValue;
                if (xzMaxYTerrain.TryGetValue(xzKey, out var ty)) best = ty;
                if (xzMaxYPiece.TryGetValue(xzKey, out var py) && py > best) best = py;
                populated = best > float.MinValue;
                if (populated) return best;
                return ZoneSystem.instance != null
                    ? ZoneSystem.instance.GetGroundHeight(
                        new Vector3(gx * cell + cell * 0.5f, 0f, gz * cell + cell * 0.5f))
                    : 0f;
            }

            if (beds != null && beds.Count > 0 && ZoneSystem.instance != null)
            {
                // Pass 2 flood now lives in the pure BedReachableFlood helper.
                // isPopulated keeps the bed-snap on the ContainsKey fast path (no
                // ground cast); surfaceY supplies the walk-surface height.
                BedReachableFlood(
                    gxMin, gzMin, gxMax, gzMax, beds, outsideCells, cell, bedSnapRingMax: 6,
                    isPopulated: ck => xzMaxYTerrain.ContainsKey(ck) || xzMaxYPiece.ContainsKey(ck),
                    surfaceY: (xzKey, gx, gz) => SurfaceY(xzKey, gx, gz, out _),
                    wallBlocks: (ax, az, bx, bz, ya, yb) => WallBlocks(ax, az, bx, bz, ya, yb, cell, pieceMask),
                    out bedReachableCells, out bedReachableCellY, out pass2Seeds,
                    warn: msg => Plugin.Log?.LogWarning(msg));
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
            // Discovered piece-step adjacency: edges proven walkable by the
            // cell-level flood (piece A's cell → piece B's neighbour cell at
            // compatible Y). These become formal RegionLinks below.
            //
            // Each pair carries the cell-boundary positions for the link
            // endpoints — NOT the region centroids. Centroid-anchored links
            // tell the path planner "shortcut from A's centre to B's centre",
            // which makes the agent walk through both regions to use the
            // link; boundary-anchored links are a short ~1m hop between
            // adjacent cells exactly where the walker transitions.
            var pass3DiscoveredEdges = new HashSet<string>();
            var pass3EdgePairs = new List<(string fromRid, string toRid,
                                           Vector3 startPos, Vector3 endPos)>();

            string CanonicalPair(string a, string b) =>
                string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;

            void RecordPass3Edge(string fromRid, string toRid,
                                 Vector3 startPos, Vector3 endPos)
            {
                if (string.IsNullOrEmpty(fromRid) || string.IsNullOrEmpty(toRid)) return;
                if (fromRid == toRid) return;
                var key = CanonicalPair(fromRid, toRid);
                if (pass3DiscoveredEdges.Add(key))
                    pass3EdgePairs.Add((fromRid, toRid, startPos, endPos));
            }

            // Helper: terrain region at a given cell XZ (any height bucket).
            // Returns the first lookup-key-resolved rid — sufficient for
            // recording adjacency since multiple terrain regions at the same
            // cell are coplanar-merged upstream.
            string TerrainRegionAtCell(long xz)
            {
                if (!xzToLookupKeysTerrain.TryGetValue(xz, out var keys)) return null;
                foreach (var lookupKey in keys)
                    if (lookupGrid.TryGetValue(lookupKey, out var trid)) return trid;
                return null;
            }

            // Helper: any piece at a given cell XZ already in
            // pieceReachableKeys whose centroid Y is within MaxClimb of
            // targetY. Used as a Pass 3 piece-step source-region fallback
            // when the current visit is a terrain visit at a cell that
            // has no terrain region (Pass 2 walks any non-outside cell —
            // floor pieces commonly shadow terrain so a bed-reachable
            // cell can have only piece geometry). Picks the closest-in-Y
            // candidate so the recorded edge represents the most natural
            // walker step (smallest Δy).
            string PieceAtCellInReachable(long xz, float targetY)
            {
                if (!xzToLookupKeysPiece.TryGetValue(xz, out var keys)) return null;
                string bestRid = null;
                var bestDeltaY = float.MaxValue;
                foreach (var lookupKey in keys)
                {
                    if (!pieceReachableKeys.Contains(lookupKey)) continue;
                    if (!lookupGrid.TryGetValue(lookupKey, out var prid)) continue;
                    if (!centroids.TryGetValue(prid, out var pc)) continue;
                    var delta = Mathf.Abs(pc.y - targetY);
                    if (delta > MaxClimb) continue;
                    if (delta < bestDeltaY)
                    {
                        bestDeltaY = delta;
                        bestRid = prid;
                    }
                }
                return bestRid;
            }

            // Helper: compute boundary-anchored link endpoints for a Pass 3
            // piece-step from cell `fromXz` to cell `toXz`. The endpoints sit
            // on opposite sides of the shared edge at the from/to surface Y,
            // separated by a small horizontal epsilon so SamplePosition
            // snaps each one to the correct cell's NavMesh tile.
            //
            // sameCell case: when `lastAddedAtNeighbour` resolved the source,
            // both pieces are at the SAME cell (sibling pieces stacked at
            // different Y). There's no horizontal boundary to anchor at, so
            // the link is a purely vertical hop at the cell centre.
            void ComputePieceStepLinkPositions(long fromXz, long toXz,
                float fromY, float toY, float cellSize, bool sameCell,
                out Vector3 startPos, out Vector3 endPos)
            {
                UnpackXz(fromXz, out var fGx, out var fGz);
                UnpackXz(toXz, out var tGx, out var tGz);
                var half = cellSize * 0.5f;
                var fCenterX = fGx * cellSize + half;
                var fCenterZ = fGz * cellSize + half;
                var tCenterX = tGx * cellSize + half;
                var tCenterZ = tGz * cellSize + half;
                if (sameCell)
                {
                    // Pure vertical hop — both endpoints at cell center,
                    // different Y. SamplePosition will snap each to its
                    // own NavMesh patch (different height bucket).
                    startPos = new Vector3(fCenterX, fromY, fCenterZ);
                    endPos = new Vector3(fCenterX, toY, fCenterZ);
                }
                else
                {
                    // Boundary midpoint between the two cells, with small
                    // epsilon offset on each side so each endpoint sits
                    // unambiguously on its own cell's tile.
                    const float epsilon = 0.1f;
                    var boundaryX = (fCenterX + tCenterX) * 0.5f;
                    var boundaryZ = (fCenterZ + tCenterZ) * 0.5f;
                    var dirX = Mathf.Sign(tCenterX - fCenterX);
                    var dirZ = Mathf.Sign(tCenterZ - fCenterZ);
                    startPos = new Vector3(boundaryX - dirX * epsilon, fromY,
                                           boundaryZ - dirZ * epsilon);
                    endPos = new Vector3(boundaryX + dirX * epsilon, toY,
                                           boundaryZ + dirZ * epsilon);
                }
            }

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
                //
                // sourceRid threads the most recently added piece's region id
                // through piece-step enqueues so the next piece-step can
                // record (sourceRid, newRid) as a discovered adjacency.
                // Terrain visits carry null sourceRid — when piece-step fires
                // from a terrain visit, we look up the terrain region at curXz
                // and record (terrainRid, newRid) as a piece↔terrain edge.
                var pieceQueue = new Queue<(long xz, float y, bool isTerrain, string sourceRid)>();
                foreach (var xz in bedReachableCells)
                {
                    UnpackXz(xz, out var gx, out var gz);
                    var wx = gx * cell + half;
                    var wz = gz * cell + half;
                    // Seed at the surface Pass 2 actually walked (floor pieces
                    // in a floored village), not the terrain a metre below —
                    // otherwise the floor pieces sit > MaxClimb above the seed
                    // and Pass 3 never reaches them.
                    var ty = bedReachableCellY.TryGetValue(xz, out var sy)
                        ? sy
                        : ZoneSystem.instance.GetGroundHeight(new Vector3(wx, 0f, wz));
                    pieceQueue.Enqueue((xz, ty, true, null));
                }

                while (pieceQueue.Count > 0)
                {
                    var (curXz, curY, isTerrain, sourceRid) = pieceQueue.Dequeue();
                    if (isTerrain && !visitedTerrainXz.Add(curXz)) continue;
                    UnpackXz(curXz, out var gx, out var gz);
                    for (var d = 0; d < 4; d++)
                    {
                        int ngx = gx + dx[d], ngz = gz + dz[d];
                        var nXz = XzKey(ngx, ngz);
                        // (a) Terrain bridge: enqueue as a terrain visit at the
                        //     neighbour's ground height if it's bed-reachable and
                        //     the climb from curY fits within MaxClimb. Terrain
                        //     bridges propagate sourceRid=null (no piece adjacency
                        //     created by terrain-walk bridges; they're pure
                        //     connectivity).
                        if (bedReachableCells.Contains(nXz) && !visitedTerrainXz.Contains(nXz))
                        {
                            var nwx = ngx * cell + half;
                            var nwz = ngz * cell + half;
                            // Bridge at Pass 2's recorded surface Y (same
                            // reason as the seed loop above).
                            var tnY = bedReachableCellY.TryGetValue(nXz, out var nsy)
                                ? nsy
                                : ZoneSystem.instance.GetGroundHeight(new Vector3(nwx, 0f, nwz));
                            if (Mathf.Abs(tnY - curY) <= MaxClimb) pieceQueue.Enqueue((nXz, tnY, true, null));
                        }

                        // (b) Piece step: walk every piece at neighbour XZ within
                        //     climb of curY. Dedup via pieceReachableKeys.
                        if (xzToLookupKeysPiece.TryGetValue(nXz, out var nPieceKeys))
                        {
                            // Tracks the most recently added piece in this
                            // foreach iteration so sibling pieces at the
                            // same nXz can chain to each other. Without
                            // this, two pieces stacked at the same cell
                            // (e.g. a small stair-tread piece next to its
                            // landing piece, both at gx=-2268,gz=1299) end
                            // up isolated when added from a no-terrain
                            // curXz visit — see the p509 hex-fort case.
                            string lastAddedAtNeighbour = null;
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
                                pieceQueue.Enqueue((nXz, c.y, false, rid));
                                pass3Seeds++;

                                // Resolve fromRid via a fallback chain so
                                // first-piece-at-cell cases aren't orphaned:
                                //   1. sourceRid (came from a piece visit).
                                //   2. Terrain region at curXz (came from a
                                //      terrain visit with a terrain region
                                //      present at this cell).
                                //   3. Any piece at curXz already in
                                //      pieceReachableKeys within MaxClimb
                                //      of curY (we're at a no-terrain cell
                                //      but standing on a floor piece).
                                //   4. The previous piece added in THIS
                                //      foreach iteration — sibling pieces
                                //      at the same nXz chain to each other
                                //      even when their cell has no terrain
                                //      and no other prior reachable piece.
                                var fromRid = sourceRid
                                              ?? TerrainRegionAtCell(curXz)
                                              ?? PieceAtCellInReachable(curXz, curY)
                                              ?? lastAddedAtNeighbour;
                                // Compute link endpoints AT THE CELL
                                // BOUNDARY, not the region centroids.
                                // Centroid-anchored links make the path
                                // planner route the agent through both
                                // regions to use the link; boundary-anchored
                                // links are a short hop exactly where the
                                // walker transitions cells.
                                ComputePieceStepLinkPositions(
                                    curXz, nXz, curY, c.y, cell,
                                    fromRid == lastAddedAtNeighbour,
                                    out var startPos, out var endPos);
                                RecordPass3Edge(fromRid, rid, startPos, endPos);
                                lastAddedAtNeighbour = rid;
                            }
                        }
                    }
                }
            }

            stats.Pass3Seeds = pass3Seeds;
            stats.BedReachablePieceKeys = pieceReachableKeys.Count;
            pass3DiscoveredEdgeList = pass3EdgePairs;

            // Prune complement on the piece layer: every piece lookup key we
            // indexed above that Pass 3 did NOT reach. These are the piece
            // cells HNA rejected — the second NavMesh bake will phantom-block
            // them with ModifierBox volumes so the agent NavMesh and HNA
            // graph agree on the piece-layer walkable set.
            var prunedPieceKeys = new HashSet<long>();
            foreach (var kv in xzToLookupKeysPiece)
            {
                foreach (var pKey in kv.Value)
                {
                    if (!pieceReachableKeys.Contains(pKey))
                        prunedPieceKeys.Add(pKey);
                }
            }
            prunedPieceKeysOut = prunedPieceKeys;

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

            // --- Apply Pass 3 discovered adjacency as formal RegionLinks ---
            // The cell-level piece flood above is the ground truth for
            // "these two regions are walkable-adjacent" — it actually
            // traversed the chain. Build RegionLinks from those discovered
            // edges so the formal RegionGraph reflects the same connectivity
            // Pass 3 used to populate pieceReachableKeys.
            //
            // Runs AFTER the region cascade so endpoints are guaranteed to
            // be present in the surviving graph (any pair where one side
            // got dropped is filtered out). Dedup against existing links
            // (the RegionBuilder edge-based pass may have already added
            // some piece-piece edges and the cross-kind paths may have
            // added piece-terrain edges).
            if (links != null && pass3EdgePairs.Count > 0)
            {
                var existingLinkPairs = new HashSet<string>(links.Count);
                foreach (var link in links)
                    existingLinkPairs.Add(CanonicalPair(link.FromRegionId, link.ToRegionId));

                var pass3LinksAdded = 0;
                foreach (var (fromRid, toRid, startPos, endPos) in pass3EdgePairs)
                {
                    if (dropped.Contains(fromRid) || dropped.Contains(toRid)) continue;
                    var key = CanonicalPair(fromRid, toRid);
                    if (!existingLinkPairs.Add(key)) continue;
                    if (centroids == null) continue;
                    if (!centroids.ContainsKey(fromRid)) continue;
                    if (!centroids.ContainsKey(toRid)) continue;
                    // Anchor the link at the cell boundary positions
                    // captured during the BFS step — NOT centroids.
                    // Centroid-anchored links make the path planner route
                    // through both regions; boundary-anchored is a short
                    // hop exactly where the walker transitions cells.
                    links.Add(new RegionLink
                    {
                        FromRegionId = fromRid,
                        ToRegionId = toRid,
                        LinkType = RegionLinkType.Slope,
                        PositionStart = startPos,
                        PositionEnd = endPos,
                    });
                    pass3LinksAdded++;
                }
                stats.Pass3LinksAdded = pass3LinksAdded;
            }

            // --- Pass 5: consolidate linear piece chains ---
            // Detect maximal linear sequences of piece regions where each
            // intermediate node has degree 2 in the piece-only adjacency
            // (formal RegionLinks), and the chain's centroids are roughly
            // collinear. Each valid chain collapses into a single anchor
            // region: chain members' triangles, lookup keys, and external
            // links all reroute to the anchor; intra-chain links drop.
            //
            // The cached triangles are PRESERVED (each tri's RegionId is
            // reassigned to the anchor) so vv_tri_debug still shows the
            // original step geometry, just coloured as one region. The
            // path-planner-visible structure shrinks dramatically: a
            // 7-step staircase becomes 1 region + 2 external links
            // instead of 7 regions + 6 intra-chain links + N external.
            //
            // Runs BEFORE Pass 4 (border snap) so Pass 4 processes the
            // merged region's outer boundary only — intra-chain vertices
            // are now internal and don't need snapping.
            ConsolidateLinearChains(regionIds, centroids, lookupGrid,
                boundaryCells, links, kindMap, triangles, pass3EdgePairs,
                ref stats);

            // --- Pass 4: snap border geometry to the agent NavMesh ---
            SnapBordersToAgentNavMesh(regionIds, links, triangles, ref stats);

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

            // --- Boundary cells from the perimeter-flood frontier ---
            // The region-edge boundary cells produced upstream (DetectBoundary)
            // include EVERY interior obstacle's perimeter — buildings, internal
            // walls, props — roughly half of which sit deep inside the village and
            // make any patrol-loop ordering weave between the outer wall and the
            // interior rings. The outside-in flood already separates the two: an
            // interior obstacle is enclosed by walkable cells, so it is never
            // 4-adjacent to outsideCells. Rebuild boundaryCells as the OUTER
            // frontier — bed-reachable cells with at least one 4-neighbour in
            // outsideCells — with the outward normal pointing toward the outside
            // neighbour(s). That is the clean, outer-only ring the patrol route
            // builder orders. Falls back to the upstream cells if the frontier is
            // degenerate (e.g. no beds, so no bed-reachable flood).
            if (bedReachableCells.Count > 0)
            {
                // March outward up to MaxWallSpan cells per direction. The wall
                // occupies cells that are neither walkable (bed-reachable) nor
                // outside, so inside and outside aren't directly 4-adjacent — we
                // step ACROSS the wall to find the outside. Hitting another
                // walkable cell first means walkable area continues that way (an
                // interior obstacle edge, not the outer wall), so that direction
                // is rejected. Only directions that reach outsideCells across the
                // wall contribute to the outward normal.
                const int MaxWallSpan = 4;
                int[] fdx = { 1, -1, 0, 0 };
                int[] fdz = { 0, 0, 1, -1 };
                var frontier = new List<(string id, Vector3 center, Vector3 outwardDir)>();
                for (var gx = gxMin; gx <= gxMax; gx++)
                for (var gz = gzMin; gz <= gzMax; gz++)
                {
                    var key = XzKey(gx, gz);
                    if (!bedReachableCells.Contains(key)) continue;

                    var outward = Vector3.zero;
                    for (var i = 0; i < 4; i++)
                        for (var step = 1; step <= MaxWallSpan; step++)
                        {
                            var nk = XzKey(gx + fdx[i] * step, gz + fdz[i] * step);
                            if (bedReachableCells.Contains(nk)) break; // walkable beyond → interior
                            if (outsideCells.Contains(nk))
                            {
                                outward += new Vector3(fdx[i], 0f, fdz[i]);
                                break;
                            }
                            // else wall/blocked cell — keep marching across it
                        }

                    if (outward.sqrMagnitude < 1e-6f) continue; // interior cell

                    var y = SurfaceY(key, gx, gz, out _);
                    var center = new Vector3(gx * cell + cell * 0.5f, y, gz * cell + cell * 0.5f);
                    var hb = RegionGraph.HeightBucket(y);
                    var rid = lookupGrid.TryGetValue(RegionGraph.PackLookup(gx, gz, hb), out var r)
                        ? r
                        : "frontier";
                    frontier.Add((rid, center, outward.normalized));
                }

                if (frontier.Count >= 3)
                {
                    boundaryCells.Clear();
                    boundaryCells.AddRange(frontier);
                    Plugin.Log?.LogInfo(
                        $"[RubberBand] Boundary cells from flood frontier: {frontier.Count} outer cells");
                }
                else
                {
                    Plugin.Log?.LogWarning(
                        $"[RubberBand] Flood frontier degenerate ({frontier.Count} cells); " +
                        "keeping upstream region-edge boundary cells");
                }
            }

            // Publish authoritative HNA reachability sets to the caller so
            // the second NavMesh bake can carve cells the prune dropped but
            // the first bake's voxelizer left walkable. bedReachableCells is
            // the post-Pass-2 keep set; outsideCells is the Pass-1 perimeter
            // flood result. Both are at LookupCellSize XZ resolution.
            bedReachableCellsOut = new HashSet<long>(bedReachableCells);
            outsideCellsOut = new HashSet<long>(outsideCells);

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

        /// <summary>
        ///     Pure Pass-1 perimeter ("outside-in") flood. 4-connected BFS seeded
        ///     from every cell on the border of [gxMin..gxMax] x [gzMin..gzMax].
        ///     A cell→cell step is severed when <paramref name="wallBlocks" />
        ///     returns true; per-cell heights come from <paramref name="cellY" />.
        ///     Engine-free — callers inject the ground/wall providers (live code
        ///     wires them to ZoneSystem/Physics, tests pass synthetic grids).
        ///     Returns every cell reachable from the perimeter without crossing a
        ///     wall — i.e. everything outside the outermost wall ring.
        ///     <paramref name="perimeterSeedCount" /> reports the seed count
        ///     (set before the BFS expands) for the diagnostic stats.
        /// </summary>
        internal static HashSet<long> PerimeterOutsideFlood(
            int gxMin, int gzMin, int gxMax, int gzMax,
            Func<int, int, float> cellY,
            Func<int, int, int, int, float, float, bool> wallBlocks,
            out int perimeterSeedCount)
        {
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

            perimeterSeedCount = outsideCells.Count;

            int[] dx = { 1, -1, 0, 0 };
            int[] dz = { 0, 0, 1, -1 };
            while (queue.Count > 0)
            {
                var curKey = queue.Dequeue();
                UnpackXz(curKey, out var gx, out var gz);
                var curY = cellY(gx, gz);
                for (var d = 0; d < 4; d++)
                {
                    int ngx = gx + dx[d], ngz = gz + dz[d];
                    if (ngx < gxMin || ngx > gxMax || ngz < gzMin || ngz > gzMax) continue;
                    var nKey = XzKey(ngx, ngz);
                    if (outsideCells.Contains(nKey)) continue;
                    var nY = cellY(ngx, ngz);
                    if (wallBlocks(gx, gz, ngx, ngz, curY, nY)) continue;
                    outsideCells.Add(nKey);
                    queue.Enqueue(nKey);
                }
            }

            return outsideCells;
        }

        /// <summary>
        ///     Pure Pass-2 bed-reachable ("inside-out") flood. For each bed, snaps
        ///     to the nearest populated, non-outside cell within
        ///     <paramref name="bedSnapRingMax" /> rings (the floor the villager
        ///     stands on, even when the bed's own cell is carved out), then runs a
        ///     4-connected BFS that refuses to enter <paramref name="outsideCells" />
        ///     and is gated by <paramref name="wallBlocks" />. Engine-free:
        ///     <paramref name="isPopulated" /> answers "does any region cover this
        ///     cell" (the bed-snap fast path) and <paramref name="surfaceY" /> gives
        ///     each cell's walk-surface height. Beds that cannot snap are reported
        ///     via <paramref name="warn" /> and skipped.
        /// </summary>
        internal static void BedReachableFlood(
            int gxMin, int gzMin, int gxMax, int gzMax,
            IReadOnlyList<Vector3> beds,
            HashSet<long> outsideCells,
            float cell,
            int bedSnapRingMax,
            Func<long, bool> isPopulated,
            Func<long, int, int, float> surfaceY,
            Func<int, int, int, int, float, float, bool> wallBlocks,
            out HashSet<long> bedReachableCells,
            out Dictionary<long, float> bedReachableCellY,
            out int seedCount,
            Action<string> warn = null)
        {
            bedReachableCells = new HashSet<long>();
            bedReachableCellY = new Dictionary<long, float>();
            seedCount = 0;
            if (beds == null || beds.Count == 0) return;

            var bedQueue = new Queue<(long key, float y)>();
            foreach (var bed in beds)
            {
                var bgx0 = Mathf.FloorToInt(bed.x / cell);
                var bgz0 = Mathf.FloorToInt(bed.z / cell);

                // Snap to the nearest populated, non-outside cell (the floor the
                // bed rests on, even when the bed's own cell is carved).
                int bgx = bgx0, bgz = bgz0;
                var snapped = false;
                for (var r = 0; r <= bedSnapRingMax && !snapped; r++)
                for (var ox = -r; ox <= r && !snapped; ox++)
                for (var oz = -r; oz <= r && !snapped; oz++)
                {
                    if (Mathf.Max(Mathf.Abs(ox), Mathf.Abs(oz)) != r) continue; // ring only
                    var cx = bgx0 + ox;
                    var cz = bgz0 + oz;
                    if (cx < gxMin || cx > gxMax || cz < gzMin || cz > gzMax) continue;
                    var ck = XzKey(cx, cz);
                    if (outsideCells.Contains(ck)) continue;
                    if (!isPopulated(ck)) continue;
                    bgx = cx;
                    bgz = cz;
                    snapped = true;
                }

                if (!snapped)
                {
                    warn?.Invoke(
                        $"[RubberBand] Pass 2 bed skipped: bed=({bed.x:F1},{bed.z:F1}) " +
                        $"cell=({bgx0},{bgz0}) — no populated, non-outside cell within " +
                        $"{bedSnapRingMax} cells (bed walled off or outside the village?).");
                    continue;
                }

                var bedKey = XzKey(bgx, bgz);
                if (bedReachableCells.Add(bedKey))
                {
                    var seedY = surfaceY(bedKey, bgx, bgz);
                    bedReachableCellY[bedKey] = seedY;
                    bedQueue.Enqueue((bedKey, seedY));
                    seedCount++;
                }
            }

            int[] dx = { 1, -1, 0, 0 };
            int[] dz = { 0, 0, 1, -1 };
            while (bedQueue.Count > 0)
            {
                var (curKey, curY) = bedQueue.Dequeue();
                UnpackXz(curKey, out var gx, out var gz);
                for (var d = 0; d < 4; d++)
                {
                    int ngx = gx + dx[d], ngz = gz + dz[d];
                    if (ngx < gxMin || ngx > gxMax || ngz < gzMin || ngz > gzMax) continue;
                    var nKey = XzKey(ngx, ngz);
                    if (bedReachableCells.Contains(nKey)) continue;
                    if (outsideCells.Contains(nKey)) continue;
                    var nY = surfaceY(nKey, ngx, ngz);
                    if (wallBlocks(gx, gz, ngx, ngz, curY, nY)) continue;
                    bedReachableCells.Add(nKey);
                    bedReachableCellY[nKey] = nY;
                    bedQueue.Enqueue((nKey, nY));
                }
            }
        }

        /// <summary>
        ///     Compute the outside-cell set for a bake using only
        ///     <c>ZoneSystem.GetGroundHeight</c> for cell Y values.
        ///     Runs the same perimeter flood as Pass 1 but doesn't require
        ///     baked NavMesh data or RegionBuilder-built terrain centroids,
        ///     so it can run BEFORE <see cref="NavMeshBakeManager.BakeVillage" />.
        ///
        ///     Used by the bake to inject phantom <c>NotWalkable</c> box
        ///     sources covering outside cells, carving them out of the
        ///     agent NavMesh up front instead of leaving the bake to
        ///     produce walkable surface there.
        /// </summary>
        public static HashSet<long> ComputeOutsideCellsForBake(Bounds bounds)
        {
            var pieceMask = LayerMask.GetMask("piece");
            if (pieceMask == 0) return new HashSet<long>();
            if (ZoneSystem.instance == null) return new HashSet<long>();

            var cell = RegionGraph.LookupCellSize;
            var gxMin = Mathf.FloorToInt(bounds.min.x / cell) - 1;
            var gzMin = Mathf.FloorToInt(bounds.min.z / cell) - 1;
            var gxMax = Mathf.FloorToInt(bounds.max.x / cell) + 1;
            var gzMax = Mathf.FloorToInt(bounds.max.z / cell) + 1;
            var half = cell * 0.5f;

            // Same perimeter flood as Apply's Pass 1, but cell heights come from
            // the ZoneSystem heightmap (no baked centroids exist yet at bake
            // time). Seal gates the same way so the bake's outside-cell phantom
            // blockers and the HNA partition agree on the village hull.
            var gateSeals = GatherGateSeals(
                bounds.min.x, bounds.min.z, bounds.max.x, bounds.max.z);
            return PerimeterOutsideFlood(gxMin, gzMin, gxMax, gzMax,
                (gx, gz) => ZoneSystem.instance.GetGroundHeight(
                    new Vector3(gx * cell + half, 0f, gz * cell + half)),
                (ax, az, bx, bz, ya, yb) =>
                    WallBlocks(ax, az, bx, bz, ya, yb, cell, pieceMask)
                    || GateBlocksStep(ax, az, bx, bz, cell, gateSeals),
                out _);
        }

        /// <summary>
        ///     Diagnostic helper: unpack an outside-cell key into its grid
        ///     coordinates. Used by <see cref="NavMeshBakeManager" /> when
        ///     synthesizing phantom NotWalkable box sources.
        /// </summary>
        public static void UnpackOutsideCellKey(long key, out int gx, out int gz)
        {
            UnpackXz(key, out gx, out gz);
        }

        /// <summary>
        ///     Test whether a world-space position falls in an outside cell. Packs the position
        ///     to an XZ key at <see cref="RegionGraph.LookupCellSize" /> resolution and checks
        ///     set membership. Used by callers (e.g. VillageStationRegistry) that want to know
        ///     "is this position inside the village's outer hull, even if it's on top of a
        ///     non-walkable obstacle like a smelter."
        /// </summary>
        public static bool IsOutsideCell(Vector3 worldPos, HashSet<long> outsideCells)
        {
            if (outsideCells == null || outsideCells.Count == 0) return false;
            var cell = RegionGraph.LookupCellSize;
            var gx = Mathf.FloorToInt(worldPos.x / cell);
            var gz = Mathf.FloorToInt(worldPos.z / cell);
            return outsideCells.Contains(XzKey(gx, gz));
        }

        /// <summary>
        ///     Public XZ-key packer matching the encoding used by
        ///     <c>outsideCells</c>, <c>bedReachableCells</c>, and the diagnostic
        ///     snapshot. External callers that need to build sets in the same
        ///     coordinate space (e.g. the second-bake prune-complement) use this.
        /// </summary>
        public static long PackXzKey(int gx, int gz)
        {
            return XzKey(gx, gz);
        }

        /// <summary>
        ///     Inclusive grid-coordinate rectangle. Used by
        ///     <see cref="DecomposeToRectangles" /> to describe a contiguous block
        ///     of XZ cells the caller can collapse into a single ModifierBox.
        /// </summary>
        public readonly struct CellRect
        {
            public readonly int Gx0, Gz0, Gx1, Gz1;
            public CellRect(int gx0, int gz0, int gx1, int gz1)
            {
                Gx0 = gx0;
                Gz0 = gz0;
                Gx1 = gx1;
                Gz1 = gz1;
            }
        }

        /// <summary>
        ///     Greedy-rectangle decomposition of a packed XZ cell set. For each
        ///     remaining cell: extend the rectangle horizontally as far as the
        ///     row stays "on", then extend vertically as far as columns stay
        ///     "on" for that x-range. Mark covered cells consumed and repeat.
        ///     O(n) per cell on average. Typically reduces ~3000 cells to tens
        ///     of rectangles for a village, cutting the bake's source-count by
        ///     ~50x and the voxelizer's per-source overhead with it.
        /// </summary>
        public static List<CellRect> DecomposeToRectangles(HashSet<long> cells)
        {
            var result = new List<CellRect>();
            if (cells == null || cells.Count == 0) return result;

            var remaining = new HashSet<long>(cells);
            // Deterministic iteration order so log output is comparable
            // run-to-run when the input set is the same.
            var ordered = new List<long>(remaining);
            ordered.Sort();

            foreach (var startKey in ordered)
            {
                if (!remaining.Contains(startKey)) continue;
                UnpackXz(startKey, out var gx0, out var gz0);

                var gx1 = gx0;
                while (remaining.Contains(XzKey(gx1 + 1, gz0))) gx1++;

                var gz1 = gz0;
                while (true)
                {
                    var nextZ = gz1 + 1;
                    var rowComplete = true;
                    for (var x = gx0; x <= gx1; x++)
                    {
                        if (!remaining.Contains(XzKey(x, nextZ)))
                        {
                            rowComplete = false;
                            break;
                        }
                    }
                    if (!rowComplete) break;
                    gz1 = nextZ;
                }

                for (var z = gz0; z <= gz1; z++)
                for (var x = gx0; x <= gx1; x++)
                    remaining.Remove(XzKey(x, z));

                result.Add(new CellRect(gx0, gz0, gx1, gz1));
            }

            return result;
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

        // Reused buffer for the bed-aware waist probe. Sized generously —
        // dense village cells can stack many piece colliders in one waist box.
        private static readonly Collider[] s_waistProbeBuf = new Collider[64];

        private static bool ProbeAtWaist(int gxA, int gzA, int gxB, int gzB,
            float floorY, float cell, int mask)
        {
            ComputeWaistProbeBox(gxA, gzA, gxB, gzB, floorY, cell,
                out var center, out var halfExtents);
            var count = Physics.OverlapBoxNonAlloc(center, halfExtents,
                s_waistProbeBuf, Quaternion.identity, mask,
                QueryTriggerInteraction.Ignore);
            for (var i = 0; i < count; i++)
            {
                var col = s_waistProbeBuf[i];
                if (col == null) continue;
                // Beds are carved out of the agent NavMesh by the bake, so
                // they must never ALSO gate flood reachability. A bed standing
                // on a raised floor sits squarely in the waist band and would
                // otherwise wall off its own seed cell (and any neighbour it
                // overhangs), collapsing bed-reachability to nothing.
                if (IsBedCollider(col)) continue;
                return true;
            }
            return false;
        }

        private static bool IsBedCollider(Collider col)
        {
            return col.GetComponentInParent<Bed>() != null;
        }

        // --- Gate / door sealing for the Pass 1 outside flood --------------
        // A wall's collider sits in the waist band, so WallBlocks catches it.
        // A gate does too — but only while CLOSED. Open it and the leaf swings
        // clear of the doorway, the waist probe finds nothing, and the outside
        // flood pours through the gap, flooding the whole village "outside" and
        // collapsing the region graph. We seal the doorway geometrically from
        // the Door's fixed transform (position + forward are the closed
        // reference frame, unchanged by open state — DoorHandler.OpenDoor
        // relies on the same invariant), so a gate bounds the village whether
        // it is open or shut. Applied to Pass 1 (and the bake's outside-cell
        // flood) only; Pass 2's bed flood is left ungated so the gate cell
        // itself stays inside and walkable.

        internal readonly struct GateSeal
        {
            public readonly Vector3 Mid;     // door pivot / panel centre (XZ used)
            public readonly Vector3 Forward; // through-axis, horizontal, normalized
            public readonly float HalfWidth; // half doorway span + cell margin

            public GateSeal(Vector3 mid, Vector3 forward, float halfWidth)
            {
                Mid = mid;
                Forward = forward;
                HalfWidth = halfWidth;
            }
        }

        /// <summary>
        ///     Collect a sealing segment for every <see cref="Door" /> whose
        ///     pivot lies in [minX..maxX] x [minZ..maxZ]. Mirrors
        ///     NavMeshBakeManager.AddDoorBlockers' detection so the partition
        ///     flood and the bake agree on where the gates are.
        /// </summary>
        internal static List<GateSeal> GatherGateSeals(
            float minX, float minZ, float maxX, float maxZ)
        {
            var seals = new List<GateSeal>();
            var doors = UnityEngine.Object.FindObjectsByType<Door>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var door in doors)
            {
                if (door == null || door.transform == null) continue;
                var p = door.transform.position;
                if (p.x < minX || p.x > maxX || p.z < minZ || p.z > maxZ) continue;
                var fwd = door.transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 1e-4f) continue;
                fwd.Normalize();
                seals.Add(new GateSeal(p, fwd, GateHalfWidth(door)));
            }

            return seals;
        }

        // Half the doorway span the seal must cover. Derived from the door's
        // widest horizontal collider extent — rotation-independent (local size
        // x lossyScale, never world bounds, which rotate with an open leaf) —
        // plus half a lookup cell so a 4-connected step whose crossing point
        // lands at the cell boundary is still caught. Falls back to a standard
        // 1.2m door panel when no box collider is found.
        private static float GateHalfWidth(Door door)
        {
            var widest = 0f;
            var cols = door.GetComponentsInChildren<Collider>(true);
            foreach (var c in cols)
            {
                if (c == null || c.isTrigger) continue;
                if (c is BoxCollider bc)
                {
                    var ls = bc.transform.lossyScale;
                    widest = Mathf.Max(widest,
                        Mathf.Max(Mathf.Abs(bc.size.x * ls.x),
                            Mathf.Abs(bc.size.z * ls.z)));
                }
            }

            if (widest <= 0f) widest = 1.2f;
            return widest * 0.5f + RegionGraph.LookupCellSize * 0.5f;
        }

        /// <summary>
        ///     True iff the cell-centre step A→B passes through any gathered
        ///     gate's doorway (crosses the door plane within its half-width).
        /// </summary>
        private static bool GateBlocksStep(
            int gxA, int gzA, int gxB, int gzB, float cell, List<GateSeal> seals)
        {
            if (seals == null || seals.Count == 0) return false;
            var half = cell * 0.5f;
            var ax = gxA * cell + half;
            var az = gzA * cell + half;
            var bx = gxB * cell + half;
            var bz = gzB * cell + half;
            foreach (var s in seals)
                if (SegmentCrossesGateXz(ax, az, bx, bz, s))
                    return true;
            return false;
        }

        // XZ-only port of DoorHandler.SegmentCrossesDoor: the segment must
        // straddle the door plane (sign change of the forward-axis projection)
        // and the crossing point must lie within HalfWidth of the door midpoint.
        private static bool SegmentCrossesGateXz(
            float ax, float az, float bx, float bz, GateSeal s)
        {
            var aDot = (ax - s.Mid.x) * s.Forward.x + (az - s.Mid.z) * s.Forward.z;
            var bDot = (bx - s.Mid.x) * s.Forward.x + (bz - s.Mid.z) * s.Forward.z;

            // Same sign => both endpoints on the same side of the door plane.
            if (aDot >= 0f == bDot >= 0f) return false;

            var denom = aDot - bDot;
            if (Mathf.Abs(denom) < 1e-5f) return false;
            var t = aDot / denom;
            var cx = ax + (bx - ax) * t;
            var cz = az + (bz - az) * t;
            var dx = cx - s.Mid.x;
            var dz = cz - s.Mid.z;
            return dx * dx + dz * dz <= s.HalfWidth * s.HalfWidth;
        }

        /// <summary>
        ///     Pass 5: consolidate maximal linear piece-region chains into
        ///     single regions. A chain is a sequence [R0..Rn] of piece
        ///     regions where each link in the piece-only RegionLink
        ///     adjacency forms a degree-2 path with collinear centroids.
        ///
        ///     For each valid chain, the first region (R0) becomes the
        ///     anchor; the rest are absorbed: their cached triangles get
        ///     re-tagged to the anchor's id, their lookupGrid entries
        ///     point to the anchor, their incoming/outgoing links reroute
        ///     to the anchor, and intra-chain links (now anchor→anchor
        ///     self-loops) drop out.
        ///
        ///     Triangles are PRESERVED (not synthesized into a smooth
        ///     ramp) — vv_tri_debug still shows the original stair
        ///     geometry, just coloured as one region. This logical merge
        ///     reduces link count and waypoint count for the path planner
        ///     without changing what the agent NavMesh actually walks on.
        /// </summary>
        private static void ConsolidateLinearChains(
            HashSet<string> regionIds,
            Dictionary<string, Vector3> centroids,
            Dictionary<long, string> lookupGrid,
            List<(string id, Vector3 center, Vector3 outwardDir)> boundaryCells,
            List<RegionLink> links,
            Dictionary<string, SurfaceKind> kindMap,
            List<RegionBuilder.CachedTriangle> triangles,
            List<(string fromRid, string toRid, Vector3 startPos, Vector3 endPos)> pass3EdgePairs,
            ref Stats stats)
        {
            if (links == null || links.Count == 0) return;
            if (regionIds == null || regionIds.Count == 0) return;

            // Build piece-only adjacency from current links.
            var adj = new Dictionary<string, HashSet<string>>();
            foreach (var link in links)
            {
                if (kindMap == null) break;
                if (!kindMap.TryGetValue(link.FromRegionId, out var fk)) continue;
                if (!kindMap.TryGetValue(link.ToRegionId, out var tk)) continue;
                if (fk != SurfaceKind.Piece || tk != SurfaceKind.Piece) continue;
                if (!adj.TryGetValue(link.FromRegionId, out var sa))
                {
                    sa = new HashSet<string>();
                    adj[link.FromRegionId] = sa;
                }
                sa.Add(link.ToRegionId);
                if (!adj.TryGetValue(link.ToRegionId, out var sb))
                {
                    sb = new HashSet<string>();
                    adj[link.ToRegionId] = sb;
                }
                sb.Add(link.FromRegionId);
            }
            if (adj.Count == 0) return;

            // Find chains. Start from degree-1 endpoints, trace via
            // degree-2 neighbours, stop at next endpoint or hub.
            const float MinCollinearDot = 0.94f; // ~20° tolerance
            const float MinVerticalDelta = 0.1f; // skip pure-flat chains
            var visited = new HashSet<string>();
            var ridToAnchor = new Dictionary<string, string>();
            var chainsConsolidated = 0;
            var regionsConsumed = 0;

            foreach (var startRid in adj.Keys)
            {
                if (visited.Contains(startRid)) continue;
                if (adj[startRid].Count != 1) continue; // start only from endpoints

                var chain = new List<string> { startRid };
                visited.Add(startRid);
                string prev = null;
                var current = startRid;
                while (true)
                {
                    string next = null;
                    foreach (var n in adj[current])
                    {
                        if (n != prev) { next = n; break; }
                    }
                    if (next == null) break;
                    if (visited.Contains(next)) break;
                    if (!adj.TryGetValue(next, out var nAdj)) break;
                    if (nAdj.Count > 2) break; // hub — stop chain here
                    chain.Add(next);
                    visited.Add(next);
                    if (nAdj.Count == 1) break; // reached other endpoint
                    prev = current;
                    current = next;
                }

                if (chain.Count < 2) continue;

                // Validate collinearity + vertical component.
                var chainDir = Vector3.zero;
                for (var i = 0; i < chain.Count - 1; i++)
                {
                    if (!centroids.TryGetValue(chain[i], out var cA)) goto skip;
                    if (!centroids.TryGetValue(chain[i + 1], out var cB)) goto skip;
                    chainDir += (cB - cA).normalized;
                }
                chainDir = (chainDir / (chain.Count - 1)).normalized;

                // Skip pure-flat chains — they're not stair runs and the
                // logical merge offers less value (no vertical step
                // clutter to collapse). Vertical component must be at
                // least MinVerticalDelta of the chain direction.
                if (Mathf.Abs(chainDir.y) < MinVerticalDelta) continue;

                var valid = true;
                for (var i = 0; i < chain.Count - 1; i++)
                {
                    var seg = (centroids[chain[i + 1]] - centroids[chain[i]]).normalized;
                    if (Vector3.Dot(seg, chainDir) < MinCollinearDot)
                    {
                        valid = false;
                        break;
                    }
                }
                if (!valid) continue;

                // Merge: anchor is chain[0]. Compute new centroid as the
                // mean of chain centroids.
                var anchor = chain[0];
                var sum = Vector3.zero;
                foreach (var r in chain) sum += centroids[r];
                centroids[anchor] = sum / chain.Count;
                for (var i = 1; i < chain.Count; i++)
                {
                    ridToAnchor[chain[i]] = anchor;
                    regionsConsumed++;
                }
                chainsConsolidated++;
                continue;

                skip: ;
            }

            if (ridToAnchor.Count == 0)
            {
                stats.Pass5ChainsConsolidated = 0;
                stats.Pass5RegionsConsumed = 0;
                stats.Pass5LinksRemoved = 0;
                return;
            }

            // Apply ID remap across all data structures.
            // 1) Remove consumed regions from regionIds / centroids / kindMap.
            foreach (var consumed in ridToAnchor.Keys)
            {
                regionIds.Remove(consumed);
                centroids?.Remove(consumed);
                kindMap?.Remove(consumed);
            }

            // 2) Reassign triangles' RegionId.
            if (triangles != null)
                for (var i = 0; i < triangles.Count; i++)
                {
                    var t = triangles[i];
                    if (ridToAnchor.TryGetValue(t.RegionId, out var a))
                    {
                        t.RegionId = a;
                        triangles[i] = t;
                    }
                }

            // 3) Update lookupGrid values.
            if (lookupGrid != null)
            {
                var keysToUpdate = new List<long>();
                foreach (var kv in lookupGrid)
                    if (ridToAnchor.ContainsKey(kv.Value))
                        keysToUpdate.Add(kv.Key);
                foreach (var k in keysToUpdate)
                    lookupGrid[k] = ridToAnchor[lookupGrid[k]];
            }

            // 4) Remap links: redirect endpoints through ridToAnchor.
            //    Self-loops drop; dedup by canonical pair so we don't
            //    leave several copies of a link between the same anchor
            //    and an external region.
            var linksRemoved = 0;
            if (links != null)
            {
                var seenPairs = new HashSet<string>();
                for (var i = links.Count - 1; i >= 0; i--)
                {
                    var link = links[i];
                    var fromMapped = ridToAnchor.TryGetValue(link.FromRegionId, out var fa)
                        ? fa : link.FromRegionId;
                    var toMapped = ridToAnchor.TryGetValue(link.ToRegionId, out var ta)
                        ? ta : link.ToRegionId;
                    if (fromMapped == toMapped)
                    {
                        links.RemoveAt(i);
                        linksRemoved++;
                        continue;
                    }
                    var pairKey = string.CompareOrdinal(fromMapped, toMapped) < 0
                        ? fromMapped + "|" + toMapped
                        : toMapped + "|" + fromMapped;
                    if (!seenPairs.Add(pairKey))
                    {
                        links.RemoveAt(i);
                        linksRemoved++;
                        continue;
                    }
                    link.FromRegionId = fromMapped;
                    link.ToRegionId = toMapped;
                    links[i] = link;
                }
            }

            // 5) Remap pass3EdgePairs (consumed downstream by
            //    RegionPartitionHandler to merge into BfsAdjacencyStore).
            if (pass3EdgePairs != null)
            {
                var seenEdges = new HashSet<string>();
                for (var i = pass3EdgePairs.Count - 1; i >= 0; i--)
                {
                    var ep = pass3EdgePairs[i];
                    var fromMapped = ridToAnchor.TryGetValue(ep.fromRid, out var fa)
                        ? fa : ep.fromRid;
                    var toMapped = ridToAnchor.TryGetValue(ep.toRid, out var ta)
                        ? ta : ep.toRid;
                    if (fromMapped == toMapped)
                    {
                        pass3EdgePairs.RemoveAt(i);
                        continue;
                    }
                    var pairKey = string.CompareOrdinal(fromMapped, toMapped) < 0
                        ? fromMapped + "|" + toMapped
                        : toMapped + "|" + fromMapped;
                    if (!seenEdges.Add(pairKey))
                    {
                        pass3EdgePairs.RemoveAt(i);
                        continue;
                    }
                    pass3EdgePairs[i] = (fromMapped, toMapped, ep.startPos, ep.endPos);
                }
            }

            // 6) Remap boundaryCells ids (don't dedup — multiple cells
            //    with the same id are valid; each is a distinct cell).
            if (boundaryCells != null)
                for (var i = 0; i < boundaryCells.Count; i++)
                {
                    var bc = boundaryCells[i];
                    if (ridToAnchor.TryGetValue(bc.id, out var a))
                        boundaryCells[i] = (a, bc.center, bc.outwardDir);
                }

            stats.Pass5ChainsConsolidated = chainsConsolidated;
            stats.Pass5RegionsConsumed = regionsConsumed;
            stats.Pass5LinksRemoved = linksRemoved;

            DebugLog.Event("RubberBandPrune", "pass5_chain_consolidation",
                ("chains_consolidated", chainsConsolidated),
                ("regions_consumed", regionsConsumed),
                ("links_removed", linksRemoved));
        }

        /// <summary>
        ///     Pass 4: snap region boundary vertices and link endpoints to
        ///     the slot-31 agent NavMesh. Boundary vertices are vertices
        ///     incident to edges that appear only once in their region's
        ///     triangulation (the region's outer perimeter); interior
        ///     vertices are deliberately left alone since they have no
        ///     bearing on pathing or link placement.
        ///
        ///     Skips early if VillagerAgentType isn't registered (no agent
        ///     NavMesh to snap against) — leaves all vertices untouched.
        /// </summary>
        private static void SnapBordersToAgentNavMesh(
            HashSet<string> regionIds,
            List<RegionLink> links,
            List<RegionBuilder.CachedTriangle> triangles,
            ref Stats stats)
        {
            if (!VillagerAgentType.IsRegistered) return;
            if (triangles == null || triangles.Count == 0) return;

            var agentTypeID = VillagerAgentType.UnityAgentTypeID;
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = agentTypeID,
                areaMask = NavMesh.AllAreas,
            };
            const float snapRadius = 0.5f;

            // Group triangle indices by region.
            var ridToTris = new Dictionary<string, List<int>>();
            for (var i = 0; i < triangles.Count; i++)
            {
                var rid = triangles[i].RegionId;
                if (string.IsNullOrEmpty(rid)) continue;
                if (!regionIds.Contains(rid)) continue;
                if (!ridToTris.TryGetValue(rid, out var list))
                {
                    list = new List<int>();
                    ridToTris[rid] = list;
                }
                list.Add(i);
            }

            // Per-region boundary vertex collection, then snap.
            // Cache snap results so a vertex shared by multiple regions
            // gets one SamplePosition call total (and a consistent
            // snapped position across regions).
            var snapCache = new Dictionary<Vector3, Vector3>();
            var boundarySnapped = 0;
            var boundaryMisses = 0;

            foreach (var kv in ridToTris)
            {
                var triIndices = kv.Value;
                // Count each undirected edge within this region. Boundary
                // edges have count == 1 (no neighbouring triangle in this
                // region shares them — they're either the region perimeter
                // or shared with a different region).
                var edgeCount = new Dictionary<long, int>();
                var edgeVerts = new Dictionary<long, (Vector3 a, Vector3 b)>();
                foreach (var ti in triIndices)
                {
                    var t = triangles[ti];
                    AddEdgeForSnap(edgeCount, edgeVerts, t.V0, t.V1);
                    AddEdgeForSnap(edgeCount, edgeVerts, t.V1, t.V2);
                    AddEdgeForSnap(edgeCount, edgeVerts, t.V2, t.V0);
                }

                foreach (var ekv in edgeCount)
                {
                    if (ekv.Value != 1) continue;
                    var (a, b) = edgeVerts[ekv.Key];
                    TrySnapAndCache(a, filter, snapRadius, snapCache,
                        ref boundarySnapped, ref boundaryMisses);
                    TrySnapAndCache(b, filter, snapRadius, snapCache,
                        ref boundarySnapped, ref boundaryMisses);
                }
            }

            // Apply snapped positions back to cached triangles. Interior
            // vertices aren't in snapCache so they pass through unchanged.
            for (var i = 0; i < triangles.Count; i++)
            {
                var t = triangles[i];
                var changed = false;
                if (snapCache.TryGetValue(t.V0, out var sv0) && sv0 != t.V0)
                { t.V0 = sv0; changed = true; }
                if (snapCache.TryGetValue(t.V1, out var sv1) && sv1 != t.V1)
                { t.V1 = sv1; changed = true; }
                if (snapCache.TryGetValue(t.V2, out var sv2) && sv2 != t.V2)
                { t.V2 = sv2; changed = true; }
                if (changed) triangles[i] = t;
            }

            // Snap link endpoints. Most should already be in snapCache from
            // the boundary pass (Pass 3 emits links at cell boundaries which
            // typically coincide with region boundary vertices), but probe
            // unconditionally so edge-based RegionBuilder links (which use
            // centroid positions) also get snapped.
            var linksSnapped = 0;
            if (links != null)
            {
                for (var i = 0; i < links.Count; i++)
                {
                    var link = links[i];
                    var changed = false;
                    if (NavMesh.SamplePosition(link.PositionStart,
                            out var startHit, snapRadius, filter))
                    {
                        if (link.PositionStart != startHit.position)
                        {
                            link.PositionStart = startHit.position;
                            changed = true;
                        }
                    }
                    if (NavMesh.SamplePosition(link.PositionEnd,
                            out var endHit, snapRadius, filter))
                    {
                        if (link.PositionEnd != endHit.position)
                        {
                            link.PositionEnd = endHit.position;
                            changed = true;
                        }
                    }
                    if (changed)
                    {
                        links[i] = link;
                        linksSnapped++;
                    }
                }
            }

            stats.Pass4BoundaryVertsSnapped = boundarySnapped;
            stats.Pass4SnapMisses = boundaryMisses;
            stats.Pass4LinksSnapped = linksSnapped;

            DebugLog.Event("RubberBandPrune", "pass4_border_snap",
                ("boundary_verts_snapped", boundarySnapped),
                ("snap_misses", boundaryMisses),
                ("links_snapped", linksSnapped));
        }

        /// <summary>
        ///     Helper: register an undirected edge under a canonical key so
        ///     edge counting works regardless of triangle winding order.
        /// </summary>
        private static void AddEdgeForSnap(
            Dictionary<long, int> edgeCount,
            Dictionary<long, (Vector3 a, Vector3 b)> edgeVerts,
            Vector3 a, Vector3 b)
        {
            var ha = a.GetHashCode();
            var hb = b.GetHashCode();
            // Canonical key — order by hash so (a,b) and (b,a) collide.
            var key = ha <= hb
                ? ((long)ha << 32) | (uint)hb
                : ((long)hb << 32) | (uint)ha;
            if (edgeCount.TryGetValue(key, out var c))
            {
                edgeCount[key] = c + 1;
            }
            else
            {
                edgeCount[key] = 1;
                edgeVerts[key] = (a, b);
            }
        }

        /// <summary>
        ///     Helper: snap one vertex via SamplePosition, caching the
        ///     result so subsequent calls for the same position don't re-
        ///     query the NavMesh.
        /// </summary>
        private static void TrySnapAndCache(Vector3 v,
            NavMeshQueryFilter filter, float radius,
            Dictionary<Vector3, Vector3> cache,
            ref int snapped, ref int misses)
        {
            if (cache.ContainsKey(v)) return;
            if (NavMesh.SamplePosition(v, out var hit, radius, filter))
            {
                cache[v] = hit.position;
                if (hit.position != v) snapped++;
            }
            else
            {
                cache[v] = v;
                misses++;
            }
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
            // Mirror ProbeAtWaist: bed colliders are ignored for the blocking
            // verdict (the bake carves them out of the agent NavMesh) but still
            // listed — tagged — so vv_probe shows what's physically there.
            var blocked = false;

            void AddNames(Collider[] arr, int max)
            {
                if (arr == null) return;
                for (var i = 0; i < arr.Length; i++)
                {
                    var col = arr[i];
                    var isBed = col != null && IsBedCollider(col);
                    if (!isBed && col != null) blocked = true; // verdict scans all
                    if (i >= max) continue;                    // names are capped
                    if (col == null) names.Add("<null>");
                    else names.Add(isBed ? col.name + "(bed,ignored)" : col.name);
                }
            }

            AddNames(hitsA, 4);
            AddNames(hitsB, 4);
            hitNames = string.Join(";", names);
            if (total > names.Count) hitNames += $";+{total - names.Count}";
            return blocked;
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

        // 2D XZ key — delegates to RegionGraph.PackXz, the single source of
        // truth for the cell key space shared by the partition classification,
        // v4 persistence, and the incremental reconcilers.
        private static long XzKey(int gx, int gz)
        {
            return RegionGraph.PackXz(gx, gz);
        }

        private static void UnpackXz(long key, out int gx, out int gz)
        {
            RegionGraph.UnpackXz(key, out gx, out gz);
        }

        // Inverse of RegionGraph.PackLookup. Uses symmetric mod centred on
        // zero so negative gx/gz/hb round-trip correctly.
        internal static void UnpackLookup(long key, out int gx, out int gz, out int hb)
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
            /// <summary>RegionLinks added from Pass 3's discovered piece-step adjacency (cell-level flood is more reliable than vertex-distance heuristics for proving walkable adjacency).</summary>
            public int Pass3LinksAdded;

            /// <summary>Pass 4: unique boundary vertices snapped to the agent NavMesh.</summary>
            public int Pass4BoundaryVertsSnapped;
            /// <summary>Pass 4: boundary vertices whose snap missed (no agent NavMesh within snap radius).</summary>
            public int Pass4SnapMisses;
            /// <summary>Pass 4: RegionLink endpoints snapped to the agent NavMesh.</summary>
            public int Pass4LinksSnapped;

            /// <summary>Pass 5: linear piece-region chains consolidated (e.g. stair runs merged into a single region).</summary>
            public int Pass5ChainsConsolidated;
            /// <summary>Pass 5: piece regions absorbed by chain consolidation (anchor regions retained, others removed).</summary>
            public int Pass5RegionsConsumed;
            /// <summary>Pass 5: RegionLinks removed because they became intra-chain (anchor→anchor self-loops) or duplicates of an existing external link.</summary>
            public int Pass5LinksRemoved;

            public int RegionsDropped;
            public int PerimeterSeeds;
            public int LookupCellsDropped;
            public int TrianglesDropped;
            public int StaticSolidTrianglesDropped;
        }
    }
}