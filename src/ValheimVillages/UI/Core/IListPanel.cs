using System.Collections.Generic;
using ValheimVillages.UI.Interaction;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    /// Extended IListPanel interface with Unity-dependent UI methods.
    /// Panel implementations should implement this interface.
    /// The Core IListPanel provides the Unity-free base (Tag, ParentTab).
    /// </summary>
    public interface IListPanelUI : IListPanel
    {
        /// <summary>Get list items contributed by this panel.</summary>
        List<TabListItemUI> GetListItems(VillagerBehaviorBridge villager);

        /// <summary>Get detail data for a selected item from this panel.</summary>
        TabDetailDataUI GetDetail(int index, VillagerBehaviorBridge villager);
    }
}
