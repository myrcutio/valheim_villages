using UnityEngine;

namespace ValheimVillages.Items
{
    /// <summary>
    ///     Procedurally generates the Lode Core's inventory icon: a glowing blue faceted core on a
    ///     transparent field, with a soft halo and a few bright rays. The item is cloned from the
    ///     Surtling core, whose icon is ORANGE — replacing <c>m_icons</c> with this makes the Lode
    ///     Core read as a cold blue "ours" core everywhere a sprite is shown (inventory slot, recruit
    ///     requirement row), matching the retinted world model in <see cref="LodeCoreModel" />.
    /// </summary>
    internal static class LodeCoreIcon
    {
        private const int Size = 128;

        // Same cold blue as the world model's glow, plus a near-white hot core so the centre reads
        // as "brightly shining" rather than a flat blue blob.
        private static readonly Color Glow = new(0.10f, 0.35f, 1f);
        private static readonly Color Hot = new(0.82f, 0.92f, 1f);

        public static Sprite Generate()
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "vv_lodecore_icon",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var px = new Color[Size * Size];
            var center = new Vector2(0.5f, 0.5f);

            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                // Position in [-0.5, 0.5], y up.
                var p = new Vector2((x + 0.5f) / Size, (y + 0.5f) / Size) - center;
                var dist = p.magnitude;

                // Soft radial halo behind the gem: bright near the centre, gone by the rim.
                var halo = Mathf.Clamp01(1f - dist / 0.46f);
                halo *= halo; // tighten the falloff

                // Hexagonal crystal body (flat-top). Negative SDF = inside.
                var sdf = HexSdf(p, -0.27f);
                var body = Mathf.Clamp01(-sdf / 0.27f); // 0 at edge -> 1 at centre

                // Facet shading: brighten the middle of each 60° sector, darken the seams, so the
                // flat fill reads as a cut gem rather than a disc.
                if (body > 0f)
                {
                    var ang = Mathf.Atan2(p.y, p.x);
                    var sector = Mathf.Repeat(ang / (Mathf.PI / 3f), 1f); // 0..1 within a facet
                    var facet = 0.78f + 0.22f * Mathf.Sin(sector * Mathf.PI); // bright mid-facet
                    body *= facet;
                    // Crisp crystalline rim highlight just inside the edge.
                    if (sdf > -0.03f) body = Mathf.Max(body, 0.9f);
                }

                var alpha = Mathf.Clamp01(halo * 0.7f + body);
                if (alpha <= 0.003f)
                {
                    px[y * Size + x] = new Color(0f, 0f, 0f, 0f);
                    continue;
                }

                // Hot white-blue toward the dense core, cold blue out in the halo.
                var hot = Mathf.Clamp01(body * 1.1f + Mathf.Clamp01(1f - dist / 0.16f));
                var rgb = Color.Lerp(Glow, Hot, hot);
                px[y * Size + x] = new Color(rgb.r, rgb.g, rgb.b, alpha);
            }

            DrawRays(px, center);

            tex.SetPixels(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), Size);
        }

        // Flat-top regular-hexagon signed distance (Inigo Quilez). r is the flat-to-flat radius.
        private static float HexSdf(Vector2 p, float r)
        {
            const float kx = -0.866025404f, ky = 0.5f, kz = 0.577350269f;
            p = new Vector2(Mathf.Abs(p.x), Mathf.Abs(p.y));
            var t = 2f * Mathf.Min(kx * p.x + ky * p.y, 0f);
            p -= new Vector2(t * kx, t * ky);
            p -= new Vector2(Mathf.Clamp(p.x, -kz * r, kz * r), r);
            return p.magnitude * Mathf.Sign(p.y);
        }

        // Four bright additive rays (a glint) radiating from the core for the "shining" read.
        private static void DrawRays(Color[] px, Vector2 center)
        {
            var dirs = new[]
            {
                new Vector2(1f, 0f), new Vector2(-1f, 0f),
                new Vector2(0f, 1f), new Vector2(0f, -1f),
            };
            const int steps = 60;
            foreach (var dir in dirs)
            for (var i = 1; i <= steps; i++)
            {
                var t = i / (float)steps;
                var reach = 0.46f * t;
                var uv = center + dir * reach;
                // Taper the ray and fade it out toward the tip.
                var bright = (1f - t) * (1f - t);
                Stamp(px, uv, Hot, bright * 0.8f);
            }
        }

        // Additive-blend a soft 2x2 dot at a UV onto whatever is already there.
        private static void Stamp(Color[] px, Vector2 uv, Color color, float strength)
        {
            if (strength <= 0f) return;
            int cx = Mathf.RoundToInt(uv.x * Size), cy = Mathf.RoundToInt(uv.y * Size);
            for (var dy = 0; dy <= 1; dy++)
            for (var dx = 0; dx <= 1; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
                var idx = y * Size + x;
                var cur = px[idx];
                var a = Mathf.Clamp01(cur.a + strength);
                px[idx] = new Color(
                    Mathf.Clamp01(cur.r + color.r * strength),
                    Mathf.Clamp01(cur.g + color.g * strength),
                    Mathf.Clamp01(cur.b + color.b * strength),
                    a);
            }
        }
    }
}
