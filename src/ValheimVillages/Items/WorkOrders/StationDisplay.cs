using System.Globalization;

namespace ValheimVillages.Items.WorkOrders
{
    /// <summary>
    ///     Formats internal station tokens ($vv_blacksmith, $piece_forge, ...)
    ///     into readable names ("Blacksmith", "Forge") for UI display.
    /// </summary>
    public static class StationDisplay
    {
        public static string Pretty(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "?";

            var clean = raw
                .Replace("$piece_", "")
                .Replace("$vv_", "")
                .Replace("_", " ");
            return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(clean);
        }
    }
}
