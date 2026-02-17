namespace ValheimVillages.UI.Core
{
    /// <summary>
    /// Extended IContextMenu interface with Unity-dependent UI methods.
    /// Context menu implementations should implement this interface.
    /// The Core IContextMenu provides the Unity-free base (Id).
    /// </summary>
    public interface IContextMenuUI : IContextMenu
    {
        /// <summary>Whether this menu should be available for the current NPC.</summary>
        bool CanShow(Interaction.VillagerBehaviorBridge villager);

        /// <summary>Show the context menu.</summary>
        void Show(Interaction.VillagerBehaviorBridge villager);
    }
}
