using HarmonyLib;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Harmony prefix on MonsterAI.UpdateAI that intercepts the game's AI loop
    /// for registered villagers and delegates to our VillagerAI instead.
    /// Inspired by MobAILib's MonsterAI_patch pattern.
    /// 
    /// This is the key architectural improvement: instead of fighting the game's
    /// RandomMovement system with timer hacks, we completely replace the AI update
    /// for our villagers and handle movement ourselves.
    /// </summary>
    [HarmonyPatch(typeof(MonsterAI), "UpdateAI")]
    public static class VillagerAIPatch
    {
        /// <summary>
        /// Prefix: if this MonsterAI belongs to a registered villager, run our AI instead.
        /// Returns false to skip the original MonsterAI.UpdateAI entirely.
        /// </summary>
        static bool Prefix(MonsterAI __instance, float dt, ZNetView ___m_nview)
        {
            if (___m_nview == null || !___m_nview.IsValid()) return true;

            var zdo = ___m_nview.GetZDO();
            if (zdo == null) return true;

            // Check if this is one of our villagers
            string uniqueId = zdo.GetString("vv_villager_id", "");
            if (string.IsNullOrEmpty(uniqueId)) return true;
            if (!VillagerAIManager.IsRegistered(uniqueId)) return true;

            // Only the owner runs AI
            if (!___m_nview.IsOwner()) return false;

            // Get or create the VillagerAI instance
            var villagerAI = VillagerAIManager.GetOrCreate(uniqueId, __instance);
            if (villagerAI == null) return true;

            // Run our custom AI instead of the game's
            villagerAI.UpdateAI(dt);

            return false; // Skip original MonsterAI.UpdateAI
        }
    }
}
