using UnityEngine;

namespace ValheimVillages.UI.Alerts
{
    /// <summary>
    ///     Procedurally draws the "!" badge that floats over a villager with something to say.
    ///     Generated rather than shipped as an asset for the same reason as the ransom-fragment
    ///     art: no asset bundle to load, no prefab to keep in sync, and it survives a hot reload
    ///     because it is rebuilt from code.
    /// </summary>
    internal static class AlertArt
    {
        private const int Size = 64;

        private static Sprite s_sprite;

        /// <summary>The shared badge sprite, built on first use.</summary>
        public static Sprite Sprite
        {
            get
            {
                // Unity's fake-null: a hot reload destroys the texture behind the sprite, so
                // compare against null rather than caching blindly.
                if (s_sprite != null) return s_sprite;

                var tex = Generate();
                s_sprite = Sprite.Create(
                    tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), Size);
                s_sprite.name = "vv_alert_sprite";
                return s_sprite;
            }
        }

        private static Texture2D Generate()
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "vv_alert_tex",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            // Warm amber disc with a darker rim, and a dark glyph — reads at a distance and
            // against both the grass and the night sky.
            var fill = new Color(0.98f, 0.78f, 0.24f);
            var rim = new Color(0.42f, 0.28f, 0.06f);
            var glyph = new Color(0.16f, 0.11f, 0.03f);
            var clear = new Color(0f, 0f, 0f, 0f);

            var px = new Color[Size * Size];
            for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
            {
                // u,v normalised with v measured from the TOP so the glyph reads naturally.
                var u = (x + 0.5f) / Size;
                var v = 1f - (y + 0.5f) / Size;

                var dx = u - 0.5f;
                var dy = v - 0.5f;
                var r = Mathf.Sqrt(dx * dx + dy * dy);

                Color c;
                if (r > 0.47f) c = clear;
                else if (r > 0.40f) c = rim;
                else c = fill;

                // The bar and the dot of the exclamation mark.
                var inBar = Mathf.Abs(dx) < 0.075f && v > 0.20f && v < 0.60f;
                var inDot = Mathf.Abs(dx) < 0.085f && v > 0.68f && v < 0.82f;
                if (r <= 0.40f && (inBar || inDot)) c = glyph;

                px[y * Size + x] = c;
            }

            tex.SetPixels(px);
            tex.Apply();
            return tex;
        }
    }
}
