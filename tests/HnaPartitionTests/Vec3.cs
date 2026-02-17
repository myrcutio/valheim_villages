namespace HnaPartitionTests;

/// <summary>
/// Lightweight 3D vector for offline pipeline tests (no Unity dependency).
/// </summary>
public readonly record struct Vec3(float X, float Y, float Z)
{
    public static Vec3 FromArray(float[] a) => new(a[0], a[1], a[2]);

    public static Vec3 Lerp(Vec3 a, Vec3 b, float t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);

    public static float DistXZ(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    public static float Dist3D(Vec3 a, Vec3 b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
}
