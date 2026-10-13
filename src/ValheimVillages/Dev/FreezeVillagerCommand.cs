using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Hold a villager still so its model can be inspected or its equipment posed:
    ///     <c>vv_freeze &lt;villager|all&gt; [on|off] [seconds]</c>.
    ///
    ///     <para>The normal pause is deliberately short-lived — <see cref="Villager.VillagerPauseRpc.LeaseSeconds" />
    ///     is 6s, so a peer that goes away cannot strand a villager frozen forever. That safety
    ///     valve makes it useless for tuning a held tool, which takes many round trips. This asks
    ///     for the same hold with a long lease, so it still expires on its own rather than
    ///     needing an un-freeze that a crash could skip.</para>
    /// </summary>
    internal static class FreezeVillagerCommand
    {
        private const float DefaultSeconds = 600f;

        [DevCommand("Hold a villager still for posing: vv_freeze <villager|all> [on|off] [seconds]",
            Name = "vv_freeze")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            if (args == null || args.Length < 2)
            {
                Print("[vv_freeze] usage: vv_freeze <villager|all> [on|off] [seconds]");
                return;
            }

            var wanted = args[1];
            var on = args.Length < 3 ||
                     !string.Equals(args[2], "off", System.StringComparison.OrdinalIgnoreCase);

            var seconds = DefaultSeconds;
            if (args.Length >= 4) float.TryParse(args[3], out seconds);

            var all = string.Equals(wanted, "all", System.StringComparison.OrdinalIgnoreCase);

            var bridges = Object.FindObjectsByType<UI.Interaction.VillagerBehaviorBridge>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            var hits = 0;
            foreach (var bridge in bridges)
            {
                if (bridge == null) continue;

                var record = Villager.Records.VillagerRecordTable.FindById(bridge.UniqueId);
                var name = record != null ? record.Name : bridge.VillagerType;
                if (!all &&
                    !string.Equals(name, wanted, System.StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(bridge.VillagerType, wanted,
                        System.StringComparison.OrdinalIgnoreCase))
                    continue;

                hits++;

                // One pause call does NOT hold: the lease is short by design, so this keeps it
                // alive the same way the villager menu does — a host-routed pause, renewed on
                // the shared heartbeat.
                var holder = bridge.GetComponent<Villager.FreezeHolder>();

                if (!on)
                {
                    if (holder != null) holder.Release();
                    else bridge.SetPaused(false);
                    Print($"[vv_freeze] {name}: released");
                    continue;
                }

                holder ??= bridge.gameObject.AddComponent<Villager.FreezeHolder>();
                holder.Hold(bridge, seconds);
                Print($"[vv_freeze] {name}: held for {seconds:F0}s (renewing)");
            }

            if (hits == 0) Print($"[vv_freeze] no villager matched '{wanted}'");
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
