using System;
using System.Collections.Generic;
using System.Linq;
using ValheimVillages.Attributes;
using ValheimVillages.Diagnostics;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Groups the "throw away accumulated state" commands under one verb:
    ///     <c>vv_reset &lt;target&gt;</c>. Replaces the former <c>vv_drop_all_state</c>,
    ///     <c>vv_purge_stale</c>, <c>vv_patrol_reset</c>, <c>vv_forage_forget</c> and
    ///     <c>vv_hna_cleanup</c>, which shared a purpose but nothing else — including any
    ///     naming that hinted they belonged together.
    ///     <para>
    ///         Only <c>all</c> is gated behind a confirmation: the others are cheap,
    ///         self-healing, and gating them would make routine cleanup tedious. Bare
    ///         <c>vv_reset</c> lists the targets rather than doing anything.
    ///     </para>
    /// </summary>
    public static class ResetCommand
    {
        private sealed class ResetTarget
        {
            public ResetTarget(string description, bool destructive, Action<Terminal.ConsoleEventArgs> run)
            {
                Description = description;
                Destructive = destructive;
                Run = run;
            }

            public string Description { get; }
            public bool Destructive { get; }
            public Action<Terminal.ConsoleEventArgs> Run { get; }
        }

        private static readonly Dictionary<string, ResetTarget> Targets =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["all"] = new(
                    "wipe ALL village/villager state to a clean slate ([--dry-run] to preview)",
                    true, DropAllStateCommand.DropAll),
                ["stale"] = new(
                    "purge stale (old-assembly) mod instances left by hot reloads",
                    false, PurgeStaleCommand.Run),
                ["patrols"] = new(
                    "reset patrol discovery, forcing a fresh route rebuild from the region graph",
                    false, _ => VillagerAI.ResetPatrols()),
                ["forage"] = new(
                    "forget forage recipes for plants this character has never picked",
                    false, ForageForgetCommand.Forget),
                ["markers"] = new(
                    "remove ALL persisted torch markers left by the old torch-based viz",
                    false, _ => RegionDebugVisualization.CleanupPersistedMarkers())
            };

        private static IEnumerable<string> TargetNames()
        {
            return Targets.Keys;
        }

        [DevCommand("Reset accumulated state: vv_reset <all|stale|patrols|forage|markers>",
            Name = "vv_reset", OptionsProvider = nameof(TargetNames))]
        public static void Reset(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2)
            {
                PrintTargets();
                return;
            }

            var name = args[1];
            if (!Targets.TryGetValue(name, out var target))
            {
                Print($"[vv_reset] unknown target '{name}'");
                PrintTargets();
                return;
            }

            if (target.Destructive && !DevConfirm.IsConfirmed(args))
            {
                DevConfirm.PrintRefusal($"vv_reset {name.ToLowerInvariant()}");
                return;
            }

            target.Run(args);
        }

        private static void PrintTargets()
        {
            Print($"[vv_reset] {Targets.Count} target(s) — vv_reset <target>");
            var width = Targets.Keys.Max(k => k.Length);
            foreach (var kvp in Targets.OrderBy(t => t.Key))
                Print($"  {kvp.Key.PadRight(width)}  {(kvp.Value.Destructive ? "[needs --yes] " : "")}{kvp.Value.Description}");
        }

        private static void Print(string line)
        {
            Console.instance?.Print(line);
        }
    }
}
