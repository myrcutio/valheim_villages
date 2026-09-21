using System.Collections.Generic;
using System.Text;
using ValheimVillages.Attributes;
using ValheimVillages.Items;
using ValheimVillages.Settings;
using ValheimVillages.Villager;
using ValheimVillages.Villager.AI.Work;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Place or remove a work order without the UI.
    ///
    ///     <para><c>vv_village orders</c> reads them; this writes them. Setting an order is
    ///     otherwise a five-click journey through the station panel with a player character
    ///     standing at the station — impossible on a headless server, which is exactly where
    ///     the villager side of the order has to be tested.</para>
    ///
    ///     <para>Goes through the same routed RPC the UI uses, so the host stays the only
    ///     writer of the village record.</para>
    /// </summary>
    public static class WorkOrderCommand
    {
        [DevCommand("Set or delete a work order: vv_order <station> <item> [min] [max] | " +
                    "vv_order delete <station> <item>", Name = "vv_order")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var sb = new StringBuilder();
            var argv = args?.Args ?? new string[0];

            if (argv.Length < 3)
            {
                Print("[vv_order] usage: vv_order <station> <item> [min] [max] | " +
                      "vv_order delete <station> <item>");
                return;
            }

            var deleting = argv[1].ToLowerInvariant() == "delete";
            var offset = deleting ? 2 : 1;
            if (argv.Length < offset + 2)
            {
                Print("[vv_order] needs a station and an item");
                return;
            }

            var station = argv[offset];
            var item = argv[offset + 1];
            var min = argv.Length > offset + 2 && int.TryParse(argv[offset + 2], out var lo) ? lo : 1;
            var max = argv.Length > offset + 3 && int.TryParse(argv[offset + 3], out var hi) ? hi : 5;

            foreach (var village in new List<Village>(VillageRegistry.EnumerateAll()))
            {
                var villageId = village.VillageId;
                if (string.IsNullOrEmpty(villageId)) continue;

                if (deleting)
                {
                    WorkOrderConfigRpc.RequestDelete(villageId, station, item);
                    sb.AppendLine($"[vv_order] deleted {item} @ {station} in {villageId}");
                    continue;
                }

                WorkOrderConfigRpc.RequestSet(villageId, station, item, item, min, max);
                sb.AppendLine($"[vv_order] set {item} @ {station} = {min}..{max} in {villageId}" +
                              $"{EnsureToken(village, station, item)}");
            }

            Print(sb.Length > 0 ? sb.ToString() : "[vv_order] no villages");
        }

        private static void Print(string text)
        {
            // Capped + chunked: a single oversized write to a headless server's
            // stdout pipe blocks the main thread. See ConsoleReport.
            ValheimVillages.Dev.ConsoleReport.Emit(text);
        }

        /// <summary>
        ///     Put this station's work-order token in a chest if the village has none.
        ///
        ///     <para>The record alone is not enough: a configured order with no token in any
        ///     chest is released on the next adoption pass ("no token"), because the token is
        ///     what ties the order to a village and to the chest its goods belong in. Setting
        ///     one from the console therefore has to place the token too, exactly as a player
        ///     does before opening the order panel.</para>
        /// </summary>
        private static string EnsureToken(Village village, string station, string item)
        {
            if (!ItemFactory.BuildStationWorkOrderMap().TryGetValue(station, out var token))
                return $" (no token item known for {station})";

            var containers = ContainerScanner.FindVillageContainers(
                village.Anchor, WorkSettings.HaulScanRadius);

            // A token is only a token once it carries the order's identity, so the check is
            // for a STAMPED one — counting the prefab alone would see a blank scroll (or one
            // for a different item) and wrongly conclude the order was already backed.
            var blanksCleared = 0;
            foreach (var container in containers)
            {
                var existing = container?.GetInventory();
                if (existing == null) continue;
                foreach (var candidate in existing.GetAllItems())
                {
                    if (candidate?.m_dropPrefab == null || candidate.m_dropPrefab.name != token) continue;
                    if (candidate.m_customData != null
                        && candidate.m_customData.TryGetValue("wo_item", out var stamped)
                        && stamped == item)
                        return "";

                    // A blank one from an earlier run: drop it and place a stamped one below,
                    // rather than leave a scroll that no adoption pass will ever recognise.
                    // RemoveItem notifies the container itself, so the chest saves.
                    existing.RemoveItem(candidate, 1);
                    blanksCleared++;
                    break;
                }
            }

            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(token) : null;
            var drop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (drop?.m_itemData == null) return $" (no item prefab '{token}')";

            var data = drop.m_itemData.Clone();
            data.m_stack = 1;
            data.m_dropPrefab = prefab;
            // The same four keys the station panel stamps. A token without them is invisible
            // to WorkOrderAdoption, which then releases the order on its next pass as
            // "no token for it in its chests" — the record alone is never enough.
            data.m_customData = Stamp(village, station, item);

            foreach (var container in containers)
                if (container != null && ContainerScanner.TryDepositItemData(container, data))
                    return $" (+ placed {token} for {item} in the chest at " +
                           $"({container.transform.position.x:F1},{container.transform.position.z:F1})" +
                           $"{(blanksCleared > 0 ? $", cleared {blanksCleared} blank token(s)" : "")})";

            return $" (could not place {token} — no chest took it)";
        }

        /// <summary>
        ///     The four keys the station panel writes onto a work-order token. Adoption reads
        ///     wo_item/wo_station to decide which village owns the order, and wo_village binds
        ///     the token to the record holding its quota.
        /// </summary>
        private static Dictionary<string, string> Stamp(Village village, string station, string item)
        {
            return new Dictionary<string, string>
            {
                ["wo_station"] = station,
                ["wo_item"] = item,
                ["wo_item_name"] = item,
                ["wo_village"] = village.VillageId,
            };
        }
    }
}
