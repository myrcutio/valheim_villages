using System;
using System.Collections.Generic;
using System.Linq;
using ValheimVillages.Attributes;
using ValheimVillages.Scheduling;

namespace ValheimVillages.Settings
{
    /// <summary>
    ///     Dev console command for toggling runtime logging verbosity.
    ///     Companion to <see cref="LogSettings" />.
    ///     <para>
    ///         Replaces the former one-command-per-flag set (<c>vv_log_navmesh</c>,
    ///         <c>vv_log_ingredients</c>, <c>vv_log_itemspawns</c>), which were pure
    ///         toggles: there was no way to set a known state or read the current one.
    ///     </para>
    /// </summary>
    internal static class LogCommands
    {
        /// <summary>
        ///     The toggleable verbosity channels, by console name. Add an entry here when
        ///     a new high-volume channel is introduced — the command, its tab completion
        ///     and its state readout all derive from this table.
        /// </summary>
        private static readonly Dictionary<string, LogChannel> Channels =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["ingredients"] = new(
                    "per-container ingredient-scan probes",
                    () => LogSettings.VerboseIngredientScan,
                    v => LogSettings.VerboseIngredientScan = v),
                ["itemspawns"] = new(
                    "every ItemDrop.Awake spawn",
                    () => LogSettings.VerboseItemSpawns,
                    v => LogSettings.VerboseItemSpawns = v),
                ["talk"] = new(
                    "every line a villager speaks",
                    () => LogSettings.VerboseTalk,
                    v => LogSettings.VerboseTalk = v),
                ["training"] = new(
                    "every scheduler reranker training step",
                    () => SchedulerSettings.LogTraining,
                    v => SchedulerSettings.LogTraining = v)
            };

        private static IEnumerable<string> ChannelNames()
        {
            return Channels.Keys;
        }

        [DevCommand(
            "Show or toggle verbose log channels: vv_log [channel] [on|off]",
            Name = "vv_log", OptionsProvider = nameof(ChannelNames))]
        public static void Log(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2)
            {
                PrintAll();
                return;
            }

            var name = args[1];
            if (!Channels.TryGetValue(name, out var channel))
            {
                Print($"[vv_log] unknown channel '{name}'. Known: {string.Join(", ", Channels.Keys)}");
                return;
            }

            bool target;
            if (args.Length < 3)
            {
                target = !channel.Get();
            }
            else if (!TryParseState(args[2], out target))
            {
                Print($"[vv_log] expected on|off, got '{args[2]}'");
                return;
            }

            channel.Set(target);
            var msg = $"[vv_log] {name} = {(target ? "ON" : "OFF")}  ({channel.Description})";
            Print(msg);
            Plugin.Log?.LogInfo(msg);
        }

        private static bool TryParseState(string raw, out bool state)
        {
            switch (raw.ToLowerInvariant())
            {
                case "on":
                case "true":
                case "1":
                    state = true;
                    return true;
                case "off":
                case "false":
                case "0":
                    state = false;
                    return true;
                default:
                    state = false;
                    return false;
            }
        }

        private static void PrintAll()
        {
            Print($"[vv_log] {Channels.Count} channel(s) — vv_log <channel> [on|off]");
            var width = Channels.Keys.Max(k => k.Length);
            foreach (var kvp in Channels.OrderBy(c => c.Key))
                Print($"  {kvp.Key.PadRight(width)}  {(kvp.Value.Get() ? "ON " : "OFF")}  {kvp.Value.Description}");
        }

        private static void Print(string line)
        {
            Console.instance?.Print(line);
        }

        private sealed class LogChannel
        {
            public LogChannel(string description, Func<bool> get, Action<bool> set)
            {
                Description = description;
                Get = get;
                Set = set;
            }

            public string Description { get; }
            public Func<bool> Get { get; }
            public Action<bool> Set { get; }
        }
    }
}
