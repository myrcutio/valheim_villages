namespace ValheimVillages.UI.Core
{
    /// <summary>
    /// Interface for a tab in the villager interaction UI.
    /// Core version with Unity-free members only.
    /// Unity-dependent methods are defined in the mod assembly.
    /// </summary>
    public interface IVillagerTab
    {
        /// <summary>Display name shown on the tab button.</summary>
        string TabName { get; }

        /// <summary>Called when this tab is no longer active.</summary>
        void OnDeselected();
    }
}
