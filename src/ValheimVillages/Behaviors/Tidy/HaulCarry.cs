using UnityEngine;

namespace ValheimVillages.Behaviors.Tidy
{
    /// <summary>
    ///     The item a hauling villager is physically carrying, written through to the
    ///     villager's own NPC ZDO the moment it is picked up.
    ///
    ///     <para>A haul used to leave the item lying on the ground until the deposit and then
    ///     move it into the chest from wherever it lay — which, with the arrival firing twice,
    ///     meant every haul deposited remotely. Carrying for real means the ground copy is gone
    ///     while the villager walks, so the item exists only in the villager's hands. Keeping
    ///     that only in memory would lose it to a hot reload, a zone unload or a death
    ///     mid-carry; storing it on the NPC ZDO lets each of those put it back on the ground
    ///     (see <see cref="DropIfCarrying" />).</para>
    /// </summary>
    public static class HaulCarry
    {
        private const string PrefabKey = "vv_haul_carry_prefab";

        /// <summary>Index for <c>ItemDrop.SaveToZDO</c>'s per-index key ("N_itemData").</summary>
        private const int ItemIndex = 7471;

        /// <summary>Record <paramref name="item" /> as carried by the NPC owning <paramref name="npcZdo" />.</summary>
        public static void Store(ZDO npcZdo, ItemDrop.ItemData item)
        {
            if (npcZdo == null || item?.m_dropPrefab == null) return;
            ItemDrop.SaveToZDO(item, npcZdo, ItemIndex);
            npcZdo.Set(PrefabKey, item.m_dropPrefab.name);
        }

        /// <summary>Forget the carried item (it was deposited or dropped).</summary>
        public static void Clear(ZDO npcZdo)
        {
            npcZdo?.Set(PrefabKey, "");
        }

        public static bool IsCarrying(ZDO npcZdo)
        {
            return npcZdo != null && !string.IsNullOrEmpty(npcZdo.GetString(PrefabKey));
        }

        /// <summary>
        ///     If the NPC is recorded as carrying something, put it back on the ground at
        ///     <paramref name="position" /> and clear the record. Returns the dropped item, or null.
        /// </summary>
        public static ItemDrop DropIfCarrying(ZDO npcZdo, Vector3 position)
        {
            if (!IsCarrying(npcZdo)) return null;

            var prefabName = npcZdo.GetString(PrefabKey);
            var prefab = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            var template = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (template == null)
            {
                Plugin.Log?.LogError(
                    $"[HaulCarry] carried item '{prefabName}' has no ItemDrop prefab — cannot return it to the world.");
                Clear(npcZdo);
                return null;
            }

            var item = template.m_itemData.Clone();
            item.m_dropPrefab = prefab;
            ItemDrop.LoadFromZDO(item, npcZdo, ItemIndex);
            Clear(npcZdo);

            var drop = ItemDrop.DropItem(item, item.m_stack, position + Vector3.up * 0.5f, Quaternion.identity);
            DebugLog.Event("Haul", "carry_dropped",
                ("prefab", prefabName), ("stack", item.m_stack), ("pos", position));
            return drop;
        }
    }
}
