using System.Text.Json.Serialization;

namespace HnaPartitionTests;

/// <summary>Deserialized spatial dump from hna_dump command.</summary>
public class SpatialData
{
    [JsonPropertyName("playerPosition")] public float[] PlayerPosition { get; set; } = [];
    [JsonPropertyName("beds")] public float[][] Beds { get; set; } = [];
    [JsonPropertyName("guardBounds")] public float[]? GuardBounds { get; set; }
    [JsonPropertyName("cellSize")] public float CellSize { get; set; }
    [JsonPropertyName("origin")] public float[] Origin { get; set; } = [];
    [JsonPropertyName("gridSize")] public int[] GridSize { get; set; } = [];
    [JsonPropertyName("heightGrid")] public HeightCell[] HeightGrid { get; set; } = [];
    [JsonPropertyName("doors")] public DoorData[] Doors { get; set; } = [];
    [JsonPropertyName("buildingPieces")] public PieceData[] BuildingPieces { get; set; } = [];
}

public class HeightCell
{
    [JsonPropertyName("ix")] public int Ix { get; set; }
    [JsonPropertyName("iz")] public int Iz { get; set; }
    [JsonPropertyName("wx")] public float Wx { get; set; }
    [JsonPropertyName("wz")] public float Wz { get; set; }
    [JsonPropertyName("hits")] public RayHit[] Hits { get; set; } = [];
}

public class RayHit
{
    [JsonPropertyName("refY")] public float RefY { get; set; }
    [JsonPropertyName("hitY")] public float HitY { get; set; }
    [JsonPropertyName("layer")] public string Layer { get; set; } = "";
    [JsonPropertyName("layerIdx")] public int LayerIdx { get; set; }
}

public class DoorData
{
    [JsonPropertyName("pos")] public float[] Pos { get; set; } = [];
    [JsonPropertyName("forward")] public float[] Forward { get; set; } = [];
    [JsonPropertyName("hasPiece")] public bool HasPiece { get; set; }
}

public class PieceData
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("pos")] public float[] Pos { get; set; } = [];
    [JsonPropertyName("fwd")] public float[]? Fwd { get; set; }
    [JsonPropertyName("layer")] public int? Layer { get; set; }
    [JsonPropertyName("extents")] public float[] Extents { get; set; } = [];
}

/// <summary>Deserialized walkable path from hna_record.</summary>
public class WalkablePath
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("positions")] public float[][] Positions { get; set; } = [];
}
