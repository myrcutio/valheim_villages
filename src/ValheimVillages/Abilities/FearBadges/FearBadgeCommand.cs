using System.Text;
using ValheimVillages.Attributes;

namespace ValheimVillages.Abilities.FearBadges
{
    /// <summary>
    ///     Inspect and change this character's fear wards directly, without going through
    ///     trophies or badge items. Lets the fear mechanic be tested before the acquisition
    ///     half exists, and afterwards separates "the badge did not apply" from "the ward is set
    ///     but creatures ignore it" — two failures that look identical in-world.
    /// </summary>
    public static class FearBadgeCommand
    {
        [DevCommand("List this character's fear wards, or change one: " +
                    "vv_fearbadge [add|remove <creaturePrefab>]", Name = "vv_fearbadge")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var player = Player.m_localPlayer;
            if (player == null)
            {
                Print("[vv_fearbadge] no local player — wards live on the player's ZDO, " +
                      "so run this on a client.");
                return;
            }

            var mode = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            var creature = args != null && args.Length > 2 ? args[2] : null;

            if (mode is "add" or "remove")
            {
                if (string.IsNullOrEmpty(creature))
                {
                    Print($"[vv_fearbadge] {mode} needs a creature prefab: vv_fearbadge {mode} Troll");
                    return;
                }

                // Checked against the prefab table, because a typo would otherwise store a ward
                // that can never match a real creature and read as the mechanic being broken.
                if (ZNetScene.instance != null && ZNetScene.instance.GetPrefab(creature) == null)
                {
                    Print($"[vv_fearbadge] no prefab named '{creature}' — ward not changed");
                    return;
                }

                var changed = mode == "add"
                    ? FearBadgeStore.Add(player, creature)
                    : FearBadgeStore.Remove(player, creature);

                Print(changed
                    ? $"[vv_fearbadge] {creature}: {(mode == "add" ? "warded" : "ward removed")}"
                    : $"[vv_fearbadge] {creature}: no change (already {(mode == "add" ? "warded" : "absent")}, " +
                      "or this peer does not own the player ZDO)");
                return;
            }

            var wards = FearBadgeStore.Wards(player);
            var sb = new StringBuilder();
            sb.AppendLine($"[vv_fearbadge] character '{player.GetPlayerName()}' " +
                          $"range={FearBadgeStore.FearRange:F0}m cap={FearBadgeStore.MaxBadges}");

            if (wards == null || wards.Count == 0)
            {
                sb.AppendLine("  (no wards — warded creatures flee when they target you)");
            }
            else
            {
                foreach (var ward in wards)
                    sb.AppendLine($"  [x] {ward}");
            }

            Print(sb.ToString().TrimEnd());
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
