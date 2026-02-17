using UnityEngine;

namespace ValheimVillages
{
    /// <summary>
    /// Bridge extension methods between UnityEngine.Vector3 and ValheimVillages.Vec3.
    /// Used at mod assembly boundaries where Vector3 is converted to/from Core types.
    /// </summary>
    public static class Vec3Extensions
    {
        /// <summary>Convert a UnityEngine.Vector3 to a Core Vec3.</summary>
        public static Vec3 ToVec3(this Vector3 v) => new Vec3(v.x, v.y, v.z);

        /// <summary>Convert a Core Vec3 to a UnityEngine.Vector3.</summary>
        public static Vector3 ToVector3(this Vec3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
