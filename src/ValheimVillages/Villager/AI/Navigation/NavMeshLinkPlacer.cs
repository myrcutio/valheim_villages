using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Algorithms;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    /// Places NavMeshLinks between disconnected NavMesh islands.
    /// Probes the baked NavMesh at a grid of positions, uses CalculatePath to
    /// discover which positions are path-connected, groups them into islands
    /// via Union-Find, and bridges the closest points of each island pair.
    /// Call after NavMesh bake is complete.
    /// </summary>
    public static class NavMeshLinkPlacer
    {
        // #region agent log
        private static void DL(string hId, string loc, string data)
        {
            try { System.IO.File.AppendAllText("/home/benny/Projects/valheim_villages/.cursor/debug.log",
                $"{{\"hypothesisId\":\"{hId}\",\"location\":\"NavMeshLinkPlacer.cs:{loc}\",\"data\":{data},\"timestamp\":{System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}\n"); } catch { }
        }
        // #endregion

        private static readonly List<NavMeshLinkInstance> s_linkInstances =
            new List<NavMeshLinkInstance>();

        /// <summary>True if links have been placed at least once.</summary>
        public static bool HasLinks => s_linkInstances.Count > 0;

        /// <summary>Probe grid step size in meters for island detection.</summary>
        private const float ProbeStep = 3f;
        /// <summary>Radius around bed center to probe for NavMesh islands.</summary>
        private const float ProbeRadius = 20f;
        /// <summary>Y probe range below bed height.</summary>
        private const float ProbeYBelow = 3f;
        /// <summary>Y probe range above bed height.</summary>
        private const float ProbeYAbove = 20f;
        /// <summary>Y probe step size.</summary>
        private const float ProbeYStep = 1.5f;

        /// <summary>Maximum 3D distance between two probe positions to test connectivity.</summary>
        private const float MaxNeighborDist = 6f;

        /// <summary>Maximum 3D distance between two island points for a link to be placed.</summary>
        private const float MaxLinkDistance = 10f;

        /// <summary>
        /// No-op: link placement disabled; experiments use built-in MoveTo/FindPath only.
        /// </summary>
        public static void PlaceLinks()
        {
        }

        /// <summary>
        /// Probe the baked NavMesh at a grid of positions, discover disconnected
        /// islands via CalculatePath connectivity, and bridge them.
        /// </summary>
        private static int BridgeDisconnectedIslands(int agentTypeID, NavMeshQueryFilter filter)
        {
            var beds = ValheimVillages.Villager.AI.VillagerAIManager.GetAllBedPositions();
            if (beds == null || beds.Count == 0) return 0;

            float cx = 0f, cz = 0f, baseY = 0f;
            foreach (var bed in beds) { cx += bed.x; cz += bed.z; baseY += bed.y; }
            cx /= beds.Count; cz /= beds.Count; baseY /= beds.Count;

            const float sampleRadius = 4f;
            var allHits = new List<Vector3>();
            var seen = new HashSet<string>();

            for (float px = cx - ProbeRadius; px <= cx + ProbeRadius; px += ProbeStep)
            for (float pz = cz - ProbeRadius; pz <= cz + ProbeRadius; pz += ProbeStep)
            for (float py = baseY - ProbeYBelow; py <= baseY + ProbeYAbove; py += ProbeYStep)
            {
                var probe = new Vector3(px, py, pz);
                if (NavMesh.SamplePosition(probe, out NavMeshHit hit, sampleRadius, filter))
                {
                    int kx = Mathf.RoundToInt(hit.position.x * 2);
                    int ky = Mathf.RoundToInt(hit.position.y * 2);
                    int kz = Mathf.RoundToInt(hit.position.z * 2);
                    string key = $"{kx}_{ky}_{kz}";
                    if (seen.Add(key))
                        allHits.Add(hit.position);
                }
            }

            if (allHits.Count < 2) return 0;

            float maxNeighborDist2 = MaxNeighborDist * MaxNeighborDist;
            var cachedFilter = filter;

            var islands = ConnectedIslands.FindIslands(
                allHits.Count,
                isConnected: (a, b) =>
                {
                    var path = new NavMeshPath();
                    NavMesh.CalculatePath(allHits[a], allHits[b], cachedFilter, path);
                    return IsPathPhysicallyWalkable(path);
                },
                shouldTest: (a, b) =>
                {
                    float dx = allHits[a].x - allHits[b].x;
                    float dy = allHits[a].y - allHits[b].y;
                    float dz = allHits[a].z - allHits[b].z;
                    return dx * dx + dy * dy + dz * dz <= maxNeighborDist2;
                });

            Plugin.Log?.LogInfo(
                $"[NavMeshLink] Island detection: {allHits.Count} probe points, " +
                $"{islands.Count} islands found");

            if (islands.Count < 2) return 0;

            int placed = 0;
            for (int i = 0; i < islands.Count; i++)
            for (int j = i + 1; j < islands.Count; j++)
            {
                float bestDist = float.MaxValue;
                Vector3 bestA = default, bestB = default;

                foreach (int ai in islands[i])
                foreach (int bi in islands[j])
                {
                    float d = Vector3.Distance(allHits[ai], allHits[bi]);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestA = allHits[ai];
                        bestB = allHits[bi];
                    }
                }

                if (bestDist > MaxLinkDistance)
                {
                    Plugin.Log?.LogDebug(
                        $"[NavMeshLink] Skipping island pair {i}-{j}: " +
                        $"closest distance {bestDist:F1}m exceeds MaxLinkDistance {MaxLinkDistance}m");
                    continue;
                }

                var testPath = new NavMeshPath();
                NavMesh.CalculatePath(bestA, bestB, filter, testPath);
                if (IsPathPhysicallyWalkable(testPath))
                    continue;

                Plugin.Log?.LogInfo(
                    $"[NavMeshLink] Bridging islands {i} ({islands[i].Count} pts) " +
                    $"and {j} ({islands[j].Count} pts) at distance {bestDist:F1}m");

                if (TryAddLink(bestA, bestB, agentTypeID))
                    placed++;
            }

            return placed;
        }

        private static bool IsPathPhysicallyWalkable(NavMeshPath path)
        {
            if (path.status != NavMeshPathStatus.PathComplete) return false;
            if (path.corners == null || path.corners.Length < 2) return true;
            float maxSlope = VillageNavMeshBake.DefaultAgentSlope;
            float maxClimb = VillageNavMeshBake.DefaultAgentClimb;
            for (int k = 1; k < path.corners.Length; k++)
            {
                float dy = Mathf.Abs(path.corners[k].y - path.corners[k - 1].y);
                float dxh = path.corners[k].x - path.corners[k - 1].x;
                float dzh = path.corners[k].z - path.corners[k - 1].z;
                float horiz = Mathf.Sqrt(dxh * dxh + dzh * dzh);
                if (horiz > 0.01f)
                {
                    float slope = Mathf.Atan2(dy, horiz) * Mathf.Rad2Deg;
                    if (slope > maxSlope) return false;
                }
                else if (dy > maxClimb)
                    return false;
            }
            return true;
        }

        private static bool TryAddLink(Vector3 start, Vector3 end, int agentTypeID)
        {
            var linkData = new NavMeshLinkData
            {
                startPosition = start,
                endPosition = end,
                width = 1f,
                bidirectional = true,
                area = VillageNavMeshBake.OverlayAreaIndex,
                agentTypeID = agentTypeID
            };

            var instance = NavMesh.AddLink(linkData);
            if (NavMesh.IsLinkValid(instance))
            {
                s_linkInstances.Add(instance);
                return true;
            }
            return false;
        }

        /// <summary>
        /// No-op: no links are placed when baking is disabled.
        /// </summary>
        public static void RemoveAllLinks()
        {
        }
    }
}
