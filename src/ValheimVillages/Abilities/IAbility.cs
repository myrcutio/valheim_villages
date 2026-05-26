using ValheimVillages.Interfaces;

namespace ValheimVillages.Abilities
{
    /// <summary>
    ///     Extension of the Core IAbility interface adding Player-dependent methods.
    ///     Ability implementations should implement this interface.
    /// </summary>
    public interface IPlayerAbility : IAbility
    {
        /// <summary>Check if the player has learned this ability.</summary>
        bool HasLearned(Player player);

        /// <summary>Teach this ability to the player.</summary>
        void Learn(Player player);

        /// <summary>Activate this ability for the player.</summary>
        void Activate(Player player);
    }
}