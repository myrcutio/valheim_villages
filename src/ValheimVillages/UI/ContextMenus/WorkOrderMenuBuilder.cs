using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using ValheimVillages.UI.Core;

namespace ValheimVillages.UI.ContextMenus
{
    /// <summary>
    ///     Holds references to the interactive UI elements in the work order panel.
    /// </summary>
    public struct WorkOrderMenuElements
    {
        public GameObject Root;
        public Image IconImage;
        public Slider MinSlider;
        public InputField MinInput;
        public Slider MaxSlider;
        public InputField MaxInput;
        public GameObject StationLabel;
        public GameObject ItemLabel;
    }

    /// <summary>
    ///     Builds the Unity UI panel hierarchy for the work order settings menu.
    ///     Uses VillagerUIFactory for Valheim-native styling.
    /// </summary>
    public static class WorkOrderMenuBuilder
    {
        private const int MinQuota = 1;

        /// <summary>Slider cap (~2-3 full stacks). Input field is uncapped.</summary>
        private const int SliderMax = 100;

        private const float PanelWidth = 340f;
        private const float PanelHeight = 360f;

        // Slider/input row height (taller than default so controls aren't cramped).
        private const float RowHeight = 34f;

        // White labels, matching the item-detail panel theme (white label,
        // orange value, yellow parenthetical — values coloured via rich text).
        private static readonly Color HeaderText = Color.white;

        // Sized to match the other inventory UI text (titles/buttons), not the
        // dense stat list.
        private const float HeaderSize = 18f;
        private const float LabelSize = 16f;

        public static WorkOrderMenuElements Build(
            Transform parent,
            UnityAction onBack,
            UnityAction<float> onMinSlider, UnityAction<float> onMaxSlider,
            UnityAction<string> onMinInput, UnityAction<string> onMaxInput)
        {
            var (root, content) = VillagerUIFactory.CreateDockedPanel(
                parent, "Work Order", PanelWidth, PanelHeight);

            var elements = new WorkOrderMenuElements { Root = root };

            // Back button, top-right — same spot as the chest's "Place stacks".
            var backBtn = VillagerUIFactory.CreateButton(root.transform, "Back", onBack);
            if (backBtn != null)
            {
                var ble = backBtn.GetComponent<LayoutElement>()
                          ?? backBtn.gameObject.AddComponent<LayoutElement>();
                ble.ignoreLayout = true;
                var brt = backBtn.GetComponent<RectTransform>();
                brt.anchorMin = brt.anchorMax = new Vector2(1f, 1f);
                brt.pivot = new Vector2(1f, 1f);
                brt.sizeDelta = new Vector2(120f, 38f);
                brt.anchoredPosition = new Vector2(-26f, -16f);
            }

            // Header: order icon on the left of the station/item info column.
            var headerRow = VillagerUIFactory.CreateHorizontalGroup(
                content, 12f, 46f, false);
            elements.IconImage = VillagerUIFactory.CreateIcon(
                headerRow.transform, 50f);
            var infoCol = VillagerUIFactory.CreateVerticalGroup(
                headerRow.transform, 4f);
            elements.StationLabel = VillagerUIFactory.CreateLabel(
                infoCol.transform, "Station: ---", HeaderSize, HeaderText);
            elements.ItemLabel = VillagerUIFactory.CreateLabel(
                infoCol.transform, "Item: ---", HeaderSize, HeaderText);

            VillagerUIFactory.CreateDivider(content);

            // Minimum quantity: label + slider/input row
            VillagerUIFactory.CreateLabel(
                content, "Minimum Quantity:", LabelSize, HeaderText);
            var minRow = VillagerUIFactory.CreateHorizontalGroup(
                content, 8f, RowHeight, false);
            elements.MinSlider = VillagerUIFactory.CreateSlider(
                minRow.transform, MinQuota, SliderMax, true, onMinSlider);
            elements.MinInput = VillagerUIFactory.CreateInputField(
                minRow.transform, "1", onMinInput);

            VillagerUIFactory.CreateSpacer(content, 4f);

            // Maximum quantity: label + slider/input row
            VillagerUIFactory.CreateLabel(
                content, "Maximum Quantity:", LabelSize, HeaderText);
            var maxRow = VillagerUIFactory.CreateHorizontalGroup(
                content, 8f, RowHeight, false);
            elements.MaxSlider = VillagerUIFactory.CreateSlider(
                maxRow.transform, MinQuota, SliderMax, true, onMaxSlider);
            elements.MaxInput = VillagerUIFactory.CreateInputField(
                maxRow.transform, "10", onMaxInput);

            return elements;
        }
    }
}