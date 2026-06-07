using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Dev command to damage nearby structures so the carpenter's repair behavior
    ///     can be exercised by hand (the vanilla <c>dmg</c> command only targets
    ///     characters, not pieces). Mirrors how <c>vv_kill_villager</c> exists to test
    ///     the death flow.
    /// </summary>
    public static class DamageStructuresCommand
    {
        [DevCommand("Damage nearby structures to test repair: vv_damage_structures [radius=15] [amount=40]",
            Name = "vv_damage_structures")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var radius = args.Length > 1 && float.TryParse(args[1], out var r) ? r : 15f;
            var amount = args.Length > 2 && float.TryParse(args[2], out var a) ? a : 40f;

            if (Player.m_localPlayer == null)
            {
                Print("[vv_damage_structures] no local player");
                return;
            }

            var center = Player.m_localPlayer.transform.position;

            var seen = new HashSet<WearNTear>();
            var count = 0;
            foreach (var wnt in PhysicsHelper.GetAllInRadius<WearNTear>(center, radius))
            {
                if (wnt == null || !seen.Add(wnt)) continue;
                var nview = wnt.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                nview.ClaimOwnership();
                wnt.ApplyDamage(amount);
                count++;
            }

            Print($"[vv_damage_structures] damaged {count} structure(s) by {amount} within {radius}m");
        }

        private static void Print(string msg)
        {
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
