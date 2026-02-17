using System.Text.Json.Serialization;

namespace HnaPartitionTests;

/// <summary>Deserialized boundary dump from hna_boundary_dump command.</summary>
public class BoundaryData
{
    [JsonPropertyName("cellSize")] public float CellSize { get; set; }
    [JsonPropertyName("regionCount")] public int RegionCount { get; set; }
    [JsonPropertyName("bedPosition")] public float[] BedPosition { get; set; } = [];
    [JsonPropertyName("parameters")] public BoundaryParameters Parameters { get; set; } = new();
    [JsonPropertyName("boundaryCells")] public BoundaryCellData[] BoundaryCells { get; set; } = [];
}

public class BoundaryParameters
{
    [JsonPropertyName("rdpEpsilon")] public float RdpEpsilon { get; set; }
    [JsonPropertyName("sharpAngleThreshold")] public float SharpAngleThreshold { get; set; }
    [JsonPropertyName("xzDedupeRadius")] public float XzDedupeRadius { get; set; }
    [JsonPropertyName("navMeshProbeRadius")] public float NavMeshProbeRadius { get; set; }
    [JsonPropertyName("maxEdgeXZDrift")] public float MaxEdgeXzDrift { get; set; }
}

public class BoundaryCellData
{
    [JsonPropertyName("center")] public float[] Center { get; set; } = [];
    [JsonPropertyName("outwardDir")] public float[] OutwardDir { get; set; } = [];
    [JsonPropertyName("edgeSnapped")] public float[]? EdgeSnapped { get; set; }
    [JsonPropertyName("elevated")] public bool Elevated { get; set; }
}

/// <summary>Deserialized perimeter path from hna_perimeter_start/stop.</summary>
public class PerimeterPath
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("positions")] public float[][] Positions { get; set; } = [];
}
