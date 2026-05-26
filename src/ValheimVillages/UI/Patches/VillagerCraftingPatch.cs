using HarmonyLib;
using UnityEngine;
using ValheimVillages.UI.Core;

namespace ValheimVillages.UI.Patches
{
    /// <summary>
    ///     Patches to support the villager crafting UI tab system.
    ///     Non-crafter NPCs: hides the crafting panel entirely via CanvasGroup.
    ///     Custom tabs on crafters are handled by VillagerTabManager.LateUpdate
    ///     which re-applies content after Valheim's UpdateCraftingPanel.
    /// </summary>
    [HarmonyPatch]
    public static class VillagerCraftingPatch
    {
        private static CanvasGroup s_craftingCanvasGroup;

        /// <summary>
        ///     After UpdateCraftingPanel, hide the whole panel for non-crafters.
        /// </summary>
        [HarmonyPatch(typeof(InventoryGui), "UpdateCraftingPanel")]
        [HarmonyPostfix]
        public static void UpdateCraftingPanelPostfix(InventoryGui __instance)
        {
            if (__instance.m_crafting == null) return;
            if (!VillagerTabManager.IsNonCrafterActive) return;

            if (s_craftingCanvasGroup == null)
                s_craftingCanvasGroup =
                    __instance.m_crafting.GetComponent<CanvasGroup>()
                    ?? __instance.m_crafting.gameObject
                        .AddComponent<CanvasGroup>();

            s_craftingCanvasGroup.interactable = false;
            s_craftingCanvasGroup.blocksRaycasts = false;
            s_craftingCanvasGroup.alpha = 0f;
        }

        /// <summary>
        ///     Clean up on InventoryGui.Hide to restore the crafting panel.
        /// </summary>
        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
        [HarmonyPrefix]
        public static void HidePrefix()
        {
            if (s_craftingCanvasGroup != null)
            {
                s_craftingCanvasGroup.interactable = true;
                s_craftingCanvasGroup.blocksRaycasts = true;
                s_craftingCanvasGroup.alpha = 1f;
            }
        }
    }
}