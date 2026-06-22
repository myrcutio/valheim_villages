using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ValheimVillages.Villages.Entity
{
    /// <summary>
    ///     Serialization of <see cref="WorkOrderEntry" /> lists for the village ZDO blob,
    ///     using the same framing as <see cref="VillageAnchorPersistence" />: records
    ///     separated by <see cref="RecordSep" />, each record "station|item|itemDisplay|min|max"
    ///     with fields separated by <see cref="FieldSep" />. Ints under
    ///     <see cref="CultureInfo.InvariantCulture" />; string fields are sanitized of the
    ///     delimiter chars so they can never break the framing.
    /// </summary>
    public static class WorkOrderPersistence
    {
        private const char FieldSep = '|';
        private const char RecordSep = ';';

        public static string Serialize(IEnumerable<WorkOrderEntry> orders)
        {
            if (orders == null) return "";
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            var first = true;
            foreach (var o in orders)
            {
                if (string.IsNullOrEmpty(o.Station) || string.IsNullOrEmpty(o.Item)) continue;
                if (!first) sb.Append(RecordSep);
                first = false;
                sb.Append(Sanitize(o.Station)).Append(FieldSep)
                    .Append(Sanitize(o.Item)).Append(FieldSep)
                    .Append(Sanitize(o.ItemDisplay ?? "")).Append(FieldSep)
                    .Append(o.Min.ToString(inv)).Append(FieldSep)
                    .Append(o.Max.ToString(inv));
            }

            return sb.ToString();
        }

        /// <summary>
        ///     Parse <paramref name="data" /> into <paramref name="outOrders" /> (cleared
        ///     first). Returns true if at least one order parsed; false for empty/garbage.
        /// </summary>
        public static bool Restore(string data, List<WorkOrderEntry> outOrders)
        {
            outOrders.Clear();
            if (string.IsNullOrEmpty(data)) return false;

            var inv = CultureInfo.InvariantCulture;
            foreach (var record in data.Split(RecordSep))
            {
                if (string.IsNullOrEmpty(record)) continue;
                var f = record.Split(FieldSep);
                if (f.Length < 5) continue;

                var station = f[0];
                var item = f[1];
                if (string.IsNullOrEmpty(station) || string.IsNullOrEmpty(item)) continue;
                var display = f[2];
                if (!int.TryParse(f[3], NumberStyles.Integer, inv, out var min)) continue;
                if (!int.TryParse(f[4], NumberStyles.Integer, inv, out var max)) continue;

                outOrders.Add(new WorkOrderEntry(station, item, display, min, max));
            }

            return outOrders.Count > 0;
        }

        private static string Sanitize(string s)
        {
            return s.Replace(FieldSep, '_').Replace(RecordSep, '_');
        }
    }
}
