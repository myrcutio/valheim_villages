using ValheimVillages.Attributes;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Toggle the partition's outside-cell gate, to answer "is the prune what is dropping
    ///     this area?" with a repartition rather than by argument. Diagnostic only.
    /// </summary>
    internal static class OutsideGateCommand
    {
        [DevCommand("Toggle the partition outside gate: vv_outsidegate <on|off>",
            Name = "vv_outsidegate")]
        public static void Toggle(Terminal.ConsoleEventArgs args)
        {
            if (args != null && args.Length > 1)
                RubberBandPrune.OutsideGateEnabled =
                    !string.Equals(args[1], "off", System.StringComparison.OrdinalIgnoreCase);

            var state = RubberBandPrune.OutsideGateEnabled ? "ON (normal)" : "OFF (diagnostic)";
            global::Console.instance?.Print($"[vv_outsidegate] outside gate {state}");
            Plugin.Log?.LogInfo($"[vv_outsidegate] outside gate {state}");
        }
    }
}
