using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Schemas;
using ValheimVillages.UI.Interaction;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     Extended IVillagerTab interface with Unity-dependent UI methods.
    ///     Tab implementations should implement this interface.
    ///     The Core IVillagerTab provides the Unity-free base (Name, OnDeselected).
    /// </summary>
    public interface IVillagerTabUI : IVillagerTab
    {
        /// <summary>Called when this tab becomes the active tab.</summary>
        void OnSelected(VillagerBehaviorBridge villager);

        /// <summary>Called periodically to refresh tab data.</summary>
        void OnUpdate(VillagerBehaviorBridge villager);

        /// <summary>Get items to display in the recipe list (left pane).</summary>
        List<TabListItemUI> GetListItems(VillagerBehaviorBridge villager);

        /// <summary>Get detail information for the selected list item (right pane).</summary>
        TabDetailDataUI GetDetail(int selectedIndex, VillagerBehaviorBridge villager);
    }

    /// <summary>
    ///     Extended TabListItem with Unity Sprite support.
    /// </summary>
    public class TabListItemUI : TabListItem
    {
        public Sprite Icon { get; set; }
    }

    /// <summary>
    ///     Extended TabDetailData with Unity Sprite and Texture2D support.
    /// </summary>
    public class TabDetailDataUI : TabDetailData
    {
        public Sprite Icon { get; set; }

        /// <summary>
        ///     Optional texture rendered as a panel above the action button.
        ///     Used by the debug tab to show a top-down village patrol map.
        /// </summary>
        public Texture2D MapTexture { get; set; }
    }
}