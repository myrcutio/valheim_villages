using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages
{
    /// <summary>
    ///     Shared physics queries: OverlapSphere + GetComponentInParent with consistent null handling.
    /// </summary>
    public static class PhysicsHelper
    {
        /// <summary>
        ///     Return the first component of type T found in radius (via OverlapSphere + GetComponentInParent on each collider).
        ///     Returns null if none found.
        /// </summary>
        public static T GetFirstInRadius<T>(Vector3 center, float radius) where T : Component
        {
            var colliders = Physics.OverlapSphere(center, radius);
            foreach (var col in colliders)
            {
                if (col == null || col.gameObject == null) continue;
                var c = col.gameObject.GetComponentInParent<T>();
                if (c != null) return c;
            }

            return null;
        }

        /// <summary>
        ///     Return all components of type T in radius. Same object may appear multiple times if multiple colliders reference
        ///     it.
        ///     Caller should dedupe if needed (e.g. by reference).
        /// </summary>
        public static List<T> GetAllInRadius<T>(Vector3 center, float radius) where T : Component
        {
            var list = new List<T>();
            var colliders = Physics.OverlapSphere(center, radius);
            foreach (var col in colliders)
            {
                if (col == null || col.gameObject == null) continue;
                var c = col.gameObject.GetComponentInParent<T>();
                if (c != null) list.Add(c);
            }

            return list;
        }

        /// <summary>
        ///     True when <see cref="Collider.ClosestPoint" /> is legal on this collider.
        ///     Unity only supports it on Box/Sphere/Capsule and CONVEX MeshCollider; on
        ///     anything else (a non-convex MeshCollider, a TerrainCollider, a WheelCollider)
        ///     it logs
        ///     "Physics.ClosestPoint can only be used with a BoxCollider, SphereCollider,
        ///     CapsuleCollider and a convex MeshCollider" and returns the input point.
        /// </summary>
        public static bool SupportsClosestPoint(Collider col)
        {
            if (col == null) return false;
            if (col is BoxCollider || col is SphereCollider || col is CapsuleCollider) return true;
            return col is MeshCollider mesh && mesh.convex;
        }

        /// <summary>
        ///     <see cref="Collider.ClosestPoint" /> that never logs.
        ///
        ///     <para>Most Valheim geometry — terrain, and the mesh colliders on building
        ///     pieces — is NOT a supported shape, so calling ClosestPoint on the results of
        ///     an OverlapSphere emits one warning per collider. That is not merely noisy:
        ///     on a headless server every warning is a synchronous formatted write to the
        ///     log sink, and a few hundred colliders was enough to hang the dedicated
        ///     server (2026-09-19, vv_zdo_audit / vv_probe). Falling back to the bounding
        ///     box keeps the answer usable and the log quiet.</para>
        ///
        ///     <para>The fallback is the AABB's closest point, which encloses the real
        ///     shape — so the distance it implies is an UNDER-estimate, never an over-
        ///     estimate. Callers using this for a clearance/spacing test therefore stay on
        ///     the conservative side (they reject a candidate the true shape might have
        ///     allowed) rather than accepting one that actually overlaps.</para>
        /// </summary>
        public static Vector3 ClosestPointSafe(Collider col, Vector3 point)
        {
            if (col == null) return point;
            return SupportsClosestPoint(col)
                ? col.ClosestPoint(point)
                : col.bounds.ClosestPoint(point);
        }

        /// <summary>
        ///     Squared distance from <paramref name="point" /> to the collider's surface,
        ///     via <see cref="ClosestPointSafe" />. Prefer this as a precomputed sort key:
        ///     calling it inside an Array.Sort comparator evaluates it ~2·n·log(n) times
        ///     instead of n, which is how the ClosestPoint warning storm got so large.
        /// </summary>
        public static float SqrDistanceTo(Collider col, Vector3 point)
        {
            return (ClosestPointSafe(col, point) - point).sqrMagnitude;
        }
    }
}
