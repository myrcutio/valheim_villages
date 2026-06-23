using UnityEngine;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    ///     Procedurally generates a biome-tinted "torn map scrap" texture for ransom fragments,
    ///     used for both the inventory icon and the cloth world model's skin. A warm parchment
    ///     fill with perlin grain, irregular torn (alpha-cut) edges, and an inked dashed route +
    ///     "X marks the spot" in the biome's ink colour. Stylised, deterministic, unique per biome.
    /// </summary>
    internal static class FragmentArt
    {
        private const int Size = 96;

        /// <summary>Ink colour for a biome id, from the inkColor strings in ItemFactory.FragmentBiomes.</summary>
        public static Color InkFor(string biomeId)
        {
            var ink = "black";
            foreach (var (_, _, id, _, inkColor) in ItemFactory.FragmentBiomes)
                if (id == biomeId)
                {
                    ink = inkColor;
                    break;
                }

            return ink switch
            {
                "green" => new Color(0.28f, 0.52f, 0.24f),
                "dark blue" => new Color(0.16f, 0.22f, 0.48f),
                "sickly brown" => new Color(0.42f, 0.38f, 0.18f),
                "blue" => new Color(0.24f, 0.46f, 0.76f),
                "golden" => new Color(0.78f, 0.60f, 0.18f),
                "purple" => new Color(0.46f, 0.24f, 0.56f),
                "crimson" => new Color(0.66f, 0.20f, 0.20f),
                _ => new Color(0.24f, 0.18f, 0.12f),
            };
        }

        public static Texture2D Generate(Color ink)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "vv_fragment_tex",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var px = new Color[Size * Size];
            var light = new Color(0.82f, 0.73f, 0.54f);
            var dark = new Color(0.60f, 0.50f, 0.34f);

            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                float u = x / (float)Size, v = y / (float)Size;

                // Irregular torn edge: a perlin-perturbed inset margin -> alpha cutout outside it.
                var margin = 0.09f + 0.07f * Mathf.PerlinNoise(u * 5f + v * 0.5f, v * 5f);
                if (u < margin || u > 1f - margin || v < margin || v > 1f - margin)
                {
                    px[y * Size + x] = new Color(0f, 0f, 0f, 0f);
                    continue;
                }

                var grain = Mathf.PerlinNoise(u * 14f, v * 14f);
                px[y * Size + x] = Color.Lerp(dark, light, 0.4f + 0.6f * grain);
            }

            // Inked dashed "route" with an X at its end — a little map motif.
            DrawDashedRoute(px, new Vector2(0.30f, 0.32f), new Vector2(0.68f, 0.66f), ink);
            DrawX(px, new Vector2(0.68f, 0.66f), 0.06f, ink);

            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }

        public static Sprite ToSprite(Texture2D tex)
        {
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), tex.width);
        }

        // A dashed, wandering S-curve "route" from a to b: the straight path offset
        // perpendicular by a low-frequency sine (the S) plus a smaller second harmonic so it
        // wanders rather than bowing symmetrically. Endpoints stay put (offset is 0 at t=0 and 1).
        private static void DrawDashedRoute(Color[] px, Vector2 a, Vector2 b, Color ink)
        {
            var perp = new Vector2(-(b.y - a.y), b.x - a.x);
            if (perp.sqrMagnitude > 0f) perp.Normalize();
            const float amp = 0.08f;
            const int steps = 72;
            for (var i = 0; i <= steps; i++)
            {
                if (i / 4 % 2 == 1) continue; // dash gaps
                var t = i / (float)steps;
                var wander = Mathf.Sin(t * Mathf.PI * 2f) * 0.75f + Mathf.Sin(t * Mathf.PI * 3.3f + 0.6f) * 0.25f;
                Stamp(px, Vector2.Lerp(a, b, t) + perp * (amp * wander), ink);
            }
        }

        private static void DrawX(Color[] px, Vector2 c, float r, Color ink)
        {
            const int steps = 18;
            for (var i = -steps; i <= steps; i++)
            {
                var t = i / (float)steps * r;
                Stamp(px, c + new Vector2(t, t), ink);
                Stamp(px, c + new Vector2(t, -t), ink);
            }
        }

        // Stamp a 3x3 ink dot at a UV, but only over existing parchment (never on the torn alpha).
        private static void Stamp(Color[] px, Vector2 uv, Color ink)
        {
            int cx = Mathf.RoundToInt(uv.x * Size), cy = Mathf.RoundToInt(uv.y * Size);
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                int x = cx + dx, y = cy + dy;
                if (x < 0 || y < 0 || x >= Size || y >= Size) continue;
                var idx = y * Size + x;
                if (px[idx].a < 0.5f) continue;
                px[idx] = Color.Lerp(px[idx], ink, 0.85f);
            }
        }
    }
}
