using UnityEngine;

namespace ValheimVillages.Items.Icons
{
    /// <summary>
    ///     Draws status indicator badges into a work order icon's RGBA32 pixel
    ///     buffer (Color32[], bottom-left origin to match Texture2D.GetPixels32).
    ///     Badges are drawn in the top-right corner, similar to Valheim's broken
    ///     equipment overlay. Operating on the raw array avoids the per-pixel
    ///     Texture2D.GetPixel/SetPixel calls that made compositing slow.
    /// </summary>
    public static class WorkOrderStatusOverlay
    {
        // Badge positioned in top-right corner of the icon
        private const int BadgeSize = 20;
        private const int BadgePadding = 2;
        private const int BorderWidth = 2;

        // Outline color for all badges
        private static readonly Color32 OutlineColor = new(26, 26, 26, 230);
        private static readonly Color32 White = new(255, 255, 255, 255);

        /// <summary>
        ///     Draw the appropriate status badge into the pixel buffer based on
        ///     status. Pending status draws nothing.
        /// </summary>
        public static void Draw(Color32[] px, int w, int h, WorkOrderStatus status)
        {
            switch (status)
            {
                case WorkOrderStatus.Completed:
                    DrawBadge(px, w, h, new Color32(38, 166, 38, 242), DrawCheckmark);
                    break;
                case WorkOrderStatus.InProgress:
                    DrawBadge(px, w, h, new Color32(217, 166, 26, 242), DrawHourglass);
                    break;
                case WorkOrderStatus.Unworkable:
                    DrawBadge(px, w, h, new Color32(191, 31, 31, 242), DrawRedX);
                    break;
            }
        }

        /// <summary>
        ///     Draw the shared badge disc (outline + fill) then invoke the
        ///     glyph painter at the badge center.
        /// </summary>
        private static void DrawBadge(
            Color32[] px, int w, int h, Color32 fill,
            System.Action<Color32[], int, int, int, int> glyph)
        {
            var cx = w - BadgePadding - BadgeSize / 2;
            var cy = h - BadgePadding - BadgeSize / 2;
            var radius = BadgeSize / 2;

            DrawFilledCircle(px, w, h, cx, cy, radius, OutlineColor);
            DrawFilledCircle(px, w, h, cx, cy, radius - BorderWidth, fill);
            glyph(px, w, h, cx, cy);
        }

        private static void DrawCheckmark(Color32[] px, int w, int h, int cx, int cy)
        {
            var x0 = cx - 4;
            var y0 = cy;
            // Short left stroke down to the bottom of the check, then long up-right
            DrawThickLine(px, w, h, x0, y0, x0 + 3, y0 - 3, White, 2);
            DrawThickLine(px, w, h, x0 + 3, y0 - 3, x0 + 8, y0 + 4, White, 2);
        }

        private static void DrawHourglass(Color32[] px, int w, int h, int cx, int cy)
        {
            var left = cx - 3;
            var right = cx + 3;
            var top = cy + 4;
            var bot = cy - 4;
            var mid = cy;

            DrawThickLine(px, w, h, left, top, right, top, White, 1);
            DrawThickLine(px, w, h, left, bot, right, bot, White, 1);
            DrawThickLine(px, w, h, left, top - 1, cx, mid, White, 1);
            DrawThickLine(px, w, h, right, top - 1, cx, mid, White, 1);
            DrawThickLine(px, w, h, cx, mid, left, bot + 1, White, 1);
            DrawThickLine(px, w, h, cx, mid, right, bot + 1, White, 1);
        }

        private static void DrawRedX(Color32[] px, int w, int h, int cx, int cy)
        {
            const int half = 4;
            DrawThickLine(px, w, h, cx - half, cy - half, cx + half, cy + half, White, 2);
            DrawThickLine(px, w, h, cx - half, cy + half, cx + half, cy - half, White, 2);
        }

        #region Drawing primitives

        /// <summary>Alpha-blend a source color onto the pixel at (x, y).</summary>
        private static void Blend(Color32[] px, int w, int h, int x, int y, Color32 c)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            var i = y * w + x;
            var bg = px[i];
            int a = c.a;
            int inv = 255 - a;
            px[i] = new Color32(
                (byte)((bg.r * inv + c.r * a) / 255),
                (byte)((bg.g * inv + c.g * a) / 255),
                (byte)((bg.b * inv + c.b * a) / 255),
                (byte)Mathf.Max(bg.a, c.a));
        }

        private static void DrawFilledCircle(
            Color32[] px, int w, int h, int cx, int cy, int radius, Color32 color)
        {
            var r2 = radius * radius;
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > r2) continue;
                Blend(px, w, h, cx + dx, cy + dy, color);
            }
        }

        private static void DrawThickLine(
            Color32[] px, int w, int h, int x0, int y0, int x1, int y1,
            Color32 color, int thickness)
        {
            var half = thickness / 2;
            for (var oy = -half; oy <= half; oy++)
            for (var ox = -half; ox <= half; ox++)
                DrawLine(px, w, h, x0 + ox, y0 + oy, x1 + ox, y1 + oy, color);
        }

        /// <summary>Bresenham's line algorithm.</summary>
        private static void DrawLine(
            Color32[] px, int w, int h, int x0, int y0, int x1, int y1, Color32 color)
        {
            var dx = Mathf.Abs(x1 - x0);
            var dy = -Mathf.Abs(y1 - y0);
            var sx = x0 < x1 ? 1 : -1;
            var sy = y0 < y1 ? 1 : -1;
            var err = dx + dy;

            for (var i = 0; i < 200; i++) // safety limit
            {
                Blend(px, w, h, x0, y0, color);
                if (x0 == x1 && y0 == y1) break;

                var e2 = 2 * err;
                if (e2 >= dy)
                {
                    err += dy;
                    x0 += sx;
                }

                if (e2 <= dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        #endregion
    }
}
