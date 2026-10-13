using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Show every work order a villager can see, with the quota recorded against it and the
    ///     stock the scan actually counts: <c>vv_orderstock [villagerType]</c>.
    ///
    ///     <para>Exists because "nothing to do — every order is stocked" and a visibly empty chest
    ///     cannot both be right, and the panel only reports the conclusion. This reproduces the
    ///     inputs the scan reaches that conclusion from — the same
    ///     <see cref="ContainerScanner.FindAllWorkOrders" /> and
    ///     <see cref="ContainerScanner.CountAcrossContainers" /> calls — so a disagreement between
    ///     min, max and have is visible rather than inferred.</para>
    /// </summary>
    internal static class OrderStockCommand
    {
        [DevCommand("Show work orders with their quota and counted stock: vv_orderstock [type]",
            Name = "vv_orderstock")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var wanted = args != null && args.Length > 1 ? args[1] : null;

            var bridges = Object.FindObjectsByType<UI.Interaction.VillagerBehaviorBridge>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            var sb = new StringBuilder();
            foreach (var bridge in bridges)
            {
                if (bridge == null) continue;
                if (!string.IsNullOrEmpty(wanted) &&
                    !string.Equals(bridge.VillagerType, wanted,
                        System.StringComparison.OrdinalIgnoreCase))
                    continue;

                var ai = bridge.AI;
                if (ai == null) continue;

                var anchor = ai.HomeAnchor;
                // The SAME lookup WorkOrderScanHandler uses, so a village mismatch shows up
                // here rather than being papered over by a different resolver.
                var village = Villages.Entity.VillageRegistry.GetVillageAt(anchor);
                var covering = Villages.Entity.VillageRegistry.GetVillageCovering(anchor);
                if (village == null)
                {
                    sb.AppendLine($"  {bridge.VillagerType}: no village at anchor");
                    continue;
                }

                var containers = ContainerScanner.FindVillageContainers(
                    anchor, Settings.WorkSettings.ChestScanRadius);

                var matches = ContainerScanner.FindAllWorkOrders(village, bridge.VillagerType);
                sb.AppendLine(
                    $"  {bridge.VillagerType} anchor=({anchor.x:F0},{anchor.z:F0}) " +
                    $"village={village.VillageId} " +
                    $"covering={(covering != null ? covering.VillageId : "-")} " +
                    $"containers={containers.Count} orders={(matches == null ? 0 : matches.Count)}");

                if (matches == null) continue;
                foreach (var m in matches)
                {
                    if (m?.ItemPrefabName == null) continue;

                    var have = ContainerScanner.CountAcrossContainers(containers, m.ItemPrefabName);
                    var short_ = have < m.MinQuantity;
                    sb.AppendLine(
                        $"    {m.ItemPrefabName,-22} station='{m.StationName}' " +
                        $"min={m.MinQuantity} max={m.MaxQuantity} have={have} " +
                        $"-> {(short_ ? "SHORT" : "stocked")}");
                }
            }

            Print(sb.Length > 0
                ? "[vv_orderstock]\n" + sb.ToString().TrimEnd()
                : $"[vv_orderstock] no villager matched '{wanted}'");
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
