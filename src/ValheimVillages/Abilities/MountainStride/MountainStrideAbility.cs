using ValheimVillages.Attributes;
using ValheimVillages.Interfaces;

namespace ValheimVillages.Abilities.MountainStride
{
    /// <summary>
    ///     Teachable ability: Mountain Stride. When activated, grants the player
    ///     immunity to sliding on steep mountain terrain for 5 minutes.
    ///     Taught by Mountaineer villagers.
    /// </summary>
    [RegisterAbility("mountainstride")]
    public class MountainStrideAbility : IAbility
    {
        public string Id => "mountainstride";
        public string DisplayName => "Mountain Stride";

        public string Description =>
            "Grants immunity to sliding on steep mountain terrain for 5 minutes. " +
            $"Cooldown: {SE_MountainStride.Cooldown / 60f:F0} minutes.";

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