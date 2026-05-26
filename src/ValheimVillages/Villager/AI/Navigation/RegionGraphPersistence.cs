using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Serialization and restoration of the region graph for ZDO persistence.
    ///     Format: "v3;regionId:cx,cy,cz,K;...||fromId,toId,T,sx,sy,sz,ex,ey,ez;..."
    ///     where K is the SurfaceKind char (T = Terrain, P = Piece). Legacy v1/v2
    ///     formats are no longer read; <see cref="Restore" /> returns false for any
    ///     non-v3 payload so the caller can wipe the ZDO entry and trigger a fresh
    ///     partition build (no default-fill — the kind tag is load-bearing).
    /// </summary>
    internal static class RegionGraphPersistence
    {
        private const string LinkSectionDelimiter = "||";
        private const string V3Header = "v3";
        internal static Action<string> LogAction;

        private static char KindToChar(SurfaceKind k)
        {
            return k == SurfaceKind.Terrain ? 'T' : 'P';
        }

        private static SurfaceKind CharToKind(char c)
        {
            return c == 'T' ? SurfaceKind.Terrain : SurfaceKind.Piece;
        }

        private static char LinkTypeToChar(RegionLinkType t)
        {
            switch (t)
            {
                case RegionLinkType.Door: return 'D';
                case RegionLinkType.Stair: return 'S';
                case RegionLinkType.Slope: return 'L';
                default: return 'L';
            }
        }

        private static RegionLinkType CharToLinkType(char c)
        {
            switch (c)
            {
                case 'D': return RegionLinkType.Door;
                case 'S': return RegionLinkType.Stair;
                default: return RegionLinkType.Slope;
            }
        }

        internal static string Serialize(RegionGraph graph)
        {
            if (graph == null || !graph.IsAvailable) return "";
            return SerializeV3(graph);
        }

        private static string SerializeV3(RegionGraph graph)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(V3Header);

            foreach (var id in graph.GetRegionIds())
            {
                sb.Append(';').Append(id).Append(':');
                if (graph.GetCellWorldXZ(id, out var wx, out var wz) &&
                    graph.TryGetCellHeight(id, out var h))
                    sb.Append(wx.ToString("F2", inv)).Append(',')
                        .Append(h.ToString("F2", inv)).Append(',')
                        .Append(wz.ToString("F2", inv)).Append(',')
                        .Append(KindToChar(graph.GetRegionKind(id)));
                else sb.Append('?');
            }

            AppendLinks(sb, graph);
            return sb.ToString();
        }

        private static void AppendLinks(StringBuilder sb, RegionGraph graph)
        {
            var inv = CultureInfo.InvariantCulture;
            var links = graph.GetAllLinks();
            if (links == null || links.Count == 0) return;

            sb.Append(LinkSectionDelimiter);
            for (var i = 0; i < links.Count; i++)
            {
                if (i > 0) sb.Append(';');
                var lnk = links[i];
                sb.Append(lnk.FromRegionId).Append(',')
                    .Append(lnk.ToRegionId).Append(',')
                    .Append(LinkTypeToChar(lnk.LinkType)).Append(',')
                    .Append(lnk.PositionStart.x.ToString("F2", inv)).Append(',')
                    .Append(lnk.PositionStart.y.ToString("F2", inv)).Append(',')
                    .Append(lnk.PositionStart.z.ToString("F2", inv)).Append(',')
                    .Append(lnk.PositionEnd.x.ToString("F2", inv)).Append(',')
                    .Append(lnk.PositionEnd.y.ToString("F2", inv)).Append(',')
                    .Append(lnk.PositionEnd.z.ToString("F2", inv));
            }
        }

        internal static bool Restore(RegionGraph graph, string data)
        {
            if (graph == null || string.IsNullOrEmpty(data)) return false;

            // v1 and v2 payloads are no longer supported. v3 adds per-region
            // SurfaceKind, which is load-bearing for the two-pass partition
            // pipeline — default-filling would silently mis-tag every legacy
            // region. Reject so the caller wipes the ZDO entry and re-triggers
            // an hna_partition build.
            if (!data.StartsWith(V3Header))
            {
                LogInfo("[Region] Legacy non-v3 graph in ZDO; purging (caller will wipe + rebuild)");
                return false;
            }

            return RestoreV3(graph, data);
        }

        private static bool RestoreV3(RegionGraph graph, string data)
        {
            var regionSection = data;
            string linkSection = null;
            var linkDelim = data.IndexOf(LinkSectionDelimiter);
            if (linkDelim >= 0)
            {
                regionSection = data.Substring(0, linkDelim);
                linkSection = data.Substring(linkDelim + LinkSectionDelimiter.Length);
            }

            var segments = regionSection.Split(';');
            if (segments.Length < 2) return false;

            var inv = CultureInfo.InvariantCulture;
            var regionIds = new HashSet<string>();
            var centroids = new Dictionary<string, Vector3>();
            var kinds = new Dictionary<string, SurfaceKind>();

            for (var i = 1; i < segments.Length; i++)
            {
                var seg = segments[i];
                var colon = seg.IndexOf(':');
                if (colon <= 0) continue;

                var regionId = seg.Substring(0, colon);
                var rest = seg.Substring(colon + 1);
                regionIds.Add(regionId);

                if (rest == "?") continue;
                var parts = rest.Split(',');
                if (parts.Length >= 4 &&
                    float.TryParse(parts[0], NumberStyles.Float, inv, out var cx) &&
                    float.TryParse(parts[1], NumberStyles.Float, inv, out var cy) &&
                    float.TryParse(parts[2], NumberStyles.Float, inv, out var cz))
                {
                    centroids[regionId] = new Vector3(cx, cy, cz);
                    if (parts[3].Length > 0)
                        kinds[regionId] = CharToKind(parts[3][0]);
                }
            }

            if (regionIds.Count == 0) return false;

            var links = DeserializeLinks(linkSection);

            // Build a minimal lookup grid from centroids so PointToRegionId works
            var lookupGrid = new Dictionary<long, string>();
            foreach (var kv in centroids)
            {
                var gx = Mathf.FloorToInt(kv.Value.x / RegionGraph.LookupCellSize);
                var gz = Mathf.FloorToInt(kv.Value.z / RegionGraph.LookupCellSize);
                var hb = RegionGraph.HeightBucket(kv.Value.y);
                // Cover a small area around each centroid
                for (var dx = -1; dx <= 1; dx++)
                for (var dz = -1; dz <= 1; dz++)
                {
                    var key = RegionGraph.PackLookup(gx + dx, gz + dz, hb);
                    lookupGrid[key] = kv.Key;
                }
            }

            graph.SetGraph(regionIds, links, centroids, lookupGrid,
                null, kinds);
            LogInfo(
                $"[Region] Restored v3 graph from ZDO: {regionIds.Count} regions, {links.Count} links, {kinds.Count} kinded");
            return true;
        }

        private static void LogInfo(string message)
        {
            LogAction?.Invoke(message);
        }

        private static List<RegionLink> DeserializeLinks(string linkSection)
        {
            var links = new List<RegionLink>();
            if (string.IsNullOrEmpty(linkSection)) return links;

            var inv = CultureInfo.InvariantCulture;
            var entries = linkSection.Split(';');
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var f = entry.Split(',');
                if (f.Length < 9) continue;

                string fromId = f[0], toId = f[1];
                if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId)) continue;
                if (f[2].Length < 1) continue;

                var linkType = CharToLinkType(f[2][0]);
                if (!float.TryParse(f[3], NumberStyles.Float, inv, out var sx)) continue;
                if (!float.TryParse(f[4], NumberStyles.Float, inv, out var sy)) continue;
                if (!float.TryParse(f[5], NumberStyles.Float, inv, out var sz)) continue;
                if (!float.TryParse(f[6], NumberStyles.Float, inv, out var ex)) continue;
                if (!float.TryParse(f[7], NumberStyles.Float, inv, out var ey)) continue;
                if (!float.TryParse(f[8], NumberStyles.Float, inv, out var ez)) continue;

                links.Add(new RegionLink
                {
                    FromRegionId = fromId, ToRegionId = toId, LinkType = linkType,
                    PositionStart = new Vector3(sx, sy, sz),
                    PositionEnd = new Vector3(ex, ey, ez),
                });
            }

            return links;
        }
    }
}