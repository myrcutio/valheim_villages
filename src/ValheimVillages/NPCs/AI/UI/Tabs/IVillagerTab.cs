using System;
using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.NPCs.AI.UI.Tabs
{
    /// <summary>
    /// Interface for a tab in the villager interaction UI.
    /// Tabs provide list items (left pane) and detail data (right pane)
    /// that are rendered in Valheim's native RecipeList and Description panels.
    /// </summary>
    public interface IVillagerTab
    {
        /// <summary>Display name shown on the tab button.</summary>
        string Name { get; }

        /// <summary>Called when this tab becomes the active tab.</summary>
        void OnSelected(VillagerBehaviorBridge villager);

        /// <summary>Called when this tab is no longer active.</summary>
        void OnDeselected();

        /// <summary>Called periodically to refresh tab data.</summary>
        void OnUpdate(VillagerBehaviorBridge villager);

        /// <summary>
        /// Get items to display in the recipe list (left pane).
        /// Each item appears as a clickable row.
        /// </summary>
        List<TabListItem> GetListItems(VillagerBehaviorBridge villager);

        /// <summary>
        /// Get detail information for the selected list item (right pane).
        /// Returns null if nothing should be shown.
        /// </summary>
        TabDetailData GetDetail(
            int selectedIndex, VillagerBehaviorBridge villager);
    }

    /// <summary>
    /// A single item in the tab's recipe list (left pane).
    /// </summary>
    public class TabListItem
    {
        public string Name { get; set; }
        public Sprite Icon { get; set; }
    }

    /// <summary>
    /// Detail data shown in the description pane (right pane)
    /// when a list item is selected.
    /// </summary>
    public class TabDetailData
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public Sprite Icon { get; set; }
        public string ActionText { get; set; }
        public Action OnAction { get; set; }

        /// <summary>
        /// Optional texture rendered as a panel above the action button.
        /// Used by the debug tab to show a top-down village patrol map.
        /// </summary>
        public Texture2D MapTexture { get; set; }
    }
}
