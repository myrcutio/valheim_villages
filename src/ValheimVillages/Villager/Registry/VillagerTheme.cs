using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using ValheimVillages.Schemas;

namespace ValheimVillages.Villager.Registry
{
    /// <summary>
    ///     A villager type's signature colour, authored as <c>themeColor</c> on its definition.
    ///
    ///     <para>Deliberately NOT blanket-specific. The cot's bedding is the first consumer, but
    ///     the same colour is meant to drive the villager's own clothing later, so it lives on
    ///     the definition as one shared identity rather than as a furniture detail.</para>
    ///
    ///     <para>Not to be confused with <c>skinColor</c>, which is a different concept (and is
    ///     currently read by nothing — <c>VisEquipment.SetSkinColor</c> is gated to players, so
    ///     it never applied to a Dvergr-bodied villager in the first place).</para>
    /// </summary>
    internal static class VillagerTheme
    {
        /// <summary>
        ///     The theme colour for <paramref name="villagerType" />.
        ///
        ///     <para>Throws rather than substituting a placeholder: every definition is checked
        ///     by <see cref="Validate" /> at registry load, so reaching here with an unparseable
        ///     colour means the registry was bypassed, not that a default is wanted.</para>
        /// </summary>
        public static Color ColorFor(string villagerType)
        {
            var def = VillagerRegistry.Get(villagerType);
            if (def == null)
                throw new System.InvalidOperationException(
                    $"No villager definition named '{villagerType}'");

            if (!TryParse(def.themeColor, out var color))
                throw new System.InvalidOperationException(
                    $"{villagerType}.themeColor is '{def.themeColor}', which is not three " +
                    "comma-separated 0..1 floats");

            return color;
        }

        /// <summary>"r,g,b" with each component in 0..1. Invariant culture, so a comma decimal
        /// separator in the user's locale cannot silently reinterpret the string.</summary>
        public static bool TryParse(string raw, out Color color)
        {
            color = Color.white;
            if (string.IsNullOrWhiteSpace(raw)) return false;

            var parts = raw.Split(',');
            if (parts.Length != 3) return false;

            var rgb = new float[3];
            for (var i = 0; i < 3; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float,
                        CultureInfo.InvariantCulture, out var v))
                    return false;
                if (v < 0f || v > 1f) return false;
                rgb[i] = v;
            }

            color = new Color(rgb[0], rgb[1], rgb[2], 1f);
            return true;
        }

        /// <summary>
        ///     Every definition must carry a usable theme colour, checked once at registry load
        ///     alongside the level graph. Disabled types are checked too — they are authored data
        ///     that becomes live the moment the flag flips, and a colour that only fails then is
        ///     a trap.
        /// </summary>
        public static void Validate(IReadOnlyDictionary<string, VillagerDef> defs)
        {
            var errors = new List<string>();
            foreach (var entry in defs)
                if (!TryParse(entry.Value.themeColor, out _))
                    errors.Add($"{entry.Key}.themeColor is '{entry.Value.themeColor}', " +
                               "expected three comma-separated 0..1 floats");

            if (errors.Count == 0) return;

            var message = new StringBuilder("Villager theme colours are invalid:");
            foreach (var error in errors)
                message.Append("\n  - ").Append(error);
            throw new System.InvalidOperationException(message.ToString());
        }
    }
}
