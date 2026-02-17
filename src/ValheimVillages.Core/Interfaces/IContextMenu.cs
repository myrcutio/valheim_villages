namespace ValheimVillages.UI.Core
{
    /// <summary>
    /// Interface for tag-driven context menus shown on NPC interaction.
    /// Core version with Unity-free members only.
    /// Unity-dependent methods (CanShow, Show) are defined in the mod assembly.
    /// </summary>
    public interface IContextMenu
    {
        /// <summary>Tag that identifies this context menu (e.g. "workorder").</summary>
        string Id { get; }
    }
}
