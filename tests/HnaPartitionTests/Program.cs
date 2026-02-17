using System.Text.Json;
using HnaPartitionTests;

const string DataDir = "/home/benny/Projects/valheim_villages/.cursor";
const string SpatialFile = "hna_spatial_dump.json";
const string PathFile = "hna_walkable_path.json";

// ── Load data ──────────────────────────────────────────────
Console.WriteLine("Loading spatial data...");
var spatialJson = File.ReadAllText(Path.Combine(DataDir, SpatialFile));
var spatial = JsonSerializer.Deserialize<SpatialData>(spatialJson)!;
Console.WriteLine($"  Beds: {spatial.Beds.Length}");
Console.WriteLine($"  Grid: {spatial.GridSize[0]}x{spatial.GridSize[1]} (cellSize={spatial.CellSize})");
Console.WriteLine($"  Height cells: {spatial.HeightGrid.Length}");
Console.WriteLine($"  Doors: {spatial.Doors.Length}");
Console.WriteLine($"  Pieces: {spatial.BuildingPieces.Length}");

Console.WriteLine("\nLoading walkable path...");
var pathJson = File.ReadAllText(Path.Combine(DataDir, PathFile));
var walkPath = JsonSerializer.Deserialize<WalkablePath>(pathJson)!;
Console.WriteLine($"  Positions: {walkPath.Count}");

// ── Print bed info ─────────────────────────────────────────
Console.WriteLine("\nBed positions:");
foreach (var bed in spatial.Beds)
    Console.WriteLine($"  ({bed[0]:F1}, {bed[1]:F1}, {bed[2]:F1})");

// ── Run BFS ────────────────────────────────────────────────
Console.WriteLine("\nRunning flood-fill BFS...");
var heightProvider = new GridHeightProvider(spatial);
var regions = HnaFloodFill.Run(spatial, heightProvider);

// ── Map player path positions to cells ─────────────────────
Console.WriteLine("\nAnalyzing player path coverage...");
float originX = spatial.Origin[0], originZ = spatial.Origin[1];
float cellSize = spatial.CellSize;

var walkedCells = new HashSet<string>();
var walkedCellPositions = new Dictionary<string, (float x, float y, float z)>();
foreach (var pos in walkPath.Positions)
{
    int ix = (int)MathF.Floor((pos[0] - originX) / cellSize);
    int iz = (int)MathF.Floor((pos[2] - originZ) / cellSize);
    string id = $"{ix}_{iz}";
    if (walkedCells.Add(id))
        walkedCellPositions[id] = (pos[0], pos[1], pos[2]);
}
Console.WriteLine($"  Player walked through {walkedCells.Count} unique cells");

var regionSet = new HashSet<string>(regions.Select(r => $"{r.Ix}_{r.Iz}"));

// ── Find which bed cluster is near the walked path ─────────
// Use the centroid of the walked path to find the nearest bed
float avgWalkX = walkPath.Positions.Average(p => p[0]);
float avgWalkZ = walkPath.Positions.Average(p => p[2]);
float[] nearestBed = spatial.Beds
    .OrderBy(b => (b[0] - avgWalkX) * (b[0] - avgWalkX) + (b[2] - avgWalkZ) * (b[2] - avgWalkZ))
    .First();
float nearBedRadius = HnaFloodFill.FloodFillRadius + 5f; // generous margin
Console.WriteLine($"  Nearest bed to walked area: ({nearestBed[0]:F1}, {nearestBed[1]:F1}, {nearestBed[2]:F1})");

// Filter regions to only those near the relevant bed
var nearRegionSet = new HashSet<string>(regions
    .Where(r => {
        float dx = r.Wx - nearestBed[0], dz = r.Wz - nearestBed[2];
        return dx * dx + dz * dz <= nearBedRadius * nearBedRadius;
    })
    .Select(r => $"{r.Ix}_{r.Iz}"));
Console.WriteLine($"  Regions near walked bed: {nearRegionSet.Count} (of {regionSet.Count} total)");

// ── Compare: which walked cells are in the BFS result? ─────
var covered = walkedCells.Intersect(regionSet).ToHashSet();
var missed = walkedCells.Except(regionSet).ToHashSet();
var extra = nearRegionSet.Except(walkedCells).ToHashSet();

Console.WriteLine($"\n══ RESULTS ══════════════════════════════════════");
Console.WriteLine($"  BFS regions:          {regionSet.Count}");
Console.WriteLine($"  Player walked cells:  {walkedCells.Count}");
Console.WriteLine($"  Correctly covered:    {covered.Count} ({100.0 * covered.Count / walkedCells.Count:F0}%)");
Console.WriteLine($"  MISSED (walkable but not found): {missed.Count}");
Console.WriteLine($"  EXTRA (found but not walked):    {extra.Count}");

if (missed.Count > 0)
{
    Console.WriteLine($"\n── Missed cells (player walked but BFS didn't reach) ──");
    foreach (var id in missed.OrderBy(s => s))
    {
        var (px, py, pz) = walkedCellPositions[id];
        string nearestBedInfo = "";
        float minDist = float.MaxValue;
        foreach (var bed in spatial.Beds)
        {
            float d = MathF.Sqrt((px - bed[0]) * (px - bed[0]) + (pz - bed[2]) * (pz - bed[2]));
            if (d < minDist) { minDist = d; nearestBedInfo = $"dist={d:F1}m from bed ({bed[0]:F0},{bed[2]:F0})"; }
        }
        Console.WriteLine($"  {id}  playerY={py:F1}  {nearestBedInfo}");
    }
}

if (extra.Count > 0)
{
    Console.WriteLine($"\n── Extra cells (BFS found but player didn't walk) ──");
    // Group by approximate Y to see if they're on wrong floors
    var extraByY = new SortedDictionary<int, int>();
    foreach (var id in extra)
    {
        var region = regions.First(r => $"{r.Ix}_{r.Iz}" == id);
        int yBucket = (int)MathF.Round(region.Y);
        extraByY.TryGetValue(yBucket, out int count);
        extraByY[yBucket] = count + 1;
    }
    Console.WriteLine("  Height distribution of extra cells:");
    foreach (var (y, count) in extraByY)
        Console.WriteLine($"    Y≈{y}: {count} cells");
}

// ── Validated markers analysis ──────────────────────────────
const string ValidatedFile = "hna_validated_markers.json";
string validatedPath = Path.Combine(DataDir, ValidatedFile);
if (File.Exists(validatedPath))
{
    Console.WriteLine("\nLoading validated markers...");
    var validJson = File.ReadAllText(validatedPath);
    var validated = JsonSerializer.Deserialize<ValidatedMarkersData>(validJson)!;
    Console.WriteLine($"  Regions: {validated.RegionCount}, Links: {validated.LinkCount}");

    ValidatedMarkerAnalysis.Run(regions, validated, cellSize, originX, originZ,
        spatial.GridSize[0], spatial.GridSize[1]);
}
else
{
    Console.WriteLine($"\nNo validated markers file found ({ValidatedFile}), skipping marker analysis.");
}

Console.WriteLine($"\n══ WALK PATH PASS/FAIL ══════════════════════════");
float coveragePct = 100.0f * covered.Count / walkedCells.Count;
float precisionPct = nearRegionSet.Count > 0 ? 100.0f * covered.Count / nearRegionSet.Count : 0;
Console.WriteLine($"  Coverage (recall):   {coveragePct:F0}% (want ≥90%)");
Console.WriteLine($"  Precision:           {precisionPct:F0}% (want ≥50%)");
bool pass = coveragePct >= 90 && precisionPct >= 50;
Console.WriteLine($"  Overall: {(pass ? "PASS ✓" : "FAIL ✗")}");

// ── Boundary Pipeline Replay ────────────────────────────────
const string BoundaryFile = "hna_boundary_dump.json";
const string PerimeterFile = "hna_perimeter_path.json";

string boundaryPath = Path.Combine(DataDir, BoundaryFile);
string perimeterPath = Path.Combine(DataDir, PerimeterFile);

if (File.Exists(boundaryPath) && File.Exists(perimeterPath))
{
    Console.WriteLine("\n══ BOUNDARY PIPELINE SWEEP ══════════════════════");

    Console.WriteLine("Loading boundary dump...");
    var boundaryJson = File.ReadAllText(boundaryPath);
    var boundary = JsonSerializer.Deserialize<BoundaryData>(boundaryJson)!;
    Console.WriteLine($"  Cells: {boundary.BoundaryCells.Length}, " +
                      $"CellSize: {boundary.CellSize}, Regions: {boundary.RegionCount}");

    Console.WriteLine("Loading perimeter reference path...");
    var perimeterJson = File.ReadAllText(perimeterPath);
    var perimeter = JsonSerializer.Deserialize<PerimeterPath>(perimeterJson)!;
    Console.WriteLine($"  Positions: {perimeter.Count}");

    // Extract edge-snapped positions (skip cells that failed to snap)
    var edgeSnapped = new List<Vec3>();
    int skipCount = 0;
    foreach (var cell in boundary.BoundaryCells)
    {
        if (cell.EdgeSnapped != null)
            edgeSnapped.Add(Vec3.FromArray(cell.EdgeSnapped));
        else
            skipCount++;
    }
    Console.WriteLine($"  Edge-snapped: {edgeSnapped.Count} (skipped {skipCount} unsnapped)");

    var bedCenter = Vec3.FromArray(boundary.BedPosition);
    var reference = perimeter.Positions.Select(Vec3.FromArray).ToList();

    // Gap analysis: where does the edge-snap step fail?
    GapAnalysis.Print(edgeSnapped, reference);

    Console.WriteLine($"\nRunning parameter sweep ({edgeSnapped.Count} inputs, " +
                      $"{reference.Count} reference points)...");
    var sweepResults = BoundaryPipelineSweep.Run(edgeSnapped, bedCenter, reference);
    BoundaryPipelineSweep.PrintResults(sweepResults);

    // Also show the current in-game parameters for comparison
    var currentParams = new BoundaryPipeline.PipelineParams(
        boundary.Parameters.RdpEpsilon,
        boundary.Parameters.SharpAngleThreshold,
        true, // Chaikin enabled in current build
        boundary.Parameters.XzDedupeRadius);
    var currentOutput = BoundaryPipeline.Run(new List<Vec3>(edgeSnapped), bedCenter, currentParams);
    var currentScore = PathScoring.ScoreSegments(currentOutput, reference);
    Console.WriteLine($"\n  Current in-game params: RDP={currentParams.RdpEpsilon}, " +
                      $"Angle={currentParams.SharpAngleThreshold}, Chaikin=on, " +
                      $"Dedup={currentParams.XzDedupeRadius}");
    Console.WriteLine($"  -> {currentOutput.Count} waypoints, " +
                      $"segMean={currentScore.MeanDistance:F2}m, " +
                      $"hausdorff={currentScore.Hausdorff:F2}m, " +
                      $"coverage={currentScore.Coverage * 100:F0}%, " +
                      $"score={currentScore.Combined:F2}");
}
else
{
    if (!File.Exists(boundaryPath))
        Console.WriteLine($"\nNo boundary dump ({BoundaryFile}), skipping pipeline sweep.");
    if (!File.Exists(perimeterPath))
        Console.WriteLine($"\nNo perimeter path ({PerimeterFile}), skipping pipeline sweep.");
}

return pass ? 0 : 1;
