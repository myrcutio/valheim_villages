namespace HnaPartitionTests;

/// <summary>
/// Analyzes the player's walkable path to determine what slope angles
/// occur between adjacent cells. This helps calibrate MaxWalkableSlopeDeg.
/// </summary>
public static class SlopeAnalysis
{
    public static void Run(WalkablePath walkPath, float cellSize, float originX, float originZ)
    {
        Console.WriteLine("\n── Slope Analysis (from player path) ──");

        // Group path positions by cell, recording the Y they walked at
        var cellYs = new Dictionary<string, List<float>>();
        foreach (var pos in walkPath.Positions)
        {
            int ix = (int)MathF.Floor((pos[0] - originX) / cellSize);
            int iz = (int)MathF.Floor((pos[2] - originZ) / cellSize);
            string id = $"{ix}_{iz}";
            if (!cellYs.ContainsKey(id))
                cellYs[id] = new List<float>();
            cellYs[id].Add(pos[1]);
        }

        // For each pair of adjacent cells, compute the slope
        var slopes = new List<(string fromId, string toId, float dy, float dist, float slopeDeg)>();
        foreach (var (id, ys) in cellYs)
        {
            var parts = id.Split('_');
            int ix = int.Parse(parts[0]);
            int iz = int.Parse(parts[1]);
            float avgY = ys.Average();

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    string nId = $"{ix + dx}_{iz + dz}";
                    if (!cellYs.ContainsKey(nId)) continue;
                    float nAvgY = cellYs[nId].Average();
                    float dy = MathF.Abs(nAvgY - avgY);
                    float hDist = cellSize * MathF.Sqrt(dx * dx + dz * dz);
                    float slopeDeg = MathF.Atan2(dy, hDist) * (180f / MathF.PI);
                    slopes.Add((id, nId, dy, hDist, slopeDeg));
                }
            }
        }

        // Sort by slope descending
        slopes.Sort((a, b) => b.slopeDeg.CompareTo(a.slopeDeg));

        Console.WriteLine("  Top 15 steepest transitions the player walked:");
        foreach (var (fromId, toId, dy, dist, slopeDeg) in slopes.Take(15))
        {
            float fromY = cellYs[fromId].Average();
            float toY = cellYs[toId].Average();
            Console.WriteLine($"    {fromId} (Y={fromY:F1}) → {toId} (Y={toY:F1}): dy={dy:F1}m, dist={dist:F1}m, slope={slopeDeg:F1}°");
        }

        // Histogram
        var buckets = new int[10]; // 0-5, 5-10, ..., 45+
        foreach (var s in slopes)
        {
            int bucket = Math.Min(9, (int)(s.slopeDeg / 5));
            buckets[bucket]++;
        }
        Console.WriteLine("\n  Slope distribution:");
        for (int i = 0; i < 10; i++)
        {
            string range = i < 9 ? $"{i * 5:D2}°-{(i + 1) * 5:D2}°" : "45°+  ";
            Console.WriteLine($"    {range}: {buckets[i]} transitions");
        }

        float maxSlope = slopes.Count > 0 ? slopes[0].slopeDeg : 0;
        float p95 = slopes.Count > 0 ? slopes[(int)(slopes.Count * 0.05)].slopeDeg : 0;
        Console.WriteLine($"\n  Max slope walked: {maxSlope:F1}°");
        Console.WriteLine($"  95th percentile:  {p95:F1}°");
        Console.WriteLine($"  Recommendation:   MaxWalkableSlopeDeg should be ≥ {MathF.Ceiling(maxSlope):F0}°");
    }
}
