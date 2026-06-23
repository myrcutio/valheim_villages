using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Schemas;
using ValheimVillages.UI.Interaction;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     Extended IVillagerTab interface with Unity-dependent UI methods.
    ///     Tab implementations should implement this interface.
    ///     The Core IVillagerTab provides the Unity-free base (Name, OnDeselected);
    ///     the per-subject UI surface (OnSelected/OnUpdate/GetListItems/GetDetail,
    ///     all taking a <see cref="VillagerBehaviorBridge" />) comes from
    ///     <see cref="ITabContent{TSubject}" /> so the villager UI and the Village
    ///     Registry UI share the same <see cref="CraftingTabHostBase{TSubject}" /> chrome.
    /// </summary>
    public interface IVillagerTabUI : IVillagerTab, ITabContent<VillagerBehaviorBridge>
    {
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

        /// <summary>
        ///     Optional recipe-style ingredient rows (item + amount), rendered with the native
        ///     <c>InventoryGui.SetupRequirement</c> so icons + have/need colouring match the
        ///     craft menu. Null = no ingredient row (the default text-only detail).
        /// </summary>
        public Piece.Requirement[] Requirements { get; set; }

        /// <summary>Optional min crafting-station level shown as the native level star. 0 = hidden.</summary>
        public int StationLevel { get; set; }
    }
}