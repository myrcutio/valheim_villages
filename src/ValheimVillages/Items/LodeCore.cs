using UnityEngine;

namespace ValheimVillages.Items
{
    /// <summary>
    ///     Helpers for the Lode Core recruitment currency: the canonical prefab name and the
    ///     single place that spawns one into the world (the rescue-dungeon reward and the
    ///     villager death drop), so both stay consistent. Spending a core FROM an inventory
    ///     lives in <c>RecruitCost</c>; this is the spawn-INTO-world side.
    /// </summary>
    public static class LodeCore
    {
        public const string Prefab = "vv_lode_core";

        /// <summary>
        ///     Spawn one Lode Core as a world-dropped item at <paramref name="position" />.
        ///     The caller must already be on the owning peer (host) to avoid duplicate drops.
        ///     Returns the spawned ItemDrop, or null if the prefab wasn't available.
        /// </summary>
        public static ItemDrop DropAt(Vector3 position)
        {
            var scene = ZNetScene.instance;
            if (scene == null)
            {
                Plugin.Log?.LogError("[LodeCore] ZNetScene not available; cannot drop a Lode Core");
                return null;
            }

            var prefab = scene.GetPrefab(Prefab);
            var itemDrop = prefab != null ? prefab.GetComponent<ItemDrop>() : null;
            if (itemDrop == null)
            {
                Plugin.Log?.LogError($"[LodeCore] prefab '{Prefab}' missing or has no ItemDrop; cannot drop");
                return null;
            }

            var dropped = ItemDrop.DropItem(itemDrop.m_itemData, 1, position, Quaternion.identity);
            if (dropped == null)
                Plugin.Log?.LogError($"[LodeCore] failed to drop a Lode Core at {position}");
            return dropped;
        }
    }
}
