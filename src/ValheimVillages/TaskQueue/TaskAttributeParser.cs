using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ValheimVillages.TaskQueue
{
    /// <summary>
    ///     Shared helpers for parsing task attribute dictionaries (e.g. position from prefix_x, prefix_y, prefix_z).
    /// </summary>
    public static class TaskAttributeParser
    {
        /// <summary>
        ///     Parse a Vector3 from task attributes with keys "{prefix}_x", "{prefix}_y", "{prefix}_z".
        ///     Uses invariant culture for float parsing.
        /// </summary>
        public static bool TryParsePosition(
            Dictionary<string, string> attrs, string prefix, out Vector3 result)
        {
            result = Vector3.zero;
            if (attrs == null) return false;

            if (!attrs.TryGetValue($"{prefix}_x", out var xs) ||
                !attrs.TryGetValue($"{prefix}_y", out var ys) ||
                !attrs.TryGetValue($"{prefix}_z", out var zs))
                return false;

            if (!float.TryParse(xs, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !float.TryParse(ys, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !float.TryParse(zs, NumberStyles.Float, CultureInfo.InvariantCulture, out var z))
                return false;

            result = new Vector3(x, y, z);
            return true;
        }
    }
}