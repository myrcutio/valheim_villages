using System.Collections.Generic;
using System.Globalization;
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
        private GameObject m_panelRoot;
        private string m_stationDisplay = "";
        private GameObject m_stationLabel;
        private GameObject m_currentLabel;
        private GameObject m_statusLabel;

        // Prevents circular updates between slider <-> input field
        private bool m_updatingUI;

        private bool m_visible;

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
                Hide();
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

            m_minimum = int.TryParse(
                GetData(item, "wo_min", "1"), out var min)
                ? min
                : 1;
            m_maximum = int.TryParse(
                GetData(item, "wo_max", "10"), out var max)
                ? max
                : 10;

            var rawItemName = GetData(item, "wo_item_name", "");
            m_itemDisplay = string.IsNullOrEmpty(rawItemName)
                ? "Not set"
                : Localization.instance.Localize(rawItemName);
        }

        private void SaveToItem()
        {
            if (m_currentItem == null) return;

            if (m_currentItem.m_customData == null)
                m_currentItem.m_customData =
                    new Dictionary<string, string>();

            m_currentItem.m_customData["wo_min"] = m_minimum.ToString();
            m_currentItem.m_customData["wo_max"] = m_maximum.ToString();
            m_currentItem.m_customData["wo_range"] =
                $"{m_minimum}-{m_maximum}";

            // Update tooltip description if shared data is per-instance
            if (m_currentItem.m_shared != null)
                m_currentItem.m_shared.m_description =
                    $"{m_itemDisplay} ({m_minimum}-{m_maximum})" +
                    "\nRight-click to change settings.";
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
                Hide, // single "Back" button — commits and closes
                OnMinSliderChanged, OnMaxSliderChanged,
                OnMinInputEnd, OnMaxInputEnd);

            m_panelRoot = el.Root;
            m_iconImage = el.IconImage;
            m_minSlider = el.MinSlider;
            m_minInput = el.MinInput;
            m_maxSlider = el.MaxSlider;
            m_maxInput = el.MaxInput;
            m_stationLabel = el.StationLabel;
            m_itemLabel = el.ItemLabel;
            m_currentLabel = el.CurrentLabel;
            m_statusLabel = el.StatusLabel;
        }

        /// <summary>
        ///     Overlay the editor on top of the chest (container) window, covering
        ///     its grid. The editor inherits the window's footprint — so it always
        ///     fits and scales with the UI regardless of resolution — and only
        ///     grows past it if the chest is smaller than the editor's minimum.
        ///     Falls back to the player inventory window if no container is open.
        /// </summary>
        private void PositionPanel(InventoryGui gui)
        {
            if (m_panelRoot == null) return;

            var window = gui.m_container != null &&
                         gui.m_container.gameObject.activeInHierarchy
                ? gui.m_container
                : gui.m_player;
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