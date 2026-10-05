using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Trace one lookup cell through the partition, reporting each decision as it is made.
    ///
    ///     <para>`vv_probe` can only describe the finished graph plus Pass 1's terrain-level
    ///     view; it cannot show what Pass 2 concluded per surface. This emits <c>[CellTrace]</c>
    ///     lines for what the column contains at index time, every Pass 2 visit, every refused
    ///     step into the cell with its reason, and the cell's final reachability.</para>
    ///
    ///     <para>Off unless set, so it costs nothing when unused. The watch is a static and is
    ///     cleared by a hot reload — re-run it after F6.</para>
    /// </summary>
    public static class WatchCellCommand
    {
        [DevCommand("Trace one cell through the partition: vv_watchcell <x> <z> | off",
            Name = "vv_watchcell")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            if (args.Length >= 2 && args[1].ToLowerInvariant() == "off")
            {
                RubberBandPrune.WatchXz = null;
                Print("[vv_watchcell] tracing off");
                return;
            }

            if (args.Length < 3
                || !float.TryParse(args[1], out var x)
                || !float.TryParse(args[2], out var z))
            {
                Print("[vv_watchcell] usage: vv_watchcell <x> <z> | off   (world X and Z)");
                return;
            }

            var gx = Mathf.FloorToInt(x / RegionGraph.LookupCellSize);
            var gz = Mathf.FloorToInt(z / RegionGraph.LookupCellSize);
            RubberBandPrune.WatchXz = RubberBandPrune.PackXzKey(gx, gz);
            Print($"[vv_watchcell] tracing cell ({gx},{gz}) — run vv_repartition, then read the log");
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
