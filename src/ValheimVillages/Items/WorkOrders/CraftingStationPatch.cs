using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ValheimVillages.Attributes;
using ValheimVillages.Items.Icons;
using ValheimVillages.UI.Core;

namespace ValheimVillages.Items.WorkOrders
{
    /// <summary>
    ///     Adds a small "Order" button next to the Craft button in crafting
    ///     station UIs. Creates a work order scroll in the player's inventory.
    /// </summary>
    [HarmonyPatch]
    public static class CraftingStationPatch
    {
        private const string WorkOrderTooltipText =
            "Orders placed in a chest will be fulfilled by nearby villagers.";

        private static GameObject _workOrderButton;
        private static bool _buttonCreated;

        /// <summary>
        ///     Gamepad focus target. The Order button is a clone of the Craft
        ///     button, so both share the X (ButtonX) hotkey and X fires whichever
        ///     is interactable. We route X by focus: left/right moves between the
        ///     recipe list and the action panel, up/down within the panel picks
        ///     Order (top) or Craft (bottom).
        /// </summary>
        private enum CraftFocus
        {
            List,
            Craft,
            Order,
        }

        private static CraftFocus s_focus = CraftFocus.List;

        // Gamepad focus borders: one wrapping each action button (Craft, Order)
        // and one over the recipe list, toggled by focus.
        private static GameObject s_craftFrame;
        private static GameObject s_orderFrame;
        private static GameObject s_listFrame;
        private static bool s_framesCreated;

        // Craft button corruption repair:
        // Previous builds incorrectly set sizeDelta.x = rect.width (the
        // rendered width) on a stretch-anchored button, compounding the
        // width each hot-reload. We detect and fix this every frame.
        private static Vector2 _cleanCraftSizeDelta;
        private static bool _cleanSizeDeltaSaved;

        private static Dictionary<string, string> s_stationWorkOrderMap;

        /// <summary>
        ///     Station name → work order item name, built from ItemFactory
        ///     (physical stations + virtual stations from VillagerRegistry).
        /// </summary>
        private static Dictionary<string, string> StationWorkOrderMap =>
            s_stationWorkOrderMap ??= ItemFactory.BuildStationWorkOrderMap();

        /// <summary>
        ///     Reset all static state (hot reload / world unload).
        ///     The button GameObject itself is destroyed by the stale object sweep.
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            _workOrderButton = null;
            _buttonCreated = false;
            _cleanSizeDeltaSaved = false;
            _cleanCraftSizeDelta = Vector2.zero;
            s_focus = CraftFocus.List;
            s_craftFrame = null;
            s_orderFrame = null;
            s_listFrame = null;
            s_framesCreated = false;
        }

        /// <summary>
        ///     Hide the work order button so custom tabs can use the craft
        ///     button area for their own actions.  Called from LateUpdate
        ///     to guarantee it stays hidden for the entire frame.
        /// </summary>
        public static void HideWorkOrderButton()
        {
            if (_workOrderButton != null)
                _workOrderButton.SetActive(false);
        }

        /// <summary>Virtual stations: show only Order button in place of Craft (no direct crafting).</summary>
        private static bool IsVirtualStation(string stationName)
        {
            return stationName != null && stationName.StartsWith("$vv_");
        }

        [HarmonyPatch(typeof(InventoryGui), "UpdateCraftingPanel")]
        [HarmonyPostfix]
        public static void UpdateCraftingPanelPostfix(InventoryGui __instance)
        {
            RepairCraftButtonIfCorrupted(__instance);
            EnsureButtonCreated(__instance);
            EnsureFocusFrames(__instance);
            UpdateButtonVisibility(__instance);
            ForceRecipeListFullColorAtVillagerStation(__instance);
        }

        private static readonly Color FocusFrameColor = new(1f, 0.78f, 0.28f, 0.95f);
        private const float FocusFrameThickness = 3f;

        /// <summary>
        ///     Lazily build a self-drawn focus border (four edge strips) once,
        ///     wrapping each action button and the recipe list. Toggled per-frame
        ///     by focus in UpdateFocusNavPostfix. Self-drawn rather than cloning
        ///     Valheim's native focus frame, which didn't render at our size.
        /// </summary>
        private static void EnsureFocusFrames(InventoryGui gui)
        {
            if (s_framesCreated) return;
            // Wait until both action buttons exist so each gets its own border.
            if (gui.m_craftButton == null || _workOrderButton == null) return;
            s_framesCreated = true;

            s_craftFrame = CreateFocusFrame(gui.m_craftButton.transform);
            s_orderFrame = CreateFocusFrame(_workOrderButton.transform);

            // The focus border is the button's last sibling (drawn on top); lift
            // each button's gamepad glyph above it so the border never occludes it.
            BringGlyphToFront(gui.m_craftButton.gameObject);
            BringGlyphToFront(_workOrderButton);

            // Render the Order button beneath the Craft button so it can't occlude
            // the Craft glyph. They don't overlap (Order is stacked above Craft with
            // a gap), and both glyphs stay children of their buttons — so they still
            // hide with their button (e.g. the Craft glyph during a craft).
            var craftT = gui.m_craftButton.transform;
            var orderT = _workOrderButton.transform;
            if (craftT.parent == orderT.parent
                && orderT.GetSiblingIndex() > craftT.GetSiblingIndex())
                orderT.SetSiblingIndex(craftT.GetSiblingIndex());

            // The recipe list's scroll viewport (parent of the scrolling content).
            var listViewport = gui.m_recipeListRoot != null ? gui.m_recipeListRoot.parent : null;
            if (listViewport != null) s_listFrame = CreateFocusFrame(listViewport);
        }

        private static void BringGlyphToFront(GameObject buttonGO)
        {
            var hint = buttonGO.GetComponent<UIGamePad>()?.m_hint;
            if (hint != null) hint.transform.SetAsLastSibling();
        }

        private static GameObject CreateFocusFrame(Transform parent)
        {
            var root = new GameObject("VV_FocusFrame", typeof(RectTransform));
            var rt = root.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            rt.SetAsLastSibling();

            var t = FocusFrameThickness;
            AddFrameEdge(rt, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, t)); // top
            AddFrameEdge(rt, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, t)); // bottom
            AddFrameEdge(rt, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(t, 0f)); // left
            AddFrameEdge(rt, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(t, 0f)); // right

            root.SetActive(false);
            return root;
        }

        private static void AddFrameEdge(Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 sizeDelta)
        {
            var go = new GameObject("Edge", typeof(RectTransform), typeof(Image));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = sizeDelta;
            rt.anchoredPosition = Vector2.zero;
            var img = go.GetComponent<Image>();
            img.color = FocusFrameColor;
            img.raycastTarget = false;
        }

        // ----- Gamepad focus navigation (Craft vs Order) ---------------------
        // Runs every frame (Update postfix runs after UpdateRecipe, which sets the
        // Craft button's interactable from recipe requirements). Only the focused
        // action stays interactable, so the shared X hotkey hits exactly one.

        [HarmonyPatch(typeof(InventoryGui), "Update")]
        [HarmonyPostfix]
        public static void UpdateFocusNavPostfix(InventoryGui __instance)
        {
            if (!FocusNavActive(__instance))
            {
                // Preserve focus through a transient craft (the Craft button is
                // hidden for the progress bar) so it doesn't jump off Craft;
                // otherwise — different station, no work order, mouse — reset to
                // the list. The Order button stays present during a craft.
                var orderPresent = _workOrderButton != null && _workOrderButton.activeSelf;
                if (!orderPresent) s_focus = CraftFocus.List;

                RestoreButtons(__instance);
                SetFrameActive(s_craftFrame, false);
                SetFrameActive(s_orderFrame, false);
                SetFrameActive(s_listFrame, false);
                return;
            }

            // Native UpdateRecipe set this from recipe requirements earlier this
            // frame (before our postfix), so it's the true craftability.
            var craftAvailable = __instance.m_craftButton.interactable;

            HandleFocusInput(craftAvailable);
            ApplyFocusGating(__instance);

            SetFrameActive(s_craftFrame, s_focus == CraftFocus.Craft);
            SetFrameActive(s_orderFrame, s_focus == CraftFocus.Order);
            SetFrameActive(s_listFrame, s_focus == CraftFocus.List);
        }

        /// <summary>
        ///     Leave both action buttons fully usable when focus nav isn't driving
        ///     them (mouse/keyboard, or a transient craft): Order stays enabled
        ///     (so its text keeps the bronze color), and both X hotkeys work.
        /// </summary>
        private static void RestoreButtons(InventoryGui gui)
        {
            var ob = _workOrderButton != null ? _workOrderButton.GetComponent<Button>() : null;
            if (ob != null) ob.interactable = true;

            // Keep Order's X hotkey + glyph live only at a virtual (villager)
            // station, where Order is the sole action. At a physical station this
            // branch means a craft is in progress (Craft button hidden) — there the
            // Order glyph should not show, so leave its UIGamePad off.
            var station = Player.m_localPlayer != null
                ? Player.m_localPlayer.GetCurrentCraftingStation()
                : null;
            var isVirtual = station != null && IsVirtualStation(station.m_name);
            SetGamepadEnabled(_workOrderButton, isVirtual);

            if (gui.m_craftButton != null) SetGamepadEnabled(gui.m_craftButton.gameObject, true);
        }

        private static void SetFrameActive(GameObject frame, bool active)
        {
            if (frame != null && frame.activeSelf != active) frame.SetActive(active);
        }

        // When the action panel is focused, up/down toggles Craft/Order rather
        // than scrolling the recipe selection — so suppress native recipe nav.
        [HarmonyPatch(typeof(InventoryGui), "UpdateRecipeGamepadInput")]
        [HarmonyPrefix]
        public static bool SuppressRecipeNavWhenPanelFocused()
        {
            return s_focus == CraftFocus.List;
        }

        /// <summary>
        ///     Focus nav applies only when both Craft and Order buttons are shown
        ///     (physical station with a work order) and a gamepad is active. Virtual
        ///     villager stations show only Order (no conflict, no nav needed).
        /// </summary>
        private static bool FocusNavActive(InventoryGui gui)
        {
            return ZInput.IsGamepadActive()
                   && _workOrderButton != null && _workOrderButton.activeSelf
                   && gui.m_craftButton != null && gui.m_craftButton.gameObject.activeSelf;
        }

        private static void HandleFocusInput(bool craftAvailable)
        {
            var left = ZInput.GetButtonDown("JoyDPadLeft") || ZInput.GetButtonDown("JoyLStickLeft");
            var right = ZInput.GetButtonDown("JoyDPadRight") || ZInput.GetButtonDown("JoyLStickRight");
            var up = ZInput.GetButtonDown("JoyDPadUp") || ZInput.GetButtonDown("JoyLStickUp");
            var down = ZInput.GetButtonDown("JoyDPadDown") || ZInput.GetButtonDown("JoyLStickDown");

            switch (s_focus)
            {
                case CraftFocus.List:
                    // Enter the panel on Craft when it's usable, else on Order.
                    if (right) s_focus = craftAvailable ? CraftFocus.Craft : CraftFocus.Order;
                    break;
                case CraftFocus.Craft:
                    if (left) s_focus = CraftFocus.List;
                    else if (up) s_focus = CraftFocus.Order; // Order sits above Craft
                    break;
                case CraftFocus.Order:
                    if (left) s_focus = CraftFocus.List;
                    // Down reaches Craft only when it's actually makeable.
                    else if (down && craftAvailable) s_focus = CraftFocus.Craft;
                    break;
            }
        }

        private static void ApplyFocusGating(InventoryGui gui)
        {
            // Order always looks enabled (same bronze text as an available Craft
            // button); focus is shown only by the gold border, never by greying.
            var orderBtn = _workOrderButton.GetComponent<Button>();
            if (orderBtn != null) orderBtn.interactable = true;

            // Route the shared X hotkey to exactly the focused action by enabling
            // only that button's UIGamePad — without changing either appearance.
            // Craft's own interactable still reflects craftability natively, so an
            // unmakeable recipe can't be crafted even while its UIGamePad is on.
            var orderFocused = s_focus == CraftFocus.Order;
            SetGamepadEnabled(_workOrderButton, orderFocused);
            SetGamepadEnabled(gui.m_craftButton.gameObject, !orderFocused);
        }

        private static void SetGamepadEnabled(GameObject go, bool enabled)
        {
            if (go == null) return;
            var pad = go.GetComponent<UIGamePad>();
            if (pad == null) return;
            if (pad.enabled != enabled) pad.enabled = enabled;
            // A disabled UIGamePad stops running its Update, which would otherwise
            // leave its X hint glyph frozen visible. Hide it explicitly so the
            // glyph only shows on the button X will actually trigger. When enabled,
            // UIGamePad.Update manages the hint (interactable + gamepad) itself.
            if (!enabled && pad.m_hint != null) pad.m_hint.SetActive(false);
        }

        /// <summary>
        ///     When at a villager virtual station ($vv_*), recipe list elements are
        ///     shown with full color so the player can always create work orders.
        /// </summary>
        private static void ForceRecipeListFullColorAtVillagerStation(InventoryGui gui)
        {
            if (gui?.m_recipeListRoot == null) return;
            var player = Player.m_localPlayer;
            var station = player?.GetCurrentCraftingStation();
            if (station?.m_name == null || !station.m_name.StartsWith("$vv_")) return;

            var white = Color.white;
            for (var i = 0; i < gui.m_recipeListRoot.childCount; i++)
            {
                var child = gui.m_recipeListRoot.GetChild(i);
                if (!child.gameObject.activeSelf) continue;
                var icon = child.Find("icon")?.GetComponent<Image>();
                if (icon != null) icon.color = white;
                var nameT = child.Find("name");
                if (nameT != null)
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

        /// <summary>
        ///     Detect and repair craft button sizeDelta corruption from previous
        ///     builds that incorrectly set sizeDelta.x = rect.width on a
        ///     stretch-anchored button (compounding each ScriptEngine reload).
        /// </summary>
        private static void RepairCraftButtonIfCorrupted(InventoryGui gui)
        {
            var craftRect =
                gui.m_craftButton?.GetComponent<RectTransform>();
            if (craftRect == null) return;

            var isStretch = craftRect.anchorMin.x < craftRect.anchorMax.x;

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
                // Match the Craft button's color states so Order shows the same
                // bronze "active" background (not a washed-out gray) when it is
                // the interactable/selected action.
                var craftBtn = craftButton.GetComponent<Button>();
                if (craftBtn != null)
                {
                    button.transition = craftBtn.transition;
                    button.colors = craftBtn.colors;
                }
            }

            SetOrderButtonTooltip(_workOrderButton);
            PinOrderTextColor(craftButton, _workOrderButton);

            _buttonCreated = true;
            Plugin.Log?.LogInfo("Work Order button created in crafting UI");
        }

        /// <summary>
        ///     Pin the Order button's text to the Craft button's enabled color and
        ///     disable Order's ButtonTextColor, so the text never greys with
        ///     interactable/focus state — only the gold focus border changes.
        /// </summary>
        private static void PinOrderTextColor(Button craftButton, GameObject orderGO)
        {
            // Craft's ButtonTextColor captured its enabled color from the prefab in
            // Awake; read it (private field) as the canonical "available" color.
            var enabledColor = Color.white;
            var craftBtc = craftButton.GetComponent<ButtonTextColor>();
            if (craftBtc != null)
            {
                var field = typeof(ButtonTextColor).GetField(
                    "m_defaultColor", BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null) enabledColor = (Color)field.GetValue(craftBtc);
            }

            var orderBtc = orderGO.GetComponent<ButtonTextColor>();
            if (orderBtc != null) orderBtc.enabled = false;

            // Set color via reflection (matches the rest of this file's TMP-free
            // style): a text component is one exposing both `text` and `color`.
            foreach (var comp in orderGO.GetComponentsInChildren<Component>(true))
            {
                if (comp == null) continue;
                var type = comp.GetType();
                var textProp = type.GetProperty("text", BindingFlags.Public | BindingFlags.Instance);
                var colorProp = type.GetProperty("color", BindingFlags.Public | BindingFlags.Instance);
                if (textProp != null && textProp.PropertyType == typeof(string)
                    && colorProp != null && colorProp.PropertyType == typeof(Color))
                    colorProp.SetValue(comp, enabledColor);
            }
        }

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
            if (VillagerTabManager.IsCustomTabActive)
            {
                _workOrderButton.SetActive(false);
                return;
            }

            // No work orders on the Upgrade tab — you upgrade existing gear there,
            // you don't queue production of it. (InUpradeTab is Valheim's typo.)
            if (gui.InUpradeTab())
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
            var hasWorkOrder = station != null && StationWorkOrderMap.ContainsKey(station.m_name);
            var isVirtual = station != null && IsVirtualStation(station.m_name);

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
        ///     For virtual stations: make the Order button use the Craft button's
        ///     rect so it replaces it in place (native Valheim layout).
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
        ///     Position the work order button identical to the craft button in size,
        ///     stacked directly above it with a small gap (for physical stations).
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
            var craftHeight = craftRect.rect.height;
            var woHeight = woRect.rect.height;
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

            var itemPrefabName = selectedRecipe.m_item?.gameObject?.name;
            var itemDisplayName =
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
            var localizedName = Localization.instance.Localize(
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
            // Re-enable the Craft button's X hotkey so the player's own crafting
            // menu isn't left with X disabled after a focus-nav session.
            var gui = InventoryGui.instance;
            if (gui != null && gui.m_craftButton != null)
                SetGamepadEnabled(gui.m_craftButton.gameObject, true);
            s_focus = CraftFocus.List;
        }
    }
}