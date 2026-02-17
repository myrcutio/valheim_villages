namespace HnaPartitionTests;

/// <summary>
/// Replays recorded height data from the spatial dump.
/// Given a world position (wx, wz) and a reference Y (the BFS parent's height),
/// finds the closest matching raycast hit from the pre-recorded grid.
/// </summary>
public class GridHeightProvider
{
    private readonly Dictionary<(int ix, int iz), HeightCell> _cells = new();
    private readonly float _originX;
    private readonly float _originZ;
    private readonly float _cellSize;
    private readonly int _cellCountX;
    private readonly int _cellCountZ;

    public GridHeightProvider(SpatialData data)
    {
        _originX = data.Origin[0];
        _originZ = data.Origin[1];
        _cellSize = data.CellSize;
        _cellCountX = data.GridSize[0];
        _cellCountZ = data.GridSize[1];

        foreach (var cell in data.HeightGrid)
            _cells[(cell.Ix, cell.Iz)] = cell;
    }

    /// <summary>
    /// Simulate a downward raycast from (wx, refY + offset, wz).
    /// Finds the recorded hit closest to refY from the cell's hit list.
    /// </summary>
    public bool TryGetHeight(float wx, float wz, float refY, out float hitY, float maxDown = 8f)
    {
        hitY = 0f;
        int ix = (int)MathF.Floor((wx - _originX) / _cellSize);
        int iz = (int)MathF.Floor((wz - _originZ) / _cellSize);
        if (ix < 0 || ix >= _cellCountX || iz < 0 || iz >= _cellCountZ)
            return false;
        if (!_cells.TryGetValue((ix, iz), out var cell) || cell.Hits.Length == 0)
            return false;

        // Find the hit whose hitY is closest to refY but not too far above
        // (simulates a downward ray from refY + offset)
        float rayTop = refY + 3f; // RaycastHeightOffset
        float rayBottom = rayTop - maxDown;
        RayHit? best = null;
        float bestDist = float.MaxValue;

        foreach (var hit in cell.Hits)
        {
            // Hit must be below the ray origin and above the ray bottom
            if (hit.HitY > rayTop || hit.HitY < rayBottom)
                continue;
            float dist = MathF.Abs(hit.HitY - refY);
            if (dist < bestDist)
            {
                bestDist = dist;
                best = hit;
            }
        }

        if (best == null) return false;
        hitY = best.HitY;
        return true;
    }
}
