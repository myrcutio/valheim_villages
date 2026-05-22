using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    /// Serialization and restoration of the region graph for ZDO persistence.
    /// Format: "v3;regionId:cx,cy,cz,K;...||fromId,toId,T,sx,sy,sz,ex,ey,ez;..."
    /// where K is the SurfaceKind char (T = Terrain, P = Piece). Legacy v1/v2
    /// formats are no longer read; <see cref="Restore"/> returns false for any
    /// non-v3 payload so the caller can wipe the ZDO entry and trigger a fresh
    /// partition build (no default-fill — the kind tag is load-bearing).
    /// </summary>
    internal static class RegionGraphPersistence
    {
        internal static System.Action<string> LogAction;

        private const string LinkSectionDelimiter = "||";
        private const string V3Header = "v3";

        private static char KindToChar(SurfaceKind k) => k == SurfaceKind.Terrain ? 'T' : 'P';
        private static SurfaceKind CharToKind(char c) => c == 'T' ? SurfaceKind.Terrain : SurfaceKind.Piece;

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
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.Append(V3Header);

            foreach (string id in graph.GetRegionIds())
            {
                sb.Append(';').Append(id).Append(':');
                if (graph.GetCellWorldXZ(id, out float wx, out float wz) &&
                    graph.TryGetCellHeight(id, out float h))
                {
                    sb.Append(wx.ToString("F2", inv)).Append(',')
                      .Append(h.ToString("F2", inv)).Append(',')
                      .Append(wz.ToString("F2", inv)).Append(',')
                      .Append(KindToChar(graph.GetRegionKind(id)));
                }
                else sb.Append('?');
            }

            AppendLinks(sb, graph);
            return sb.ToString();
        }

        private static void AppendLinks(System.Text.StringBuilder sb, RegionGraph graph)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var links = graph.GetAllLinks();
            if (links == null || links.Count == 0) return;

            sb.Append(LinkSectionDelimiter);
            for (int i = 0; i < links.Count; i++)
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
            string regionSection = data;
            string linkSection = null;
            int linkDelim = data.IndexOf(LinkSectionDelimiter);
            if (linkDelim >= 0)
            {
                regionSection = data.Substring(0, linkDelim);
                linkSection = data.Substring(linkDelim + LinkSectionDelimiter.Length);
            }

            var segments = regionSection.Split(';');
            if (segments.Length < 2) return false;

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var regionIds = new HashSet<string>();
            var centroids = new Dictionary<string, Vector3>();
            var kinds = new Dictionary<string, SurfaceKind>();

            for (int i = 1; i < segments.Length; i++)
            {
                var seg = segments[i];
                int colon = seg.IndexOf(':');
                if (colon <= 0) continue;

                string regionId = seg.Substring(0, colon);
                string rest = seg.Substring(colon + 1);
                regionIds.Add(regionId);

                if (rest == "?") continue;
                var parts = rest.Split(',');
                if (parts.Length >= 4 &&
                    float.TryParse(parts[0], System.Globalization.NumberStyles.Float, inv, out float cx) &&
                    float.TryParse(parts[1], System.Globalization.NumberStyles.Float, inv, out float cy) &&
                    float.TryParse(parts[2], System.Globalization.NumberStyles.Float, inv, out float cz))
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
                int gx = Mathf.FloorToInt(kv.Value.x / RegionGraph.LookupCellSize);
                int gz = Mathf.FloorToInt(kv.Value.z / RegionGraph.LookupCellSize);
                int hb = RegionGraph.HeightBucket(kv.Value.y);
                // Cover a small area around each centroid
                for (int dx = -1; dx <= 1; dx++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    long key = RegionGraph.PackLookup(gx + dx, gz + dz, hb);
                    lookupGrid[key] = kv.Key;
                }
            }

            graph.SetGraph(regionIds, links, centroids, lookupGrid,
                boundaryCells: null, regionKinds: kinds);
            LogInfo($"[Region] Restored v3 graph from ZDO: {regionIds.Count} regions, {links.Count} links, {kinds.Count} kinded");
            return true;
        }

        private static void LogInfo(string message) => LogAction?.Invoke(message);

        private static List<RegionLink> DeserializeLinks(string linkSection)
        {
            var links = new List<RegionLink>();
            if (string.IsNullOrEmpty(linkSection)) return links;

            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var entries = linkSection.Split(';');
            foreach (var entry in entries)
            {
                if (string.IsNullOrEmpty(entry)) continue;
                var f = entry.Split(',');
                if (f.Length < 9) continue;

                string fromId = f[0], toId = f[1];
                if (string.IsNullOrEmpty(fromId) || string.IsNullOrEmpty(toId)) continue;
                if (f[2].Length < 1) continue;

                RegionLinkType linkType = CharToLinkType(f[2][0]);
                if (!float.TryParse(f[3], System.Globalization.NumberStyles.Float, inv, out float sx)) continue;
                if (!float.TryParse(f[4], System.Globalization.NumberStyles.Float, inv, out float sy)) continue;
                if (!float.TryParse(f[5], System.Globalization.NumberStyles.Float, inv, out float sz)) continue;
                if (!float.TryParse(f[6], System.Globalization.NumberStyles.Float, inv, out float ex)) continue;
                if (!float.TryParse(f[7], System.Globalization.NumberStyles.Float, inv, out float ey)) continue;
                if (!float.TryParse(f[8], System.Globalization.NumberStyles.Float, inv, out float ez)) continue;

                links.Add(new RegionLink
                {
                    FromRegionId = fromId, ToRegionId = toId, LinkType = linkType,
                    PositionStart = new Vector3(sx, sy, sz),
                    PositionEnd = new Vector3(ex, ey, ez)
                });
            }
            return links;
        }
    }
}
