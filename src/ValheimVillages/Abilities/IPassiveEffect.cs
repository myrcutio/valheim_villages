using UnityEngine;

namespace ValheimVillages.Abilities
{
    /// <summary>
    /// Extension of the Core IPassiveEffect interface adding Vector3 overload.
    /// The Core version uses Vec3; this provides a Unity-compatible overload.
    /// </summary>
    public static class PassiveEffectExtensions
    {
        /// <summary>Check if this passive effect is active at the given Unity Vector3 position.</summary>
        public static bool IsActive(this IPassiveEffect effect, Vector3 position)
        {
            return effect.IsActive(position.ToVec3());
        }
    }
}
