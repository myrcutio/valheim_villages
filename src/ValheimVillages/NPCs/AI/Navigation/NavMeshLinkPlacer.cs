using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Places NavMeshLinks between disconnected NavMesh islands.
    /// Two strategies:
    ///   1. HNA-based: uses stair/door link positions from the HNA region graph.
    ///   2. Island probe: directly probes the baked NavMesh for disconnected floor
    ///      levels and bridges them at the nearest-edge points.
    /// Call after NavMesh bake (and optionally HNA partition) are complete.
    /// </summary>
    public static class NavMeshLinkPlacer
    {
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
        /// <summary>Minimum Y gap between NavMesh hits to consider them different floors.</summary>
        private const float FloorSeparation = 3f;

        // #region agent log
        private const string DebugLogPath = "/home/benny/Projects/valheim_villages/.cursor/debug.log";
        private static void DebugLog(string hypothesisId, string location, string message, string data)
        {
            try
            {
                long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line = $"{{\"hypothesisId\":\"{hypothesisId}\",\"location\":\"{location}\",\"message\":\"{message}\",\"data\":{data},\"timestamp\":{ts}}}\n";
                System.IO.File.AppendAllText(DebugLogPath, line);
            }
            catch { }
        }
        // #endregion

        /// <summary>
        /// Place NavMeshLinks between disconnected NavMesh islands.
        /// First tries HNA-based links (stair/door), then falls back to direct
        /// NavMesh probing to bridge any remaining disconnected floor levels.
        /// Safe to call multiple times (removes previous links first).
        /// </summary>
        public static void PlaceLinks()
        {
            RemoveAllLinks();

            int agentTypeID = VillageNavMeshBake.ResolveValheimHumanoidAgentTypeID();
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = agentTypeID,
                areaMask = NavMesh.AllAreas
            };

            int hnaPlaced = PlaceHnaLinks(agentTypeID, filter);
            int islandPlaced = BridgeDisconnectedIslands(agentTypeID, filter);

            Plugin.Log?.LogInfo(
                $"[NavMeshLink] Total links: {s_linkInstances.Count} " +
                $"(HNA={hnaPlaced}, island-bridge={islandPlaced})");
        }

        /// <summary>
        /// Strategy 1: Create links from HNA stair/door positions.
        /// </summary>
        private static int PlaceHnaLinks(int agentTypeID, NavMeshQueryFilter filter)
        {
            if (!HnaRegionGraph.IsAvailable) return 0;

            var hnaLinks = HnaRegionGraph.GetAllLinks();
            if (hnaLinks == null || hnaLinks.Count == 0) return 0;

            int placed = 0;
            int skippedConnected = 0;
            int skippedNoSample = 0;

            foreach (var link in hnaLinks)
            {
                if (link.LinkType != HnaLinkType.Stair && link.LinkType != HnaLinkType.Door)
                    continue;

                if (!NavMesh.SamplePosition(link.PositionStart, out NavMeshHit startHit, 5f, filter))
                {
                    skippedNoSample++;
                    continue;
                }

                if (!NavMesh.SamplePosition(link.PositionEnd, out NavMeshHit endHit, 5f, filter))
                {
                    skippedNoSample++;
                    continue;
                }

                var testPath = new NavMeshPath();
                NavMesh.CalculatePath(startHit.position, endHit.position, filter, testPath);
                if (testPath.status == NavMeshPathStatus.PathComplete)
                {
                    skippedConnected++;
                    continue;
                }

                if (TryAddLink(startHit.position, endHit.position, agentTypeID))
                    placed++;
            }

            Plugin.Log?.LogInfo(
                $"[NavMeshLink] HNA pass: {placed} placed " +
                $"({skippedConnected} connected, {skippedNoSample} no sample) " +
                $"from {hnaLinks.Count} HNA links");

            return placed;
        }

        /// <summary>
        /// Strategy 2: Probe the baked NavMesh at a grid of positions to find
        /// disconnected floor islands and bridge them at their nearest edges.
        /// </summary>
        private static int BridgeDisconnectedIslands(int agentTypeID, NavMeshQueryFilter filter)
        {
            var beds = VillagerAIManager.GetAllBedPositions();
            if (beds == null || beds.Count == 0) return 0;

            // Use average of all bed positions as center for broader coverage
            float cx = 0f, cz = 0f, baseY = 0f;
            foreach (var bed in beds) { cx += bed.x; cz += bed.z; baseY += bed.y; }
            cx /= beds.Count; cz /= beds.Count; baseY /= beds.Count;

            // Probe NavMesh at a grid of XZ positions at multiple heights.
            // Use a larger sample radius (4m) to catch small upper-floor NavMesh areas.
            const float sampleRadius = 4f;
            var allHits = new List<Vector3>();
            var seen = new HashSet<string>();

            // #region agent log
            DebugLog("H_ISLAND", "NavMeshLinkPlacer:ProbeStart",
                "Starting island probe",
                $"{{\"bedCenter\":\"{cx:F1},{baseY:F1},{cz:F1}\",\"beds\":{beds.Count}," +
                $"\"probeRadius\":{ProbeRadius},\"probeStep\":{ProbeStep}," +
                $"\"yRange\":\"{(baseY - ProbeYBelow):F1} to {(baseY + ProbeYAbove):F1}\"," +
                $"\"sampleRadius\":{sampleRadius}}}");
            // #endregion

            int totalProbes = 0;
            int totalHits = 0;
            int upperHits = 0;

            for (float px = cx - ProbeRadius; px <= cx + ProbeRadius; px += ProbeStep)
            for (float pz = cz - ProbeRadius; pz <= cz + ProbeRadius; pz += ProbeStep)
            for (float py = baseY - ProbeYBelow; py <= baseY + ProbeYAbove; py += ProbeYStep)
            {
                totalProbes++;
                var probe = new Vector3(px, py, pz);
                if (NavMesh.SamplePosition(probe, out NavMeshHit hit, sampleRadius, filter))
                {
                    totalHits++;
                    if (hit.position.y > baseY + FloorSeparation) upperHits++;
                    // Deduplicate by rounding to 0.5m grid
                    int kx = Mathf.RoundToInt(hit.position.x * 2);
                    int ky = Mathf.RoundToInt(hit.position.y * 2);
                    int kz = Mathf.RoundToInt(hit.position.z * 2);
                    string key = $"{kx}_{ky}_{kz}";
                    if (seen.Add(key))
                        allHits.Add(hit.position);
                }
            }

            // #region agent log
            DebugLog("H_ISLAND", "NavMeshLinkPlacer:ProbeComplete",
                "Probe sweep complete",
                $"{{\"totalProbes\":{totalProbes},\"totalHits\":{totalHits}," +
                $"\"upperFloorHits\":{upperHits},\"uniquePositions\":{allHits.Count}}}");
            // #endregion

            if (allHits.Count < 2) return 0;

            // Group by floor level (Y-clusters separated by FloorSeparation)
            var floors = GroupByFloorLevel(allHits);
            // #region agent log
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("[");
                for (int f = 0; f < floors.Count; f++)
                {
                    if (f > 0) sb.Append(",");
                    float avgY = 0f;
                    foreach (var p in floors[f]) avgY += p.y;
                    avgY /= floors[f].Count;
                    sb.Append($"{{\"floorIdx\":{f},\"avgY\":{avgY:F1},\"points\":{floors[f].Count}}}");
                }
                sb.Append("]");
                DebugLog("H_ISLAND", "NavMeshLinkPlacer:BridgeIslands",
                    "Island probe results",
                    $"{{\"totalHits\":{allHits.Count},\"floors\":{floors.Count},\"floorDetails\":{sb}}}");
            }
            // #endregion

            if (floors.Count < 2) return 0;

            // For each pair of floors that aren't path-connected, find closest
            // pair and add a link
            int placed = 0;
            for (int i = 0; i < floors.Count; i++)
            for (int j = i + 1; j < floors.Count; j++)
            {
                // Find the closest pair of NavMesh points across the two floors
                float bestDist = float.MaxValue;
                Vector3 bestA = default, bestB = default;
                foreach (var a in floors[i])
                foreach (var b in floors[j])
                {
                    float d = Vector3.Distance(a, b);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestA = a;
                        bestB = b;
                    }
                }

                if (bestDist >= float.MaxValue) continue;

                // Check if already connected (by HNA links or existing connection)
                var testPath = new NavMeshPath();
                NavMesh.CalculatePath(bestA, bestB, filter, testPath);
                if (testPath.status == NavMeshPathStatus.PathComplete) continue;

                // #region agent log
                DebugLog("H_ISLAND", "NavMeshLinkPlacer:AddingBridge",
                    "Adding cross-floor bridge",
                    $"{{\"floorA\":{i},\"floorB\":{j}," +
                    $"\"startPos\":\"{bestA.x:F1},{bestA.y:F1},{bestA.z:F1}\"," +
                    $"\"endPos\":\"{bestB.x:F1},{bestB.y:F1},{bestB.z:F1}\"," +
                    $"\"dist3D\":{bestDist:F1}," +
                    $"\"calcStatus\":\"{testPath.status}\"}}");
                // #endregion

                if (TryAddLink(bestA, bestB, agentTypeID))
                    placed++;
            }

            Plugin.Log?.LogInfo(
                $"[NavMeshLink] Island bridge pass: probed {allHits.Count} points, " +
                $"{floors.Count} floor levels, {placed} bridges added");

            return placed;
        }

        /// <summary>
        /// Group NavMesh positions into floor levels by finding the largest Y gaps
        /// in the sorted position list. Splits occur at gaps exceeding FloorSeparation,
        /// starting from the largest gap. This avoids the ratcheting problem where
        /// intermediate stair/ramp points prevent detection of distinct floors.
        /// </summary>
        private static List<List<Vector3>> GroupByFloorLevel(List<Vector3> points)
        {
            if (points.Count < 2) return new List<List<Vector3>> { points };

            // Sort by Y
            points.Sort((a, b) => a.y.CompareTo(b.y));

            // Find all gaps between consecutive sorted Y values that exceed FloorSeparation
            var splitIndices = new List<int>();
            for (int i = 1; i < points.Count; i++)
            {
                float gap = points[i].y - points[i - 1].y;
                if (gap > FloorSeparation)
                    splitIndices.Add(i);
            }

            if (splitIndices.Count == 0)
                return new List<List<Vector3>> { points };

            // Split into floors at each gap
            var floors = new List<List<Vector3>>();
            int start = 0;
            foreach (int splitAt in splitIndices)
            {
                floors.Add(points.GetRange(start, splitAt - start));
                start = splitAt;
            }
            floors.Add(points.GetRange(start, points.Count - start));

            return floors;
        }

        /// <summary>Create a bidirectional NavMeshLink between two world positions.</summary>
        private static bool TryAddLink(Vector3 start, Vector3 end, int agentTypeID)
        {
            var linkData = new NavMeshLinkData
            {
                startPosition = start,
                endPosition = end,
                width = 1f,
                bidirectional = true,
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
        /// Remove all previously placed NavMeshLinks.
        /// Call before rebake or on world unload.
        /// </summary>
        public static void RemoveAllLinks()
        {
            foreach (var inst in s_linkInstances)
            {
                if (NavMesh.IsLinkValid(inst))
                    NavMesh.RemoveLink(inst);
            }
            s_linkInstances.Clear();
        }
    }
}
