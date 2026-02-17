namespace HnaPartitionTests;

/// <summary>
/// Identifies where the edge-snap step fails to capture the reference perimeter,
/// producing the worst-case Hausdorff distance.
/// </summary>
public static class GapAnalysis
{
    public record GapPoint(Vec3 ReferencePos, float NearestEdgeDist, Vec3 NearestEdge, int RefIndex);

    /// <summary>
    /// Find the reference perimeter points that are furthest from any edge-snapped position.
    /// Returns the top N worst gaps sorted by distance descending.
    /// </summary>
    public static List<GapPoint> FindWorstGaps(
        List<Vec3> edgeSnapped, List<Vec3> reference, int topN = 10)
    {
        var gaps = new List<GapPoint>();

        for (int i = 0; i < reference.Count; i++)
        {
            float bestDist = float.MaxValue;
            Vec3 bestEdge = default;
            foreach (var e in edgeSnapped)
            {
                float d = Vec3.DistXZ(reference[i], e);
                if (d < bestDist) { bestDist = d; bestEdge = e; }
            }
            gaps.Add(new GapPoint(reference[i], bestDist, bestEdge, i));
        }

        gaps.Sort((a, b) => b.NearestEdgeDist.CompareTo(a.NearestEdgeDist));
        return gaps.GetRange(0, Math.Min(topN, gaps.Count));
    }

    /// <summary>
    /// Also find edge-snapped positions furthest from any reference point
    /// (pipeline waypoints in areas the player didn't walk).
    /// </summary>
    public static List<GapPoint> FindWorstExtras(
        List<Vec3> edgeSnapped, List<Vec3> reference, int topN = 10)
    {
        var extras = new List<GapPoint>();

        for (int i = 0; i < edgeSnapped.Count; i++)
        {
            float bestDist = float.MaxValue;
            Vec3 bestRef = default;
            foreach (var r in reference)
            {
                float d = Vec3.DistXZ(edgeSnapped[i], r);
                if (d < bestDist) { bestDist = d; bestRef = r; }
            }
            extras.Add(new GapPoint(edgeSnapped[i], bestDist, bestRef, i));
        }

        extras.Sort((a, b) => b.NearestEdgeDist.CompareTo(a.NearestEdgeDist));
        return extras.GetRange(0, Math.Min(topN, extras.Count));
    }

    public static void Print(List<Vec3> edgeSnapped, List<Vec3> reference)
    {
        Console.WriteLine("\n── Worst gaps (reference points far from any edge-snap) ──");
        var gaps = FindWorstGaps(edgeSnapped, reference);
        Console.WriteLine($"  {"Idx",-5} {"Dist",-7} {"RefPos",-30} {"NearestEdge",-30}");
        foreach (var g in gaps)
            Console.WriteLine($"  {g.RefIndex,-5} {g.NearestEdgeDist,-7:F1} " +
                              $"{g.ReferencePos,-30} {g.NearestEdge,-30}");

        Console.WriteLine("\n── Worst extras (edge-snaps far from any reference point) ──");
        var extras = FindWorstExtras(edgeSnapped, reference);
        Console.WriteLine($"  {"Idx",-5} {"Dist",-7} {"EdgePos",-30} {"NearestRef",-30}");
        foreach (var e in extras)
            Console.WriteLine($"  {e.RefIndex,-5} {e.NearestEdgeDist,-7:F1} " +
                              $"{e.ReferencePos,-30} {e.NearestEdge,-30}");
    }
}
