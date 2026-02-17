using UnityEngine;

namespace ValheimVillages.NPCs.AI.Work
{
    /// <summary>
    /// Cooking station polling and item collection logic for CraftingBehavior.
    /// Handles real CookingStation interaction: slot polling, removing done items,
    /// and collecting spawned ItemDrop objects.
    /// </summary>
    public partial class CraftingBehavior
    {
        /// <summary>
        /// Poll a real CookingStation for done items.
        /// Returns true if the cooking check was handled (done or still in progress).
        /// Returns false if this is not a cooking recipe (caller should use fixed timer).
        /// </summary>
        private bool TryPollCookingStation()
        {
            if (m_context.CookingStationRef == null || string.IsNullOrEmpty(m_context.WorkOrder?.ItemPrefabName))
                return false;

            float cookTime = m_context.CraftCookTimeSeconds > 0 ? m_context.CraftCookTimeSeconds : WorkSettings.CraftDuration;
            float cookElapsed = Time.time - m_context.CraftStartTime;
            if (cookElapsed < cookTime + WorkSettings.CookingDoneGraceSeconds)
                return true; // Not ready to check yet

            string outputName = m_context.WorkOrder.ItemPrefabName;
            int slotCount = CookingStationHelper.GetSlotCount(m_context.CookingStationRef);
            for (int i = 0; i < slotCount; i++)
            {
                CookingStationHelper.GetSlot(m_context.CookingStationRef, i, out string name, out float cookedTime, out int status);
                if (name != outputName) continue;
                if (status != CookingStationHelper.StatusDone) continue;

                if (m_context.CookingRemovalRequested)
                {
                    CompleteCraft();
                    return true;
                }
                m_context.CookingRemovalRequested = true;

                Vector3 spawnAt = m_ai.Position + Vector3.forward * 0.5f;
                if (CookingStationHelper.RemoveDoneItem(m_context.CookingStationRef, spawnAt, 1))
                {
                    try
                    {
                        CollectSpawnedCookedItemAndComplete(spawnAt);
                    }
                    finally
                    {
                        if (m_subState == WorkSubState.Crafting)
                            CompleteCraft();
                    }
                }
                else
                    CompleteCraft();
                return true;
            }
            return true; // Still cooking
        }

        /// <summary>
        /// After RPC_RemoveDoneItem the cooked item spawns at the station; find it and add to output chest.
        /// </summary>
        private void CollectSpawnedCookedItemAndComplete(Vector3 _)
        {
            string outputName = m_context.WorkOrder.ItemPrefabName;
            var inv = m_context.SourceContainer?.GetInventory();
            if (inv == null) { CompleteCraft(); return; }

            const float radius = 2.5f;
            Vector3 stationPos = m_context.CookingStationRef != null ? m_context.CookingStationRef.transform.position : m_ai.Position;
            if (TryCollectItemDropInRadius(stationPos, radius, outputName, inv))
            {
                m_context.CookingItemAlreadyInChest = true;
                CompleteCraft();
                return;
            }
            if (TryCollectItemDropInRadius(m_ai.Position, radius, outputName, inv))
            {
                m_context.CookingItemAlreadyInChest = true;
                CompleteCraft();
                return;
            }
            CompleteCraft();
        }

        /// <summary>
        /// Search for an ItemDrop matching outputName in radius; add to inv and consume the drop.
        /// </summary>
        private static bool TryCollectItemDropInRadius(Vector3 center, float radius, string outputName, Inventory inv)
        {
            if (inv == null || string.IsNullOrEmpty(outputName)) return false;
            var colliders = Physics.OverlapSphere(center, radius);
            foreach (var col in colliders)
            {
                if (col == null || col.gameObject == null) continue;
                var drop = col.GetComponentInParent<ItemDrop>();
                if (drop == null || drop.m_itemData?.m_dropPrefab == null) continue;
                if (drop.m_itemData.m_dropPrefab.name != outputName) continue;
                int take = drop.m_itemData.m_stack;
                if (take <= 0) continue;
                var clone = drop.m_itemData.Clone();
                clone.m_stack = take;
                if (inv.AddItem(clone))
                {
                    drop.m_itemData.m_stack -= take;
                    if (drop.m_itemData.m_stack <= 0)
                        Object.Destroy(drop.gameObject);
                    return true;
                }
            }
            return false;
        }
    }
}
