using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     Named lookups for Valheim's own UI sprites, cached.
    ///
    ///     <para>Scanning every loaded sprite is expensive, and three different call sites were
    ///     each about to do their own scan with their own cache. One place also means one place
    ///     to be wrong about a name — which has already happened once: "trophies" is the
    ///     achievements CUP, and the shield is "trophy_board".</para>
    /// </summary>
    internal static class UiSprites
    {
        /// <summary>The shield plaque villagers' icons are mounted on, and the level badge.</summary>
        public const string CrestName = "trophy_board";

        /// <summary>Valheim's standard button plate, used for small label chips.</summary>
        public const string ButtonName = "button";

        /// <summary>
        ///     The star behind Valheim's own crafting-station level readout
        ///     (InventoryGui.m_craftingStationLevelRoot). Villager level badges use it so a
        ///     villager's level is drawn the same way the game draws a station's.
        /// </summary>
        public const string LevelStarName = "craft_icon_64";

        /// <summary>
        ///     The tint the native level icon applies to that star. The sprite itself is
        ///     greyscale; the gold comes entirely from the Image colour, so matching the look
        ///     means matching this too.
        /// </summary>
        public static readonly Color LevelStarTint = new(1f, 0.714f, 0.361f, 1f);

        public static Sprite LevelStar() => Find(LevelStarName);

        /// <summary>Width/height of <see cref="CrestName" /> (92x113), for sizing boxes.</summary>
        public const float CrestAspect = 92f / 113f;

        private static readonly Dictionary<string, Sprite> s_cache = new();
        private static readonly HashSet<string> s_warned = new();

        public static Sprite Crest() => Find(CrestName);

        public static Sprite Button() => Find(ButtonName);

        /// <summary>
        ///     A loaded sprite by exact name, or null. A miss warns ONCE — a renamed sprite is
        ///     something to go and fix, not to silently draw without.
        /// </summary>
        public static Sprite Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (s_cache.TryGetValue(name, out var cached) && cached != null) return cached;

            foreach (var sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sprite == null || sprite.name != name) continue;
                s_cache[name] = sprite;
                return sprite;
            }

            // Only hits are cached: a miss is usually "the UI atlas was not loaded yet", and
            // caching it would leave the sprite missing for the rest of the session.
            if (s_warned.Add(name))
                Plugin.Log?.LogWarning(
                    $"[UiSprites] no loaded sprite named '{name}' — anything using it will " +
                    "fall back to a plain plate. Did the game rename it?");

            return null;
        }
    }
}
