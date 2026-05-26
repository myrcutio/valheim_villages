namespace ValheimVillages.Interfaces
{
    /// <summary>
    ///     Interface for tag-driven list panels that provide items for a parent tab.
    ///     Core version with Unity-free members only.
    ///     Unity-dependent methods are defined in the mod assembly.
    /// </summary>
    public interface IListPanel
    {
        /// <summary>Tag that identifies this panel (e.g. "patrolstatus", "villagemap").</summary>
        string Tag { get; }

        /// <summary>The parent tab this panel contributes items to (e.g. "info", "debug").</summary>
        string ParentTab { get; }
    }
}