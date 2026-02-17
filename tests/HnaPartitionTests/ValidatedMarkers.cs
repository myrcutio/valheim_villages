using System.Text.Json.Serialization;

namespace HnaPartitionTests;

public class ValidatedMarkersData
{
    [JsonPropertyName("regionCount")] public int RegionCount { get; set; }
    [JsonPropertyName("linkCount")] public int LinkCount { get; set; }
    [JsonPropertyName("regions")] public float[][] Regions { get; set; } = [];
    [JsonPropertyName("links")] public float[][] Links { get; set; } = [];
}

/// <summary>
/// Compares BFS regions against user-validated markers.
/// Markers the user kept = valid cells. Markers the user deleted = invalid cells.
/// </summary>
public static class ValidatedMarkerAnalysis
{
    /// <summary>Height offset used when spawning markers (must match MarkerHeightOffset in game code).</summary>
    private const float MarkerHeightOffset = 0.25f;

    public static void Run(
        List<RegionCell> bfsRegions,
        ValidatedMarkersData validated,
        float cellSize, float originX, float originZ,
        int cellCountX, int cellCountZ)
    {
        Console.WriteLine("\n══ VALIDATED MARKER ANALYSIS ══════════════════════");

        // Convert validated marker positions to cell IDs
        // Subtract the marker height offset to get the actual surface Y
        var validatedCells = new Dictionary<string, float>(); // cellId → Y
        foreach (var pos in validated.Regions)
        {
            int ix = (int)MathF.Floor((pos[0] - originX) / cellSize);
            int iz = (int)MathF.Floor((pos[2] - originZ) / cellSize);
            if (ix < 0 || ix >= cellCountX || iz < 0 || iz >= cellCountZ) continue;
            string id = $"{ix}_{iz}";
            validatedCells.TryAdd(id, pos[1] - MarkerHeightOffset);
        }
        Console.WriteLine($"  Validated region cells: {validatedCells.Count} (from {validated.RegionCount} markers)");

        // Also collect validated link cell positions
        var validatedLinkCells = new HashSet<string>();
        foreach (var pos in validated.Links)
        {
            int ix = (int)MathF.Floor((pos[0] - originX) / cellSize);
            int iz = (int)MathF.Floor((pos[2] - originZ) / cellSize);
            if (ix < 0 || ix >= cellCountX || iz < 0 || iz >= cellCountZ) continue;
            validatedLinkCells.Add($"{ix}_{iz}");
        }
        Console.WriteLine($"  Validated link cells:   {validatedLinkCells.Count} unique");

        // All validated cells (regions + links)
        var allValidatedCells = new HashSet<string>(validatedCells.Keys);
        foreach (var c in validatedLinkCells) allValidatedCells.Add(c);
        Console.WriteLine($"  Total validated cells:  {allValidatedCells.Count}");

        // BFS cell set
        var bfsCells = new HashSet<string>(bfsRegions.Select(r => $"{r.Ix}_{r.Iz}"));
        Console.WriteLine($"  BFS cells:              {bfsCells.Count}");

        // Compare
        var correctlyFound = bfsCells.Intersect(allValidatedCells).ToHashSet();
        var falsePositives = bfsCells.Except(allValidatedCells).ToHashSet();
        var missedValid = allValidatedCells.Except(bfsCells).ToHashSet();

        Console.WriteLine($"\n  Correctly found:     {correctlyFound.Count}");
        Console.WriteLine($"  FALSE POSITIVES:     {falsePositives.Count} (BFS found, user deleted marker)");
        Console.WriteLine($"  MISSED VALID:        {missedValid.Count} (user kept marker, BFS didn't find)");

        // Details on false positives
        if (falsePositives.Count > 0)
        {
            Console.WriteLine($"\n── False Positives (BFS found but user deleted) ──");
            foreach (var id in falsePositives.OrderBy(s => s))
            {
                var region = bfsRegions.First(r => $"{r.Ix}_{r.Iz}" == id);
                Console.WriteLine($"  {id}  pos=({region.Wx:F1}, {region.Y:F1}, {region.Wz:F1})");
            }
        }

        // Details on missed valid cells
        if (missedValid.Count > 0)
        {
            Console.WriteLine($"\n── Missed Valid (user kept marker but BFS missed) ──");
            foreach (var id in missedValid.OrderBy(s => s))
            {
                float y = validatedCells.ContainsKey(id) ? validatedCells[id] : -1;
                var parts = id.Split('_');
                int ix = int.Parse(parts[0]), iz = int.Parse(parts[1]);
                float wx = originX + (ix + 0.5f) * cellSize;
                float wz = originZ + (iz + 0.5f) * cellSize;
                Console.WriteLine($"  {id}  pos=({wx:F1}, {y:F1}, {wz:F1})");
            }
        }

        // Metrics
        float precision = bfsCells.Count > 0 ? 100f * correctlyFound.Count / bfsCells.Count : 0;
        float recall = allValidatedCells.Count > 0 ? 100f * correctlyFound.Count / allValidatedCells.Count : 0;
        Console.WriteLine($"\n  Precision:  {precision:F0}% (want ≥85%)");
        Console.WriteLine($"  Recall:     {recall:F0}% (want ≥90%)");
        bool pass = precision >= 85 && recall >= 90;
        Console.WriteLine($"  Result:     {(pass ? "PASS" : "FAIL")}");
    }
}
