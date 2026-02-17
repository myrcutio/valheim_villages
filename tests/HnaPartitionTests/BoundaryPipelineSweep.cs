using static HnaPartitionTests.BoundaryPipeline;
using static HnaPartitionTests.PathScoring;

namespace HnaPartitionTests;

/// <summary>
/// Runs the boundary pipeline with many parameter combinations and ranks
/// the results against a player-walked reference perimeter path.
/// </summary>
public static class BoundaryPipelineSweep
{
    private static readonly float[] RdpEpsilons = [0.3f, 0.5f, 0.75f, 1.0f];
    private static readonly float[] AngleThresholds = [200f, 210f, 230f, 250f, 270f];
    private static readonly bool[] ChaikinOptions = [true, false];
    private static readonly float[] DedupeRadii = [1.5f, 2.0f, 2.5f, 3.0f];

    public record SweepResult(
        PipelineParams Params,
        ScoreResult PointScore,
        ScoreResult SegmentScore,
        int InputCount,
        int OutputCount);

    /// <summary>
    /// Run all parameter combinations and return results sorted by segment mean distance.
    /// </summary>
    public static List<SweepResult> Run(
        List<Vec3> edgeSnapped, Vec3 bedCenter, List<Vec3> reference)
    {
        var results = new List<SweepResult>();

        foreach (float rdp in RdpEpsilons)
        foreach (float angle in AngleThresholds)
        foreach (bool chaikin in ChaikinOptions)
        foreach (float dedup in DedupeRadii)
        {
            var p = new PipelineParams(rdp, angle, chaikin, dedup);
            var input = new List<Vec3>(edgeSnapped);
            var output = BoundaryPipeline.Run(input, bedCenter, p);

            var pointScore = PathScoring.Score(output, reference);
            var segmentScore = PathScoring.ScoreSegments(output, reference);

            results.Add(new SweepResult(p, pointScore, segmentScore,
                edgeSnapped.Count, output.Count));
        }

        results.Sort((a, b) => a.SegmentScore.Combined.CompareTo(b.SegmentScore.Combined));
        return results;
    }

    /// <summary>Print the top N results as a formatted table.</summary>
    public static void PrintResults(List<SweepResult> results, int topN = 20)
    {
        Console.WriteLine($"\n{"Rank",-5} {"RDP",-6} {"Angle",-7} {"Chaikn",-7} {"Dedup",-7} " +
                          $"{"WP",-5} {"SegMean",-8} {"SegHaus",-8} {"Cover",-7} {"PtMean",-8} {"Score",-8}");
        Console.WriteLine(new string('─', 82));

        int limit = Math.Min(topN, results.Count);
        for (int i = 0; i < limit; i++)
        {
            var r = results[i];
            var p = r.Params;
            var s = r.SegmentScore;
            Console.WriteLine(
                $"{i + 1,-5} {p.RdpEpsilon,-6:F2} {p.SharpAngleThreshold,-7:F0} " +
                $"{(p.ChaikinEnabled ? "on" : "off"),-7} {p.XzDedupeRadius,-7:F1} " +
                $"{r.OutputCount,-5} {s.MeanDistance,-8:F2} {s.Hausdorff,-8:F2} " +
                $"{s.Coverage * 100,-7:F0}% {r.PointScore.MeanDistance,-8:F2} " +
                $"{s.Combined,-8:F2}");
        }

        if (results.Count > 0)
        {
            var best = results[0];
            Console.WriteLine($"\n  Best: RDP={best.Params.RdpEpsilon}, " +
                              $"Angle={best.Params.SharpAngleThreshold}, " +
                              $"Chaikin={(best.Params.ChaikinEnabled ? "on" : "off")}, " +
                              $"Dedup={best.Params.XzDedupeRadius}");
            Console.WriteLine($"  {best.InputCount} input -> {best.OutputCount} waypoints, " +
                              $"segment mean={best.SegmentScore.MeanDistance:F2}m, " +
                              $"coverage={best.SegmentScore.Coverage * 100:F0}%");
        }
    }
}
