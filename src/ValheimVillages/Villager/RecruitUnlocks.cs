namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Per-player gate on which villager types a player has learned to recruit. Completing
    ///     a biome "map" (combining 3 ransom fragments in <c>FragmentCombiner</c>) teaches that
    ///     biome's villager type. The unlock is stored as a Valheim per-player unique key
    ///     (Player.m_uniques via AddUniqueKey/HaveUniqueKey), so it persists in the character
    ///     profile and travels with the player across worlds — the agreed per-player scope.
    ///     Fresh recruits require the type to be unlocked; reviving an existing record does not.
    /// </summary>
    public static class RecruitUnlocks
    {
        private static string Key(string villagerType)
        {
            return "vv_recruit_" + villagerType?.ToLower();
        }

        /// <summary>Has <paramref name="player" /> learned to recruit this villager type?</summary>
        public static bool IsUnlocked(Player player, string villagerType)
        {
            return player != null && !string.IsNullOrEmpty(villagerType) &&
                   player.HaveUniqueKey(Key(villagerType));
        }

        /// <summary>Check against the local player — for UI / client-side recruit gating.</summary>
        public static bool IsUnlockedLocal(string villagerType)
        {
            return IsUnlocked(Player.m_localPlayer, villagerType);
        }

        /// <summary>
        ///     Teach <paramref name="player" /> to recruit this villager type. Idempotent.
        ///     Returns true only if this was a newly learned recipe.
        /// </summary>
        public static bool Unlock(Player player, string villagerType)
        {
            if (player == null || string.IsNullOrEmpty(villagerType)) return false;
            if (player.HaveUniqueKey(Key(villagerType))) return false;
            player.AddUniqueKey(Key(villagerType));
            return true;
        }
    }
}
