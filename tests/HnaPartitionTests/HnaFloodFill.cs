namespace HnaPartitionTests;

/// <summary>A discovered region cell with its world position.</summary>
public record RegionCell(int Ix, int Iz, float Wx, float Wz, float Y);

/// <summary>A blocking barrier in XZ plane (used for both doors and wall pieces).</summary>
public struct Barrier
{
    public float Px, Pz, Py;       // position
    public float Fx, Fz;           // forward direction (blocking normal, normalized in XZ)
    public float HalfWidth2;       // squared half-width perpendicular to forward
    public float YTolerance;       // max Y distance for this barrier to apply (0 = ignore Y)
}

/// <summary>
/// Offline replica of the HNA flood-fill BFS.
/// Uses GridHeightProvider instead of live Physics.Raycast.
/// </summary>
public static class HnaFloodFill
{
    public const float MaxWalkableSlopeDeg = 26f;
    public const float FloodFillRadius = 25f;
    public const float DoorBlockRadius = 4f;
    /// <summary>Wall pieces with this half-width (2m for a wall4x2) block movement.</summary>
    public const float WallHalfWidth = 2.5f;
    /// <summary>Max Y distance from a wall for it to block BFS (covers 1 floor).</summary>
    public const float WallYTolerance = 3f;

    /// <summary>
    /// Build all barriers (doors + wall pieces) from spatial data.
    /// Walls are identified by name containing "wall" and being on layer 10 (piece).
    /// </summary>
    public static List<Barrier> BuildBarriers(DoorData[] doors, PieceData[] pieces)
    {
        var barriers = new List<Barrier>();

        // Doors: block within DoorBlockRadius, ignore Y (doors block all floors)
        foreach (var door in doors)
        {
            if (!door.HasPiece || door.Forward.Length < 3 || door.Pos.Length < 3) continue;
            float fx = door.Forward[0], fz = door.Forward[2];
            float fLen = MathF.Sqrt(fx * fx + fz * fz);
            if (fLen < 0.01f) continue;
            barriers.Add(new Barrier
            {
                Px = door.Pos[0], Pz = door.Pos[2], Py = door.Pos[1],
                Fx = fx / fLen, Fz = fz / fLen,
                HalfWidth2 = DoorBlockRadius * DoorBlockRadius,
                YTolerance = 0 // doors block regardless of Y
            });
        }

        // Wall pieces: block within WallHalfWidth, check Y proximity
        foreach (var piece in pieces)
        {
            if (piece.Pos.Length < 3 || piece.Fwd == null || piece.Fwd.Length < 3) continue;
            // Only wall-type pieces
            string name = piece.Name.ToLowerInvariant();
            if (!name.Contains("wall") && !name.Contains("gate")) continue;
            // Must be on piece layer (10)
            if (piece.Layer is not 10) continue;

            float fx = piece.Fwd[0], fz = piece.Fwd[2];
            float fLen = MathF.Sqrt(fx * fx + fz * fz);
            if (fLen < 0.01f) continue;
            barriers.Add(new Barrier
            {
                Px = piece.Pos[0], Pz = piece.Pos[2], Py = piece.Pos[1],
                Fx = fx / fLen, Fz = fz / fLen,
                HalfWidth2 = WallHalfWidth * WallHalfWidth,
                YTolerance = WallYTolerance
            });
        }

        return barriers;
    }

    /// <summary>
    /// Returns true if the line from (ax,az)→(bx,bz) at height cellY crosses any barrier
    /// within its blocking radius and Y tolerance.
    /// </summary>
    public static bool CrossesBarrier(float ax, float az, float bx, float bz,
        float cellY, List<Barrier> barriers)
    {
        for (int i = 0; i < barriers.Count; i++)
        {
            var b = barriers[i];
            // Y proximity check (0 = ignore Y)
            if (b.YTolerance > 0 && MathF.Abs(cellY - b.Py) > b.YTolerance) continue;

            float dA = (ax - b.Px) * b.Fx + (az - b.Pz) * b.Fz;
            float dB = (bx - b.Px) * b.Fx + (bz - b.Pz) * b.Fz;
            if (dA * dB > 0) continue; // same side
            float denom = dA - dB;
            if (MathF.Abs(denom) < 0.001f) continue;
            float t = dA / denom;
            float cx = ax + t * (bx - ax);
            float cz = az + t * (bz - az);
            float dist2 = (cx - b.Px) * (cx - b.Px) + (cz - b.Pz) * (cz - b.Pz);
            if (dist2 <= b.HalfWidth2) return true;
        }
        return false;
    }

    public static List<RegionCell> Run(SpatialData data, GridHeightProvider heightProvider)
    {
        float cellSize = data.CellSize;
        float originX = data.Origin[0];
        float originZ = data.Origin[1];
        int cellCountX = data.GridSize[0];
        int cellCountZ = data.GridSize[1];

        var beds = data.Beds;
        if (beds.Length == 0) return [];

        // Build barriers from doors + wall pieces
        var barriers = BuildBarriers(data.Doors, data.BuildingPieces);

        var regionIds = new HashSet<string>();
        var cellPositions = new Dictionary<string, RegionCell>();
        var queue = new Queue<string>();
        float r2 = FloodFillRadius * FloodFillRadius;

        // Seed from beds
        foreach (var bed in beds)
        {
            float bx = bed[0], by = bed[1], bz = bed[2];
            int ix = (int)MathF.Floor((bx - originX) / cellSize);
            int iz = (int)MathF.Floor((bz - originZ) / cellSize);
            if (ix < 0 || ix >= cellCountX || iz < 0 || iz >= cellCountZ)
                continue;

            string id = $"{ix}_{iz}";
            float wx = originX + (ix + 0.5f) * cellSize;
            float wz = originZ + (iz + 0.5f) * cellSize;

            if (!heightProvider.TryGetHeight(wx, wz, by, out float hitY))
                hitY = by;

            var cell = new RegionCell(ix, iz, wx, wz, hitY);
            if (regionIds.Add(id))
            {
                cellPositions[id] = cell;
                queue.Enqueue(id);
            }
        }

        // BFS
        int expanded = 0, rejNoHit = 0, rejSlope = 0, rejBarrier = 0;
        while (queue.Count > 0)
        {
            string fromId = queue.Dequeue();
            if (!cellPositions.TryGetValue(fromId, out var posA)) continue;

            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dz == 0) continue;
                    int nx = posA.Ix + dx, nz = posA.Iz + dz;
                    if (nx < 0 || nx >= cellCountX || nz < 0 || nz >= cellCountZ) continue;
                    string toId = $"{nx}_{nz}";
                    if (regionIds.Contains(toId)) continue;

                    float wx = originX + (nx + 0.5f) * cellSize;
                    float wz = originZ + (nz + 0.5f) * cellSize;

                    // Distance limit from any bed
                    bool nearBed = false;
                    foreach (var bed in beds)
                    {
                        float bdx = wx - bed[0], bdz = wz - bed[2];
                        if (bdx * bdx + bdz * bdz <= r2) { nearBed = true; break; }
                    }
                    if (!nearBed) continue;

                    // Height lookup using parent's Y as reference
                    if (!heightProvider.TryGetHeight(wx, wz, posA.Y, out float hitY))
                    {
                        rejNoHit++;
                        continue;
                    }

                    // Slope check
                    float dxzLen = MathF.Sqrt((wx - posA.Wx) * (wx - posA.Wx) + (wz - posA.Wz) * (wz - posA.Wz));
                    if (dxzLen < 0.01f) continue;
                    float dy = MathF.Abs(hitY - posA.Y);
                    float slopeDeg = MathF.Atan2(dy, dxzLen) * (180f / MathF.PI);
                    if (slopeDeg > MaxWalkableSlopeDeg)
                    {
                        rejSlope++;
                        continue;
                    }

                    // Barrier check: block expansion through walls and doors
                    if (CrossesBarrier(posA.Wx, posA.Wz, wx, wz, posA.Y, barriers))
                    {
                        rejBarrier++;
                        continue;
                    }

                    var cell = new RegionCell(nx, nz, wx, wz, hitY);
                    regionIds.Add(toId);
                    cellPositions[toId] = cell;
                    queue.Enqueue(toId);
                    expanded++;
                }
            }
        }

        Console.WriteLine($"  BFS: {beds.Length} beds → {regionIds.Count} regions ({expanded} expanded, " +
            $"rejNoHit={rejNoHit}, rejSlope={rejSlope}, rejBarrier={rejBarrier}, barriers={barriers.Count})");
        return cellPositions.Values.ToList();
    }
}
