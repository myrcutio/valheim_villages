using UnityEngine;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     The material cost of bringing a villager into the world: one Lode Core, spent from
    ///     the requesting player's OWN inventory on the client before the spawn RPC is sent.
    ///     Player inventory is client-owned, so the client is the authority for the spend; the
    ///     spawn itself stays server-authoritative (VillagerRecruitRpc). Both fresh recruits
    ///     and revives cost one core; a villager's death drops one back, closing the loop.
    /// </summary>
    public static class RecruitCost
    {
        public const string ItemPrefab = Items.LodeCore.Prefab;

        /// <summary>Does the player hold at least one Lode Core?</summary>
        public static bool Has(Player player)
        {
            var inv = player?.GetInventory();
            if (inv == null) return false;
            foreach (var item in inv.GetAllItems())
                if (IsLodeCore(item) && item.m_stack > 0)
                    return true;
            return false;
        }

        /// <summary>Remove one Lode Core from the player. Returns true if one was consumed.</summary>
        public static bool TryConsumeOne(Player player)
        {
            var inv = player?.GetInventory();
            if (inv == null) return false;
            foreach (var item in inv.GetAllItems())
            {
                if (!IsLodeCore(item) || item.m_stack <= 0) continue;
                inv.RemoveOneItem(item);
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Give one Lode Core back to the player — the refund leg of the atomic recruit/
        ///     revive spend, used when the host rejects a paid spawn. Falls back to dropping it
        ///     at the player's feet if the inventory is full, so the core is never lost.
        /// </summary>
        public static void Refund(Player player)
        {
            if (player == null) return;

            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(ItemPrefab) : null;
            if (prefab == null)
            {
                Plugin.Log?.LogError("[RecruitCost] cannot refund Lode Core: prefab missing");
                return;
            }

            var inv = player.GetInventory();
            if (inv != null && inv.AddItem(prefab, 1)) return;

            // Inventory full / unavailable: drop it at the player's feet rather than lose it.
            Items.LodeCore.DropAt(player.transform.position + Vector3.up * 0.5f);
        }

        private static bool IsLodeCore(ItemDrop.ItemData item)
        {
            return item?.m_dropPrefab != null && item.m_dropPrefab.name == ItemPrefab;
        }
    }
}
