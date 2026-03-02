using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    /// Grid sampling, flood-fill BFS, and link detection for the HNA region graph.
    /// Extracted from HnaPartitionHandler to keep the orchestration thin.
    /// </summary>
    internal static class HnaGridBuilder
    {
        /// <summary>Height above parent cell to start the downward raycast (meters). Must clear furniture/small objects.</summary>
        private const float RaycastHeightOffset = 3f;
        /// <summary>Max downward raycast distance. Must be large enough to find the floor below the offset.</summary>
        private const float RaycastMaxDown = 8f;
        /// <summary>Layer mask for downward height raycasts (terrain only — avoids foundation/overhang interference).</summary>
        private static readonly int GroundLayerMask = LayerMask.GetMask("Default", "static_solid", "terrain");
        /// <summary>Layer mask for piece-only fallback raycasts (stairs/upper floors where terrain is too far below).</summary>
        private static readonly int PieceOnlyMask = LayerMask.GetMask("piece");
        /// <summary>Layer mask for horizontal wall-check raycasts (includes "piece" so walls block the BFS).</summary>
        private static readonly int WallCheckMask = LayerMask.GetMask("Default", "static_solid", "terrain", "piece");

        /// <summary>Height above ground to cast horizontal wall-check ray (m). Must clear low furniture but hit walls.</summary>
        private const float WallCheckHeight = 1.0f;
        /// <summary>Size of height buckets for multi-floor cell IDs (m). Surfaces within one bucket share a cell.</summary>
        private const float HeightBucketSize = 2f;
        /// <summary>Min headroom above a piece surface to consider it walkable (m).</summary>
        private const float PlayerClearanceHeight = 2f;
        /// <summary>Height above parent Y to start piece-probing raycasts (m).</summary>
        private const float PieceProbeAbove = 8f;
        /// <summary>Max downward distance for piece-probing raycasts (m).</summary>
        private const float PieceProbeDown = 16f;
        /// <summary>Max height above parent Y to accept a piece surface (m). Filters roofs.</summary>
        private const float MaxPieceSurfaceAbove = 5f;
        /// <summary>Min distance above terrain for a piece surface to be treated as a separate floor (m).</summary>
        private const float MinFloorSeparation = 1f;
        /// <summary>Max height above terrain at this cell to accept a piece (m). Surfaces above this are treated as roof/arch and skipped.</summary>
        private const float MaxPieceAboveTerrain = 3.5f;
        /// <summary>Max distance from a door to block BFS expansion (m). Covers the wall section around the door.</summary>
        private const float DoorBlockRadius = 4f;
        /// <summary>Max slope (degrees) considered walkable; ~26° is typical for player climb.</summary>
        private const float MaxWalkableSlopeDeg = 26f;
        /// <summary>Height tolerance (m) when validating slope path samples; allows stepped (stair) geometry.</summary>
        private const float SlopePathHeightTolerance = 1.5f;
        private const int SlopePathSamples = 5;
        /// <summary>Sample offset from cell center (m) when detecting multiple height levels; must stay within cell.</summary>
        private static float VerticalSampleOffset => HnaRegionGraph.CellSize * 0.4f;
        /// <summary>Min vertical spread (m) in a cell to add a same-cell stair link (platform/stair).</summary>
        private const float MinVerticalSpread = 0.5f;

        internal static string CellKey(int ix, int iz, int hBucket) => $"{ix}_{iz}_h{hBucket}";

        internal static void CellFromWorld(float originX, float originZ, float worldX, float worldZ,
            out int ix, out int iz)
        {
            ix = Mathf.FloorToInt((worldX - originX) / HnaRegionGraph.CellSize);
            iz = Mathf.FloorToInt((worldZ - originZ) / HnaRegionGraph.CellSize);
        }

        internal static int HeightBucket(float y) => Mathf.FloorToInt(y / HeightBucketSize);

        internal static bool TryParseRegionId(string id, out int ix, out int iz)
        {
            return TryParseRegionId(id, out ix, out iz, out _);
        }

        internal static bool TryParseRegionId(string id, out int ix, out int iz, out int hBucket)
        {
            ix = iz = hBucket = 0;
            if (string.IsNullOrEmpty(id)) return false;
            var parts = id.Split('_');
            if (parts.Length < 2) return false;
            if (!int.TryParse(parts[0], out ix)) return false;
            if (!int.TryParse(parts[1], out iz)) return false;
            if (parts.Length >= 3 && parts[2].Length > 1 && parts[2][0] == 'h')
                int.TryParse(parts[2].Substring(1), out hBucket);
            return true;
        }

        /// <summary>Returns true if there is at least PlayerClearanceHeight of open space above the surface.</summary>
        private static bool HasClearanceAbove(float wx, float surfaceY, float wz, int mask)
        {
            var origin = new Vector3(wx, surfaceY + 0.15f, wz);
            return !Physics.Raycast(origin, Vector3.up, PlayerClearanceHeight, mask, QueryTriggerInteraction.Ignore);
        }

        private struct DoorBarrier
        {
            public float Px, Pz, Fx, Fz, Radius2;
        }

        private static List<DoorBarrier> CollectDoorBarriers()
        {
            var barriers = new List<DoorBarrier>();
            var doors = Object.FindObjectsByType<Door>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var door in doors)
            {
                if (door == null || door.GetComponentInParent<Piece>() == null) continue;
                var pos = door.transform.position;
                var fwd = door.transform.forward;
                fwd.y = 0;
                if (fwd.sqrMagnitude < 0.01f) continue;
                fwd.Normalize();
                barriers.Add(new DoorBarrier
                {
                    Px = pos.x, Pz = pos.z,
                    Fx = fwd.x, Fz = fwd.z,
                    Radius2 = DoorBlockRadius * DoorBlockRadius
                });
            }
            return barriers;
        }

        /// <summary>Returns true if the line from (ax,az)→(bx,bz) crosses any door's blocking plane within its radius.</summary>
        private static bool CrossesDoorBarrier(float ax, float az, float bx, float bz, List<DoorBarrier> barriers)
        {
            for (int i = 0; i < barriers.Count; i++)
            {
                var d = barriers[i];
                float dA = (ax - d.Px) * d.Fx + (az - d.Pz) * d.Fz;
                float dB = (bx - d.Px) * d.Fx + (bz - d.Pz) * d.Fz;
                if (dA * dB > 0) continue; // same side
                float denom = dA - dB;
                if (Mathf.Abs(denom) < 0.001f) continue;
                float t = dA / denom;
                float cx = ax + t * (bx - ax);
                float cz = az + t * (bz - az);
                float dist2 = (cx - d.Px) * (cx - d.Px) + (cz - d.Pz) * (cz - d.Pz);
                if (dist2 <= d.Radius2) return true;
            }
            return false;
        }

        /// <summary>From an array of RaycastHit, return the one whose Y is closest to referenceY.</summary>
        private static bool TryPickClosestHit(RaycastHit[] hits, float referenceY, out RaycastHit bestHit)
        {
            bestHit = default;
            if (hits == null || hits.Length == 0) return false;
            float bestDist = float.MaxValue;
            for (int i = 0; i < hits.Length; i++)
            {
                float d = Mathf.Abs(hits[i].point.y - referenceY);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestHit = hits[i];
                }
            }
            return true;
        }

        /// <summary>
        /// Discover walkable cells by BFS from beds using Physics.Raycast downward.
        /// Each cell's height is found by casting DOWN from the parent cell's known Y + offset.
        /// This naturally stays on the correct floor (ground rays hit ground, upper floor rays
        /// hit upper floor) and avoids the dual-terrain-layer problem.
        /// </summary>
        internal static (HashSet<string> regionIds, Dictionary<string, float> cellHeights) FloodFillFromBeds(
            float originX, float originZ, int cellCountX, int cellCountZ, List<Vector3> beds)
        {
            var regionIds = new HashSet<string>();
            var cellHeights = new Dictionary<string, float>();
            if (beds == null || beds.Count == 0) return (regionIds, cellHeights);

            // Resolve layer masks; fall back to all-but-triggers if named layers don't exist
            int mask = GroundLayerMask;
            if (mask == 0) mask = ~0;
            int wallMask = WallCheckMask;
            if (wallMask == 0) wallMask = ~0;

            // Collect door barriers for BFS blocking
            var doorBarriers = CollectDoorBarriers();

            // Cache: cell id → world position with correct Y
            var cellPos = new Dictionary<string, Vector3>();
            float r2 = HnaPartitionHandler.FloodFillRadius * HnaPartitionHandler.FloodFillRadius;

            // Step 1: Seed from beds – bed Y is known-good
            var queue = new Queue<string>();
            foreach (var bed in beds)
            {
                CellFromWorld(originX, originZ, bed.x, bed.z, out int bx, out int bz);
                if (bx < 0 || bx >= cellCountX || bz < 0 || bz >= cellCountZ)
                    continue;
                string id;
                float wx = originX + (bx + 0.5f) * HnaRegionGraph.CellSize;
                float wz = originZ + (bz + 0.5f) * HnaRegionGraph.CellSize;

                // Raycast down from bed height + offset to find the walkable surface
                var rayOrigin = new Vector3(wx, bed.y + RaycastHeightOffset, wz);
                float hitY;
                if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit seedHit, RaycastMaxDown, mask, QueryTriggerInteraction.Ignore))
                {
                    hitY = seedHit.point.y;
                }
                else
                {
                    hitY = bed.y; // trust the bed's own Y
                }

                id = CellKey(bx, bz, HeightBucket(hitY));
                var pos = new Vector3(wx, hitY, wz);
                if (regionIds.Add(id))
                {
                    cellPos[id] = pos;
                    cellHeights[id] = hitY;
                    queue.Enqueue(id);
                }
            }

            // Step 2: BFS flood-fill — at each neighbor, discover all walkable surfaces (terrain + pieces)
            int expanded = 0, upperCells = 0;
            int rejNoHit = 0, rejSlope = 0, rejWall = 0, rejDoor = 0;
            var candidates = new List<float>(4);
            while (queue.Count > 0)
            {
                string fromId = queue.Dequeue();
                if (!TryParseRegionId(fromId, out int ix, out int iz)) continue;
                if (!cellPos.TryGetValue(fromId, out Vector3 posA)) continue;

                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = ix + dx, nz = iz + dz;
                    if (nx < 0 || nx >= cellCountX || nz < 0 || nz >= cellCountZ) continue;

                    float wx = originX + (nx + 0.5f) * HnaRegionGraph.CellSize;
                    float wz = originZ + (nz + 0.5f) * HnaRegionGraph.CellSize;

                    bool nearBed = false;
                    foreach (var bed in beds)
                    {
                        float bdx2 = wx - bed.x, bdz2 = wz - bed.z;
                        if (bdx2 * bdx2 + bdz2 * bdz2 <= r2) { nearBed = true; break; }
                    }
                    if (!nearBed) continue;

                    float dxzLen = new Vector2(wx - posA.x, wz - posA.z).magnitude;
                    if (dxzLen < 0.01f) continue;

                    // --- Gather all candidate walkable surfaces at (wx, wz) ---
                    candidates.Clear();
                    float terrainY = float.NaN;

                    // 1. Terrain surface
                    var terrainRayOrigin = new Vector3(wx, posA.y + RaycastHeightOffset, wz);
                    if (Physics.Raycast(terrainRayOrigin, Vector3.down, out RaycastHit tHit, RaycastMaxDown, mask,
                            QueryTriggerInteraction.Ignore))
                    {
                        terrainY = tHit.point.y;
                        candidates.Add(terrainY);
                    }

                    // 2. Piece surfaces with headroom clearance (stairs, upper floors)
                    var pieceRayOrigin = new Vector3(wx, posA.y + PieceProbeAbove, wz);
                    var pHits = Physics.RaycastAll(pieceRayOrigin, Vector3.down, PieceProbeDown, PieceOnlyMask,
                        QueryTriggerInteraction.Ignore);
                    for (int pi = 0; pi < pHits.Length; pi++)
                    {
                        float py = pHits[pi].point.y;
                        if (!float.IsNaN(terrainY) && Mathf.Abs(py - terrainY) < MinFloorSeparation) continue;
                        if (!float.IsNaN(terrainY) && py - terrainY > MaxPieceAboveTerrain) continue;
                        if (py > posA.y + MaxPieceSurfaceAbove) continue;
                        if (py < posA.y - RaycastMaxDown) continue;
                        if (!HasClearanceAbove(wx, py, wz, WallCheckMask)) continue;
                        candidates.Add(py);
                    }

                    if (candidates.Count == 0) { rejNoHit++; continue; }

                    // --- Try each candidate surface ---
                    for (int ci = 0; ci < candidates.Count; ci++)
                    {
                        float cy = candidates[ci];
                        string toId = CellKey(nx, nz, HeightBucket(cy));
                        if (regionIds.Contains(toId)) continue;

                        float dy = Mathf.Abs(cy - posA.y);
                        float slope = Mathf.Atan2(dy, dxzLen) * Mathf.Rad2Deg;
                        if (slope > MaxWalkableSlopeDeg) { rejSlope++; continue; }

                        // Wall check at waist height of the lower surface
                        float wallY = Mathf.Min(posA.y, cy) + WallCheckHeight;
                        var wFrom = new Vector3(posA.x, wallY, posA.z);
                        var wTo = new Vector3(wx, wallY, wz);
                        var wDir = wTo - wFrom;
                        float wDist = wDir.magnitude;
                        if (wDist > 0.01f && Physics.Raycast(wFrom, wDir / wDist, wDist, wallMask,
                                QueryTriggerInteraction.Ignore))
                        { rejWall++; continue; }

                        if (CrossesDoorBarrier(posA.x, posA.z, wx, wz, doorBarriers))
                        { rejDoor++; continue; }

                        var posB = new Vector3(wx, cy, wz);
                        regionIds.Add(toId);
                        cellPos[toId] = posB;
                        cellHeights[toId] = cy;
                        queue.Enqueue(toId);
                        expanded++;
                        bool isUpper = !float.IsNaN(terrainY) && Mathf.Abs(cy - terrainY) >= MinFloorSeparation;
                        if (isUpper) upperCells++;
                    }
                }
            }

            Plugin.Log?.LogInfo($"[HNA] Raycast flood-fill: {beds.Count} beds → {regionIds.Count} reachable cells " +
                $"({expanded} edges, upper={upperCells}, rej: noHit={rejNoHit} slope={rejSlope} wall={rejWall} door={rejDoor}, " +
                $"doorBarriers={doorBarriers.Count})");

            return (regionIds, cellHeights);
        }

        internal static string RegionIdAt(float originX, float originZ, float worldX, float worldY, float worldZ,
            HashSet<string> regionIds, Dictionary<string, float> cellHeights)
        {
            if (regionIds == null) return null;
            CellFromWorld(originX, originZ, worldX, worldZ, out int ix, out int iz);
            int hb = HeightBucket(worldY);
            // Search nearby height buckets to find the closest match
            for (int d = 0; d <= 3; d++)
            {
                string k1 = CellKey(ix, iz, hb + d);
                if (regionIds.Contains(k1)) return k1;
                if (d > 0)
                {
                    string k2 = CellKey(ix, iz, hb - d);
                    if (regionIds.Contains(k2)) return k2;
                }
            }
            return null;
        }

        /// <summary>Offset from door position to sample each side (meters). Must be > CellSize/2 so the two points land in different cells.</summary>
        private static float DoorSideOffset => HnaRegionGraph.CellSize * 0.6f;

        internal static void CollectDoorLinks(float originX, float originZ, float minX, float minZ, float maxX, float maxZ,
            HashSet<string> regionIds, Dictionary<string, float> cellHeights, List<HnaLink> links)
        {
            var doors = Object.FindObjectsByType<Door>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (doors == null || regionIds == null) return;
            int inBounds = 0, hasPiece = 0, bothRegions = 0;
            foreach (var door in doors)
            {
                if (door == null) continue;
                var pos = door.transform.position;
                if (pos.x < minX - HnaRegionGraph.CellSize || pos.x > maxX + HnaRegionGraph.CellSize ||
                    pos.z < minZ - HnaRegionGraph.CellSize || pos.z > maxZ + HnaRegionGraph.CellSize)
                    continue;
                inBounds++;
                if (door.GetComponentInParent<Piece>() == null) continue;
                hasPiece++;
                Vector3 forward = door.transform.forward;
                forward.y = 0f;
                if (forward.sqrMagnitude < 0.01f) continue;
                forward.Normalize();
                float doorOff = DoorSideOffset;
                var fromPos = pos - forward * doorOff;
                var toPos = pos + forward * doorOff;
                if (HnaRegionGraph.GetSolidHeightAt(fromPos.x, fromPos.z, out float hyFrom))
                    fromPos.y = hyFrom;
                if (HnaRegionGraph.GetSolidHeightAt(toPos.x, toPos.z, out float hyTo))
                    toPos.y = hyTo;
                string fromId = RegionIdAt(originX, originZ, fromPos.x, fromPos.y, fromPos.z, regionIds, cellHeights);
                string toId = RegionIdAt(originX, originZ, toPos.x, toPos.y, toPos.z, regionIds, cellHeights);
                if (fromId == null || toId == null || fromId == toId) continue;
                bothRegions++;
                links.Add(new HnaLink
                {
                    FromRegionId = fromId,
                    ToRegionId = toId,
                    LinkType = HnaLinkType.Door,
                    PositionStart = fromPos,
                    PositionEnd = toPos
                });
            }
            Plugin.Log?.LogInfo($"[HNA] Doors: {doors.Length} total, {inBounds} in bounds, {hasPiece} with Piece, {bothRegions} linked (both sides in regions)");
        }

        /// <summary>
        /// Link adjacent regions. Uses a spatial lookup to find all cells at adjacent XZ,
        /// including cells at different floor levels (multi-floor support).
        /// </summary>
        internal static void CollectSlopeLinks(float originX, float originZ, HashSet<string> regionIds,
            Dictionary<string, float> cellHeights, List<HnaLink> links)
        {
            if (regionIds == null) return;
            var centerByRegion = new Dictionary<string, Vector3>();
            var cellsByXZ = new Dictionary<long, List<string>>();
            foreach (string id in regionIds)
            {
                if (string.IsNullOrEmpty(id) || !TryParseRegionId(id, out int ix, out int iz)) continue;
                float wx = originX + (ix + 0.5f) * HnaRegionGraph.CellSize;
                float wz = originZ + (iz + 0.5f) * HnaRegionGraph.CellSize;
                float h;
                if (cellHeights != null && cellHeights.TryGetValue(id, out float bfsY))
                    h = bfsY;
                else if (!HnaRegionGraph.GetSolidHeightAt(wx, wz, out h))
                    continue;
                centerByRegion[id] = new Vector3(wx, h, wz);
                long xzKey = ((long)ix << 32) | (uint)iz;
                if (!cellsByXZ.TryGetValue(xzKey, out var list))
                {
                    list = new List<string>(2);
                    cellsByXZ[xzKey] = list;
                }
                list.Add(id);
            }

            int added = 0;
            foreach (string fromId in regionIds)
            {
                if (!centerByRegion.TryGetValue(fromId, out var posA)) continue;
                if (!TryParseRegionId(fromId, out int ix, out int iz)) continue;
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    long nKey = ((long)(ix + dx) << 32) | (uint)(iz + dz);
                    if (!cellsByXZ.TryGetValue(nKey, out var neighbors)) continue;
                    foreach (string toId in neighbors)
                    {
                        if (!centerByRegion.TryGetValue(toId, out var posB)) continue;
                        if (string.CompareOrdinal(fromId, toId) >= 0) continue;
                        added++;
                        var mid = (posA + posB) * 0.5f;
                        links.Add(new HnaLink
                        {
                            FromRegionId = fromId, ToRegionId = toId,
                            LinkType = HnaLinkType.Slope,
                            PositionStart = mid, PositionEnd = mid
                        });
                    }
                }
            }
            Plugin.Log?.LogInfo($"[HNA] Slope links: {added} adjacent-region links added");
        }

        /// <summary>
        /// Add stair links between cells at the same XZ but different floor levels.
        /// </summary>
        internal static void CollectVerticalLinksInCell(float originX, float originZ, HashSet<string> regionIds,
            Dictionary<string, float> cellHeights, List<HnaLink> links)
        {
            if (regionIds == null || cellHeights == null) return;
            var cellsByXZ = new Dictionary<long, List<string>>();
            foreach (string id in regionIds)
            {
                if (!TryParseRegionId(id, out int ix, out int iz)) continue;
                long xzKey = ((long)ix << 32) | (uint)iz;
                if (!cellsByXZ.TryGetValue(xzKey, out var list))
                {
                    list = new List<string>(2);
                    cellsByXZ[xzKey] = list;
                }
                list.Add(id);
            }
            int added = 0;
            foreach (var kv in cellsByXZ)
            {
                var list = kv.Value;
                if (list.Count < 2) continue;
                for (int i = 0; i < list.Count; i++)
                for (int j = i + 1; j < list.Count; j++)
                {
                    if (!cellHeights.TryGetValue(list[i], out float yA)) continue;
                    if (!cellHeights.TryGetValue(list[j], out float yB)) continue;
                    if (!TryParseRegionId(list[i], out int ix, out int iz)) continue;
                    float wx = originX + (ix + 0.5f) * HnaRegionGraph.CellSize;
                    float wz = originZ + (iz + 0.5f) * HnaRegionGraph.CellSize;
                    links.Add(new HnaLink
                    {
                        FromRegionId = list[i], ToRegionId = list[j],
                        LinkType = HnaLinkType.Stair,
                        PositionStart = new Vector3(wx, yA, wz),
                        PositionEnd = new Vector3(wx, yB, wz)
                    });
                    added++;
                }
            }
            if (added > 0)
                Plugin.Log?.LogInfo($"[HNA] Vertical links: {added} same-XZ multi-floor stair links added");
        }

        private static bool ValidateSlopePath(Vector3 a, Vector3 b)
        {
            for (int i = 0; i <= SlopePathSamples; i++)
            {
                float t = (float)i / SlopePathSamples;
                float x = Mathf.Lerp(a.x, b.x, t);
                float z = Mathf.Lerp(a.z, b.z, t);
                float expectedY = Mathf.Lerp(a.y, b.y, t);
                if (!HnaRegionGraph.GetSolidHeightAt(x, z, out float solidY))
                    return false;
                if (Mathf.Abs(solidY - expectedY) > SlopePathHeightTolerance)
                    return false;
            }
            return true;
        }
    }
}
