using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager;
using ValheimVillages.Villager.Records;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Recall a villager from the console: <c>vv_recall &lt;name|recordId&gt;</c>.
    ///
    ///     <para>Recall is the player's failsafe, and until now it existed only as a button in
    ///     the registry UI — which means it could only be exercised by a person standing at a
    ///     station, and could not be tested or used on a headless host at all. That made a
    ///     broken recall expensive to diagnose: every attempt needed the operator in-game, and
    ///     a failure told you nothing about which half had failed. This drives the same
    ///     host-side path the RPC handler uses, so it both verifies that half on its own and
    ///     gives an admin a way to recover a villager without the UI.</para>
    /// </summary>
    public static class RecallCommand
    {
        [DevCommand("Recall a villager to its village registry: vv_recall <name|recordId>",
            Name = "vv_recall")]
        public static void Recall(Terminal.ConsoleEventArgs args)
        {
            if (args == null || args.Length < 2)
            {
                ConsoleReport.Emit("[vv_recall] usage: vv_recall <name|recordId>");
                return;
            }

            var needle = args[1];
            VillagerRecord match = null;
            foreach (var record in VillagerRecordTable.EnumerateAll())
            {
                if (record == null) continue;
                if (!string.Equals(record.RecordId, needle, System.StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(record.Name, needle, System.StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(record.Type, needle, System.StringComparison.OrdinalIgnoreCase))
                    continue;
                match = record;
                break;
            }

            if (match == null)
            {
                ConsoleReport.Emit(
                    $"[vv_recall] no villager matching '{needle}' — see vv_records for names.");
                return;
            }

            // Recall to the village's own registry, which is where the UI button sends them.
            var village = VillageRegistry.FindById(match.Village);
            if (village == null)
            {
                ConsoleReport.Emit(
                    $"[vv_recall] {match.Name} belongs to village '{match.Village}', " +
                    "which is not loaded — nothing to recall it to.");
                return;
            }

            var destination = village.Anchor;
            if (village.TryGetAnchor(VillageAnchor.Registry, out var registry))
                destination = registry;

            VillagerRecallRpc.RecallOnHost(match, destination, out var report);
            ConsoleReport.Emit($"[vv_recall] {report}");
        }
    }
}
