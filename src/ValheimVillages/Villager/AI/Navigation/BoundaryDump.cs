using System.IO;
using System.Text;
using BepInEx;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Patrol;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Dumps HNA boundary cell data + edge-snapped positions to JSON for offline
    ///     pipeline testing. Run via console command: vv_hna_boundary_dump
    /// </summary>
    public static class BoundaryDump
    {
        private static readonly string OutputPath = Path.Combine(
            Paths.ConfigPath, "vv_dumps", "hna_boundary_dump.json");

        [DevCommand("Dump HNA boundary cells + edge-snapped positions to JSON for offline pipeline testing",
            Name = "vv_hna_boundary_dump")]
        public static void Dump()
        {
            if (!RegionGraph.IsAnyAvailable)
            {
                Console.instance?.Print("HNA graph not available. Spawn a patroller or run hna_partition first.");
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

            var edgeData = BoundaryMapper.DiagnosticEdgeSnap(bedPos);
            if (edgeData.Count == 0)
            {
                Console.instance?.Print("No boundary cells found.");
                return;
            }

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"cellSize\": {RegionGraph.CellSize:F2},\n");
            sb.Append($"  \"regionCount\": {graph.RegionCount},\n");
            sb.Append($"  \"bedPosition\": [{bedPos.x:F2}, {bedPos.y:F2}, {bedPos.z:F2}],\n");

            sb.Append("  \"parameters\": {\n");
            sb.Append("    \"rdpEpsilon\": 1.0,\n");
            sb.Append("    \"sharpAngleThreshold\": 270,\n");
            sb.Append("    \"xzDedupeRadius\": 3.0,\n");
            sb.Append("    \"navMeshProbeRadius\": 4.0,\n");
            sb.Append("    \"maxEdgeXZDrift\": 4.0\n");
            sb.Append("  },\n");

            sb.Append("  \"boundaryCells\": [\n");
            for (var i = 0; i < edgeData.Count; i++)
            {
                var (center, outDir, edge, elevated) = edgeData[i];
                sb.Append("    {\n");
                sb.Append($"      \"center\": [{center.x:F2}, {center.y:F2}, {center.z:F2}],\n");
                sb.Append($"      \"outwardDir\": [{outDir.x:F3}, {outDir.y:F3}, {outDir.z:F3}],\n");
                if (edge.HasValue)
                {
                    var e = edge.Value;
                    sb.Append($"      \"edgeSnapped\": [{e.x:F2}, {e.y:F2}, {e.z:F2}],\n");
                    sb.Append($"      \"elevated\": {(elevated ? "true" : "false")}\n");
                }
                else
                {
                    sb.Append("      \"edgeSnapped\": null,\n");
                    sb.Append("      \"elevated\": false\n");
                }

                sb.Append(i < edgeData.Count - 1 ? "    },\n" : "    }\n");
            }

            sb.Append("  ]\n");
            sb.Append("}\n");

            File.WriteAllText(OutputPath, sb.ToString());
            var snapped = edgeData.FindAll(e => e.edgeSnapped.HasValue).Count;
            var elevCount = edgeData.FindAll(e => e.elevated).Count;
            Console.instance?.Print(
                $"Boundary dump: {edgeData.Count} cells ({snapped} snapped, {elevCount} elevated) -> {OutputPath}");
            Plugin.Log?.LogInfo(
                $"[Region] Boundary dump: {edgeData.Count} cells -> {OutputPath}");
        }
    }
}