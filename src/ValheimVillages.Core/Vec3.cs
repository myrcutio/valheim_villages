using System;

namespace ValheimVillages
{
    /// <summary>
    /// Lightweight readonly 3D vector for Core assembly (no Unity dependency).
    /// Used by data types that need position data without depending on UnityEngine.Vector3.
    /// </summary>
    public readonly struct Vec3 : IEquatable<Vec3>
    {
        public readonly float X;
        public readonly float Y;
        public readonly float Z;

        public Vec3(float x, float y, float z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        /// <summary>3D Euclidean distance between two points.</summary>
        public float DistanceTo(Vec3 other)
        {
            float dx = X - other.X, dy = Y - other.Y, dz = Z - other.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Horizontal (XZ plane) distance between two points.</summary>
        public static float DistXZ(Vec3 a, Vec3 b)
        {
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>3D Euclidean distance between two points (static).</summary>
        public static float Dist3D(Vec3 a, Vec3 b)
        {
            float dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        /// <summary>Linear interpolation between two points.</summary>
        public static Vec3 Lerp(Vec3 a, Vec3 b, float t)
        {
            return new Vec3(
                a.X + (b.X - a.X) * t,
                a.Y + (b.Y - a.Y) * t,
                a.Z + (b.Z - a.Z) * t);
        }

        /// <summary>Create from a float array [x, y, z].</summary>
        public static Vec3 FromArray(float[] a) => new Vec3(a[0], a[1], a[2]);

        public static Vec3 Zero => new Vec3(0, 0, 0);

        public bool Equals(Vec3 other) =>
            X == other.X && Y == other.Y && Z == other.Z;

        public override bool Equals(object obj) =>
            obj is Vec3 other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = 17;
                hash = hash * 31 + X.GetHashCode();
                hash = hash * 31 + Y.GetHashCode();
                hash = hash * 31 + Z.GetHashCode();
                return hash;
            }
        }

        public static bool operator ==(Vec3 left, Vec3 right) => left.Equals(right);
        public static bool operator !=(Vec3 left, Vec3 right) => !left.Equals(right);

        public override string ToString() => $"({X:F2}, {Y:F2}, {Z:F2})";
    }
}
