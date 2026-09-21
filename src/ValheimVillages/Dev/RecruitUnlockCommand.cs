using System.Text;
using ValheimVillages.Attributes;
using ValheimVillages.Villager;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Which villager types THIS character has learned to recruit.
    ///
    ///     <para>The unlock is a per-player unique key earned by combining a biome's three
    ///     ransom fragments, and it gates more than recruiting: the station's Order button only
    ///     appears when the player has an unlocked type capable of that station. So "the Order
    ///     button is missing" and "that villager type is locked" look identical in-world, with
    ///     nothing on screen to tell them apart — hence this.</para>
    ///
    ///     <para>Client-side by nature: unique keys live on the local character, so this reads
    ///     nothing on a dedicated server.</para>
    /// </summary>
    public static class RecruitUnlockCommand
    {
        [DevCommand("List recruit unlocks for this character, or change one: " +
                    "vv_unlocks [grant|revoke <type>]", Name = "vv_unlocks")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Print("[vv_unlocks] no local player — unique keys are per-character, so run this on a client.");
                return;
            }

            var mode = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            var type = args != null && args.Length > 2 ? args[2] : null;

            if (mode is "grant" or "revoke")
            {
                if (string.IsNullOrEmpty(type))
                {
                    Print($"[vv_unlocks] {mode} needs a villager type: vv_unlocks {mode} Lumberjack");
                    return;
                }

                var def = VillagerRegistry.Get(type);
                if (def == null)
                {
                    Print($"[vv_unlocks] no villager type named '{type}'");
                    return;
                }

                if (mode == "grant")
                {
                    var learned = RecruitUnlocks.Unlock(player, def.type);
                    Print($"[vv_unlocks] {def.type}: {(learned ? "unlocked" : "already unlocked")}");
                }
                else
                {
                    player.RemoveUniqueKey("vv_recruit_" + def.type.ToLower());
                    Print($"[vv_unlocks] {def.type}: revoked");
                }

                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[vv_unlocks] character '{player.GetPlayerName()}'");
            foreach (var def in VillagerRegistry.EnabledDefinitions)
                sb.AppendLine(
                    $"  {(RecruitUnlocks.IsUnlockedLocal(def.type) ? "[x]" : "[ ]")} {def.type,-14} " +
                    $"station='{def.stationName}'");

            Print(sb.ToString());
        }

        private static void Print(string text)
        {
            // Capped + chunked: a single oversized write to a headless server's
            // stdout pipe blocks the main thread. See ConsoleReport.
            ValheimVillages.Dev.ConsoleReport.Emit(text);
        }
    }
}
