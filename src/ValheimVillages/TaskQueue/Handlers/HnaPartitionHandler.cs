using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using ValheimVillages.NPCs.AI;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villages;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    /// Low-priority task that builds the HNA region graph for village pathfinding.
    /// Partitions only non-spawnable areas: guard patrol polygons and, at minimum,
    /// any space within 15m of a villager's bed. Adds doors and stairs as links.
    /// </summary>
    public class HnaPartitionHandler : ITaskHandler
    {
        // #region agent log
        private const string DebugLogPath = "/home/benny/Projects/valheim_villages/.cursor/debug.log";
        private static void DebugLog(string hypothesisId, string location, string message, string data)
        {
            try
            {
                long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line = $"{{\"hypothesisId\":\"{hypothesisId}\",\"location\":\"{location}\",\"message\":\"{message}\",\"data\":{data},\"timestamp\":{ts}}}\n";
                File.AppendAllText(DebugLogPath, line);
            }
            catch { }
        }
        // #endregion
        public const string HnaPartitionTaskName = "hna_partition";

        /// <summary>Radius in meters around each bed to treat as village for scanning.</summary>
        public const float BedVillageRadius = 15f;
        /// <summary>Max distance (m) from any bed that the flood-fill may reach.</summary>
        private const float FloodFillRadius = 45f;
        /// <summary>When building regions, use this radius so link endpoints (door ± DoorSideOffset) still land in a region.</summary>
        private const float RegionBuildRadius = 30f;

        public string TaskName => HnaPartitionTaskName;

        /// <summary>Max distance from the anchor bed for another bed to be considered
        /// part of the same village.</summary>
        private const float VillageClusterRadius = 50f;

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            var allBeds = VillagerAIManager.GetAllBedPositions();
            var beds = FilterBedsByAnchor(allBeds, task);
            bool hasGuardBounds = VillageAreaManager.TryGetCombinedBounds(
                out float guardMinX, out float guardMinZ, out float guardMaxX, out float guardMaxZ);

            float minX, minZ, maxX, maxZ;
            if (hasGuardBounds && beds != null && beds.Count > 0)
            {
                minX = guardMinX;
                minZ = guardMinZ;
                maxX = guardMaxX;
                maxZ = guardMaxZ;
                foreach (var bed in beds)
                {
                    if (bed.x - RegionBuildRadius < minX) minX = bed.x - RegionBuildRadius;
                    if (bed.z - RegionBuildRadius < minZ) minZ = bed.z - RegionBuildRadius;
                    if (bed.x + RegionBuildRadius > maxX) maxX = bed.x + RegionBuildRadius;
                    if (bed.z + RegionBuildRadius > maxZ) maxZ = bed.z + RegionBuildRadius;
                }
            }
            else if (hasGuardBounds)
            {
                minX = guardMinX;
                minZ = guardMinZ;
                maxX = guardMaxX;
                maxZ = guardMaxZ;
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
                HnaRegionGraph.Clear();
                HnaDebugVisualization.ClearMarkers();
                Plugin.Log?.LogInfo("[HNA] Partition skipped: no village areas and no villager beds.");
                return TaskResult.Ok(new Dictionary<string, string>
                {
                    { "regions", "0" },
                    { "links", "0" },
                    { "reason", "no_beds_or_areas" }
                });
            }

            float originX = minX;
            float originZ = minZ;
            int cellCountX = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / HnaRegionGraph.CellSize));
            int cellCountZ = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / HnaRegionGraph.CellSize));

            var (regionIds, cellHeights) = FloodFillFromBeds(originX, originZ, cellCountX, cellCountZ, beds);

            var links = new List<HnaLink>();
            CollectDoorLinks(originX, originZ, minX, minZ, maxX, maxZ, regionIds, cellHeights, links);
            CollectSlopeLinks(originX, originZ, regionIds, cellHeights, links);
            CollectVerticalLinksInCell(originX, originZ, regionIds, cellHeights, links);

            HnaRegionGraph.SetGraph(originX, originZ, regionIds, links, cellHeights);

            string regionCentersStr = BuildRegionCentersString(originX, originZ, regionIds, cellHeights);
            string linksStr = BuildLinksSummaryString(links);
            ValheimVillages.PathTelemetry.LogHnaGraph(regionIds.Count, links.Count, minX, minZ, maxX, maxZ, regionCentersStr, linksStr);

            // Place NavMeshLinks between floor islands if NavMesh has been baked
            if (VillageNavMeshBake.HasBakedInstance)
                NavMeshLinkPlacer.PlaceLinks();

            // Only refresh markers if they were already toggled on
            if (HnaDebugVisualization.MarkersEnabled)
                HnaDebugVisualization.SpawnTorchesAtRegions();

            Plugin.Log?.LogInfo(
                $"[HNA] Partition complete: {regionIds.Count} regions, {links.Count} links " +
                $"(bounds {minX:F0},{minZ:F0} to {maxX:F0},{maxZ:F0})");

            return TaskResult.Ok(new Dictionary<string, string>
            {
                { "regions", regionIds.Count.ToString() },
                { "links", links.Count.ToString() }
            });
        }

        private static string BuildRegionCentersString(float originX, float originZ, HashSet<string> regionIds,
            Dictionary<string, float> cellHeights)
        {
            var sb = new StringBuilder();
            foreach (string id in regionIds)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (!TryParseRegionId(id, out int ix, out int iz)) continue;
                float wx = originX + (ix + 0.5f) * HnaRegionGraph.CellSize;
                float wz = originZ + (iz + 0.5f) * HnaRegionGraph.CellSize;
                float wy = 0f;
                if (cellHeights != null && cellHeights.TryGetValue(id, out float bfsY))
                    wy = bfsY;
                else if (HnaRegionGraph.GetSolidHeightAt(wx, wz, out float h))
                    wy = h;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(id).Append(',').Append(wx.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(',').Append(wy.ToString("F1", CultureInfo.InvariantCulture))
                    .Append(',').Append(wz.ToString("F1", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string BuildLinksSummaryString(List<HnaLink> links)
        {
            var sb = new StringBuilder();
            foreach (var link in links)
            {
                if (sb.Length > 0) sb.Append(';');
                string typeStr = link.LinkType == HnaLinkType.Door ? "door" : link.LinkType == HnaLinkType.Slope ? "slope" : "stair";
                sb.Append(link.FromRegionId).Append(',').Append(link.ToRegionId).Append(',').Append(typeStr);
            }
            return sb.ToString();
        }

        private static bool TryParseRegionId(string id, out int ix, out int iz)
        {
            return TryParseRegionId(id, out ix, out iz, out _);
        }

        private static bool TryParseRegionId(string id, out int ix, out int iz, out int hBucket)
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

        private static int HeightBucket(float y) => Mathf.FloorToInt(y / HeightBucketSize);

        /// <summary>
        /// If the task includes an anchor position (from the requesting guard's bed),
        /// filter beds to only those within VillageClusterRadius. This prevents
        /// nearby but unconnected villages from merging into one HNA graph.
        /// </summary>
        private static List<Vector3> FilterBedsByAnchor(List<Vector3> allBeds, VillagerTask task)
        {
            if (allBeds == null || allBeds.Count == 0) return allBeds;

            if (task?.Attributes == null ||
                !task.Attributes.TryGetValue("anchor_x", out string axStr) ||
                !task.Attributes.TryGetValue("anchor_z", out string azStr))
                return allBeds;

            if (!float.TryParse(axStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float anchorX) ||
                !float.TryParse(azStr, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out float anchorZ))
                return allBeds;

            float r2 = VillageClusterRadius * VillageClusterRadius;
            var filtered = new List<Vector3>();
            foreach (var bed in allBeds)
            {
                float dx = bed.x - anchorX;
                float dz = bed.z - anchorZ;
                if (dx * dx + dz * dz <= r2)
                    filtered.Add(bed);
            }

            Plugin.Log?.LogInfo(
                $"[HNA] Filtered beds by anchor ({anchorX:F0},{anchorZ:F0}): " +
                $"{filtered.Count}/{allBeds.Count} within {VillageClusterRadius}m");

            return filtered.Count > 0 ? filtered : allBeds;
        }

        private static string CellKey(int ix, int iz, int hBucket) => $"{ix}_{iz}_h{hBucket}";

        private static void CellFromWorld(float originX, float originZ, float worldX, float worldZ,
            out int ix, out int iz)
        {
            ix = Mathf.FloorToInt((worldX - originX) / HnaRegionGraph.CellSize);
            iz = Mathf.FloorToInt((worldZ - originZ) / HnaRegionGraph.CellSize);
        }

        /// <summary>Returns true if there is at least PlayerClearanceHeight of open space above the surface.</summary>
        private static bool HasClearanceAbove(float wx, float surfaceY, float wz, int mask)
        {
            var origin = new Vector3(wx, surfaceY + 0.15f, wz);
            return !Physics.Raycast(origin, Vector3.up, PlayerClearanceHeight, mask, QueryTriggerInteraction.Ignore);
        }

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
        private static (HashSet<string> regionIds, Dictionary<string, float> cellHeights) FloodFillFromBeds(
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
            float r2 = FloodFillRadius * FloodFillRadius;

            // Step 1: Seed from beds – bed Y is known-good
            var queue = new Queue<string>();
            foreach (var bed in beds)
            {
                CellFromWorld(originX, originZ, bed.x, bed.z, out int bx, out int bz);
                if (bx < 0 || bx >= cellCountX || bz < 0 || bz >= cellCountZ)
                    continue;
                // Use hitY for the bucket so the seed matches BFS-expanded neighbors
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
                    // #region agent log
                    DebugLog("H4", "BFS_seed", "Seed cell created", $"{{\"id\":\"{id}\",\"bedY\":{bed.y:F2},\"hitY\":{hitY:F2},\"hBucket\":{HeightBucket(hitY)}}}");
                    // #endregion
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
                        if (!float.IsNaN(terrainY) && py - terrainY > MaxPieceAboveTerrain) continue; // skip roof/arch (piece high above terrain)
                        if (py > posA.y + MaxPieceSurfaceAbove) continue;
                        if (py < posA.y - RaycastMaxDown) continue;
                        if (!HasClearanceAbove(wx, py, wz, WallCheckMask)) continue;
                        candidates.Add(py);
                    }

                    // #region agent log
                    if (pHits.Length > 0)
                    {
                        var pySb = new StringBuilder("[");
                        for (int pi2 = 0; pi2 < pHits.Length; pi2++)
                        {
                            if (pi2 > 0) pySb.Append(",");
                            pySb.Append(pHits[pi2].point.y.ToString("F2"));
                        }
                        pySb.Append("]");
                        int pieceCandidate = candidates.Count - (float.IsNaN(terrainY) ? 0 : 1);
                        if (pieceCandidate > 0)
                            DebugLog("H1", "BFS_probe", "Piece surfaces found", $"{{\"nx\":{nx},\"nz\":{nz},\"posAy\":{posA.y:F2},\"terrainY\":{(float.IsNaN(terrainY) ? "null" : terrainY.ToString("F2"))},\"pieceHitYs\":{pySb},\"pieceCandidates\":{pieceCandidate}}}");
                    }
                    // #endregion
                    if (candidates.Count == 0) { rejNoHit++; continue; }

                    // --- Try each candidate surface ---
                    bool anyAccepted = false;
                    for (int ci = 0; ci < candidates.Count; ci++)
                    {
                        float cy = candidates[ci];
                        string toId = CellKey(nx, nz, HeightBucket(cy));
                        if (regionIds.Contains(toId)) continue;

                        float dy = Mathf.Abs(cy - posA.y);
                        float slope = Mathf.Atan2(dy, dxzLen) * Mathf.Rad2Deg;
                        if (slope > MaxWalkableSlopeDeg) { rejSlope++; continue; }

                        // Wall check at waist height of the lower surface so we always intersect walls (never cast above them)
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
                        anyAccepted = true;
                        bool isUpper = !float.IsNaN(terrainY) && Mathf.Abs(cy - terrainY) >= MinFloorSeparation;
                        if (isUpper) upperCells++;
                        // #region agent log
                        if (isUpper)
                            DebugLog("H5", "BFS_accept_upper", "Upper cell accepted", $"{{\"toId\":\"{toId}\",\"cy\":{cy:F2},\"terrainY\":{(float.IsNaN(terrainY) ? "null" : terrainY.ToString("F2"))},\"parentId\":\"{fromId}\",\"posAy\":{posA.y:F2},\"slope\":{slope:F1}}}");
                        // #endregion
                    }
                    if (!anyAccepted && candidates.Count > 0) rejWall++; // all candidates wall-rejected
                }
            }

            // #region agent log
            DebugLog("H4", "BFS_summary", "Flood-fill complete",
                $"{{\"totalCells\":{regionIds.Count},\"upperCells\":{upperCells},\"expanded\":{expanded},\"rejNoHit\":{rejNoHit},\"rejSlope\":{rejSlope},\"rejWall\":{rejWall},\"rejDoor\":{rejDoor},\"doorBarriers\":{doorBarriers.Count}}}");
            // #endregion
            Plugin.Log?.LogInfo($"[HNA] Raycast flood-fill: {beds.Count} beds → {regionIds.Count} reachable cells " +
                $"({expanded} edges, upper={upperCells}, rej: noHit={rejNoHit} slope={rejSlope} wall={rejWall} door={rejDoor}, " +
                $"doorBarriers={doorBarriers.Count})");

            return (regionIds, cellHeights);
        }

        private static string RegionIdAt(float originX, float originZ, float worldX, float worldY, float worldZ,
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

        private static void CollectDoorLinks(float originX, float originZ, float minX, float minZ, float maxX, float maxZ,
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

        /// <summary>Max slope (degrees) considered walkable; ~26° is typical for player climb.</summary>
        private const float MaxWalkableSlopeDeg = 26f;
        /// <summary>Height tolerance (m) when validating slope path samples; allows stepped (stair) geometry.</summary>
        private const float SlopePathHeightTolerance = 1.5f;
        private const int SlopePathSamples = 5;

        /// <summary>
        /// Link adjacent regions. Uses a spatial lookup to find all cells at adjacent XZ,
        /// including cells at different floor levels (multi-floor support).
        /// </summary>
        private static void CollectSlopeLinks(float originX, float originZ, HashSet<string> regionIds,
            Dictionary<string, float> cellHeights, List<HnaLink> links)
        {
            if (regionIds == null) return;
            // Build position lookup per region and spatial bucket (ix,iz) → cell IDs
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
                // Check all 8 XZ neighbors — each may have multiple floor-level cells
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

        /// <summary>Sample offset from cell center (m) when detecting multiple height levels; must stay within cell.</summary>
        private static float VerticalSampleOffset => HnaRegionGraph.CellSize * 0.4f;
        /// <summary>Min vertical spread (m) in a cell to add a same-cell stair link (platform/stair).</summary>
        private const float MinVerticalSpread = 0.5f;

        /// <summary>
        /// Add stair links between cells at the same XZ but different floor levels.
        /// The multi-floor BFS creates separate cells (with height-bucketed IDs) for each
        /// walkable surface at a given XZ position; this links them vertically.
        /// </summary>
        private static void CollectVerticalLinksInCell(float originX, float originZ, HashSet<string> regionIds,
            Dictionary<string, float> cellHeights, List<HnaLink> links)
        {
            if (regionIds == null || cellHeights == null) return;
            // Group cells by (ix, iz) to find same-XZ multi-floor pairs
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
