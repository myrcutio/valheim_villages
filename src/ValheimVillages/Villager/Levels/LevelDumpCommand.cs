using System.Text;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.Villager.Levels
{
    /// <summary>
    ///     Diagnostic: print every villager record's derived level, where it could reach, and
    ///     the steps still outstanding. Record-authoritative, so it covers villagers whose NPC
    ///     is not loaded on this peer.
    ///
    ///     <para>Levels are derived rather than stored, so there is nothing to inspect in a ZDO
    ///     — this command is the only way to see what the UI will say and why.</para>
    /// </summary>
    public static class LevelDumpCommand
    {
        /// <summary>
        ///     Report cap. A headless server blocks its main thread on an oversized stdout
        ///     write, so the dump stays short by refusing to grow rather than by truncating
        ///     mid-villager.
        /// </summary>
        private const int MaxVillagers = 40;

        [DevCommand("Dump each villager's derived level, potential and outstanding steps",
            Name = "vv_levels")]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            var sb = new StringBuilder();
            var shown = 0;
            var total = 0;

            foreach (var record in VillagerRecordTable.EnumerateAll())
            {
                total++;
                if (!record.IsValid || shown >= MaxVillagers) continue;
                shown++;

                var def = Registry.VillagerRegistry.Get(record.Type);
                var state = VillagerLevel.Evaluate(record);
                var comfort = state.Comfort >= 0 ? $" comfort={state.Comfort}" : "";

                sb.AppendLine($"- {record.Type,-12} lvl {state.Current}/{state.Potential}" +
                              $"  scaling={def?.scalingType ?? "?"}{comfort}" +
                              $"  village={Short(record.Village)}");

                var benefit = state.Benefit;
                sb.AppendLine(benefit != null && !string.IsNullOrEmpty(benefit.description)
                    ? $"    now: {benefit.description}"
                    : "    now: (no benefit at this level)");

                foreach (var c in state.Contributors)
                    sb.AppendLine($"    {(c.Satisfied ? "[x]" : "[ ]")} {c.Label}");
            }

            if (total == 0) sb.AppendLine("  (no villager records)");
            else if (total > shown) sb.AppendLine($"  ... {total - shown} more not shown");

            Dev.ConsoleReport.Emit(sb.ToString().TrimEnd());
        }

        /// <summary>First segment of a village GUID — enough to tell two villages apart.</summary>
        private static string Short(string villageId)
        {
            if (string.IsNullOrEmpty(villageId)) return "(none)";
            var dash = villageId.IndexOf('-');
            return dash > 0 ? villageId.Substring(0, dash) : villageId;
        }
    }
}
