using HarmonyLib;
using ValheimVillages.Items;

namespace ValheimVillages.Items.WorkOrders
{
    /// <summary>
    /// Intercepts item use for work order items.
    /// When a player uses (right-clicks) a work order, opens the WorkOrderMenu
    /// to configure production quota settings.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UseItem))]
    public static class WorkOrderUsePatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.High)]
        public static bool Prefix(Humanoid __instance, Inventory inventory, ItemDrop.ItemData item)
        {
            if (__instance is not Player player)
                return true;

            string itemName = item?.m_dropPrefab?.name;
            if (string.IsNullOrEmpty(itemName))
                return true;

            // Check if this is a work order item
            var def = ItemFactory.GetDefinition(itemName);
            if (def == null || def.itemType != "workorder")
                return true;

            // Open the work order settings menu
            WorkOrderMenu.Show(item);
            return false; // Consume the interaction
        }
    }

    /// <summary>
    /// Integrates WorkOrderMenu visibility with Valheim's UI systems
    /// so the camera and input don't conflict when the menu is open.
    /// </summary>
    [HarmonyPatch(typeof(TextInput), "IsVisible")]
    public static class WorkOrderTextInputPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (WorkOrderMenu.IsVisible)
                __result = true;
        }
    }

    /// <summary>
    /// Integrates WorkOrderMenu visibility with Valheim's menu system.
    /// </summary>
    [HarmonyPatch(typeof(Menu), "IsVisible")]
    public static class WorkOrderMenuVisiblePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (WorkOrderMenu.IsVisible)
                __result = true;
        }
    }
}
