using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Interfaces;

namespace ValheimVillages.Abilities.MountainStride
{
    /// <summary>
    ///     Teachable ability: Mountain Stride. When activated, lets the player climb steep
    ///     mountain terrain without sliding and at reduced stamina cost. Timings live on
    ///     <see cref="SE_MountainStride" />; nothing here restates them.
    ///     Taught by Mountaineer villagers.
    /// </summary>
    [RegisterAbility("mountainstride")]
    public class MountainStrideAbility : IPlayerAbility
    {
        public string Id => "mountainstride";
        public string DisplayName => "Mountain Stride";

        // Both figures derive from the constants. The duration used to be written out as
        // "5 minutes" while only the cooldown was computed, so retuning the effect left the
        // description quietly claiming the old number.
        public string Description =>
            $"Climb steep mountain terrain without sliding, at a quarter of the usual stamina, " +
            $"for {Minutes(SE_MountainStride.Duration)}. Cooldown: {Minutes(SE_MountainStride.Cooldown)}.";

        private static string Minutes(float seconds)
        {
            var minutes = seconds / 60f;
            return Mathf.Approximately(minutes, 1f) ? "1 minute" : $"{minutes:F0} minutes";
        }

        public bool HasLearned(Player player)
        {
            return player != null && player.HaveUniqueKey(VillagerAbilityManager.MountainStrideKey);
        }

        public void Learn(Player player)
        {
            VillagerAbilityManager.LearnMountainStride();
        }

        public void Activate(Player player)
        {
            VillagerAbilityManager.ActivateMountainStride();
        }
    }
}