using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Items.Icons
{
    /// <summary>
    ///     Re-applies production item overlays and status badges on work order
    ///     icons when the inventory or container UI is opened. Handles game load
    ///     and scene transitions where composited textures are lost but
    ///     m_customData persists.
    /// </summary>
    [HarmonyPatch]
    public static class WorkOrderIconPatch
    {
        // Cached reflection field for InventoryGui.m_currentContainer
        private static FieldInfo _containerField;

        [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Show))]
        [HarmonyPostfix]
        private static void OnInventoryShow(InventoryGui __instance)
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            var playerPos = player.transform.position;

            // Pre-scan containers once for all work orders at this position
            var containers = ContainerScanner.FindNearbyContainers(
                playerPos, WorkSettings.ChestScanRadius);

            WorkOrderIconCompositor.EnsureOverlays(
                player.GetInventory(), playerPos, containers);

            // If a container is open, also refresh its work order icons
            RefreshContainerIcons(__instance, containers);
        }

        private static void RefreshContainerIcons(
            InventoryGui gui, List<Container> sharedContainers)
        {
            if (gui == null) return;

            var container = GetCurrentContainer(gui);
            if (container == null) return;

            var inv = container.GetInventory();
            if (inv == null) return;

            var containerPos = container.transform.position;

            // Reuse the shared container list if the container is near the
            // player (same village radius), otherwise scan from the container
            WorkOrderIconCompositor.EnsureOverlays(
                inv, containerPos, sharedContainers);
        }

        private static Container GetCurrentContainer(InventoryGui gui)
        {
            if (_containerField == null)
                _containerField = typeof(InventoryGui).GetField(
                    "m_currentContainer",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            return _containerField?.GetValue(gui) as Container;
        }
    }
}