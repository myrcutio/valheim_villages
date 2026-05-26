using UnityEngine;

namespace ValheimVillages.Interfaces
{
    /// <summary>
    ///     Interface for passive village effects that operate continuously
    ///     within village boundaries. Implementations are registered via
    ///     [RegisterPassive] and discovered by AttributeScanner.
    /// </summary>
    public interface IPassiveEffect
    {
        /// <summary>Unique passive effect identifier (e.g. "spawnblock").</summary>
        string Id { get; }

        /// <summary>Display name shown in the UI.</summary>
        string DisplayName { get; }

        /// <summary>Check if this passive effect is active at the given world position.</summary>
        bool IsActive(Vector3 position);
    }
}