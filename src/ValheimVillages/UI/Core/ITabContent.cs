using System.Collections.Generic;

namespace ValheimVillages.UI.Core
{
    /// <summary>
    ///     A tab whose content is driven by some <typeparamref name="TSubject" /> —
    ///     a villager (<see cref="Interaction.VillagerBehaviorBridge" />) for the
    ///     villager UI, or a <see cref="RegistryContext" /> for the Village Registry.
    ///     <see cref="CraftingTabHostBase{TSubject}" /> renders these into Valheim's
    ///     crafting GUI (RecipeList on the left, Decription panel on the right).
    /// </summary>
    public interface ITabContent<in TSubject>
    {
        /// <summary>Display name shown on the tab button.</summary>
        string TabName { get; }

        /// <summary>Called when this tab becomes the active tab.</summary>
        void OnSelected(TSubject subject);

        /// <summary>Called when this tab is no longer active.</summary>
        void OnDeselected();

        /// <summary>Called periodically while the tab is active to refresh data.</summary>
        void OnUpdate(TSubject subject);

        /// <summary>Items to display in the recipe list (left pane).</summary>
        List<TabListItemUI> GetListItems(TSubject subject);

        /// <summary>Detail information for the selected list item (right pane).</summary>
        TabDetailDataUI GetDetail(int selectedIndex, TSubject subject);
    }
}
