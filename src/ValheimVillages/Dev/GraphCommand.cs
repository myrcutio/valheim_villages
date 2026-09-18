using System;
using System.Collections.Generic;
using System.Linq;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Groups the region-graph inspection commands under one verb:
    ///     <c>vv_graph &lt;section&gt;</c>. Replaces the former <c>vv_tri_inspect</c>,
    ///     <c>vv_hna_boundary_dump</c> and <c>vv_bfs_trace</c> — three views of the same
    ///     object (the village's HNA region graph) whose names shared no common prefix.
    ///     <para>
    ///         <c>vv_tri_inspect</c> in particular was misleadingly named after triangles
    ///         when it is the graph-summary command; its <c>near=</c> form is still the only
    ///         place shared-edge connectivity between neighbouring regions is computed.
    ///     </para>
    /// </summary>
    public static class GraphCommand
    {
        private static readonly Dictionary<string, (string Description, Action<Terminal.ConsoleEventArgs> Run)>
            Sections = new(StringComparer.OrdinalIgnoreCase)
            {
                ["regions"] = ("per-graph region/link summary; add near=x,z or near=player for triangle detail",
                    PathDebugRenderer.TriInspect),
                ["boundary"] = ("boundary cells + the derived patrol route, written to vv_dumps JSON",
                    _ => BoundaryDump.Dump()),
                ["bfs"] = ("BFS hop trace from a region (or the player) to the anchor seed: vv_graph bfs [regionId|off]",
                    BfsTraceCommand.Trace)
            };

        private static IEnumerable<string> SectionNames()
        {
            return Sections.Keys;
        }

        [DevCommand("Inspect the village region graph: vv_graph <regions|boundary|bfs> [args]",
            Name = "vv_graph", OptionsProvider = nameof(SectionNames))]
        public static void Graph(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2)
            {
                PrintSections();
                return;
            }

            var name = args[1];
            if (!Sections.TryGetValue(name, out var section))
            {
                Print($"[vv_graph] unknown section '{name}'");
                PrintSections();
                return;
            }

            // The wrapped commands read their own arguments positionally (near=, regionId),
            // so hand them an args view with the section token removed.
            section.Run(Shift(args));
        }

        /// <summary>
        ///     Returns a copy of <paramref name="args" /> with the subcommand token dropped,
        ///     so a wrapped command that reads <c>args[1]</c> sees its own first argument.
        /// </summary>
        private static Terminal.ConsoleEventArgs Shift(Terminal.ConsoleEventArgs args)
        {
            var argv = args?.Args;
            if (argv == null || argv.Length < 2) return args;

            // ConsoleEventArgs derives Args by splitting the line in its constructor, so
            // rebuild the line without the section token rather than assigning Args.
            var rest = string.Join(" ", argv.Skip(2));
            var line = rest.Length > 0 ? $"{argv[0]} {rest}" : argv[0];
            return new Terminal.ConsoleEventArgs(line, args.Context, args.Commmand);
        }

        private static void PrintSections()
        {
            Print($"[vv_graph] {Sections.Count} section(s) — vv_graph <section> [args]");
            var width = Sections.Keys.Max(k => k.Length);
            foreach (var kvp in Sections.OrderBy(s => s.Key))
                Print($"  {kvp.Key.PadRight(width)}  {kvp.Value.Description}");
        }

        private static void Print(string line)
        {
            Console.instance?.Print(line);
        }
    }
}
