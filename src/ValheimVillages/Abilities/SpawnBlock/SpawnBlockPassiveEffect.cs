using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Interfaces;
using ValheimVillages.Villages;

namespace ValheimVillages.Abilities.SpawnBlock
{
    /// <summary>
    /// Passive village effect: blocks non-raid enemy spawns inside village areas.
    /// The actual suppression is handled by SpawnProtectionPatch (Harmony patch);
    /// this class exposes the effect state via the IPassiveEffect interface
    /// for UI queries and registry-based lookups.
    /// </summary>
    [RegisterPassive("spawnblock")]
    public class SpawnBlockPassiveEffect : IPassiveEffect
    {
        public string Id => "spawnblock";
        public string DisplayName => "Spawn Protection";

        /// <summary>
        /// Returns true if the given world position is inside a protected village area
        /// where enemy spawns are suppressed.
        /// </summary>
        public bool IsActive(Vector3 position)
        {
            return VillageAreaManager.IsInsideAnyVillage(position);
        }
    }
}
