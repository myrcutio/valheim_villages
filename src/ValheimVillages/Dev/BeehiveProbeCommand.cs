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
    ///     Diagnostic: every beehive-family piece near a villager's anchor, and each gate the
    ///     harvest search applies to it — yield, current level, distance, and whether a
    ///     walkable approach resolves.
    ///
    ///     <para>Exists because the scan can only report one flat reason ("No station 'Beehive'
    ///     in village") for four different causes: out of range, wrong yield (piece_birdnest
    ///     shares the Beehive component), empty, or no reachable standing spot. Guessing
    ///     between those cost a round-trip each time.</para>
    /// </summary>
    public static class BeehiveProbeCommand
    {
        [DevCommand("Dump beehives near each villager + why they are/aren't harvestable: " +
                    "vv_beeprobe [item]", Name = "vv_beeprobe")]
        public static void Probe(Terminal.ConsoleEventArgs args)
        {
            var expected = args.Length >= 2 ? args[1] : null;
            var sb = new StringBuilder();
            var radius = WorkSettings.ChestScanRadius;

            foreach (var ai in VillagerAIManager.ActiveVillagers.Values)
            {
                if (ai == null) continue;
                var anchor = ai.HomeAnchor;
                sb.AppendLine(
                    $"- {ai.NpcName} anchor=({anchor.x:F1},{anchor.z:F1}) radius={radius:F0}m" +
                    (expected != null ? $" expecting '{expected}'" : ""));

                var found = 0;
                foreach (var h in PhysicsHelper.GetAllInRadius<Beehive>(anchor, radius))
                {
                    if (h == null) continue;
                    found++;
                    var pos = h.transform.position;
                    var yield = BeehiveHelper.YieldOf(h) ?? "(none)";
                    var level = BeehiveHelper.GetHoneyLevel(h);
                    var dist = Vector3.Distance(pos, anchor);
                    var graph = VillageRegistry.GraphAt(anchor);
                    var region = graph?.PointToRegionId(pos) ?? "UNRESOLVED";

                    var reachable = BeehiveHelper.TryFindHarvestable(
                        anchor, radius, yield, out var picked, out var approach)
                        && ReferenceEquals(picked, h);

                    sb.AppendLine(
                        $"    {h.gameObject.name} @({pos.x:F1},{pos.y:F1},{pos.z:F1}) d={dist:F1}m " +
                        $"yield={yield} level={level} region={region}");
                    sb.AppendLine(
                        $"      selected={reachable}" +
                        (reachable ? $" approach=({approach.x:F1},{approach.y:F1},{approach.z:F1})" : "") +
                        (expected != null && yield != expected ? "  [wrong yield for this order]" : "") +
                        (level <= 0 ? "  [empty]" : ""));
                }

                if (found == 0) sb.AppendLine("    (no beehive-family pieces in range)");
            }

            if (sb.Length == 0) sb.AppendLine("no villagers loaded on this peer");
            Print("[vv_beeprobe]\n" + sb);
        }

        private static void Print(string s)
        {
            global::Console.instance?.Print(s);
            Plugin.Log?.LogInfo(s);
        }
    }
}
