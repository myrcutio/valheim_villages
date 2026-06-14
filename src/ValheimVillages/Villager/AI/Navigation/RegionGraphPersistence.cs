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
        private const string ClassSectionDelimiter = "|#|";
        private const string V4Header = "v4";

        // Classification bitmask geometry: 16x16 = 256 cells per tile, 32 bytes.
        private const int ClassTile = 16;
        private const int ClassTileCells = ClassTile * ClassTile;
        private const int ClassMaskBytes = ClassTileCells / 8;

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
            return SerializeV4(graph);
        }

        private static string SerializeV4(RegionGraph graph)
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(V4Header);

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
            AppendClassification(sb, graph);
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

            // v1/v2/v3 payloads are no longer supported. v4 adds the committed
            // perimeter classification section, which is load-bearing for the
            // incremental reconcilers — a v3 graph has no classification, so we
            // reject it and let the caller wipe the ZDO entry and re-trigger a
            // full hna_partition build (which writes v4). Same precedent as the
            // earlier v1/v2 -> v3 rejection.
            if (!data.StartsWith(V4Header))
            {
                LogInfo("[Region] Legacy non-v4 graph in ZDO; purging (caller will wipe + rebuild)");
                return false;
            }

            return RestoreV4(graph, data);
        }

        private static bool RestoreV4(RegionGraph graph, string data)
        {
            // Split off the classification section first (delimiter "|#|") so the
            // region/link parsing below operates only on the "regions||links"
            // prefix. "|#|" does not contain "||", so the link split is safe.
            var mainSection = data;
            string classSection = null;
            var classDelim = data.IndexOf(ClassSectionDelimiter, StringComparison.Ordinal);
            if (classDelim >= 0)
            {
                mainSection = data.Substring(0, classDelim);
                classSection = data.Substring(classDelim + ClassSectionDelimiter.Length);
            }

            var regionSection = mainSection;
            string linkSection = null;
            var linkDelim = mainSection.IndexOf(LinkSectionDelimiter, StringComparison.Ordinal);
            if (linkDelim >= 0)
            {
                regionSection = mainSection.Substring(0, linkDelim);
                linkSection = mainSection.Substring(linkDelim + LinkSectionDelimiter.Length);
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
            if (!string.IsNullOrEmpty(classSection))
                RestoreClassification(graph, classSection);
            LogInfo(
                $"[Region] Restored v4 graph from ZDO: {regionIds.Count} regions, {links.Count} links, " +
                $"{kinds.Count} kinded, classification={(graph.HasClassification ? "yes" : "no")}");
            return true;
        }

        private static void LogInfo(string message)
        {
            LogAction?.Invoke(message);
        }

        // --- Classification (v4) ---
        // Layout in the |#| section: <outsideBitmask>~<anchorReachableBitmask>~<pieceTriples>
        // Each terrain bitmask: tiles joined by '!', each "tileGx,tileGz,<base64 32B>"
        // (256-bit mask, bit = bx*16+bz). Piece set: "gx,gz,hb" triples joined by '!'.

        private static void AppendClassification(StringBuilder sb, RegionGraph graph)
        {
            if (!graph.HasClassification) return;
            sb.Append(ClassSectionDelimiter);
            AppendXzBitmask(sb, graph.OutsideCellsXz);
            sb.Append('~');
            AppendXzBitmask(sb, graph.AnchorReachableCellsXz);
            sb.Append('~');
            AppendPieceKeys(sb, graph.PrunedPieceKeys);
        }

        private static void RestoreClassification(RegionGraph graph, string classSection)
        {
            var subs = classSection.Split('~');
            var outside = new HashSet<long>();
            var anchorReachable = new HashSet<long>();
            var pieces = new HashSet<long>();
            if (subs.Length > 0) ParseXzBitmask(subs[0], outside);
            if (subs.Length > 1) ParseXzBitmask(subs[1], anchorReachable);
            if (subs.Length > 2) ParsePieceKeys(subs[2], pieces);
            graph.SetClassification(outside, anchorReachable, pieces);
        }

        private static void AppendXzBitmask(StringBuilder sb, IReadOnlyCollection<long> cells)
        {
            if (cells == null || cells.Count == 0) return;
            var tiles = new Dictionary<long, byte[]>();
            foreach (var key in cells)
            {
                RegionGraph.UnpackXz(key, out var gx, out var gz);
                var tgx = FloorDiv(gx, ClassTile);
                var tgz = FloorDiv(gz, ClassTile);
                var tileKey = RegionGraph.PackXz(tgx, tgz);
                if (!tiles.TryGetValue(tileKey, out var mask))
                {
                    mask = new byte[ClassMaskBytes];
                    tiles[tileKey] = mask;
                }

                var bx = gx - tgx * ClassTile; // 0..15
                var bz = gz - tgz * ClassTile; // 0..15
                var bit = bx * ClassTile + bz; // 0..255
                mask[bit >> 3] |= (byte)(1 << (bit & 7));
            }

            var inv = CultureInfo.InvariantCulture;
            var first = true;
            foreach (var kv in tiles)
            {
                RegionGraph.UnpackXz(kv.Key, out var tgx, out var tgz);
                if (!first) sb.Append('!');
                first = false;
                sb.Append(tgx.ToString(inv)).Append(',')
                    .Append(tgz.ToString(inv)).Append(',')
                    .Append(Convert.ToBase64String(kv.Value));
            }
        }

        private static void ParseXzBitmask(string section, HashSet<long> outSet)
        {
            if (string.IsNullOrEmpty(section)) return;
            foreach (var tile in section.Split('!'))
            {
                if (string.IsNullOrEmpty(tile)) continue;
                var parts = tile.Split(',');
                if (parts.Length < 3) continue;
                if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tgx)) continue;
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var tgz)) continue;
                byte[] mask;
                try { mask = Convert.FromBase64String(parts[2]); }
                catch { continue; }
                for (var bit = 0; bit < ClassTileCells && (bit >> 3) < mask.Length; bit++)
                {
                    if ((mask[bit >> 3] & (1 << (bit & 7))) == 0) continue;
                    var gx = tgx * ClassTile + bit / ClassTile;
                    var gz = tgz * ClassTile + bit % ClassTile;
                    outSet.Add(RegionGraph.PackXz(gx, gz));
                }
            }
        }

        private static void AppendPieceKeys(StringBuilder sb, IReadOnlyCollection<long> keys)
        {
            if (keys == null || keys.Count == 0) return;
            var inv = CultureInfo.InvariantCulture;
            var first = true;
            foreach (var key in keys)
            {
                RegionGraph.UnpackLookup(key, out var gx, out var gz, out var hb);
                if (!first) sb.Append('!');
                first = false;
                sb.Append(gx.ToString(inv)).Append(',')
                    .Append(gz.ToString(inv)).Append(',')
                    .Append(hb.ToString(inv));
            }
        }

        private static void ParsePieceKeys(string section, HashSet<long> outSet)
        {
            if (string.IsNullOrEmpty(section)) return;
            foreach (var e in section.Split('!'))
            {
                if (string.IsNullOrEmpty(e)) continue;
                var p = e.Split(',');
                if (p.Length < 3) continue;
                if (int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gx) &&
                    int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var gz) &&
                    int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hb))
                    outSet.Add(RegionGraph.PackLookup(gx, gz, hb));
            }
        }

        // Negative-correct integer floor division (world coords are signed).
        private static int FloorDiv(int a, int b)
        {
            var q = a / b;
            if (a % b != 0 && (a < 0) != (b < 0)) q--;
            return q;
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