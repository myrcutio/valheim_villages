using System.Collections.Generic;
using ValheimVillages.UI.Interaction;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    /// Interface for tag-driven list panels that provide items for a parent tab.
    /// Discovered via NPC definition tags (e.g. "listpanel:guardstatus").
    /// Registration is manual in this phase; attribute-based registration comes in Phase 4.
    /// </summary>
    public interface IListPanel
    {
        /// <summary>Tag that identifies this panel (e.g. "guardstatus", "villagemap").</summary>
        string Tag { get; }

        /// <summary>The parent tab this panel contributes items to (e.g. "info", "debug").</summary>
        string ParentTab { get; }

        /// <summary>Get list items contributed by this panel.</summary>
        List<TabListItem> GetListItems(VillagerBehaviorBridge villager);

        /// <summary>Get detail data for a selected item from this panel.</summary>
        TabDetailData GetDetail(int index, VillagerBehaviorBridge villager);
    }
}
