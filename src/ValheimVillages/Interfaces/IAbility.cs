namespace ValheimVillages.Interfaces
{
    /// <summary>
    ///     Interface for teachable abilities that an NPC can teach a player.
    ///     Core version with Unity-free members only.
    ///     Player-dependent methods (HasLearned, Learn, Activate) are defined
    ///     in the mod assembly via IPlayerAbility.
    /// </summary>
    public interface IAbility
    {
        /// <summary>Unique ability identifier (e.g. "mountainstride").</summary>
        string Id { get; }

        /// <summary>Display name shown in the UI.</summary>
        string DisplayName { get; }

        /// <summary>Description shown in the ability detail panel.</summary>
        string Description { get; }
    }
}