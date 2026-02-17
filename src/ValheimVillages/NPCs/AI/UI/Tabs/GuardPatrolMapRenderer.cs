using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.NPCs.AI.Guards;

namespace ValheimVillages.NPCs.AI.UI.Tabs
{
    /// <summary>
    /// Renders a top-down patrol map to a Texture2D for the debug panel.
    /// Village area is grey, patrol path is green, wild area is semi-opaque red.
    /// </summary>
    public static class GuardPatrolMapRenderer
    {
        private const int MapSize = 256;
        private const float Padding = 10f;
        private const int WaypointDotRadius = 4;
        private const int BedDotRadius = 5;
        private const int PathLineWidth = 2;

        private static readonly Color WildColor = new(0.6f, 0.15f, 0.15f, 0.7f);
        private static readonly Color VillageColor = new(0.45f, 0.45f, 0.45f, 1f);
        private static readonly Color PathColor = new(0.2f, 0.85f, 0.2f, 1f);
        private static readonly Color WaypointColor = new(0.1f, 1f, 0.1f, 1f);
        private static readonly Color InactiveWaypointColor = new(0.7f, 0.5f, 0.2f, 0.7f);
        private static readonly Color InactivePathColor = new(0.5f, 0.35f, 0.15f, 0.4f);
        private static readonly Color BedColor = new(1f, 0.9f, 0.3f, 1f);
        private static readonly Color GuardColor = new(0.3f, 0.6f, 1f, 1f);

        /// <summary>
        /// Render the patrol map. Returns a new Texture2D each call.
        /// </summary>
        public static Texture2D Render(
            IReadOnlyList<VillagerWaypoint> waypoints,
            Vector3 bedPosition,
            Vector3? guardPosition)
        {
            int activeCount = 0;
            if (waypoints != null)
                foreach (var w in waypoints) if (w.Active) activeCount++;

            if (waypoints == null || activeCount < 3)
                return RenderEmpty();

            ComputeBounds(waypoints, bedPosition, out var min, out var max);

            float worldW = max.x - min.x;
            float worldH = max.y - min.y;
            if (worldW < 1f) worldW = 1f;
            if (worldH < 1f) worldH = 1f;

            var tex = new Texture2D(MapSize, MapSize, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;

            var activePolygon = BuildActivePolygon2D(waypoints);

            FillPixels(tex, activePolygon, min, worldW, worldH);
            DrawPatrolPath(tex, waypoints, min, worldW, worldH);
            DrawDots(tex, waypoints, bedPosition, guardPosition, min, worldW, worldH);

            tex.Apply();
            return tex;
        }

        private static Texture2D RenderEmpty()
        {
            var tex = new Texture2D(MapSize, MapSize, TextureFormat.RGBA32, false);
            var pixels = new Color[MapSize * MapSize];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = WildColor;
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private static void ComputeBounds(
            IReadOnlyList<VillagerWaypoint> waypoints, Vector3 bed,
            out Vector2 min, out Vector2 max)
        {
            float minX = bed.x, maxX = bed.x;
            float minZ = bed.z, maxZ = bed.z;

            for (int i = 0; i < waypoints.Count; i++)
            {
                var p = waypoints[i].Position;
                if (p.x < minX) minX = p.x;
                if (p.x > maxX) maxX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.z > maxZ) maxZ = p.z;
            }

            min = new Vector2(minX - Padding, minZ - Padding);
            max = new Vector2(maxX + Padding, maxZ + Padding);
        }

        private static List<Vector2> BuildActivePolygon2D(
            IReadOnlyList<VillagerWaypoint> waypoints)
        {
            var poly = new List<Vector2>();
            for (int i = 0; i < waypoints.Count; i++)
                if (waypoints[i].Active)
                    poly.Add(new Vector2(waypoints[i].Position.x, waypoints[i].Position.z));
            return poly;
        }

        private static void FillPixels(
            Texture2D tex, List<Vector2> polygon,
            Vector2 min, float worldW, float worldH)
        {
            var pixels = new Color[MapSize * MapSize];

            for (int py = 0; py < MapSize; py++)
            {
                for (int px = 0; px < MapSize; px++)
                {
                    float wx = min.x + (px / (float)MapSize) * worldW;
                    float wz = min.y + (py / (float)MapSize) * worldH;
                    bool inside = PointInPolygon(new Vector2(wx, wz), polygon);
                    pixels[py * MapSize + px] = inside ? VillageColor : WildColor;
                }
            }

            tex.SetPixels(pixels);
        }

        private static void DrawPatrolPath(
            Texture2D tex, IReadOnlyList<VillagerWaypoint> waypoints,
            Vector2 min, float worldW, float worldH)
        {
            // Draw active patrol path (green lines between consecutive active waypoints)
            VillagerWaypoint prevActive = null;
            int firstActiveIdx = -1;
            for (int i = 0; i < waypoints.Count; i++)
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
            // Close the loop
            if (prevActive != null && firstActiveIdx >= 0 && prevActive != waypoints[firstActiveIdx])
            {
                var a = WorldToPixel(prevActive.Position, min, worldW, worldH);
                var b = WorldToPixel(waypoints[firstActiveIdx].Position, min, worldW, worldH);
                DrawLine(tex, a, b, PathColor, PathLineWidth);
            }

            // Draw faint lines from inactive waypoints to their nearest active neighbors
            for (int i = 0; i < waypoints.Count; i++)
            {
                if (waypoints[i].Active) continue;
                int nextActive = -1;
                for (int j = 1; j < waypoints.Count; j++)
                {
                    int idx = (i + j) % waypoints.Count;
                    if (waypoints[idx].Active) { nextActive = idx; break; }
                }
                if (nextActive >= 0)
                {
                    var a = WorldToPixel(waypoints[i].Position, min, worldW, worldH);
                    var b = WorldToPixel(waypoints[nextActive].Position, min, worldW, worldH);
                    DrawLine(tex, a, b, InactivePathColor, 1);
                }
            }
        }

        private static void DrawDots(
            Texture2D tex,
            IReadOnlyList<VillagerWaypoint> waypoints,
            Vector3 bed, Vector3? guard,
            Vector2 min, float worldW, float worldH)
        {
            // Draw inactive waypoints first (behind active ones)
            for (int i = 0; i < waypoints.Count; i++)
            {
                if (waypoints[i].Active) continue;
                var px = WorldToPixel(waypoints[i].Position, min, worldW, worldH);
                DrawCircle(tex, px, WaypointDotRadius - 1, InactiveWaypointColor);
            }

            // Draw active waypoints on top
            for (int i = 0; i < waypoints.Count; i++)
            {
                if (!waypoints[i].Active) continue;
                var px = WorldToPixel(waypoints[i].Position, min, worldW, worldH);
                DrawCircle(tex, px, WaypointDotRadius, WaypointColor);
            }

            var bedPx = WorldToPixel(bed, min, worldW, worldH);
            DrawCircle(tex, bedPx, BedDotRadius, BedColor);

            if (guard.HasValue)
            {
                var guardPx = WorldToPixel(guard.Value, min, worldW, worldH);
                DrawCircle(tex, guardPx, BedDotRadius, GuardColor);
            }
        }

        private static Vector2Int WorldToPixel(
            Vector3 world, Vector2 min, float worldW, float worldH)
        {
            int px = Mathf.Clamp(
                Mathf.RoundToInt(((world.x - min.x) / worldW) * MapSize), 0, MapSize - 1);
            int py = Mathf.Clamp(
                Mathf.RoundToInt(((world.z - min.y) / worldH) * MapSize), 0, MapSize - 1);
            return new Vector2Int(px, py);
        }

        private static void DrawCircle(
            Texture2D tex, Vector2Int center, int radius, Color color)
        {
            int rSq = radius * radius;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy > rSq) continue;
                    int px = center.x + dx;
                    int py = center.y + dy;
                    if (px >= 0 && px < MapSize && py >= 0 && py < MapSize)
                        tex.SetPixel(px, py, color);
                }
            }
        }

        private static void DrawLine(
            Texture2D tex, Vector2Int a, Vector2Int b, Color color, int width)
        {
            int dx = Mathf.Abs(b.x - a.x);
            int dy = Mathf.Abs(b.y - a.y);
            int sx = a.x < b.x ? 1 : -1;
            int sy = a.y < b.y ? 1 : -1;
            int err = dx - dy;
            int cx = a.x, cy = a.y;
            int half = width / 2;

            while (true)
            {
                for (int w = -half; w <= half; w++)
                {
                    int px = cx + w;
                    int py = cy + w;
                    if (px >= 0 && px < MapSize && cy >= 0 && cy < MapSize)
                        tex.SetPixel(px, cy, color);
                    if (cx >= 0 && cx < MapSize && py >= 0 && py < MapSize)
                        tex.SetPixel(cx, py, color);
                }

                if (cx == b.x && cy == b.y) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; cx += sx; }
                if (e2 < dx) { err += dx; cy += sy; }
            }
        }

        private static bool PointInPolygon(Vector2 point, List<Vector2> polygon)
        {
            bool inside = false;
            int count = polygon.Count;
            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                var vi = polygon[i];
                var vj = polygon[j];
                if ((vi.y > point.y) != (vj.y > point.y) &&
                    point.x < (vj.x - vi.x) * (point.y - vi.y) / (vj.y - vi.y) + vi.x)
                {
                    inside = !inside;
                }
            }
            return inside;
        }
    }
}
