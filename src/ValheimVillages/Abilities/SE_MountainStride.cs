using UnityEngine;

namespace ValheimVillages.Abilities
{
    /// <summary>
    /// Status effect for the Mountaineer's "Mountain Stride" ability.
    /// While active, the player is immune to sliding on steep surfaces.
    /// Duration: 5 minutes. Cooldown: 20 minutes.
    /// </summary>
    public class SE_MountainStride : StatusEffect
    {
        public const string EffectName = "SE_MountainStride";
        public const float Duration = 300f; // 5 minutes
        public const float Cooldown = 1200f; // 20 minutes

        public SE_MountainStride()
        {
            m_name = EffectName;
            m_ttl = Duration;
            m_tooltip = "Immune to sliding on steep terrain";
            m_startMessageType = MessageHud.MessageType.Center;
            m_startMessage = "Mountain Stride activated";
            m_stopMessageType = MessageHud.MessageType.Center;
            m_stopMessage = "Mountain Stride faded";
        }

        public override void Setup(Character character)
        {
            base.Setup(character);

            // Generate a simple icon texture for the HUD
            if (m_icon == null)
                m_icon = CreateBuffIcon();
        }

        /// <summary>
        /// Create a simple mountain-themed icon sprite for the buff HUD.
        /// </summary>
        private static Sprite CreateBuffIcon()
        {
            int size = 32;
            var tex = new Texture2D(size, size);
            var pixels = new Color[size * size];

            var bg = new Color(0.2f, 0.35f, 0.5f, 0.9f);
            var mountain = new Color(0.7f, 0.7f, 0.75f, 1f);
            var snow = new Color(0.95f, 0.95f, 1f, 1f);

            // Fill background
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = bg;

            // Draw a simple mountain triangle
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int centerX = size / 2;
                    int baseY = 6;
                    int peakY = 26;
                    float progress = (float)(y - baseY) / (peakY - baseY);

                    if (y >= baseY && y <= peakY)
                    {
                        float halfWidth = (1f - progress) * (size / 2f - 2);
                        if (Mathf.Abs(x - centerX) <= halfWidth)
                        {
                            pixels[y * size + x] = y > peakY - 6 ? snow : mountain;
                        }
                    }
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }
    }
}
