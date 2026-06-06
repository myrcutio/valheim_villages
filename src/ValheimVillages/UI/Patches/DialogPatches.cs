using HarmonyLib;
using ValheimVillages.UI.Interaction;

namespace ValheimVillages.UI.Patches
{
    /// <summary>
    ///     Patches to support villager interaction:
    ///     - Redirect hover text for villagers to VillagerInteract
    ///     - Resume NPC when the crafting UI is closed
    /// </summary>
    public static class DialogPatches
    {
        /// <summary>
        ///     Patch Character.GetHoverText to use VillagerInteract's hover text for villagers.
        /// </summary>
        [HarmonyPatch(typeof(Character), nameof(Character.GetHoverText))]
        public static class Character_GetHoverText_Patch
        {
            [HarmonyPrefix]
            public static bool Prefix(Character __instance, ref string __result)
            {
                var villagerInteract = __instance.GetComponent<VillagerInteract>();
                if (villagerInteract != null)
                {
                    __result = villagerInteract.GetHoverText();
                    return false;
                }

                return true;
            }
        }

        /// <summary>
        ///     Patch InventoryGui.Hide to resume the NPC when the crafting UI is closed.
        /// </summary>
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
        public static class InventoryGui_Hide_Patch
        {
            [HarmonyPostfix]
            public static void Postfix()
            {
                // Each no-ops unless its own session is active, and the two manage
                // disjoint static state, so sharing the close hook is order-independent.
                VillagerInteract.OnCraftingUIClosed();
                RegistryInteract.OnCraftingUIClosed();
            }
        }
    }
}