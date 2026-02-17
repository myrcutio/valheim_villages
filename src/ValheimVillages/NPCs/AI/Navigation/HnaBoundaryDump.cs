using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.Core.Attributes;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Dumps HNA boundary cell data + edge-snapped positions to JSON for offline
    /// pipeline testing. Run via console command: hna_boundary_dump
    /// </summary>
    public static class HnaBoundaryDump
    {
        private const string OutputPath = "/home/benny/Projects/valheim_villages/.cursor/hna_boundary_dump.json";

        [DevCommand("Dump HNA boundary cells + edge-snapped positions to JSON for offline pipeline testing", Name = "hna_boundary_dump")]
        public static void Dump()
        {
            if (!HnaRegionGraph.IsAvailable)
            {
                Console.instance?.Print("HNA graph not available. Spawn a guard or run hna_partition first.");
                return;
            }

            var edgeData = HnaBoundaryMapper.DiagnosticEdgeSnap();
            if (edgeData.Count == 0)
            {
                Console.instance?.Print("No boundary cells found.");
                return;
            }

            // Find the nearest bed as the center reference
            var beds = VillagerAIManager.GetAllBedPositions();
            Vector3 bedPos = beds != null && beds.Count > 0 ? beds[0] : Vector3.zero;

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"cellSize\": {HnaRegionGraph.CellSize:F2},\n");
            sb.Append($"  \"regionCount\": {HnaRegionGraph.RegionCount},\n");
            sb.Append($"  \"bedPosition\": [{bedPos.x:F2}, {bedPos.y:F2}, {bedPos.z:F2}],\n");

            // Pipeline parameters
            sb.Append("  \"parameters\": {\n");
            sb.Append($"    \"rdpEpsilon\": 1.0,\n");
            sb.Append($"    \"sharpAngleThreshold\": 270,\n");
            sb.Append($"    \"xzDedupeRadius\": 3.0,\n");
            sb.Append($"    \"navMeshProbeRadius\": 4.0,\n");
            sb.Append($"    \"maxEdgeXZDrift\": 4.0\n");
            sb.Append("  },\n");

            // Boundary cells with edge-snap results
            sb.Append($"  \"boundaryCells\": [\n");
            for (int i = 0; i < edgeData.Count; i++)
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
            int snapped = edgeData.FindAll(e => e.edgeSnapped.HasValue).Count;
            int elevCount = edgeData.FindAll(e => e.elevated).Count;
            Console.instance?.Print(
                $"Boundary dump: {edgeData.Count} cells ({snapped} snapped, {elevCount} elevated) -> {OutputPath}");
            Plugin.Log?.LogInfo(
                $"[HNA] Boundary dump: {edgeData.Count} cells -> {OutputPath}");
        }
    }
}
