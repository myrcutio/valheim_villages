using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Retint a live villager's clothing without a rebuild:
    ///     <c>vv_robe &lt;villager&gt; &lt;r&gt; &lt;g&gt; &lt;b&gt; [gloss] [bump]</c>.
    ///
    ///     <para>Converging on a look costs one round trip per guess, so doing it through
    ///     rebuild-and-hot-reload wastes the iteration. This drives the same
    ///     <see cref="Villager.VillagerAppearance.TintRobe" /> the spawn path calls, so a value
    ///     that looks right here is a value that can be written straight into the definition's
    ///     <c>themeColor</c>.</para>
    /// </summary>
    internal static class RobeCommand
    {
        [DevCommand("Retint a live villager's robe: vv_robe <villager> <r> <g> <b> [gloss] [bump]",
            Name = "vv_robe")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            if (args == null || args.Length < 5)
            {
                Print("[vv_robe] usage: vv_robe <villager> <r> <g> <b> [gloss] [bump] " +
                      "(components 0..1)");
                return;
            }

            var wanted = args[1];
            if (!float.TryParse(args[2], out var r) ||
                !float.TryParse(args[3], out var g) ||
                !float.TryParse(args[4], out var b))
            {
                Print($"[vv_robe] not a colour: '{args[2]} {args[3]} {args[4]}'");
                return;
            }

            var gloss = 0f;
            if (args.Length >= 6) float.TryParse(args[5], out gloss);

            var bump = 2.08f;
            if (args.Length >= 7) float.TryParse(args[6], out bump);

            var bridges = Object.FindObjectsByType<UI.Interaction.VillagerBehaviorBridge>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            var hits = 0;
            foreach (var bridge in bridges)
            {
                if (bridge == null) continue;

                var record = Villager.Records.VillagerRecordTable.FindById(bridge.UniqueId);
                var name = record != null ? record.Name : bridge.VillagerType;
                if (!string.IsNullOrEmpty(wanted) &&
                    !string.Equals(name, wanted, System.StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(bridge.VillagerType, wanted,
                        System.StringComparison.OrdinalIgnoreCase))
                    continue;

                var tinted = Villager.VillagerAppearance.TintRobe(
                    bridge.gameObject, name, new Color(r, g, b), gloss, bump);
                Print($"[vv_robe] {name}: {tinted} material(s) -> " +
                      $"rgb({r:F2},{g:F2},{b:F2}) gloss={gloss:F2} bump={bump:F2}");
                hits++;
            }

            if (hits == 0) Print($"[vv_robe] no villager matched '{wanted}'");
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
