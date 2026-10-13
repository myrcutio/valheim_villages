using System.Linq;
using System.Text;
using ValheimVillages.Attributes;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Dump each villager's blocked entries with the flag that decides ⚠ vs ⏳.
    ///
    ///     <para>Exists because that flag is set on the HOST, serialized into the villager's
    ///     ZDO, and re-parsed on the CLIENT — three places it can be lost, and the symptom at
    ///     every one of them is identical: an alert where an hourglass belongs.</para>
    /// </summary>
    internal static class BlockedEntriesCommand
    {
        [DevCommand("Dump blocked activity entries + their waiting flag: vv_blocked [name]",
            Name = "vv_blocked")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var wanted = args != null && args.Length > 1 ? args[1] : null;
            var sb = new StringBuilder();

            foreach (var record in VillagerRecordTable.EnumerateAll()
                         .Where(r => r.Status == RecordStatus.Alive)
                         .OrderBy(r => r.Name))
            {
                if (!string.IsNullOrEmpty(wanted) &&
                    !string.Equals(record.Name, wanted, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                var blocked = VillagerActivityLog.Instance
                    .GetEntries(record.RecordId)
                    .Where(e => e.Action == "blocked")
                    .ToList();

                // Village + id, because every settlement has a villager called "Farmer" and a
                // bare name cannot tell two of them apart.
                sb.AppendLine($"  {record.Name} ({record.Type}) id={Short(record.RecordId)} " +
                              $"village={Short(record.Village)} — {blocked.Count} blocked");
                foreach (var e in blocked)
                {
                    // A blocked entry with NO position gets no map pin at all —
                    // BuildAllIssuePins skips it — so the position is the thing to look at
                    // when an order is blocked but missing from the map.
                    var at = e.WorkOrderPosX.HasValue && e.WorkOrderPosY.HasValue &&
                             e.WorkOrderPosZ.HasValue
                        ? $"({e.WorkOrderPosX.Value:F0},{e.WorkOrderPosZ.Value:F0})"
                        : "NO-POS (will not be pinned)";

                    sb.AppendLine(
                        $"    {(e.WaitingOnTime ? "WAIT " : "ALERT")} {at,-26} " +
                        $"{e.ItemPrefab ?? "?"} [{e.StationName ?? "-"}] — {Trim(e.Reason)}");
                }
            }

            Print(sb.Length > 0 ? "[vv_blocked]\n" + sb.ToString().TrimEnd() : "[vv_blocked] none");
        }

        private static string Short(string id)
        {
            return string.IsNullOrEmpty(id) ? "-"
                : id.Length <= 8 ? id : id.Substring(0, 8);
        }

        private static string Trim(string reason)
        {
            if (string.IsNullOrEmpty(reason)) return "(no reason)";
            return reason.Length <= 70 ? reason : reason.Substring(0, 70) + "…";
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
