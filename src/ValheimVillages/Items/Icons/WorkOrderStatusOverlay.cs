using UnityEngine;

namespace ValheimVillages.Items.Icons
{
    /// <summary>
    ///     Draws status indicator badges onto work order icon textures.
    ///     Badges are drawn in the top-right corner, similar to Valheim's
    ///     broken equipment overlay.
    /// </summary>
    public static class WorkOrderStatusOverlay
    {
        // Badge positioned in top-right corner of the icon
        private const int BadgeSize = 20;
        private const int BadgePadding = 2;
        private const int BorderWidth = 2;

        // Outline color for all badges
        private static readonly Color OutlineColor = new(0.1f, 0.1f, 0.1f, 0.9f);

        /// <summary>
        ///     Draw the appropriate status badge onto a texture based on status.
        ///     Pending status draws nothing.
        /// </summary>
        public static void Draw(Texture2D tex, WorkOrderStatus status)
        {
            switch (status)
            {
                case WorkOrderStatus.Completed:
                    DrawCheckmark(tex);
                    break;
                case WorkOrderStatus.InProgress:
                    DrawHourglass(tex);
                    break;
                case WorkOrderStatus.Unworkable:
                    DrawRedX(tex);
                    break;
            }
        }

        /// <summary>
        ///     Green circle with white checkmark.
        /// </summary>
        private static void DrawCheckmark(Texture2D tex)
        {
            var cx = tex.width - BadgePadding - BadgeSize / 2;
            var cy = tex.height - BadgePadding - BadgeSize / 2;
            var radius = BadgeSize / 2;

            DrawFilledCircle(tex, cx, cy, radius, OutlineColor);
            DrawFilledCircle(tex, cx, cy, radius - BorderWidth,
                new Color(0.15f, 0.65f, 0.15f, 0.95f));

            // Draw checkmark: short stroke down-right, long stroke down-left
            var white = new Color(1f, 1f, 1f, 1f);
            var x0 = cx - 4;
            var y0 = cy;

            // Short left stroke (going down-right to the bottom of the check)
            DrawThickLine(tex, x0, y0, x0 + 3, y0 - 3, white, 2);
            // Long right stroke (going up-right from the bottom)
            DrawThickLine(tex, x0 + 3, y0 - 3, x0 + 8, y0 + 4, white, 2);
        }

        /// <summary>
        ///     Amber/yellow circle with hourglass shape for in-progress.
        /// </summary>
        private static void DrawHourglass(Texture2D tex)
        {
            var cx = tex.width - BadgePadding - BadgeSize / 2;
            var cy = tex.height - BadgePadding - BadgeSize / 2;
            var radius = BadgeSize / 2;

            DrawFilledCircle(tex, cx, cy, radius, OutlineColor);
            DrawFilledCircle(tex, cx, cy, radius - BorderWidth,
                new Color(0.85f, 0.65f, 0.1f, 0.95f));

            // Draw hourglass shape with white pixels
            var white = new Color(1f, 1f, 1f, 1f);
            var left = cx - 3;
            var right = cx + 3;
            var top = cy + 4;
            var bot = cy - 4;
            var mid = cy;

            // Top bar
            DrawThickLine(tex, left, top, right, top, white, 1);
            // Bottom bar
            DrawThickLine(tex, left, bot, right, bot, white, 1);
            // Top triangle sides (converging to center)
            DrawThickLine(tex, left, top - 1, cx, mid, white, 1);
            DrawThickLine(tex, right, top - 1, cx, mid, white, 1);
            // Bottom triangle sides (diverging from center)
            DrawThickLine(tex, cx, mid, left, bot + 1, white, 1);
            DrawThickLine(tex, cx, mid, right, bot + 1, white, 1);
        }

        /// <summary>
        ///     Red circle with white X.
        /// </summary>
        private static void DrawRedX(Texture2D tex)
        {
            var cx = tex.width - BadgePadding - BadgeSize / 2;
            var cy = tex.height - BadgePadding - BadgeSize / 2;
            var radius = BadgeSize / 2;

            DrawFilledCircle(tex, cx, cy, radius, OutlineColor);
            DrawFilledCircle(tex, cx, cy, radius - BorderWidth,
                new Color(0.75f, 0.12f, 0.12f, 0.95f));

            // Draw X
            var white = new Color(1f, 1f, 1f, 1f);
            var half = 4;
            DrawThickLine(tex, cx - half, cy - half, cx + half, cy + half, white, 2);
            DrawThickLine(tex, cx - half, cy + half, cx + half, cy - half, white, 2);
        }

        #region Drawing primitives

        private static void DrawFilledCircle(
            Texture2D tex, int cx, int cy, int radius, Color color)
        {
            var r2 = radius * radius;
            for (var dy = -radius; dy <= radius; dy++)
            for (var dx = -radius; dx <= radius; dx++)
            {
                if (dx * dx + dy * dy > r2) continue;
                var px = cx + dx;
                var py = cy + dy;
                if (px < 0 || px >= tex.width) continue;
                if (py < 0 || py >= tex.height) continue;

                var bg = tex.GetPixel(px, py);
                var a = color.a;
                tex.SetPixel(px, py, new Color(
                    bg.r * (1 - a) + color.r * a,
                    bg.g * (1 - a) + color.g * a,
                    bg.b * (1 - a) + color.b * a,
                    Mathf.Max(bg.a, a)));
            }
        }

        private static void DrawThickLine(
            Texture2D tex, int x0, int y0, int x1, int y1,
            Color color, int thickness)
        {
            var half = thickness / 2;
            for (var oy = -half; oy <= half; oy++)
            for (var ox = -half; ox <= half; ox++)
                DrawLine(tex, x0 + ox, y0 + oy, x1 + ox, y1 + oy, color);
        }

        /// <summary>Bresenham's line algorithm.</summary>
        private static void DrawLine(
            Texture2D tex, int x0, int y0, int x1, int y1, Color color)
        {
            var dx = Mathf.Abs(x1 - x0);
            var dy = -Mathf.Abs(y1 - y0);
            var sx = x0 < x1 ? 1 : -1;
            var sy = y0 < y1 ? 1 : -1;
            var err = dx + dy;

            for (var i = 0; i < 200; i++) // safety limit
            {
                if (x0 >= 0 && x0 < tex.width && y0 >= 0 && y0 < tex.height)
                    tex.SetPixel(x0, y0, color);

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