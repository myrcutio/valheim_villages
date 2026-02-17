using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ValheimVillages.Items;
using ValheimVillages.Items.Icons;

namespace ValheimVillages.Items.WorkOrders
{
    /// <summary>
    /// Adds a small "Order" button next to the Craft button in crafting
    /// station UIs. Creates a work order scroll in the player's inventory.
    /// </summary>
    [HarmonyPatch]
    public static class CraftingStationPatch
    {
        private static GameObject _workOrderButton;
        private static bool _buttonCreated;

        // Craft button corruption repair:
        // Previous builds incorrectly set sizeDelta.x = rect.width (the
        // rendered width) on a stretch-anchored button, compounding the
        // width each hot-reload. We detect and fix this every frame.
        private static Vector2 _cleanCraftSizeDelta;
        private static bool _cleanSizeDeltaSaved;

        /// <summary>
        /// Reset all static state (hot reload / world unload).
        /// The button GameObject itself is destroyed by the stale object sweep.
        /// </summary>
        public static void Clear()
        {
            _workOrderButton = null;
            _buttonCreated = false;
            _cleanSizeDeltaSaved = false;
            _cleanCraftSizeDelta = Vector2.zero;
        }

        /// <summary>
        /// Hide the work order button so custom tabs can use the craft
        /// button area for their own actions.  Called from LateUpdate
        /// to guarantee it stays hidden for the entire frame.
        /// </summary>
        public static void HideWorkOrderButton()
        {
            if (_workOrderButton != null)
                _workOrderButton.SetActive(false);
        }

        private static readonly Dictionary<string, string> StationWorkOrderMap = new()
        {
            { "$piece_workbench", "vv_workorder_workbench" },
            { "$piece_forge", "vv_workorder_forge" },
            { "$piece_cauldron", "vv_workorder_cauldron" },
            { "$piece_artisanstation", "vv_workorder_artisan" },
            { "$piece_stonecutter", "vv_workorder_stonecutter" },
            { "$vv_farmer", "vv_workorder_farmer" },
            { "$vv_tavernkeeper", "vv_workorder_tavernkeeper" }
        };

        /// <summary>Virtual stations: show only Order button in place of Craft (no direct crafting).</summary>
        private static bool IsVirtualStation(string stationName) =>
            stationName != null && stationName.StartsWith("$vv_");

        [HarmonyPatch(typeof(InventoryGui), "UpdateCraftingPanel")]
        [HarmonyPostfix]
        public static void UpdateCraftingPanelPostfix(InventoryGui __instance)
        {
            RepairCraftButtonIfCorrupted(__instance);
            EnsureButtonCreated(__instance);
            UpdateButtonVisibility(__instance);
            ForceRecipeListFullColorAtVillagerStation(__instance);
        }

        /// <summary>
        /// When at a villager virtual station ($vv_*), recipe list elements are
        /// shown with full color so the player can always create work orders.
        /// </summary>
        private static void ForceRecipeListFullColorAtVillagerStation(InventoryGui gui)
        {
            if (gui?.m_recipeListRoot == null) return;
            var player = Player.m_localPlayer;
            var station = player?.GetCurrentCraftingStation();
            if (station?.m_name == null || !station.m_name.StartsWith("$vv_")) return;

            var white = Color.white;
            for (int i = 0; i < gui.m_recipeListRoot.childCount; i++)
            {
                var child = gui.m_recipeListRoot.GetChild(i);
                if (!child.gameObject.activeSelf) continue;
                var icon = child.Find("icon")?.GetComponent<Image>();
                if (icon != null) icon.color = white;
                var nameT = child.Find("name");
                if (nameT != null)
                {
                    foreach (var comp in nameT.GetComponents<Component>())
                    {
                        var colorProp = comp?.GetType().GetProperty("color",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (colorProp != null && colorProp.PropertyType == typeof(Color))
                        {
                            colorProp.SetValue(comp, white);
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Detect and repair craft button sizeDelta corruption from previous
        /// builds that incorrectly set sizeDelta.x = rect.width on a
        /// stretch-anchored button (compounding each ScriptEngine reload).
        /// </summary>
        private static void RepairCraftButtonIfCorrupted(InventoryGui gui)
        {
            var craftRect =
                gui.m_craftButton?.GetComponent<RectTransform>();
            if (craftRect == null) return;

            bool isStretch = craftRect.anchorMin.x < craftRect.anchorMax.x;

            if (!_cleanSizeDeltaSaved)
            {
                _cleanSizeDeltaSaved = true;

                if (isStretch && craftRect.sizeDelta.x > 10f)
                {
                    // Corrupted: stretch-anchored buttons should have
                    // sizeDelta.x near 0, not hundreds of pixels
                    Plugin.Log?.LogWarning(
                        $"Craft button sizeDelta.x={craftRect.sizeDelta.x}" +
                        " looks corrupted (stretch-anchored), resetting to 0");
                    _cleanCraftSizeDelta = new Vector2(
                        0, craftRect.sizeDelta.y);
                }
                else
                {
                    _cleanCraftSizeDelta = craftRect.sizeDelta;
                }
            }

            // Continuously enforce correct sizeDelta in case old patches
            // from previous ScriptEngine loads are still modifying it
            if (isStretch)
                craftRect.sizeDelta = _cleanCraftSizeDelta;
        }

        private static void EnsureButtonCreated(InventoryGui gui)
        {
            if (_buttonCreated && _workOrderButton != null) return;

            var craftButton = gui.m_craftButton;
            if (craftButton == null) return;

            // Clone the craft button for matching Valheim styling
            _workOrderButton = Object.Instantiate(
                craftButton.gameObject, craftButton.transform.parent);
            _workOrderButton.name = "VV_WorkOrderButton";

            SetButtonText(_workOrderButton, "Order");
            SetButtonFontSize(_workOrderButton, 11);

            var button = _workOrderButton.GetComponent<Button>();
            if (button != null)
            {
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(OnWorkOrderClicked);
                button.interactable = true;
            }

            SetOrderButtonTooltip(_workOrderButton);

            _buttonCreated = true;
            Plugin.Log?.LogInfo("Work Order button created in crafting UI");
        }

        private const string WorkOrderTooltipText =
            "Orders placed in a chest will be fulfilled by nearby villagers.";

        private static void SetOrderButtonTooltip(GameObject buttonGO)
        {
            if (buttonGO == null) return;
            const string text = WorkOrderTooltipText;
            // UITooltip (assembly_guiutils) has public m_text and m_topic; clone may already have it
            foreach (var comp in buttonGO.GetComponents<Component>())
            {
                if (comp == null) continue;
                var type = comp.GetType();
                var textField = type.GetField("m_text", BindingFlags.Public | BindingFlags.Instance);
                if (textField != null && textField.FieldType == typeof(string))
                {
                    textField.SetValue(comp, text);
                    var topicField = type.GetField("m_topic", BindingFlags.Public | BindingFlags.Instance);
                    if (topicField != null && topicField.FieldType == typeof(string))
                        topicField.SetValue(comp, "");
                    return;
                }
            }
            // Add UITooltip if missing (type from assembly_guiutils, same as Localization)
            var ttType = typeof(Localization).Assembly.GetType("UITooltip");
            if (ttType != null)
            {
                var tt = buttonGO.AddComponent(ttType);
                ttType.GetField("m_text", BindingFlags.Public | BindingFlags.Instance)?.SetValue(tt, text);
                ttType.GetField("m_topic", BindingFlags.Public | BindingFlags.Instance)?.SetValue(tt, "");
            }
        }

        private static void UpdateButtonVisibility(InventoryGui gui)
        {
            if (_workOrderButton == null || gui.m_craftButton == null) return;

            // Custom tabs (Info, Debug) manage the craft button area themselves;
            // hide the work order button so it doesn't cover the tab's action button.
            if (ValheimVillages.UI.Core.VillagerTabManager.IsCustomTabActive)
            {
                _workOrderButton.SetActive(false);
                return;
            }

            var player = Player.m_localPlayer;
            if (player == null)
            {
                _workOrderButton.SetActive(false);
                gui.m_craftButton.gameObject.SetActive(true);
                return;
            }

            var station = player.GetCurrentCraftingStation();
            bool hasWorkOrder = station != null && StationWorkOrderMap.ContainsKey(station.m_name);
            bool isVirtual = station != null && IsVirtualStation(station.m_name);

            if (isVirtual)
            {
                // Virtual station: replace Craft with Order (same slot, native look)
                gui.m_craftButton.gameObject.SetActive(false);
                _workOrderButton.SetActive(true);
                PositionWorkOrderButtonAsCraftReplacement(gui);
            }
            else
            {
                gui.m_craftButton.gameObject.SetActive(true);
                _workOrderButton.SetActive(hasWorkOrder);
                if (hasWorkOrder)
                    PositionWorkOrderButton(gui);
            }
        }

        /// <summary>
        /// For virtual stations: make the Order button use the Craft button's
        /// rect so it replaces it in place (native Valheim layout).
        /// </summary>
        private static void PositionWorkOrderButtonAsCraftReplacement(InventoryGui gui)
        {
            if (gui?.m_craftButton == null || _workOrderButton == null) return;

            var craftRect = gui.m_craftButton.GetComponent<RectTransform>();
            var woRect = _workOrderButton.GetComponent<RectTransform>();
            if (craftRect == null || woRect == null) return;

            woRect.anchorMin = craftRect.anchorMin;
            woRect.anchorMax = craftRect.anchorMax;
            woRect.pivot = craftRect.pivot;
            woRect.anchoredPosition = craftRect.anchoredPosition;
            woRect.sizeDelta = craftRect.sizeDelta;
            woRect.localScale = craftRect.localScale;

            var btn = _workOrderButton.GetComponent<Button>();
            if (btn != null) btn.interactable = true;
        }

        /// <summary>
        /// Position the work order button identical to the craft button in size,
        /// stacked directly above it with a small gap (for physical stations).
        /// </summary>
        private static void PositionWorkOrderButton(InventoryGui gui)
        {
            if (gui?.m_craftButton == null || _workOrderButton == null) return;

            var craftRect = gui.m_craftButton.GetComponent<RectTransform>();
            var woRect = _workOrderButton.GetComponent<RectTransform>();
            if (craftRect == null || woRect == null) return;

            // Match craft button layout so Order looks identical
            woRect.anchorMin = craftRect.anchorMin;
            woRect.anchorMax = craftRect.anchorMax;
            woRect.pivot = craftRect.pivot;
            woRect.sizeDelta = craftRect.sizeDelta;
            woRect.localScale = craftRect.localScale;

            const float gap = 6f;
            float craftHeight = craftRect.rect.height;
            float woHeight = woRect.rect.height;
            // Place Order button center above Craft button center
            woRect.anchoredPosition = new Vector2(
                craftRect.anchoredPosition.x,
                craftRect.anchoredPosition.y + craftHeight * 0.5f + gap + woHeight * 0.5f);

            var btn = _workOrderButton.GetComponent<Button>();
            if (btn != null) btn.interactable = true;
        }

        private static void SetButtonText(GameObject buttonGO, string text)
        {
            foreach (var comp in
                buttonGO.GetComponentsInChildren<Component>(true))
            {
                var textProp = comp.GetType().GetProperty("text",
                    BindingFlags.Public | BindingFlags.Instance);
                if (textProp != null && textProp.PropertyType == typeof(string))
                {
                    var current = textProp.GetValue(comp) as string;
                    if (!string.IsNullOrEmpty(current))
                    {
                        textProp.SetValue(comp, text);
                        break;
                    }
                }
            }
        }

        private static void SetButtonFontSize(GameObject buttonGO, float size)
        {
            foreach (var comp in
                buttonGO.GetComponentsInChildren<Component>(true))
            {
                var fsProp = comp.GetType().GetProperty("fontSize",
                    BindingFlags.Public | BindingFlags.Instance);
                if (fsProp != null && fsProp.PropertyType == typeof(float))
                {
                    fsProp.SetValue(comp, size);
                    break;
                }
            }
        }

        private static void OnWorkOrderClicked()
        {
            var player = Player.m_localPlayer;
            if (player == null) return;

            var station = player.GetCurrentCraftingStation();
            if (station == null) return;

            if (!StationWorkOrderMap.TryGetValue(
                station.m_name, out var workOrderName))
            {
                player.Message(MessageHud.MessageType.Center,
                    "No work order available for this station");
                return;
            }

            var selectedRecipe = RecipeHelper.GetSelectedRecipe();
            if (selectedRecipe == null)
            {
                player.Message(MessageHud.MessageType.Center,
                    "Select a recipe first");
                return;
            }

            string itemPrefabName = selectedRecipe.m_item?.gameObject?.name;
            string itemDisplayName =
                selectedRecipe.m_item?.m_itemData?.m_shared?.m_name
                ?? itemPrefabName;
            if (string.IsNullOrEmpty(itemPrefabName))
            {
                player.Message(MessageHud.MessageType.Center,
                    "Invalid recipe selected");
                return;
            }

            var prefab = ZNetScene.instance?.GetPrefab(workOrderName);
            if (prefab == null)
            {
                Plugin.Log?.LogError(
                    $"Work order prefab not found: {workOrderName}");
                player.Message(MessageHud.MessageType.Center,
                    "Failed to create work order");
                return;
            }

            var itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop == null)
            {
                Plugin.Log?.LogError(
                    $"Work order prefab missing ItemDrop: {workOrderName}");
                return;
            }

            var inventory = player.GetInventory();
            if (inventory == null) return;

            if (!inventory.CanAddItem(prefab, 1))
            {
                player.Message(MessageHud.MessageType.Center,
                    "Inventory full");
                return;
            }

            var newItemData = itemDrop.m_itemData.Clone();
            newItemData.m_stack = 1;
            newItemData.m_dropPrefab = prefab;

            if (newItemData.m_customData == null)
                newItemData.m_customData = new Dictionary<string, string>();

            newItemData.m_customData["wo_min"] = "1";
            newItemData.m_customData["wo_max"] = "10";
            newItemData.m_customData["wo_range"] = "1-10";
            newItemData.m_customData["wo_station"] = station.m_name;
            newItemData.m_customData["wo_item"] = itemPrefabName;
            newItemData.m_customData["wo_item_name"] = itemDisplayName;

            // Resolve pretty name for display and tooltip
            string localizedName = Localization.instance.Localize(
                itemDisplayName);

            // Deep-copy SharedData so this work order gets its own description
            // without mutating the prefab's shared data (affects all instances)
            if (newItemData.m_shared != null)
            {
                newItemData.m_shared = JsonUtility.FromJson<
                    ItemDrop.ItemData.SharedData>(
                    JsonUtility.ToJson(newItemData.m_shared));
                newItemData.m_shared.m_description =
                    $"{localizedName} (1-10)\nRight-click to change settings.";
            }

            // Overlay the production item's icon on the parchment
            WorkOrderIconCompositor.ApplyOverlay(newItemData);

            if (inventory.AddItem(newItemData))
            {
                player.Message(MessageHud.MessageType.Center,
                    $"Created work order: {localizedName}");
                WorkOrderTutorial.MaybeShowWorkOrderTutorial(player);
                Plugin.Log?.LogInfo(
                    $"Created work order: {workOrderName} for " +
                    itemPrefabName);
            }
            else
            {
                player.Message(MessageHud.MessageType.Center,
                    "Failed to add work order to inventory");
            }
        }

        [HarmonyPatch(typeof(InventoryGui), "Hide")]
        [HarmonyPostfix]
        public static void HidePostfix()
        {
            if (_workOrderButton != null)
                _workOrderButton.SetActive(false);
        }
    }
}
