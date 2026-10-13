using UnityEngine;
using ValheimVillages.UI.ContextMenus;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     Opens a work order's settings panel from its pin on a villager's map, so an order can
    ///     be retuned where the player noticed the problem instead of walking back to the chest.
    ///
    ///     <para>The order's quota lives on the village record, but the panel is bound to the
    ///     TOKEN item in the chest — that is what <see cref="WorkOrderMenu.Show" /> takes, and
    ///     it is where the item identity is stamped. So the pin's position finds the chest and
    ///     the token is matched inside it by the same <c>wo_item</c> custom data every other
    ///     caller reads.</para>
    /// </summary>
    internal static class MapOrderOpener
    {
        /// <summary>How far from the pin to look for the chest holding the order.</summary>
        private const float ChestSearchRadius = 3f;

        /// <summary>
        ///     Find and open the order pinned at <paramref name="at" />. Reports to the player
        ///     when it cannot be found rather than silently doing nothing — a pin that says it
        ///     opens something and then does not is worse than one that explains itself.
        /// </summary>
        public static void Open(Vector3 at, string itemPrefab)
        {
            if (string.IsNullOrEmpty(itemPrefab)) return;

            // Two different failures, reported differently: a chest that is not loaded is a
            // distance/zone problem, while a chest with no matching token is a stale order.
            // One message for both is what made this unreadable the first time.
            var containers = ContainerScanner.FindNearbyContainers(at, ChestSearchRadius);
            if (containers == null || containers.Count == 0)
            {
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center,
                    "That work order's chest is not loaded — move closer to it");
                return;
            }

            var token = FindToken(containers, itemPrefab);
            if (token == null)
            {
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center,
                    "That chest no longer holds this work order");
                Plugin.Log?.LogWarning(
                    $"[MapOrderOpener] no '{itemPrefab}' work-order token in the {containers.Count} " +
                    $"container(s) near {at}; the order may be stale.");
                return;
            }

            WorkOrderMenu.Show(token);
        }

        /// <summary>
        ///     The order token for <paramref name="itemPrefab" /> in any of these containers.
        ///
        ///     <para>Matched on the ITEM only. It used to also require the station to match,
        ///     which could never succeed: the blocked entry carries
        ///     <c>PhysicalStation ?? Station</c> — so "farm" or "smelter" — while the token
        ///     stamps the raw virtual station, "$vv_farmer". Every comparison failed and every
        ///     order reported its chest as missing. An order is unique by item within one chest
        ///     anyway, which is what the station test was redundantly guarding.</para>
        /// </summary>
        private static ItemDrop.ItemData FindToken(
            System.Collections.Generic.List<Container> containers, string itemPrefab)
        {
            foreach (var container in containers)
            {
                var inventory = container != null ? container.GetInventory() : null;
                if (inventory == null) continue;

                foreach (var item in inventory.GetAllItems())
                {
                    if (!ContainerScanner.IsWorkOrderItem(item)) continue;
                    if (item.m_customData == null) continue;

                    item.m_customData.TryGetValue("wo_item", out var orderItem);
                    if (orderItem != itemPrefab) continue;

                    return item;
                }
            }

            return null;
        }
    }
}
