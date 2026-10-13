using System.Linq;
using UnityEngine;
using System.Text;
using ValheimVillages.Attributes;
using ValheimVillages.UI.Panels;
using ValheimVillages.Villager.AI;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Dump the pins a villager's map would draw: label, where on the texture, whether an
    ///     icon resolved, and what it clicks through to.
    ///
    ///     <para>Exists because the map is CLIENT-side UI and the usual MCP target is the
    ///     headless server, so "is the villager's icon on the map?" could otherwise only be
    ///     answered by looking at a screenshot. This answers it from the same code path the
    ///     panel uses, rather than from a description of it.</para>
    /// </summary>
    internal static class MapPinsCommand
    {
        [DevCommand("Dump the map pins for a villager: vv_mappins [villagerName]",
            Name = "vv_mappins")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var wanted = args != null && args.Length > 1 ? args[1] : null;

            var bridges = UnityEngine.Object
                .FindObjectsByType<UI.Interaction.VillagerBehaviorBridge>(
                    UnityEngine.FindObjectsInactive.Exclude,
                    UnityEngine.FindObjectsSortMode.None);

            if (bridges.Length == 0)
            {
                Print("[vv_mappins] no villagers loaded on this peer");
                return;
            }

            var sb = new StringBuilder();
            foreach (var bridge in bridges)
            {
                var record = Villager.Records.VillagerRecordTable.FindById(bridge.UniqueId);
                var name = record != null ? record.Name : bridge.VillagerType;
                if (!string.IsNullOrEmpty(wanted) &&
                    !string.Equals(name, wanted, System.StringComparison.OrdinalIgnoreCase))
                    continue;

                // Drive the REAL InfoTab rather than mirroring it. The default row is where
                // BuildAllIssuePins runs, and that is private instance state — a hand-rolled
                // pin list could never exercise it, which is exactly how a missing task pin
                // stayed invisible to this probe while being visible in game.
                // vv_mappins <name> row <N> dumps THAT row's map instead of the overview, so
                // the per-row pins can be checked without clicking through the UI.
                var wantRow = 0;
                if (args.Length > 3 &&
                    string.Equals(args[2], "row", System.StringComparison.OrdinalIgnoreCase))
                    int.TryParse(args[3], out wantRow);

                var tab = new UI.Tabs.InfoTab();
                tab.OnSelected(bridge);
                tab.GetListItems(bridge); // rows must be built before a row index means anything
                var detail = tab.GetDetail(wantRow, bridge);
                var render = detail?.MapPins != null
                    ? new MapRender { Texture = detail.MapTexture, Pins = detail.MapPins }
                    : null;

                if (render == null)
                {
                    sb.AppendLine($"  {name}: RenderForTask returned NULL (no graph here?)");
                    continue;
                }

                // Gate diagnostics: the filter keeps only gates near village floor, and when it
                // keeps none it is worth seeing BY HOW MUCH rather than guessing.
                var anchor = bridge.AI != null ? bridge.AI.HomeAnchor : UnityEngine.Vector3.zero;
                var graph = Villages.Entity.VillageRegistry.GraphAt(anchor);
                var gates = graph?.GetGates();
                var cells = graph?.Diagnostics.GetLookupCellCenters();
                sb.AppendLine($"  [gates] graph has {(gates == null ? 0 : gates.Count)}, " +
                              $"{(cells == null ? 0 : cells.Count)} lookup cell(s)");
                if (gates != null && cells != null)
                    foreach (var g in gates)
                    {
                        var bestXz = float.MaxValue;
                        var dyAt = float.MaxValue;
                        foreach (var c in cells)
                        {
                            var dx = c.x - g.x;
                            var dz = c.z - g.z;
                            var xz = Mathf.Sqrt(dx * dx + dz * dz);
                            if (xz >= bestXz) continue;
                            bestXz = xz;
                            dyAt = Mathf.Abs(c.y - g.y);
                        }

                        var kept = bestXz <= 4f && dyAt <= 3f;
                        sb.AppendLine($"    gate ({g.x:F1},{g.y:F1},{g.z:F1}) nearestCell " +
                                      $"dXZ={bestXz:F2}m dY={dyAt:F2}m -> " +
                                      $"{(kept ? "KEEP" : "drop")}");
                    }

                sb.AppendLine($"  {name} ({bridge.VillagerType}) — {render.Pins.Count} pin(s), " +
                              $"texture={(render.Texture != null ? "yes" : "NULL")}");
                // The row's own words, straight off the detail the panel renders. Pins alone
                // can't tell you whether the text a player reads is the right one.
                sb.AppendLine($"  [title] {detail.Title ?? "-"}");
                foreach (var line in (detail.Description ?? "-").Split('\n'))
                    sb.AppendLine($"  [desc ] {line}");
                foreach (var pin in render.Pins)
                    sb.AppendLine(
                        $"    {pin.Label,-34} at ({pin.Normalized.x:F2},{pin.Normalized.y:F2}) " +
                        $"icon={(pin.Icon != null ? pin.Icon.name : "NONE")} " +
                        $"crest={(pin.Backdrop != null ? pin.Backdrop.name : "-")} " +
                        $"chip='{pin.ShortLabel ?? "-"}' " +
                        $"click={(pin.Clickable ? pin.TargetTab + "#" + pin.TargetRow : "-")}" +
                        $"{(pin.OffFrame ? " OFF-FRAME" : "")}");
            }

            Print(sb.Length > 0
                ? "[vv_mappins]\n" + sb.ToString().TrimEnd()
                : $"[vv_mappins] no villager matched '{wanted}'");
        }

        /// <summary>Mirrors InfoTab.PatrolPinColor / TargetPinColor so the probe is honest.</summary>
        private static readonly UnityEngine.Color PatrolPinColor = new(0.70f, 0.16f, 0.13f);

        private static readonly UnityEngine.Color TargetPinColor = new(0.3f, 0.9f, 0.3f);

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
