using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Work;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Diagnostic: every harvestable near a villager's anchor, and each gate the harvest
    ///     search applies to it — what it yields, whether it is ripe/stocked, distance, region,
    ///     and whether a walkable approach resolves.
    ///
    ///     <para>
    ///         Exists because the scan can only report one flat reason ("No station 'Beehive' in
    ///         village" / "No ripe X in village") for several different causes: out of range or
    ///         outside the footprint, wrong yield (<c>piece_birdnest</c> shares the
    ///         <see cref="Beehive" /> component), empty or unripe, or no reachable standing spot.
    ///         Guessing between those cost a round-trip each time.
    ///     </para>
    ///     <para>
    ///         Merged from the former <c>vv_beeprobe</c> and <c>vv_forageprobe</c>, which shared
    ///         their entire harness (villager iteration, anchor/radius resolution, region lookup,
    ///         approach gate, printing) and differed only in the ~6/~12 lines of subject-specific
    ///         eligibility. The two subjects keep their distinct columns: bees report honey level
    ///         and flag a yield mismatch; forage reports the village footprint it scoped to.
    ///     </para>
    /// </summary>
    public static class HarvestProbeCommand
    {
        private static readonly string[] Subjects = { "all", "bee", "forage" };

        private static IEnumerable<string> SubjectOptions()
        {
            return Subjects;
        }

        [DevCommand("Dump harvestables near each villager + why they are/aren't harvestable: " +
                    "vv_harvestprobe [bee|forage|all] [item]",
            Name = "vv_harvestprobe", OptionsProvider = nameof(SubjectOptions))]
        public static void Probe(Terminal.ConsoleEventArgs args)
        {
            // First arg is the subject when it names one, otherwise it is the item filter —
            // so `vv_harvestprobe Honey` still reads naturally.
            var subject = "all";
            string expected = null;
            if (args.Length >= 2)
            {
                if (Array.Exists(Subjects, s =>
                        string.Equals(s, args[1], StringComparison.OrdinalIgnoreCase)))
                {
                    subject = args[1].ToLowerInvariant();
                    if (args.Length >= 3) expected = args[2];
                }
                else
                {
                    expected = args[1];
                }
            }

            var wantBee = subject is "all" or "bee";
            var wantForage = subject is "all" or "forage";

            var sb = new StringBuilder();
            var radius = WorkSettings.ChestScanRadius;

            foreach (var ai in VillagerAIManager.ActiveVillagers.Values)
            {
                if (ai == null) continue;
                var anchor = ai.HomeAnchor;
                var graph = VillageRegistry.GraphAt(anchor);

                sb.AppendLine(
                    $"- {ai.NpcName} anchor=({anchor.x:F1},{anchor.z:F1}) radius={radius:F0}m" +
                    (expected != null ? $" expecting '{expected}'" : ""));

                if (wantBee) AppendBees(sb, anchor, graph, radius, expected);
                if (wantForage) AppendForage(sb, anchor, graph, radius, expected);
            }

            if (sb.Length == 0) sb.AppendLine("no villagers loaded on this peer");
            Print("[vv_harvestprobe]\n" + sb);
        }

        private static void AppendBees(StringBuilder sb, Vector3 anchor, RegionGraph graph,
            float radius, string expected)
        {
            sb.AppendLine("  bees:");

            // One resolve per distinct yield, not per candidate: TryFindHarvestable rescans
            // the whole village, so calling it inside the loop was quadratic.
            var winners = new Dictionary<string, (object Picked, Vector3 Approach)>();
            var found = 0;

            foreach (var h in PhysicsHelper.GetAllInRadius<Beehive>(anchor, radius))
            {
                if (h == null) continue;
                found++;
                var pos = h.transform.position;
                var yield = BeehiveHelper.YieldOf(h) ?? "(none)";
                var level = BeehiveHelper.GetHoneyLevel(h);
                var dist = Vector3.Distance(pos, anchor);
                var region = graph?.PointToRegionId(pos) ?? "UNRESOLVED";

                if (!winners.TryGetValue(yield, out var win))
                {
                    win = BeehiveHelper.TryFindHarvestable(
                        anchor, radius, yield, out var picked, out var approach)
                        ? ((object)picked, approach)
                        : (null, Vector3.zero);
                    winners[yield] = win;
                }

                var selected = ReferenceEquals(win.Picked, h);

                sb.AppendLine(
                    $"    {h.gameObject.name} @({pos.x:F1},{pos.y:F1},{pos.z:F1}) d={dist:F1}m " +
                    $"yield={yield} level={level} region={region}");
                sb.AppendLine(
                    $"      {DescribeSelection(selected, win.Picked)}" +
                    (selected ? $" approach=({win.Approach.x:F1},{win.Approach.y:F1},{win.Approach.z:F1})" : "") +
                    (expected != null && yield != expected ? "  [wrong yield for this order]" : "") +
                    (level <= 0 ? "  [empty]" : ""));
            }

            if (found == 0) sb.AppendLine("    (no beehive-family pieces in range)");
        }

        private static void AppendForage(StringBuilder sb, Vector3 anchor, RegionGraph graph,
            float radius, string expected)
        {
            var village = VillageRegistry.GetVillageAt(anchor)
                          ?? VillageRegistry.FindNearAnchor(anchor);
            var scope = village != null && village.TryGetFootprint(
                out var minX, out var minZ, out var maxX, out var maxZ)
                ? $"footprint x[{minX:F0}..{maxX:F0}] z[{minZ:F0}..{maxZ:F0}]"
                : $"NO FOOTPRINT — falling back to {radius:F0}m radius";
            sb.AppendLine($"  forage ({scope}):");

            // Every RIPE plant first (exactly what the order scan sees), then the
            // reachability gate per plant, which is the half that fails silently.
            var ripe = ForageHelper.FindRipeInVillage(anchor, radius, expected);
            var winners = new Dictionary<string, (object Picked, Vector3 Approach)>();

            foreach (var p in ripe)
            {
                var pos = p.transform.position;
                var yield = ForageHelper.YieldOf(p) ?? "(none)";
                var dist = Vector3.Distance(pos, anchor);
                var region = graph?.PointToRegionId(pos) ?? "UNRESOLVED";

                if (!winners.TryGetValue(yield, out var win))
                {
                    win = ForageHelper.TryFindHarvestable(
                        anchor, radius, yield, out var picked, out var approach)
                        ? ((object)picked, approach)
                        : (null, Vector3.zero);
                    winners[yield] = win;
                }

                var selected = ReferenceEquals(win.Picked, p);

                sb.AppendLine(
                    $"    {p.gameObject.name} @({pos.x:F1},{pos.y:F1},{pos.z:F1}) d={dist:F1}m " +
                    $"yield={yield} region={region}");
                sb.AppendLine(
                    $"      {DescribeSelection(selected, win.Picked)}" +
                    (selected ? $" approach=({win.Approach.x:F1},{win.Approach.y:F1},{win.Approach.z:F1})" : ""));
            }

            if (ripe.Count == 0)
                sb.AppendLine(expected != null
                    ? $"    (nothing ripe yielding '{expected}' in this village)"
                    : "    (nothing ripe in this village)");
        }

        /// <summary>
        ///     The old <c>selected=false</c> conflated "no reachable candidate at all" with
        ///     "reachable, but another candidate is nearer" — the two cases you actually need
        ///     to tell apart. Name the winner instead.
        /// </summary>
        private static string DescribeSelection(bool selected, object winner)
        {
            if (selected) return "selected=true";
            if (winner == null) return "selected=false [no reachable candidate for this yield]";
            var name = winner is Component c ? c.gameObject.name : winner.ToString();
            return $"selected=false [nearer winner: {name}]";
        }

        private static void Print(string s)
        {
            // Capped + chunked: a single oversized write to a headless server's
            // stdout pipe blocks the main thread. See ConsoleReport.
            ValheimVillages.Dev.ConsoleReport.Emit(s);
        }
    }
}
