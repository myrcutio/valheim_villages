using UnityEngine;

namespace ValheimVillages.Abilities
{
    /// <summary>
    ///     Status effect for the Mountaineer's "Mountain Stride" ability: while active the
    ///     player neither slides down steep ground nor pays the usual price for climbing it.
    ///
    ///     <para>Runs on a health-potion cadence — 1 minute up, 2 minutes down — rather than the
    ///     original 5/20. At 33% uptime it is a tactical burst instead of an expedition buff, so
    ///     it has to be STRONG for that minute or the shorter duration is just a downgrade; hence
    ///     the climb assist below rather than slide immunity alone. The old 20-minute cooldown
    ///     also lived in a non-persisted static, so a relog wiped it; at 2 minutes that stops
    ///     being worth exploiting.</para>
    ///
    ///     <para>Slide immunity is the <c>ApplySlide</c> prefix in MountaineerPatches. The climb
    ///     assist is implemented as StatusEffect overrides instead of new Harmony patches,
    ///     because the engine already routes speed, stamina and fall damage through them.</para>
    /// </summary>
    public class SE_MountainStride : StatusEffect
    {
        public const string EffectName = "SE_MountainStride";
        public const float Duration = 60f; // 1 minute — health-potion cadence
        public const float Cooldown = 120f; // 2 minutes

        /// <summary>
        ///     Upward component of the movement direction that counts as climbing. Small but
        ///     non-zero so the assist does not fire while crossing flat ground.
        /// </summary>
        private const float AscentThreshold = 0.1f;

        /// <summary>
        ///     Share of the normal run-stamina drain paid while ascending. The stamina cost of a
        ///     steep climb, not the slide, is what actually stops a player going up.
        /// </summary>
        private const float AscentStaminaFactor = 0.25f;

        /// <summary>Share of fall damage taken while the buff is up — reduced, never zero.</summary>
        private const float FallDamageFactor = 0.25f;

        private static Sprite s_icon;

        /// <summary>
        ///     The ability's icon sprite, built once and cached. Used both by the
        ///     status-effect HUD (top-centre buff) and the guardian-power-style
        ///     ability slot, which needs it before the effect is ever applied.
        /// </summary>
        public static Sprite Icon => s_icon != null ? s_icon : s_icon = CreateBuffIcon();

        public SE_MountainStride()
        {
            m_name = EffectName;
            m_ttl = Duration;
            m_tooltip = "Climb steep ground without sliding, at a quarter of the stamina";
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
                m_icon = Icon;
        }

        /// <summary>
        ///     Cancel the engine's uphill speed penalty while climbing. Raises the result to the
        ///     unmodified base rather than above it — this is meant to stop a slope slowing you
        ///     down, not to make you faster than level ground.
        /// </summary>
        public override void ModifySpeed(float baseSpeed, ref float speed, Character character, Vector3 dir)
        {
            if (dir.y <= AscentThreshold) return;
            if (speed < baseSpeed) speed = baseSpeed;
        }

        /// <summary>
        ///     Cut the stamina cost of going up. This is the override that actually delivers
        ///     "easier to climb": <c>Character.GetSlideAngle()</c> is a hardcoded 38 degrees for
        ///     players while ground contact survives to roughly 84, so a steep face was always
        ///     walkable in principle — what stopped it was running out of stamina partway.
        /// </summary>
        public override void ModifyRunStaminaDrain(float baseDrain, ref float drain, Vector3 dir)
        {
            if (dir.y <= AscentThreshold) return;
            drain *= AscentStaminaFactor;
        }

        /// <summary>
        ///     Soften falls for the duration. Climbing is only worth attempting if coming off the
        ///     face is survivable, but immunity would turn the buff into a travel exploit, so
        ///     this scales the damage rather than removing it.
        /// </summary>
        public override void ModifyFallDamage(float baseDamage, ref float damage)
        {
            if (damage <= 0f) return;
            damage *= FallDamageFactor;
        }

        /// <summary>
        ///     Create a simple mountain-themed icon sprite for the buff HUD.
        /// </summary>
        private static Sprite CreateBuffIcon()
        {
            // TODO: this is gross and bad, and the AI should feel bad.
            var size = 32;
            var tex = new Texture2D(size, size);
            var pixels = new Color[size * size];

            var bg = new Color(0.2f, 0.35f, 0.5f, 0.9f);
            var mountain = new Color(0.7f, 0.7f, 0.75f, 1f);
            var snow = new Color(0.95f, 0.95f, 1f, 1f);

            // Fill background
            for (var i = 0; i < pixels.Length; i++)
                pixels[i] = bg;

            // Draw a simple mountain triangle
            for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var centerX = size / 2;
                var baseY = 6;
                var peakY = 26;
                var progress = (float)(y - baseY) / (peakY - baseY);

                if (y >= baseY && y <= peakY)
                {
                    var halfWidth = (1f - progress) * (size / 2f - 2);
                    if (Mathf.Abs(x - centerX) <= halfWidth) pixels[y * size + x] = y > peakY - 6 ? snow : mountain;
                }
            }

            tex.SetPixels(pixels);
            tex.Apply();

            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }
    }
}