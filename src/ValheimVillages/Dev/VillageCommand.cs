using System;
using System.Collections.Generic;
using System.Linq;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villages;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Groups the "what is in this village" dumps under one verb:
    ///     <c>vv_village &lt;section&gt;</c>. Replaces the former <c>vv_anchors</c>,
    ///     <c>vv_stations</c>, <c>vv_pois</c> and <c>vv_workorders</c>.
    ///     <para>
    ///         These do NOT read one source — anchors and stations come from durable village
    ///         ZDO blobs, PoIs from a volatile in-memory registry rebuilt per partition, and
    ///         work orders from the village record — and their output does not overlap. They
    ///         are grouped for discoverability, not because they were redundant: answering
    ///         "what does this village think it has" previously meant knowing four unrelated
    ///         command names.
    ///     </para>
    ///     <para>
    ///         Sections marked <see cref="Section.TakesVillage" /> resolve the village at the
    ///         PLAYER by default, which is ambiguous the moment a world holds two. They accept
    ///         a trailing village id (any unique prefix) to target one explicitly; use
    ///         <c>vv_village list</c> to get the ids. <c>stations</c> and <c>orders</c> take no
    ///         id because they already enumerate every village's anchors / every live villager.
    ///     </para>
    /// </summary>
    public static class VillageCommand
    {
        private sealed class Section
        {
            public Section(string description, bool takesVillage, Action<Terminal.ConsoleEventArgs, Village> run)
            {
                Description = description;
                TakesVillage = takesVillage;
                Run = run;
            }

            public string Description { get; }

            /// <summary>True when a trailing village id selects which village this section reports on.</summary>
            public bool TakesVillage { get; }

            public Action<Terminal.ConsoleEventArgs, Village> Run { get; }
        }

        private static readonly Dictionary<string, Section> Sections =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["list"] = new(
                    "every village ZDO in the world + registry/record/order cross-reference",
                    false, (_, _) => VillageListCommand.List()),
                ["at"] = new(
                    "which village a position resolves to, and via which branch: vv_village at [x z [y]]",
                    false, (args, _) => VillageListCommand.At(args)),
                ["anchors"] = new(
                    "village anchor positions + pairwise connectivity matrix",
                    true, (_, village) => AnchorsDumpCommand.DumpAnchors(village)),
                ["stations"] = new(
                    "cached stations + HNA approach resolution per villager anchor (all villages)",
                    false, (_, _) => VillageStationRegistry.DumpStations()),
                ["pois"] = new(
                    "cached PoIs (fire/table/chair/farm)",
                    true, (_, village) => VillagePoiRegistry.DumpPois(village)),
                ["orders"] = new(
                    "work orders per villager + how many outputs exist in chests (all villagers)",
                    false, (_, _) => VillagerAI.DumpWorkOrders())
            };

        private static IEnumerable<string> SectionNames()
        {
            return Sections.Keys;
        }

        [DevCommand("Inspect a village: vv_village <list|at|anchors|stations|pois|orders> [villageId]",
            Name = "vv_village", OptionsProvider = nameof(SectionNames))]
        public static void Village(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2)
            {
                PrintSections();
                return;
            }

            var name = args[1];
            if (!Sections.TryGetValue(name, out var section))
            {
                Print($"[vv_village] unknown section '{name}'");
                PrintSections();
                return;
            }

            Villages.Entity.Village target = null;
            if (section.TakesVillage && args.Length >= 3 && !TryResolveVillage(args[2], out target)) return;

            section.Run(args, target);
        }

        /// <summary>
        ///     Resolves a village by id or unique id prefix. Fails loudly on a miss or an
        ///     ambiguous prefix rather than falling back to the player's village — silently
        ///     reporting on a different village than the one asked for is the exact failure
        ///     this section argument exists to rule out.
        /// </summary>
        private static bool TryResolveVillage(string token, out Villages.Entity.Village village)
        {
            village = VillageRegistry.FindById(token);
            if (village != null) return true;

            var matches = VillageRegistry.EnumerateAll()
                .Where(v => v.VillageId.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 1)
            {
                village = matches[0];
                return true;
            }

            Print(matches.Count == 0
                ? $"[vv_village] no village id matches '{token}' — run vv_village list"
                : $"[vv_village] '{token}' is ambiguous ({matches.Count} matches): " +
                  string.Join(", ", matches.Select(v => v.VillageId)));
            return false;
        }

        private static void PrintSections()
        {
            Print($"[vv_village] {Sections.Count} section(s) — vv_village <section> [villageId]");
            var width = Sections.Keys.Max(k => k.Length);
            foreach (var kvp in Sections.OrderBy(s => s.Key))
                Print($"  {kvp.Key.PadRight(width)}  {kvp.Value.Description}");
        }

        private static void Print(string line)
        {
            global::Console.instance?.Print(line);
        }
    }
}
