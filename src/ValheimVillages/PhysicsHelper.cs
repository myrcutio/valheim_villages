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
    }
}