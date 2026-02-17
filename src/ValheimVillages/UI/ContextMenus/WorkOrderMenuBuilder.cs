using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using ValheimVillages.Items.WorkOrders;
using ValheimVillages.UI.Core;

namespace ValheimVillages.UI.ContextMenus
{
    /// <summary>
    /// Holds references to the interactive UI elements in the work order panel.
    /// </summary>
    public struct WorkOrderMenuElements
    {
        public GameObject Root;
        public Slider MinSlider;
        public InputField MinInput;
        public Slider MaxSlider;
        public InputField MaxInput;
        public GameObject StationLabel;
        public GameObject ItemLabel;
        public GameObject RangeLabel;
    }

    /// <summary>
    /// Builds the Unity UI panel hierarchy for the work order settings menu.
    /// Uses VillagerUIFactory for Valheim-native styling.
    /// </summary>
    public static class WorkOrderMenuBuilder
    {
        private const int MinQuota = 1;
        /// <summary>Slider cap (~2-3 full stacks). Input field is uncapped.</summary>
        private const int SliderMax = 100;
        private const float PanelWidth = 360f;
        private const float PanelHeight = 330f;

        public static WorkOrderMenuElements Build(
            UnityAction onSave, UnityAction onCancel,
            UnityAction<float> onMinSlider, UnityAction<float> onMaxSlider,
            UnityAction<string> onMinInput, UnityAction<string> onMaxInput)
        {
            var (root, content) = VillagerUIFactory.CreatePopupPanel(
                "Work Order Settings", PanelWidth, PanelHeight);

            var elements = new WorkOrderMenuElements { Root = root };

            // Station and item info labels
            elements.StationLabel = VillagerUIFactory.CreateLabel(
                content, "Station: ---");
            elements.ItemLabel = VillagerUIFactory.CreateLabel(
                content, "Item: ---");

            VillagerUIFactory.CreateDivider(content);

            // Minimum quantity: label + slider/input row
            VillagerUIFactory.CreateLabel(content, "Minimum Quantity:", 14f);
            var minRow = VillagerUIFactory.CreateHorizontalGroup(
                content, 8f, 28f);
            elements.MinSlider = VillagerUIFactory.CreateSlider(
                minRow.transform, MinQuota, SliderMax, true, onMinSlider);
            elements.MinInput = VillagerUIFactory.CreateInputField(
                minRow.transform, "1", onMinInput);

            VillagerUIFactory.CreateSpacer(content, 4f);

            // Maximum quantity: label + slider/input row
            VillagerUIFactory.CreateLabel(content, "Maximum Quantity:", 14f);
            var maxRow = VillagerUIFactory.CreateHorizontalGroup(
                content, 8f, 28f);
            elements.MaxSlider = VillagerUIFactory.CreateSlider(
                maxRow.transform, MinQuota, SliderMax, true, onMaxSlider);
            elements.MaxInput = VillagerUIFactory.CreateInputField(
                maxRow.transform, "10", onMaxInput);

            VillagerUIFactory.CreateDivider(content);

            // Range summary
            elements.RangeLabel = VillagerUIFactory.CreateLabel(
                content, "Production Range: 1 - 10");

            VillagerUIFactory.CreateSpacer(content, 8f);

            // Save / Cancel buttons
            var btnRow = VillagerUIFactory.CreateHorizontalGroup(
                content, 10f, 36f);
            VillagerUIFactory.CreateButton(btnRow.transform, "Save", onSave);
            VillagerUIFactory.CreateButton(
                btnRow.transform, "Cancel", onCancel);

            return elements;
        }
    }
}
