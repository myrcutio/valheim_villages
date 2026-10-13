using System.Collections.Generic;
using System.Text;
using ValheimVillages.Attributes;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Diagnostic: which village passives are registered, which villages have EARNED each
    ///     one, and who would provide it otherwise.
    ///
    ///     <para>Exists because an earned passive has no in-world tell. Spawn protection in
    ///     particular was applying to any village at all, so there was no way to notice that
    ///     recruiting the Guard who is supposed to provide it changed nothing.</para>
    /// </summary>
    public static class PassiveDumpCommand
    {
        [DevCommand("Dump village passive effects and which villages have earned them",
            Name = "vv_passives")]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            var sb = new StringBuilder();

            var registered = AttributeScanner.GetAllPassives();
            sb.AppendLine($"[vv_passives] {registered.Count} registered, " +
                          $"{VillageAreaManager.AreaCount} village area(s)");

            foreach (var entry in registered)
            {
                var providers = VillagePassives.TypesProviding(entry.Key);
                sb.AppendLine($"- {entry.Key} ({entry.Value.DisplayName})  " +
                              $"provided by: {(providers.Count > 0 ? string.Join(", ", providers) : "(nobody)")}");
            }

            // Villages come from the RECORDS, not just the registered areas: a village with no
            // area yet still has villagers, and reporting only areas would show nothing while
            // implying nothing is there. The area is called out separately because the spawn
            // path needs one to resolve a position to a village at all.
            var villages = new List<string>();
            foreach (var record in Villager.Records.VillagerRecordTable.EnumerateAll())
            {
                if (record == null || !record.IsValid || string.IsNullOrEmpty(record.Village)) continue;
                if (!villages.Contains(record.Village)) villages.Add(record.Village);
            }

            foreach (var area in VillageAreaManager.AllAreas)
                if (!string.IsNullOrEmpty(area.VillageId) && !villages.Contains(area.VillageId))
                    villages.Add(area.VillageId);

            if (villages.Count == 0)
            {
                sb.AppendLine("  (no villages known on this peer)");
                Dev.ConsoleReport.Emit(sb.ToString().TrimEnd());
                return;
            }

            foreach (var villageId in villages)
            {
                var hasArea = VillageAreaManager.TryGetAreaBounds(villageId, out _, out _, out _, out _);
                var earned = VillagePassives.EarnedWithProviders(villageId);
                sb.AppendLine($"- village {Short(villageId)}  area={(hasArea ? "yes" : "NO — spawn path cannot match a position here")}");

                if (earned.Count == 0)
                {
                    sb.AppendLine("    earned: (none — no living villager here provides one)");
                    continue;
                }

                foreach (var e in earned)
                    sb.AppendLine($"    earned: {e.Key}  from {string.Join(", ", e.Value)}");
            }

            Dev.ConsoleReport.Emit(sb.ToString().TrimEnd());
        }

        private static string Short(string villageId)
        {
            if (string.IsNullOrEmpty(villageId)) return "(none)";
            var dash = villageId.IndexOf('-');
            return dash > 0 ? villageId.Substring(0, dash) : villageId;
        }
    }
}
