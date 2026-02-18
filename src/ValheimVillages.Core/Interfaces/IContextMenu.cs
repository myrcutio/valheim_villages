namespace ValheimVillages.UI.Core
{
    /// <summary>
    /// Interface for context menus triggered by inventory item actions
    /// (e.g. right-clicking a work order item to configure its settings).
    /// Not used for NPC interaction side-panel tabs (those use IVillagerTab).
    /// Core version with Unity-free members only.
    /// Unity-dependent methods (CanShow, Show) are defined in the mod assembly.
    /// </summary>
    public interface IContextMenu
    {
        /// <summary>Tag that identifies this context menu (e.g. "workorder").</summary>
        string Id { get; }
    }
}
