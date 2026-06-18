using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ValheimVillages.Villages.Entity
{
    /// <summary>
    ///     Serialization of <see cref="VillageAnchor" /> lists for ZDO persistence.
    ///     Format: records separated by <see cref="RecordSep" />, each record is
    ///     "name|x|y|z" with fields separated by <see cref="FieldSep" />. Full XYZ is
    ///     written with the round-trip ("R") float format under
    ///     <see cref="CultureInfo.InvariantCulture" /> so values survive a save/reload
    ///     bit-for-bit. Names are sanitized of the delimiter chars so they can never
    ///     break the framing.
    /// </summary>
    public static class VillageAnchorPersistence
    {
        private const char FieldSep = '|';
        private const char RecordSep = ';';

        public static string Serialize(IEnumerable<VillageAnchor> anchors)
        {
            if (anchors == null) return "";
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            var first = true;
            foreach (var a in anchors)
            {
                if (string.IsNullOrEmpty(a.Name)) continue;
                if (!first) sb.Append(RecordSep);
                first = false;
                sb.Append(Sanitize(a.Name)).Append(FieldSep)
                    .Append(a.Position.x.ToString("R", inv)).Append(FieldSep)
                    .Append(a.Position.y.ToString("R", inv)).Append(FieldSep)
                    .Append(a.Position.z.ToString("R", inv));
            }

            return sb.ToString();
        }

        /// <summary>
        ///     Parse <paramref name="data" /> into <paramref name="outAnchors" /> (cleared
        ///     first). Returns true if at least one anchor parsed; false for empty/garbage
        ///     so callers can wipe + rebuild.
        /// </summary>
        public static bool Restore(string data, List<VillageAnchor> outAnchors)
        {
            outAnchors.Clear();
            if (string.IsNullOrEmpty(data)) return false;

            var inv = CultureInfo.InvariantCulture;
            var records = data.Split(RecordSep);
            foreach (var record in records)
            {
                if (string.IsNullOrEmpty(record)) continue;
                var f = record.Split(FieldSep);
                if (f.Length < 4) continue;

                var name = f[0];
                if (string.IsNullOrEmpty(name)) continue;
                if (!float.TryParse(f[1], NumberStyles.Float, inv, out var x)) continue;
                if (!float.TryParse(f[2], NumberStyles.Float, inv, out var y)) continue;
                if (!float.TryParse(f[3], NumberStyles.Float, inv, out var z)) continue;

                outAnchors.Add(new VillageAnchor(name, new Vector3(x, y, z)));
            }

            return outAnchors.Count > 0;
        }

        private static string Sanitize(string name)
        {
            return name.Replace(FieldSep, '_').Replace(RecordSep, '_');
        }
    }
}
