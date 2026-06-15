using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     Renders a top-down patrol map to a Texture2D for the debug panel.
    ///     Layers (bottom to top): wild fill, flood-fill cells, village polygon,
    ///     ground-truth path, patrol path, dots.
    /// </summary>
    public static class PatrolMapRenderer
    {
        private const int MapSize = 256;
        private const float Padding = 10f;
        private const int WaypointDotRadius = 4;
        private const int AnchorDotRadius = 5;
        private const int PathLineWidth = 2;

        private static readonly Color WildColor = new(0.6f, 0.15f, 0.15f, 0.7f);
        private static readonly Color VillageColor = new(0.45f, 0.45f, 0.45f, 1f);
        private static readonly Color PathColor = new(0.2f, 0.85f, 0.2f, 1f);
        private static readonly Color WaypointColor = new(0.1f, 1f, 0.1f, 1f);
        private static readonly Color InactiveWaypointColor = new(0.7f, 0.5f, 0.2f, 0.7f);
        private static readonly Color InactivePathColor = new(0.5f, 0.35f, 0.15f, 0.4f);
        private static readonly Color AnchorColor = new(1f, 0.9f, 0.3f, 1f);
        private static readonly Color PatrollerColor = new(0.3f, 0.6f, 1f, 1f);
        private static readonly Color FloodFillColor = new(0.45f, 0.45f, 0.45f, 0.95f);
        private static readonly Color GroundTruthColor = new(1f, 0.4f, 0.9f, 1f);
        private static readonly Color GroundTruthDotColor = new(1f, 0.5f, 0.95f, 0.8f);

        // Minimal (player-facing Tasks tab) palette. Transparent background so the
        // map sits directly on the wood panel.
        private static readonly Color MinimalPerimeter = new(0.92f, 0.85f, 0.6f, 1f);
        private static readonly Color PinOutline = new(0.05f, 0.05f, 0.05f, 1f);

        /// <summary>
        ///     Render the patrol map with optional debug overlay layers.
        /// </summary>
        public static Texture2D Render(
            IReadOnlyList<VillagerWaypoint> waypoints,
            Vector3 anchorPosition,
            Vector3? patrollerPosition,
            List<Vector3> floodFillCells = null,
            List<Vector3> groundTruthPath = null,
            float cellSize = 3f,
            IReadOnlyList<(Vector3 position, Color color)> extraPins = null)
        {
            var activeCount = 0;
            if (waypoints != null)
                foreach (var w in waypoints)
                    if (w.Active)
                        activeCount++;

            if (waypoints == null || activeCount < 3)
                if ((floodFillCells == null || floodFillCells.Count == 0) &&
                    (groundTruthPath == null || groundTruthPath.Count == 0) &&
                    (extraPins == null || extraPins.Count == 0))
                    return RenderEmpty();

            ComputeBounds(waypoints, anchorPosition, floodFillCells, groundTruthPath, extraPins, out var min, out var max);

            var worldW = max.x - min.x;
            var worldH = max.y - min.y;
            if (worldW < 1f) worldW = 1f;
            if (worldH < 1f) worldH = 1f;

            var tex = new Texture2D(MapSize, MapSize, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;

            // When real cell coverage is supplied, paint the operable area from the
            // cells (over a wild background) so the route polygon can never mask a
            // coverage hole. Only fall back to the route polygon as the fill when
            // there are no cells.
            var hasCells = floodFillCells != null && floodFillCells.Count > 0;
            var activePolygon = !hasCells && waypoints != null
                ? BuildActivePolygon2D(waypoints)
                : new List<Vector2>();

            FillPixels(tex, activePolygon, min, worldW, worldH);

            if (floodFillCells != null && floodFillCells.Count > 0)
                DrawFloodFillCells(tex, floodFillCells, cellSize, min, worldW, worldH);

            if (waypoints != null)
            {
                DrawPatrolPath(tex, waypoints, min, worldW, worldH);
                DrawDots(tex, waypoints, anchorPosition, patrollerPosition, min, worldW, worldH);
            }

            if (groundTruthPath != null && groundTruthPath.Count >= 2)
            {
                DrawGroundTruthPath(tex, groundTruthPath, min, worldW, worldH);
            }
            else
            {
                var anchorPx = WorldToPixel(anchorPosition, min, worldW, worldH);
                DrawCircle(tex, anchorPx, AnchorDotRadius, AnchorColor);
            }

            if (extraPins != null)
                foreach (var pin in extraPins)
                {
                    var pinPx = WorldToPixel(pin.position, min, worldW, worldH);
                    DrawCircle(tex, pinPx, AnchorDotRadius, pin.color);
                }

            tex.Apply();
            return tex;
        }

        /// <summary>
        ///     Minimal, player-facing map: a neutral background, the village
        ///     perimeter as a single outline, and labeled pins. No wild/region
        ///     debug layers.
        /// </summary>
        public static Texture2D RenderMinimal(
            IReadOnlyList<Vector3> perimeter,
            IReadOnlyList<(Vector3 position, Color color)> pins)
        {
            var pts = new List<Vector2>();
            if (perimeter != null)
                foreach (var p in perimeter) pts.Add(new Vector2(p.x, p.z));
            if (pins != null)
                foreach (var p in pins) pts.Add(new Vector2(p.position.x, p.position.z));

            var tex = new Texture2D(MapSize, MapSize, TextureFormat.RGBA32, false)
                { filterMode = FilterMode.Point };
            var bg = new Color[MapSize * MapSize];
            for (var i = 0; i < bg.Length; i++) bg[i] = Color.clear;
            tex.SetPixels(bg);

            if (pts.Count == 0)
            {
                tex.Apply();
                return tex;
            }

            float minX = pts[0].x, maxX = pts[0].x, minZ = pts[0].y, maxZ = pts[0].y;
            foreach (var p in pts)
            {
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                minZ = Mathf.Min(minZ, p.y);
                maxZ = Mathf.Max(maxZ, p.y);
            }

            var min = new Vector2(minX - Padding, minZ - Padding);
            var max = new Vector2(maxX + Padding, maxZ + Padding);
            var w = max.x - min.x;
            var h = max.y - min.y;
            if (w > h)
            {
                var pad = (w - h) * 0.5f;
                min.y -= pad;
                max.y += pad;
            }
            else if (h > w)
            {
                var pad = (h - w) * 0.5f;
                min.x -= pad;
                max.x += pad;
            }

            var worldW = Mathf.Max(max.x - min.x, 1f);
            var worldH = Mathf.Max(max.y - min.y, 1f);

            if (perimeter != null && perimeter.Count >= 2)
                for (var i = 0; i < perimeter.Count; i++)
                {
                    var a = WorldToPixel(perimeter[i], min, worldW, worldH);
                    var b = WorldToPixel(
                        perimeter[(i + 1) % perimeter.Count], min, worldW, worldH);
                    DrawLine(tex, a, b, MinimalPerimeter, 3);
                }

            if (pins != null)
                foreach (var pin in pins)
                {
                    var p = WorldToPixel(pin.position, min, worldW, worldH);
                    DrawCircle(tex, p, 7, PinOutline);
                    DrawCircle(tex, p, 5, pin.color);
                }

            tex.Apply();
            return tex;
        }

        private static Texture2D RenderEmpty()
        {
            var tex = new Texture2D(MapSize, MapSize, TextureFormat.RGBA32, false);
            var pixels = new Color[MapSize * MapSize];
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = WildColor;
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private static void ComputeBounds(
            IReadOnlyList<VillagerWaypoint> waypoints, Vector3 anchor,
            List<Vector3> floodFillCells, List<Vector3> groundTruthPath,
            IReadOnlyList<(Vector3 position, Color color)> extraPins,
            out Vector2 min, out Vector2 max)
        {
            float minX = anchor.x, maxX = anchor.x;
            float minZ = anchor.z, maxZ = anchor.z;

            if (waypoints != null)
                for (var i = 0; i < waypoints.Count; i++)
                {
                    var p = waypoints[i].Position;
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }

            if (floodFillCells != null)
                for (var i = 0; i < floodFillCells.Count; i++)
                {
                    var p = floodFillCells[i];
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }

            if (groundTruthPath != null)
                for (var i = 0; i < groundTruthPath.Count; i++)
                {
                    var p = groundTruthPath[i];
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }

            if (extraPins != null)
                for (var i = 0; i < extraPins.Count; i++)
                {
                    var p = extraPins[i].position;
                    if (p.x < minX) minX = p.x;
                    if (p.x > maxX) maxX = p.x;
                    if (p.z < minZ) minZ = p.z;
                    if (p.z > maxZ) maxZ = p.z;
                }

            min = new Vector2(minX - Padding, minZ - Padding);
            max = new Vector2(maxX + Padding, maxZ + Padding);

            // Preserve aspect ratio: the map texture is square, so expand the
            // shorter world axis (centered) instead of stretching the village.
            var w = max.x - min.x;
            var h = max.y - min.y;
            if (w > h)
            {
                var pad = (w - h) * 0.5f;
                min.y -= pad;
                max.y += pad;
            }
            else if (h > w)
            {
                var pad = (h - w) * 0.5f;
                min.x -= pad;
                max.x += pad;
            }
        }

        private static List<Vector2> BuildActivePolygon2D(
            IReadOnlyList<VillagerWaypoint> waypoints)
        {
            var poly = new List<Vector2>();
            for (var i = 0; i < waypoints.Count; i++)
                if (waypoints[i].Active)
                    poly.Add(new Vector2(waypoints[i].Position.x, waypoints[i].Position.z));
            return poly;
        }

        #region Layer: Background Fill

        private static void FillPixels(
            Texture2D tex, List<Vector2> polygon,
            Vector2 min, float worldW, float worldH)
        {
            var pixels = new Color[MapSize * MapSize];

            for (var py = 0; py < MapSize; py++)
            for (var px = 0; px < MapSize; px++)
            {
                var wx = min.x + px / (float)MapSize * worldW;
                var wz = min.y + py / (float)MapSize * worldH;
                var inside = polygon.Count >= 3 && PointInPolygon(new Vector2(wx, wz), polygon);
                pixels[py * MapSize + px] = inside ? VillageColor : WildColor;
            }

            tex.SetPixels(pixels);
        }

        #endregion

        #region Layer: Flood-Fill Cells

        private static void DrawFloodFillCells(
            Texture2D tex, List<Vector3> cells, float cellSize,
            Vector2 min, float worldW, float worldH)
        {
            // Enlarge each region cell so neighbours overlap and the walkable
            // area reads as one solid village floor instead of scattered blocks.
            var halfCell = cellSize * 0.85f;
            foreach (var cell in cells)
            {
                var cornerA = WorldToPixel(new Vector3(cell.x - halfCell, 0, cell.z - halfCell), min, worldW, worldH);
                var cornerB = WorldToPixel(new Vector3(cell.x + halfCell, 0, cell.z + halfCell), min, worldW, worldH);

                var x0 = Mathf.Min(cornerA.x, cornerB.x);
                var x1 = Mathf.Max(cornerA.x, cornerB.x);
                var y0 = Mathf.Min(cornerA.y, cornerB.y);
                var y1 = Mathf.Max(cornerA.y, cornerB.y);

                for (var py = y0; py <= y1; py++)
                for (var px = x0; px <= x1; px++)
                {
                    if (px < 0 || px >= MapSize || py < 0 || py >= MapSize) continue;
                    var existing = tex.GetPixel(px, py);
                    tex.SetPixel(px, py, BlendOver(existing, FloodFillColor));
                }
            }
        }

        #endregion

        #region Layer: Ground Truth Path

        private static void DrawGroundTruthPath(
            Texture2D tex, List<Vector3> path,
            Vector2 min, float worldW, float worldH)
        {
            for (var i = 0; i < path.Count; i++)
            {
                var next = (i + 1) % path.Count;
                var a = WorldToPixel(path[i], min, worldW, worldH);
                var b = WorldToPixel(path[next], min, worldW, worldH);
                DrawLine(tex, a, b, GroundTruthColor, 2);
            }

            for (var i = 0; i < path.Count; i++)
            {
                var px = WorldToPixel(path[i], min, worldW, worldH);
                DrawCircle(tex, px, 2, GroundTruthDotColor);
            }
        }

        #endregion

        #region Layer: Patrol Path

        private static void DrawPatrolPath(
            Texture2D tex, IReadOnlyList<VillagerWaypoint> waypoints,
            Vector2 min, float worldW, float worldH)
        {
            VillagerWaypoint prevActive = null;
            var firstActiveIdx = -1;
            for (var i = 0; i < waypoints.Count; i++)
            {
                if (!waypoints[i].Active) continue;
                if (firstActiveIdx < 0) firstActiveIdx = i;
                if (prevActive != null)
                {
                    var a = WorldToPixel(prevActive.Position, min, worldW, worldH);
                    var b = WorldToPixel(waypoints[i].Position, min, worldW, worldH);
                    DrawLine(tex, a, b, PathColor, PathLineWidth);
                }

                prevActive = waypoints[i];
            }

            if (prevActive != null && firstActiveIdx >= 0 && prevActive != waypoints[firstActiveIdx])
            {
                var a = WorldToPixel(prevActive.Position, min, worldW, worldH);
                var b = WorldToPixel(waypoints[firstActiveIdx].Position, min, worldW, worldH);
                DrawLine(tex, a, b, PathColor, PathLineWidth);
            }

            for (var i = 0; i < waypoints.Count; i++)
            {
                if (waypoints[i].Active) continue;
                var nextActive = -1;
                for (var j = 1; j < waypoints.Count; j++)
                {
                    var idx = (i + j) % waypoints.Count;
                    if (waypoints[idx].Active)
                    {
                        nextActive = idx;
                        break;
                    }
                }

                if (nextActive >= 0)
                {
                    var a = WorldToPixel(waypoints[i].Position, min, worldW, worldH);
                    var b = WorldToPixel(waypoints[nextActive].Position, min, worldW, worldH);
                    DrawLine(tex, a, b, InactivePathColor, 1);
                }
            }
        }

        #endregion

        #region Layer: Dots

        private static void DrawDots(
            Texture2D tex,
            IReadOnlyList<VillagerWaypoint> waypoints,
            Vector3 anchor, Vector3? patroller,
            Vector2 min, float worldW, float worldH)
        {
            for (var i = 0; i < waypoints.Count; i++)
            {
                if (waypoints[i].Active) continue;
                var px = WorldToPixel(waypoints[i].Position, min, worldW, worldH);
                DrawCircle(tex, px, WaypointDotRadius - 1, InactiveWaypointColor);
            }

            for (var i = 0; i < waypoints.Count; i++)
            {
                if (!waypoints[i].Active) continue;
                var px = WorldToPixel(waypoints[i].Position, min, worldW, worldH);
                DrawCircle(tex, px, WaypointDotRadius, WaypointColor);
            }

            var anchorPx = WorldToPixel(anchor, min, worldW, worldH);
            DrawCircle(tex, anchorPx, AnchorDotRadius, AnchorColor);

            if (patroller.HasValue)
            {
                var patrollerPx = WorldToPixel(patroller.Value, min, worldW, worldH);
                DrawCircle(tex, patrollerPx, AnchorDotRadius, PatrollerColor);
            }
        }

        #endregion

        #region Drawing Primitives

        private static Vector2Int WorldToPixel(
            Vector3 world, Vector2 min, float worldW, float worldH)
        {
            var px = Mathf.Clamp(
                Mathf.RoundToInt((world.x - min.x) / worldW * MapSize), 0, MapSize - 1);
            var py = Mathf.Clamp(
                Mathf.RoundToInt((world.z - min.y) / worldH * MapSize), 0, MapSize - 1);
            return new Vector2Int(px, py);
        }

        private static void DrawCircle(
            Texture2D tex, Vector2Int center, int radius, Color color)
        {
            var rSq = radius * radius;
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > rSq) continue;
                var px = center.x + dx;
                var py = center.y + dy;
                if (px >= 0 && px < MapSize && py >= 0 && py < MapSize)
                    tex.SetPixel(px, py, color);
            }
        }

        private static void DrawLine(
            Texture2D tex, Vector2Int a, Vector2Int b, Color color, int width)
        {
            var dx = Mathf.Abs(b.x - a.x);
            var dy = Mathf.Abs(b.y - a.y);
            var sx = a.x < b.x ? 1 : -1;
            var sy = a.y < b.y ? 1 : -1;
            var err = dx - dy;
            int cx = a.x, cy = a.y;
            var half = width / 2;

            while (true)
            {
                for (var w = -half; w <= half; w++)
                {
                    var px = cx + w;
                    var py = cy + w;
                    if (px >= 0 && px < MapSize && cy >= 0 && cy < MapSize)
                        tex.SetPixel(px, cy, color);
                    if (cx >= 0 && cx < MapSize && py >= 0 && py < MapSize)
                        tex.SetPixel(cx, py, color);
                }

                if (cx == b.x && cy == b.y) break;
                var e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    cx += sx;
                }

                if (e2 < dx)
                {
                    err += dx;
                    cy += sy;
                }
            }
        }

        private static Color BlendOver(Color dst, Color src)
        {
            var a = src.a + dst.a * (1f - src.a);
            if (a < 0.001f) return Color.clear;
            return new Color(
                (src.r * src.a + dst.r * dst.a * (1f - src.a)) / a,
                (src.g * src.a + dst.g * dst.a * (1f - src.a)) / a,
                (src.b * src.a + dst.b * dst.a * (1f - src.a)) / a,
                a);
        }

        private static bool PointInPolygon(Vector2 point, List<Vector2> polygon)
        {
            var inside = false;
            var count = polygon.Count;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                var vi = polygon[i];
                var vj = polygon[j];
                if (vi.y > point.y != vj.y > point.y &&
                    point.x < (vj.x - vi.x) * (point.y - vi.y) / (vj.y - vi.y) + vi.x)
                    inside = !inside;
            }

            return inside;
        }

        #endregion
    }
}