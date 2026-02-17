using HarmonyLib;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    /// Patches EnvMan.SetForceEnvironment to detect when the player enters a dungeon.
    /// When a non-empty environment is set (e.g. "Crypt", "Cave"), this triggers
    /// the rescue quest tracker to check if the player is near a pending quest and
    /// spawn the captive pawn inside the dungeon.
    /// </summary>
    [HarmonyPatch(typeof(EnvMan), nameof(EnvMan.SetForceEnvironment))]
    public static class DungeonEntryPatch
    {
        [HarmonyPostfix]
        public static void Postfix(string env)
        {
            // Only trigger when entering a dungeon (non-empty environment name)
            // Empty string means the player left the dungeon
            if (string.IsNullOrEmpty(env))
                return;

            var player = Player.m_localPlayer;
            if (player == null)
                return;

            RescueQuestTracker.OnDungeonEntered(player, env);
        }
    }
}
