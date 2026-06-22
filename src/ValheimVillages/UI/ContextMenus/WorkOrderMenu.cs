using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ValheimVillages.Attributes;
using ValheimVillages.Items.WorkOrders;
using ValheimVillages.UI.Core;

namespace ValheimVillages.UI.ContextMenus
{
    /// <summary>
    ///     Unity UI menu for configuring work order production quotas (min, max).
    ///     Opens when a player right-clicks a work order item.
    ///     Styled to match Valheim's native UI aesthetic via VillagerUIFactory.
    /// </summary>
    [RegisterModObject("WorkOrderMenu")]
    public class WorkOrderMenu : MonoBehaviour
    {
        private const int MinQuota = 1;

        /// <summary>Hard ceiling for typed input. Slider has its own lower cap.</summary>
        private const int MaxQuota = 9999;

        private static WorkOrderMenu s_instance;
        private ItemDrop.ItemData m_currentItem;
        private Image m_iconImage;
        private string m_itemDisplay = "";
        private GameObject m_itemLabel;
        private InputField m_maxInput;
        private Slider m_maxSlider;
        private int m_maximum = 10;
        private InputField m_minInput;

        // UI element references (populated by WorkOrderMenuBuilder)
        private Slider m_minSlider;

        // Backing data
        private int m_minimum = 1;

        // The quota values as loaded from the record, so SaveToItem only sends a real change.
        private int m_loadedMinimum = 1;
        private int m_loadedMaximum = 10;
        private GameObject m_panelRoot;
        private string m_stationDisplay = "";
        private GameObject m_stationLabel;
        private GameObject m_currentLabel;
        private GameObject m_statusLabel;

        // Prevents circular updates between slider <-> input field
        private bool m_updatingUI;

        private bool m_visible;

        // Controller focus: stick up/down cycles Back / Min slider / Max slider;
        // left/right on a slider adjusts by 1. Text inputs stay mouse-only.
        private enum Focus
        {
            Delete,
            Back,
            MinSlider,
            MaxSlider,
        }

        private GameObject m_backButton;
        private GameObject m_deleteButton;
        private Focus m_focus = Focus.MinSlider;
        private GameObject m_focusBorder;
        private RectTransform m_lastBorderTarget;
        private bool m_ignoreXUntilReleased;

        /// <summary>Whether the menu is currently visible.</summary>
        public static bool IsVisible =>
            s_instance != null && s_instance.m_visible;

        /// <summary>
        ///     True while one of the quota input fields has keyboard focus, so
        ///     Valheim's own input polling can be suppressed during typing.
        /// </summary>
        public static bool IsEditingText =>
            s_instance != null && s_instance.m_visible &&
            ((s_instance.m_minInput != null && s_instance.m_minInput.isFocused) ||
             (s_instance.m_maxInput != null && s_instance.m_maxInput.isFocused));

        /// <summary>
        ///     Opens the work order editor docked inside the open inventory/chest
        ///     UI. Work orders are only functional inside a chest, so the editor
        ///     lives within that context rather than as a standalone popup.
        /// </summary>
        public static void Show(ItemDrop.ItemData item)
        {
            if (item == null) return;

            var gui = InventoryGui.instance;
            if (gui == null || !InventoryGui.IsVisible())
            {
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center,
                    "Open a chest to configure this work order");
                return;
            }

            CleanupStaleInstances();

            if (s_instance == null)
            {
                var go = new GameObject("WorkOrderMenu");
                s_instance = go.AddComponent<WorkOrderMenu>();
            }

            s_instance.m_currentItem = item;
            s_instance.LoadFromItem(item);
            s_instance.EnsurePanel(gui);
            s_instance.SyncUIFromData();
            s_instance.PositionPanel(gui);
            s_instance.m_panelRoot.SetActive(true);
            s_instance.m_visible = true;

            // Default controller focus to the Min slider, and ignore the X press
            // that opened this menu until it's released (so it doesn't instantly
            // close). Force the focus border to re-attach on the next frame.
            s_instance.m_focus = Focus.MinSlider;
            s_instance.m_ignoreXUntilReleased = true;
            s_instance.m_lastBorderTarget = null;
        }

        /// <summary>
        ///     Commits the current settings and hides the editor. The inventory
        ///     stays open. With a single "Back" button (no separate Save/Cancel),
        ///     closing always applies the changes.
        /// </summary>
        public static void Hide()
        {
            if (s_instance == null) return;

            s_instance.SaveToItem();
            s_instance.m_visible = false;
            s_instance.m_currentItem = null;
            if (s_instance.m_panelRoot != null)
                s_instance.m_panelRoot.SetActive(false);
        }

        #region Lifecycle

        private void Update()
        {
            if (!m_visible) return;

            if (Input.GetKeyDown(KeyCode.Escape) ||
                Input.GetKeyDown(KeyCode.Tab))
            {
                Hide();
                return;
            }

            if (ZInput.IsGamepadActive())
            {
                RouteActionButton();
                HandleNav();
            }

            if (HandleActivate()) return;

            UpdateFocusVisual();
        }

        /// <summary>
        ///     A (gamepad) or E (keyboard) also activates the focused top button —
        ///     Delete or Back — in addition to X. Returns true if it closed the menu.
        /// </summary>
        private bool HandleActivate()
        {
            if (m_focus != Focus.Delete && m_focus != Focus.Back) return false;
            if (!ZInput.GetButtonDown("JoyButtonA") && !Input.GetKeyDown(KeyCode.E)) return false;

            if (m_focus == Focus.Delete) DeleteOrder();
            else Hide();
            return true;
        }

        /// <summary>
        ///     Route the X hotkey to the focused top button: it deletes when Delete
        ///     is focused, otherwise closes (Back). Both buttons clone the craft
        ///     button's ButtonX UIGamePad, so we enable only the target's — exactly
        ///     like the craft/order buttons. While the opening X is still held, both
        ///     stay off so the menu doesn't instantly act.
        /// </summary>
        private void RouteActionButton()
        {
            if (m_ignoreXUntilReleased)
            {
                if (!ZInput.GetButton("JoyButtonX")) m_ignoreXUntilReleased = false;
                SetButtonGamepad(m_deleteButton, false);
                SetButtonGamepad(m_backButton, false);
                return;
            }

            var deleteFocused = m_focus == Focus.Delete;
            SetButtonGamepad(m_deleteButton, deleteFocused);
            SetButtonGamepad(m_backButton, !deleteFocused);
        }

        private static void SetButtonGamepad(GameObject button, bool enabled)
        {
            if (button == null) return;
            var pad = button.GetComponent<UIGamePad>();
            if (pad == null) return;
            if (pad.enabled != enabled) pad.enabled = enabled;
            // A disabled UIGamePad freezes its X glyph visible; hide it so the
            // glyph only shows on the button X will actually trigger.
            if (!enabled && pad.m_hint != null) pad.m_hint.SetActive(false);
        }

        /// <summary>
        ///     Stick/D-pad navigation: left/right switches the top buttons
        ///     (Delete/Back); down enters the sliders, up returns to the top; on a
        ///     slider, left/right adjusts its value by 1.
        /// </summary>
        private void HandleNav()
        {
            var up = ZInput.GetButtonDown("JoyLStickUp") || ZInput.GetButtonDown("JoyDPadUp");
            var down = ZInput.GetButtonDown("JoyLStickDown") || ZInput.GetButtonDown("JoyDPadDown");
            var left = ZInput.GetButtonDown("JoyLStickLeft") || ZInput.GetButtonDown("JoyDPadLeft");
            var right = ZInput.GetButtonDown("JoyLStickRight") || ZInput.GetButtonDown("JoyDPadRight");

            switch (m_focus)
            {
                case Focus.Delete:
                    if (right) m_focus = Focus.Back;
                    else if (down) m_focus = Focus.MinSlider;
                    break;
                case Focus.Back:
                    if (left) m_focus = Focus.Delete;
                    else if (down) m_focus = Focus.MinSlider;
                    break;
                case Focus.MinSlider:
                    if (up) m_focus = Focus.Back;
                    else if (down) m_focus = Focus.MaxSlider;
                    else if (left) AdjustSlider(-1);
                    else if (right) AdjustSlider(1);
                    break;
                case Focus.MaxSlider:
                    if (up) m_focus = Focus.MinSlider;
                    else if (left) AdjustSlider(-1);
                    else if (right) AdjustSlider(1);
                    break;
            }
        }

        /// <summary>Remove the current work order from its inventory and close.</summary>
        private void DeleteOrder()
        {
            var item = m_currentItem;

            // Close without committing — the item is going away.
            m_visible = false;
            m_currentItem = null;
            if (m_panelRoot != null) m_panelRoot.SetActive(false);

            if (item == null) return;

            // Remove the host-owned record first (host-authoritative), then the UI handle token.
            var station = GetData(item, "wo_station", "");
            var itemPrefab = GetData(item, "wo_item", "");
            if (!string.IsNullOrEmpty(station) && !string.IsNullOrEmpty(itemPrefab))
            {
                var village = ResolveVillage();
                if (village != null)
                    Villager.WorkOrderConfigRpc.RequestDelete(village.VillageId, station, itemPrefab);
            }

            var inv = FindInventory(item);
            if (inv != null && inv.RemoveItem(item))
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center, "Work order removed");
        }

        private static Inventory FindInventory(ItemDrop.ItemData item)
        {
            var playerInv = Player.m_localPlayer?.GetInventory();
            if (playerInv != null && playerInv.ContainsItem(item)) return playerInv;
            var containerInv = InventoryGui.instance?.ContainerGrid?.GetInventory();
            if (containerInv != null && containerInv.ContainsItem(item)) return containerInv;
            return null;
        }

        private void AdjustSlider(int delta)
        {
            var slider = m_focus == Focus.MinSlider ? m_minSlider : m_maxSlider;
            if (slider == null) return;
            // Setting value fires OnMin/MaxSliderChanged, which clamps and syncs.
            slider.value = Mathf.Clamp(
                Mathf.RoundToInt(slider.value) + delta,
                Mathf.RoundToInt(slider.minValue),
                Mathf.RoundToInt(slider.maxValue));
        }

        private RectTransform FocusTarget()
        {
            return m_focus switch
            {
                Focus.Delete => m_deleteButton != null ? m_deleteButton.transform as RectTransform : null,
                Focus.Back => m_backButton != null ? m_backButton.transform as RectTransform : null,
                Focus.MinSlider => m_minSlider != null ? m_minSlider.transform as RectTransform : null,
                Focus.MaxSlider => m_maxSlider != null ? m_maxSlider.transform as RectTransform : null,
                _ => null,
            };
        }

        /// <summary>Move the gold focus border onto the focused element (gamepad only).</summary>
        private void UpdateFocusVisual()
        {
            if (m_focusBorder == null) return;

            var target = FocusTarget();
            if (target == null || !ZInput.IsGamepadActive())
            {
                if (m_focusBorder.activeSelf) m_focusBorder.SetActive(false);
                return;
            }

            if (target != m_lastBorderTarget)
            {
                m_lastBorderTarget = target;
                var rt = m_focusBorder.GetComponent<RectTransform>();
                rt.SetParent(target, false);
                rt.anchorMin = Vector2.zero;
                rt.anchorMax = Vector2.one;
                rt.offsetMin = Vector2.zero;
                rt.offsetMax = Vector2.zero;
                rt.SetAsLastSibling();
            }

            if (!m_focusBorder.activeSelf) m_focusBorder.SetActive(true);
        }

        private void OnDestroy()
        {
            if (m_panelRoot != null)
                Destroy(m_panelRoot);
        }

        private static void CleanupStaleInstances()
        {
#pragma warning disable CS0618
            var existing = FindObjectsOfType<WorkOrderMenu>();
            foreach (var menu in existing)
                if (menu != s_instance)
                    Destroy(menu.gameObject);
#pragma warning restore CS0618
        }

        #endregion

        #region Data

        private void LoadFromItem(ItemDrop.ItemData item)
        {
            var stationRaw = GetData(item, "wo_station", "Unknown");
            m_stationDisplay = FormatStationName(stationRaw);

            // Quota is host-authoritative now (Fix C) — read it from the village record, not the
            // token. Resolve the village at the player (FindNearAnchor is graph-independent so it
            // works on a client). Fall back to the token's legacy values only for an un-migrated
            // token with no record entry yet.
            var itemPrefab = GetData(item, "wo_item", "");
            var village = ResolveVillage();
            if (village != null && !string.IsNullOrEmpty(itemPrefab)
                && village.TryGetWorkOrder(stationRaw, itemPrefab, out var entry))
            {
                m_minimum = entry.Min;
                m_maximum = entry.Max;
            }
            else
            {
                m_minimum = int.TryParse(GetData(item, "wo_min", "1"), out var min) ? min : 1;
                m_maximum = int.TryParse(GetData(item, "wo_max", "10"), out var max) ? max : 10;
            }

            m_loadedMinimum = m_minimum;
            m_loadedMaximum = m_maximum;

            var rawItemName = GetData(item, "wo_item_name", "");
            m_itemDisplay = string.IsNullOrEmpty(rawItemName)
                ? "Not set"
                : Localization.instance.Localize(rawItemName);
        }

        /// <summary>
        ///     Resolve the village at the editing player — graph coverage if available, else the
        ///     graph-independent nearest-anchor lookup (works on a client without the region
        ///     graph). Same pattern as the recruit flow.
        /// </summary>
        private static Villages.Entity.Village ResolveVillage()
        {
            var pos = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
            return Villages.Entity.VillageRegistry.GetVillageCovering(pos)
                   ?? Villages.Entity.VillageRegistry.FindNearAnchor(pos);
        }

        private void SaveToItem()
        {
            if (m_currentItem == null) return;

            // Only push an actual change. Closing without editing must NOT re-send the loaded
            // value — if it was read a hair before the host's edit replicated, re-sending would
            // clobber the player's real edit (the observed revert).
            if (m_minimum == m_loadedMinimum && m_maximum == m_loadedMaximum) return;

            var station = GetData(m_currentItem, "wo_station", "");
            var itemPrefab = GetData(m_currentItem, "wo_item", "");
            if (string.IsNullOrEmpty(station) || string.IsNullOrEmpty(itemPrefab))
            {
                Plugin.Log?.LogWarning("[WorkOrderMenu] token missing wo_station/wo_item; quota not saved.");
                return;
            }

            // Update the tooltip locally for immediate feedback (client-side display only).
            if (m_currentItem.m_shared != null)
                m_currentItem.m_shared.m_description =
                    $"{m_itemDisplay} ({m_minimum}-{m_maximum})" +
                    "\nRight-click to change settings.";

            // Quota is host-authoritative (Fix C): route the edit through the host RPC instead of
            // writing the chest token. The old PersistEdit path (mutate m_customData ->
            // Inventory.Changed -> owner-gated Container.Save) lost the ownership race against the
            // farmers (and the open-chest SetOwner flip) and got clobbered. No token write here.
            var village = ResolveVillage();
            if (village == null)
            {
                Player.m_localPlayer?.Message(
                    MessageHud.MessageType.Center, "No village here — work order quota not saved");
                return;
            }

            Villager.WorkOrderConfigRpc.RequestSet(
                village.VillageId, station, itemPrefab,
                GetData(m_currentItem, "wo_item_name", ""), m_minimum, m_maximum);
        }

        private static string GetData(
            ItemDrop.ItemData item, string key, string fallback)
        {
            if (item?.m_customData != null &&
                item.m_customData.TryGetValue(key, out var val))
                return val;
            return fallback;
        }

        private static string FormatStationName(string raw)
        {
            return StationDisplay.Pretty(raw);
        }

        #endregion

        #region Panel Setup

        // Minimum editor size, so it stays usable even over a small chest.
        private const float MinWidth = 340f;
        private const float MinHeight = 320f;

        private void EnsurePanel(InventoryGui gui)
        {
            if (m_panelRoot != null) return;

            // Built under the canvas; PositionPanel re-parents it to the chest
            // window each time it's shown.
            var canvas = gui.GetComponentInParent<Canvas>();
            var parent = canvas != null ? canvas.transform : gui.transform;

            var el = WorkOrderMenuBuilder.Build(
                parent,
                Hide, // "Back" — commits and closes
                DeleteOrder, // "Delete Order" — removes the item and closes
                OnMinSliderChanged, OnMaxSliderChanged,
                OnMinInputEnd, OnMaxInputEnd);

            m_panelRoot = el.Root;
            m_backButton = el.BackButton;
            m_deleteButton = el.DeleteButton;
            m_iconImage = el.IconImage;
            m_minSlider = el.MinSlider;
            m_minInput = el.MinInput;
            m_maxSlider = el.MaxSlider;
            m_maxInput = el.MaxInput;
            m_stationLabel = el.StationLabel;
            m_itemLabel = el.ItemLabel;
            m_currentLabel = el.CurrentLabel;
            m_statusLabel = el.StatusLabel;

            m_focusBorder = CreateFocusBorder(m_panelRoot.transform);

            // Become the active input group while open: every inventory grid's
            // UpdateGamepad early-returns when its own UIGroupHandler isn't the
            // active (highest-priority) one, so the player/container grids stop
            // reading the stick/D-pad. Auto-restores when the panel is hidden.
            var group = m_panelRoot.GetComponent<UIGroupHandler>()
                        ?? m_panelRoot.AddComponent<UIGroupHandler>();
            group.m_groupPriority = 10000;

            // The panel lives inside the inventory window, whose group we just
            // deactivated — its CanvasGroup goes non-interactable and that would
            // propagate down (graying the Back button + killing its glyph, since
            // both key off IsInteractable). Ignore parent groups so the panel
            // stays interactable on its own.
            var cg = m_panelRoot.GetComponent<CanvasGroup>()
                     ?? m_panelRoot.AddComponent<CanvasGroup>();
            cg.ignoreParentGroups = true;
            cg.interactable = true;
            cg.blocksRaycasts = true;
        }

        // Gold focus border (four edge strips), matching the craft menu's feel.
        // Re-parented onto the focused element each time focus changes.
        private static readonly Color FocusBorderColor = new(1f, 0.78f, 0.28f, 0.95f);

        private static GameObject CreateFocusBorder(Transform parent)
        {
            var root = new GameObject("VV_FocusBorder", typeof(RectTransform));
            var rt = root.GetComponent<RectTransform>();
            rt.SetParent(parent, false);

            const float t = 3f;
            AddFocusEdge(rt, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(0f, t)); // top
            AddFocusEdge(rt, new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, t)); // bottom
            AddFocusEdge(rt, new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(t, 0f)); // left
            AddFocusEdge(rt, new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(t, 0f)); // right

            root.SetActive(false);
            return root;
        }

        private static void AddFocusEdge(Transform parent, Vector2 anchorMin, Vector2 anchorMax, Vector2 sizeDelta)
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
            img.color = FocusBorderColor;
            img.raycastTarget = false;
        }

        /// <summary>
        ///     Overlay the editor on the player inventory window — always, so it
        ///     opens in the same place whether launched from a chest or the bare
        ///     inventory. The editor inherits that window's footprint (so it fits
        ///     and scales with the UI at any resolution) and only grows past it if
        ///     the window is smaller than the editor's minimum.
        /// </summary>
        private void PositionPanel(InventoryGui gui)
        {
            if (m_panelRoot == null) return;

            // Always anchor to the player inventory window (shown whether opened
            // from a chest or the bare inventory) so the editor opens in the same
            // place every time, instead of jumping to the container when present.
            var window = gui.m_player;
            if (window == null) return;

            var rt = m_panelRoot.GetComponent<RectTransform>();
            rt.SetParent(window, false);

            // Ignore any layout group on the window so we control the size.
            var le = m_panelRoot.GetComponent<LayoutElement>()
                     ?? m_panelRoot.AddComponent<LayoutElement>();
            le.ignoreLayout = true;

            // Centre on the window and size to match its wood frame (the native
            // "Bkg" sprite is ~20px larger than the window's content rect), so our
            // cloned frame aligns exactly with the chest's and the title/back
            // button land where "Chest"/"Place stacks" do.
            const float frameMargin = 20f;
            var winSize = window.rect.size;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = Vector2.zero;
            rt.sizeDelta = new Vector2(
                Mathf.Max(winSize.x, MinWidth) + frameMargin,
                Mathf.Max(winSize.y, MinHeight) + frameMargin);

            // Draw on top of the grid.
            rt.SetAsLastSibling();
        }

        private void SyncUIFromData()
        {
            m_updatingUI = true;

            SetLabel(m_stationLabel,
                $"Station: <color={ValueColor}>{m_stationDisplay}</color>");

            // The work order's composited parchment+item icon.
            if (m_iconImage != null)
            {
                var icons = m_currentItem?.m_shared?.m_icons;
                var sprite = icons != null && icons.Length > 0 ? icons[0] : null;
                m_iconImage.sprite = sprite;
                m_iconImage.enabled = sprite != null;
            }

            if (m_minSlider != null) m_minSlider.value = m_minimum;
            if (m_minInput != null) m_minInput.text = m_minimum.ToString();
            if (m_maxSlider != null) m_maxSlider.value = m_maximum;
            if (m_maxInput != null) m_maxInput.text = m_maximum.ToString();

            RefreshDynamicLabels();
            UpdateStatusAndCurrent();
            m_updatingUI = false;
        }

        /// <summary>
        ///     Scan the village once to show the current stored quantity and a
        ///     status line (out of materials, storage full, quota met, ...) in the
        ///     otherwise-empty bottom of the panel.
        /// </summary>
        private void UpdateStatusAndCurrent()
        {
            var pos = Player.m_localPlayer != null
                ? Player.m_localPlayer.transform.position
                : Vector3.zero;
            var status = WorkOrderDiagnostics.Describe(m_currentItem, pos);

            SetLabel(m_currentLabel,
                $"Current Quantity: <color={ValueColor}>{status.CurrentCount}</color>");

            if (string.IsNullOrEmpty(status.Message))
            {
                SetLabel(m_statusLabel, "");
                return;
            }

            var color = status.Severity switch
            {
                WorkOrderDiagnostics.Severity.Warning => "#E8643C",
                WorkOrderDiagnostics.Severity.Good => "#6FCF6F",
                _ => "#FFFFFF",
            };
            SetLabel(m_statusLabel, $"<color={color}>{status.Message}</color>");
        }

        // Item-detail theme: white label, orange value, yellow parenthetical.
        private const string ValueColor = "#FFA13C"; // Valheim orange
        private const string NoteColor = "#FFE300"; // Valheim yellow

        private void RefreshDynamicLabels()
        {
            // White label, orange value, white "Quota:" sub-label, yellow numbers.
            SetLabel(m_itemLabel,
                $"Item: <color={ValueColor}>{m_itemDisplay}</color> " +
                $"(Quota: <color={NoteColor}>{m_minimum}-{m_maximum}</color>)");
        }

        /// <summary>
        ///     Sets text on a label created by VillagerUIFactory.CreateLabel.
        ///     Uses TMPro reflection, then falls back to legacy Text.
        /// </summary>
        private static void SetLabel(GameObject go, string text)
        {
            if (go == null) return;
            VillagerUIFactory.SetTMPText(go, text);
        }

        #endregion

        #region Callbacks

        private void OnMinSliderChanged(float value)
        {
            if (m_updatingUI) return;
            m_minimum = Mathf.Clamp((int)value, MinQuota, MaxQuota);
            if (m_minimum > m_maximum) m_maximum = m_minimum;

            m_updatingUI = true;
            if (m_minInput != null) m_minInput.text = m_minimum.ToString();
            if (m_maxSlider != null) m_maxSlider.value = m_maximum;
            if (m_maxInput != null) m_maxInput.text = m_maximum.ToString();
            RefreshDynamicLabels();
            m_updatingUI = false;
        }

        private void OnMaxSliderChanged(float value)
        {
            if (m_updatingUI) return;
            m_maximum = Mathf.Clamp((int)value, MinQuota, MaxQuota);
            if (m_maximum < m_minimum) m_minimum = m_maximum;

            m_updatingUI = true;
            if (m_maxInput != null) m_maxInput.text = m_maximum.ToString();
            if (m_minSlider != null) m_minSlider.value = m_minimum;
            if (m_minInput != null) m_minInput.text = m_minimum.ToString();
            RefreshDynamicLabels();
            m_updatingUI = false;
        }

        private void OnMinInputEnd(string text)
        {
            if (m_updatingUI) return;
            if (!int.TryParse(text, out var val)) return;
            m_minimum = Mathf.Clamp(val, MinQuota, MaxQuota);
            if (m_minimum > m_maximum) m_maximum = m_minimum;

            m_updatingUI = true;
            if (m_minSlider != null) m_minSlider.value = m_minimum;
            if (m_minInput != null) m_minInput.text = m_minimum.ToString();
            if (m_maxSlider != null) m_maxSlider.value = m_maximum;
            if (m_maxInput != null) m_maxInput.text = m_maximum.ToString();
            RefreshDynamicLabels();
            m_updatingUI = false;
        }

        private void OnMaxInputEnd(string text)
        {
            if (m_updatingUI) return;
            if (!int.TryParse(text, out var val)) return;
            m_maximum = Mathf.Clamp(val, MinQuota, MaxQuota);
            if (m_maximum < m_minimum) m_minimum = m_maximum;

            m_updatingUI = true;
            if (m_maxSlider != null) m_maxSlider.value = m_maximum;
            if (m_maxInput != null) m_maxInput.text = m_maximum.ToString();
            if (m_minSlider != null) m_minSlider.value = m_minimum;
            if (m_minInput != null) m_minInput.text = m_minimum.ToString();
            RefreshDynamicLabels();
            m_updatingUI = false;
        }

        #endregion
    }
}