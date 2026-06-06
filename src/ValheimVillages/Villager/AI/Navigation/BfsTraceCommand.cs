using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Dev command: highlight the shortest path back to a bed seed from a
    ///     target region, via the cross-kind BFS adjacency graph stored in
    ///     <see cref="BfsAdjacencyStore" />. Useful for diagnosing why a region
    ///     the user thinks shouldn't be reachable IS reachable (some piece
    ///     chain is silently bridging it).
    ///     Without args: uses the region at the player's current position.
    ///     With one arg: treats it as a region ID (e.g., "t37" or "p120").
    ///     "off" / "clear" / "0": clears the current highlight.
    /// </summary>
    internal static class BfsTraceCommand
    {
        [DevCommand("Highlight BFS path from region (or player) to bed seed. Args: [regionId | off]",
            Name = "vv_bfs_trace")]
        public static void Trace(Terminal.ConsoleEventArgs args)
        {
            string arg = null;
            if (args?.Args != null && args.Args.Length >= 2)
                arg = args.Args[1];

            if (!string.IsNullOrEmpty(arg) &&
                (arg.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                 arg.Equals("clear", StringComparison.OrdinalIgnoreCase) ||
                 arg == "0"))
            {
                PathDebugRenderer.HighlightedRegions.Clear();
                Console.instance?.Print("[vv_bfs_trace] highlight cleared");
                return;
            }

            var targetRegionId = arg;
            if (string.IsNullOrEmpty(targetRegionId))
            {
                var player = Player.m_localPlayer;
                if (player == null)
                {
                    Console.instance?.Print("[vv_bfs_trace] no player and no region arg");
                    return;
                }

                var graph = Villages.Entity.VillageRegistry.GraphAt(player.transform.position);
                if (graph == null)
                {
                    Console.instance?.Print("[vv_bfs_trace] no RegionGraph available");
                    return;
                }

                targetRegionId = graph.PointToRegionId(player.transform.position);
                if (string.IsNullOrEmpty(targetRegionId))
                {
                    Console.instance?.Print(
                        "[vv_bfs_trace] PointToRegionId unresolved at player " +
                        $"({player.transform.position.x:F1},{player.transform.position.y:F1},{player.transform.position.z:F1})");
                    return;
                }
            }

            var path = BfsAdjacencyStore.PathToSeed(targetRegionId);
            if (path == null)
            {
                Console.instance?.Print(
                    $"[vv_bfs_trace] no BFS path from {targetRegionId} back to any seed " +
                    $"(adjacency size={BfsAdjacencyStore.Adjacency.Count}, " +
                    $"seeds={BfsAdjacencyStore.Seeds.Count})");
                return;
            }

            PathDebugRenderer.HighlightedRegions.Clear();
            foreach (var rid in path)
                PathDebugRenderer.HighlightedRegions.Add(rid);

            var sb = new StringBuilder();
            sb.Append("[vv_bfs_trace] ").Append(targetRegionId).Append(" -> seed: ");
            sb.Append(path.Count).Append(" hops [");
            for (var i = 0; i < path.Count; i++)
            {
                if (i > 0) sb.Append(" -> ");
                sb.Append(path[i]);
            }

            sb.Append("]");
            Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());

            // Per-hop centroid + AABB so we can spatial-locate the bridges.
            // Tris from the cached pool give per-region bounds + center.
            var cached = RegionBuilder.CachedTriangles;
            if (cached == null) return;
            var perHopBounds = new Dictionary<string, Bounds>(path.Count);
            foreach (var ct in cached)
            {
                if (!Enumerable.Contains(path, ct.RegionId)) continue;
                if (perHopBounds.TryGetValue(ct.RegionId, out var b))
                {
                    b.Encapsulate(ct.V0);
                    b.Encapsulate(ct.V1);
                    b.Encapsulate(ct.V2);
                    perHopBounds[ct.RegionId] = b;
                }
                else
                {
                    var nb = new Bounds(ct.V0, Vector3.zero);
                    nb.Encapsulate(ct.V1);
                    nb.Encapsulate(ct.V2);
                    perHopBounds[ct.RegionId] = nb;
                }
            }

            for (var hi = 0; hi < path.Count; hi++)
            {
                var rid = path[hi];
                // Edge label describing how this hop connects to the previous
                // one. First hop (the target region itself) has no incoming
                // edge — label is empty there. Reads BfsAdjacencyStore.EdgeMeta
                // populated at partition time by RecordCrossKindAdjacency.
                var edgeLabel = "";
                if (hi > 0)
                {
                    var prev = path[hi - 1];
                    var ekey = BfsAdjacencyStore.EdgeKey(prev, rid);
                    if (BfsAdjacencyStore.EdgeMeta.TryGetValue(ekey, out var meta))
                        edgeLabel = "  via [" + FormatEdgeMeta(meta) + "]";
                    else
                        edgeLabel = "  via [?]"; // edge exists in adjacency but missing meta
                }

                if (!perHopBounds.TryGetValue(rid, out var b))
                {
                    Plugin.Log?.LogInfo($"[vv_bfs_trace]   {rid}: (no cached tris){edgeLabel}");
                    continue;
                }

                Vector3 c = b.center, sz = b.size;
                Plugin.Log?.LogInfo(
                    $"[vv_bfs_trace]   {rid}: centroid=({c.x:F1},{c.y:F1},{c.z:F1}) " +
                    $"size=({sz.x:F1}x{sz.y:F1}x{sz.z:F1}) " +
                    $"y=[{b.min.y:F2}..{b.max.y:F2}]{edgeLabel}");
            }
        }

        /// <summary>
        ///     Formats a <see cref="BfsEdgeMeta" /> into a compact human-readable
        ///     label for the per-hop trace log. Multi-kind edges OR together
        ///     (e.g. "InPassEdge | CrossVert @ (-2264.5,37.6,1284.5)").
        /// </summary>
        private static string FormatEdgeMeta(BfsEdgeMeta meta)
        {
            var parts = new List<string>(3);
            if ((meta.Kinds & BfsEdgeKind.InPassEdge) != 0)
                parts.Add("InPassEdge");
            if ((meta.Kinds & BfsEdgeKind.CrossVert) != 0)
            {
                if (meta.RepresentativePos.HasValue)
                {
                    var p = meta.RepresentativePos.Value;
                    parts.Add($"CrossVert @ ({p.x:F1},{p.y:F1},{p.z:F1})");
                }
                else
                {
                    parts.Add("CrossVert");
                }
            }

            if ((meta.Kinds & BfsEdgeKind.CrossProx) != 0)
            {
                if (meta.RepresentativePos.HasValue)
                {
                    var p = meta.RepresentativePos.Value;
                    parts.Add($"CrossProx @ ({p.x:F1},{p.y:F1},{p.z:F1}) d={meta.ProxMinDist:F2}m");
                }
                else
                {
                    parts.Add($"CrossProx d={meta.ProxMinDist:F2}m");
                }
            }

            if ((meta.Kinds & BfsEdgeKind.Pass3Step) != 0)
                parts.Add("Pass3Step");

            return parts.Count == 0 ? "None" : string.Join(" | ", parts);
        }
    }
}