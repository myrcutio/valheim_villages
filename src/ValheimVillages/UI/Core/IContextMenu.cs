namespace ValheimVillages.UI.Core
{
    /// <summary>
    /// Interface for tag-driven context menus shown on NPC interaction.
    /// Discovered via NPC definition tags (e.g. "contextmenu:workorder").
    /// Registration is manual in this phase; attribute-based registration comes in Phase 4.
    /// </summary>
    public interface IContextMenu
    {
        /// <summary>Tag that identifies this context menu (e.g. "workorder").</summary>
        string Id { get; }

        /// <summary>Whether this menu should be available for the current NPC.</summary>
        bool CanShow(Interaction.VillagerBehaviorBridge villager);

        /// <summary>Show the context menu.</summary>
        void Show(Interaction.VillagerBehaviorBridge villager);
    }
}
