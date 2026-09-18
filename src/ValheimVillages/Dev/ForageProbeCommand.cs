using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Work;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Diagnostic: every pickable inside each villager's village, and each gate the forage
    ///     search applies to it — what it yields, whether it is ripe, and whether a walkable
    ///     approach resolves.
    ///
    ///     <para>Exists for the same reason <see cref="BeehiveProbeCommand" /> does: the scan
    ///     can only report one flat reason ("No ripe X in village") for several different
    ///     causes — outside the village footprint, wrong yield, already picked and waiting on
    ///     its respawn timer, or no reachable standing spot beside it. Guessing between those
    ///     costs a round-trip each time.</para>
    /// </summary>
    public static class ForageProbeCommand
    {
        [DevCommand("Dump pickables in each villager's village + why they are/aren't " +
                    "harvestable: vv_forageprobe [item]", Name = "vv_forageprobe")]
        public static void Probe(Terminal.ConsoleEventArgs args)
        {
            var expected = args.Length >= 2 ? args[1] : null;
            var sb = new StringBuilder();
            var radius = WorkSettings.ChestScanRadius;

            foreach (var ai in VillagerAIManager.ActiveVillagers.Values)
            {
                if (ai == null) continue;
                var anchor = ai.HomeAnchor;

                var village = VillageRegistry.GetVillageAt(anchor)
                              ?? VillageRegistry.FindNearAnchor(anchor);
                var scope = village != null && village.TryGetFootprint(
                    out var minX, out var minZ, out var maxX, out var maxZ)
                    ? $"footprint x[{minX:F0}..{maxX:F0}] z[{minZ:F0}..{maxZ:F0}]"
                    : $"NO FOOTPRINT — falling back to {radius:F0}m radius";

                sb.AppendLine(
                    $"- {ai.NpcName} anchor=({anchor.x:F1},{anchor.z:F1}) {scope}" +
                    (expected != null ? $" expecting '{expected}'" : ""));

                // Every RIPE plant first (this is exactly what the order scan sees), then the
                // reachability gate per plant, which is the half that fails silently.
                var ripe = ForageHelper.FindRipeInVillage(anchor, radius, expected);
                var graph = VillageRegistry.GraphAt(anchor);

                foreach (var p in ripe)
                {
                    var pos = p.transform.position;
                    var yield = ForageHelper.YieldOf(p) ?? "(none)";
                    var dist = Vector3.Distance(pos, anchor);
                    var region = graph?.PointToRegionId(pos) ?? "UNRESOLVED";

                    var selected = ForageHelper.TryFindHarvestable(
                                       anchor, radius, yield, out var picked, out var approach)
                                   && ReferenceEquals(picked, p);

                    sb.AppendLine(
                        $"    {p.gameObject.name} @({pos.x:F1},{pos.y:F1},{pos.z:F1}) d={dist:F1}m " +
                        $"yield={yield} region={region}");
                    sb.AppendLine(
                        $"      nearest-selected={selected}" +
                        (selected ? $" approach=({approach.x:F1},{approach.y:F1},{approach.z:F1})" : ""));
                }

                if (ripe.Count == 0)
                    sb.AppendLine(expected != null
                        ? $"    (nothing ripe yielding '{expected}' in this village)"
                        : "    (nothing ripe in this village)");
            }

            if (sb.Length == 0) sb.AppendLine("no villagers loaded on this peer");
            Print("[vv_forageprobe]\n" + sb);
        }

        private static void Print(string s)
        {
            global::Console.instance?.Print(s);
            Plugin.Log?.LogInfo(s);
        }
    }
}
