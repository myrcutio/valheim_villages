using HarmonyLib;
using ValheimVillages.UI.ContextMenus;

namespace ValheimVillages.Items.WorkOrders
{
    /// <summary>
    ///     Intercepts item use for work order items.
    ///     When a player uses (right-clicks) a work order, opens the WorkOrderMenu
    ///     to configure production quota settings.
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

            var itemName = item?.m_dropPrefab?.name;
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
    ///     While a quota input field is focused, report text input as active so
    ///     Valheim's own key polling (hotbar, movement) is suppressed during typing.
    /// </summary>
    [HarmonyPatch(typeof(TextInput), "IsVisible")]
    public static class WorkOrderTextInputPatch
    {
        [HarmonyPostfix]
        public static void Postfix(ref bool __result)
        {
            if (WorkOrderMenu.IsEditingText)
                __result = true;
        }
    }

    /// <summary>
    ///     While the work order editor is open, suppress the inventory grid's own
    ///     gamepad handling (it reads ZInput directly each frame) so stick/X drive
    ///     the editor instead of moving the grid selection or re-using items.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGrid), "UpdateGamepad")]
    public static class WorkOrderGridInputBlockPatch
    {
        [HarmonyPrefix]
        public static bool Prefix()
        {
            return !WorkOrderMenu.IsVisible;
        }
    }

    /// <summary>
    ///     Close the docked work order editor whenever the inventory/chest UI
    ///     closes, so it never lingers over the world.
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
    public static class WorkOrderInventoryHidePatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            if (WorkOrderMenu.IsVisible)
                WorkOrderMenu.Hide();
        }
    }
}