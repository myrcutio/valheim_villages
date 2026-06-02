using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Patrol;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Dumps the region-graph perimeter boundary cells AND the patrol route
    ///     <see cref="PatrolRouteBuilder" /> derives from them to JSON, for
    ///     offline verification of the geometric route pipeline (ordering,
    ///     simplification, inset, self-intersection). Run via console command:
    ///     vv_hna_boundary_dump
    /// </summary>
    public static class BoundaryDump
    {
        private static readonly string OutputPath = Path.Combine(
            Paths.ConfigPath, "vv_dumps", "hna_boundary_dump.json");

        [DevCommand("Dump region-graph boundary cells + the derived patrol route to JSON for offline pipeline testing",
            Name = "vv_hna_boundary_dump")]
        public static void Dump()
        {
            if (!RegionGraph.IsAnyAvailable)
            {
                Console.instance?.Print("Region graph not available. Spawn a patroller or run vv_repartition first.");
                return;
            }

            var beds = VillagerAIManager.GetAllBedPositions();
            var bedPos = beds != null && beds.Count > 0 ? beds[0] : Vector3.zero;

            var graph = RegionGraph.GetNearest(bedPos);
            if (graph == null || !graph.IsAvailable)
            {
                Console.instance?.Print("No graph near bed position.");
                return;
            }

            var cells = graph.GetBoundaryCells();
            if (cells.Count == 0)
            {
                Console.instance?.Print("No boundary cells found.");
                return;
            }

            var route = PatrolRouteBuilder.Build(cells);

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"cellSize\": {RegionGraph.CellSize:F2},\n");
            sb.Append($"  \"regionCount\": {graph.RegionCount},\n");
            sb.Append($"  \"bedPosition\": [{bedPos.x:F2}, {bedPos.y:F2}, {bedPos.z:F2}],\n");

            sb.Append("  \"boundaryCells\": [\n");
            for (var i = 0; i < cells.Count; i++)
            {
                var (_, center, outDir) = cells[i];
                sb.Append("    {");
                sb.Append($" \"center\": [{center.x:F2}, {center.y:F2}, {center.z:F2}],");
                sb.Append($" \"outwardDir\": [{outDir.x:F3}, {outDir.y:F3}, {outDir.z:F3}] ");
                sb.Append(i < cells.Count - 1 ? "},\n" : "}\n");
            }

            sb.Append("  ],\n");

            sb.Append("  \"route\": [\n");
            for (var i = 0; i < route.Count; i++)
            {
                var p = route[i];
                sb.Append($"    [{p.x:F2}, {p.y:F2}, {p.z:F2}]");
                sb.Append(i < route.Count - 1 ? ",\n" : "\n");
            }

            sb.Append("  ]\n");
            sb.Append("}\n");

            File.WriteAllText(OutputPath, sb.ToString());
            Console.instance?.Print(
                $"Boundary dump: {cells.Count} cells -> {route.Count} route waypoints -> {OutputPath}");
            Plugin.Log?.LogInfo(
                $"[Region] Boundary dump: {cells.Count} cells -> {route.Count} waypoints -> {OutputPath}");
        }
    }
}
