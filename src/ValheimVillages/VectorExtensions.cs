using System;
using UnityEngine;

namespace ValheimVillages
{
    public static class VectorExtensions
    {
        /// <summary>Horizontal (XZ plane) distance between two points.</summary>
        public static float DistXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }
    }
}
