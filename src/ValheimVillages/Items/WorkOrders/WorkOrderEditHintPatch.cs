using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.UI.ContextMenus;

namespace ValheimVillages.Items.WorkOrders
{
    /// <summary>
    ///     On a gamepad, pins an "X" glyph to the focused inventory slot when it
    ///     holds a work order — hinting that X (the grid's right-click) opens its
    ///     configuration menu. Mirrors the craft menu's glyph feel.
    /// </summary>
    [HarmonyPatch]
    public static class WorkOrderEditHintPatch
    {
        private static GameObject s_hint;

        [RegisterCleanup]
        public static void Clear()
        {
            s_hint = null;
        }

        [HarmonyPatch(typeof(InventoryGui), "Update")]
        [HarmonyPostfix]
        public static void Postfix(InventoryGui __instance)
        {
            if (!ZInput.IsGamepadActive() || WorkOrderMenu.IsVisible)
            {
                HideHint();
                return;
            }

            var slot = GetFocusedWorkOrderSlot(__instance);
            if (slot == null)
            {
                HideHint();
                return;
            }

            EnsureHint(__instance);
            if (s_hint == null) return;
            PositionAt(slot);
            if (!s_hint.activeSelf) s_hint.SetActive(true);
        }

        /// <summary>
        ///     The gamepad-selected slot's RectTransform if it holds a work order,
        ///     checking the player grid then the open container grid. Only the
        ///     active grid returns a selected element, so this naturally yields the
        ///     one the player is navigating.
        /// </summary>
        private static RectTransform GetFocusedWorkOrderSlot(InventoryGui gui)
        {
            return CheckGrid(gui.m_playerGrid) ?? CheckGrid(gui.ContainerGrid);
        }

        private static RectTransform CheckGrid(InventoryGrid grid)
        {
            if (grid == null) return null;
            var element = grid.GetGamepadSelectedElement();
            if (element == null) return null;
            return IsWorkOrder(grid.GetGamepadSelectedItem()) ? element : null;
        }

        private static bool IsWorkOrder(ItemDrop.ItemData item)
        {
            var name = item?.m_dropPrefab?.name;
            if (string.IsNullOrEmpty(name)) return false;
            var def = ItemFactory.GetDefinition(name);
            return def != null && def.itemType == "workorder";
        }

        private static void EnsureHint(InventoryGui gui)
        {
            if (s_hint != null) return;

            // Reuse the Craft button's X (ButtonX) glyph — the same button the
            // inventory grid uses for right-click — so the icon is always correct.
            var glyphSrc = gui.m_craftButton != null
                ? gui.m_craftButton.GetComponent<UIGamePad>()?.m_hint
                : null;
            if (glyphSrc == null) return; // retry next frame

            s_hint = Object.Instantiate(glyphSrc, gui.transform);
            s_hint.name = "VV_WorkOrderEditHint";
            // Static copy — drop any UIGamePad so input state can't toggle it.
            foreach (var pad in s_hint.GetComponentsInChildren<UIGamePad>(true))
                Object.Destroy(pad);

            var rt = s_hint.GetComponent<RectTransform>();
            if (rt != null)
                rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);

            s_hint.SetActive(false);
        }

        private static void PositionAt(RectTransform slot)
        {
            var rt = s_hint.GetComponent<RectTransform>();
            if (rt == null) return;
            var corners = new Vector3[4];
            slot.GetWorldCorners(corners); // 0=BL, 1=TL, 2=TR, 3=BR
            rt.position = corners[2]; // top-right corner of the slot
            rt.SetAsLastSibling();
        }

        private static void HideHint()
        {
            if (s_hint != null && s_hint.activeSelf) s_hint.SetActive(false);
        }
    }
}
