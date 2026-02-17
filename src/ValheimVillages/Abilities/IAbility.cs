namespace ValheimVillages.Abilities
{
    /// <summary>
    /// Interface for teachable abilities that an NPC can teach a player.
    /// Implementations are registered via [RegisterAbility] and discovered
    /// by AttributeScanner.
    /// </summary>
    public interface IAbility
    {
        /// <summary>Unique ability identifier (e.g. "mountainstride").</summary>
        string Id { get; }

        /// <summary>Display name shown in the UI.</summary>
        string DisplayName { get; }

        /// <summary>Description shown in the ability detail panel.</summary>
        string Description { get; }

        /// <summary>Check if the player has learned this ability.</summary>
        bool HasLearned(Player player);

        /// <summary>Teach this ability to the player.</summary>
        void Learn(Player player);

        /// <summary>Activate this ability for the player.</summary>
        void Activate(Player player);
    }
}
