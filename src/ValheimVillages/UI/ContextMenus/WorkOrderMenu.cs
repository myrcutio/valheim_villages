using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using ValheimVillages.Items.WorkOrders;
using ValheimVillages.UI.Core;

namespace ValheimVillages.UI.ContextMenus
{
    /// <summary>
    /// Unity UI menu for configuring work order production quotas (min, max).
    /// Opens when a player right-clicks a work order item.
    /// Styled to match Valheim's native UI aesthetic via VillagerUIFactory.
    /// </summary>
    public class WorkOrderMenu : MonoBehaviour
    {
        private static WorkOrderMenu s_instance;

        private bool m_visible;
        private ItemDrop.ItemData m_currentItem;
        private GameObject m_panelRoot;

        // UI element references (populated by WorkOrderMenuBuilder)
        private Slider m_minSlider;
        private InputField m_minInput;
        private Slider m_maxSlider;
        private InputField m_maxInput;
        private GameObject m_stationLabel;
        private GameObject m_itemLabel;
        private GameObject m_rangeLabel;

        // Backing data
        private int m_minimum = 1;
        private int m_maximum = 10;
        private string m_stationDisplay = "";
        private string m_itemDisplay = "";

        // Prevents circular updates between slider <-> input field
        private bool m_updatingUI;

        private const int MinQuota = 1;
        /// <summary>Hard ceiling for typed input. Slider has its own lower cap.</summary>
        private const int MaxQuota = 9999;

        /// <summary>Whether the menu is currently visible.</summary>
        public static bool IsVisible =>
            s_instance != null && s_instance.m_visible;

        /// <summary>Opens the work order menu for the given item.</summary>
        public static void Show(ItemDrop.ItemData item)
        {
            if (item == null) return;

            CleanupStaleInstances();

            if (s_instance == null)
            {
                var go = new GameObject("WorkOrderMenu");
                s_instance = go.AddComponent<WorkOrderMenu>();
            }

            // Close the inventory/crafting UI so it doesn't block
            // interaction with the work order panel
            InventoryGui.instance?.Hide();

            s_instance.m_currentItem = item;
            s_instance.LoadFromItem(item);
            s_instance.EnsurePanel();
            s_instance.SyncUIFromData();
            s_instance.m_panelRoot.SetActive(true);
            s_instance.m_visible = true;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            Input.ResetInputAxes();

            Plugin.Log?.LogInfo("Opened work order menu");
        }

        /// <summary>Hides the work order menu.</summary>
        public static void Hide()
        {
            if (s_instance != null)
            {
                s_instance.m_visible = false;
                s_instance.m_currentItem = null;
                if (s_instance.m_panelRoot != null)
                    s_instance.m_panelRoot.SetActive(false);
            }

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
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
            var existing = Object.FindObjectsOfType<WorkOrderMenu>();
            foreach (var menu in existing)
            {
                if (menu != s_instance)
                    Destroy(menu.gameObject);
            }
#pragma warning restore CS0618
        }

        #endregion

        #region Data

        private void LoadFromItem(ItemDrop.ItemData item)
        {
            var stationRaw = GetData(item, "wo_station", "Unknown");
            m_stationDisplay = FormatStationName(stationRaw);

            m_minimum = int.TryParse(
                GetData(item, "wo_min", "1"), out var min) ? min : 1;
            m_maximum = int.TryParse(
                GetData(item, "wo_max", "10"), out var max) ? max : 10;

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
            var clean = raw
                .Replace("$piece_", "")
                .Replace("$vv_", "")
                .Replace("_", " ");
            return System.Globalization.CultureInfo.CurrentCulture
                .TextInfo.ToTitleCase(clean);
        }

        #endregion

        #region Panel Setup

        private void EnsurePanel()
        {
            if (m_panelRoot != null) return;

            var el = WorkOrderMenuBuilder.Build(
                OnSave, OnCancel,
                OnMinSliderChanged, OnMaxSliderChanged,
                OnMinInputEnd, OnMaxInputEnd);

            m_panelRoot = el.Root;
            m_minSlider = el.MinSlider;
            m_minInput = el.MinInput;
            m_maxSlider = el.MaxSlider;
            m_maxInput = el.MaxInput;
            m_stationLabel = el.StationLabel;
            m_itemLabel = el.ItemLabel;
            m_rangeLabel = el.RangeLabel;
        }

        private void SyncUIFromData()
        {
            m_updatingUI = true;

            SetLabel(m_stationLabel, $"Station: {m_stationDisplay}");
            SetLabel(m_itemLabel, $"Item: {m_itemDisplay}");

            if (m_minSlider != null) m_minSlider.value = m_minimum;
            if (m_minInput != null) m_minInput.text = m_minimum.ToString();
            if (m_maxSlider != null) m_maxSlider.value = m_maximum;
            if (m_maxInput != null) m_maxInput.text = m_maximum.ToString();

            UpdateRangeLabel();
            m_updatingUI = false;
        }

        private void UpdateRangeLabel()
        {
            SetLabel(m_rangeLabel,
                $"Production Range: {m_minimum} - {m_maximum}");
        }

        /// <summary>
        /// Sets text on a label created by VillagerUIFactory.CreateLabel.
        /// Uses TMPro reflection, then falls back to legacy Text.
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
            UpdateRangeLabel();
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
            UpdateRangeLabel();
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
            UpdateRangeLabel();
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
            UpdateRangeLabel();
            m_updatingUI = false;
        }

        private void OnSave()
        {
            SaveToItem();
            Player.m_localPlayer?.Message(
                MessageHud.MessageType.Center,
                $"Work order set: {m_minimum}-{m_maximum}");
            Hide();
        }

        private void OnCancel()
        {
            Hide();
        }

        #endregion
    }
}
