using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Villages
{
    /// <summary>
    /// Represents a protected village area defined by a polygon of patrol waypoints.
    /// Provides point-in-polygon and boundary proximity tests for spawn protection
    /// and enemy avoidance.
    /// </summary>
    public class VillageArea
    {
        private readonly List<Vector3> m_waypoints;
        private readonly string m_guardId;
        private readonly Vector3 m_bedPosition;

        /// <summary>Cached 2D polygon vertices (XZ projection).</summary>
        private readonly List<Vector2> m_polygon2D;

        public VillageArea(string guardId, Vector3 bedPosition, List<Vector3> waypoints)
        {
            m_guardId = guardId;
            m_bedPosition = bedPosition;
            m_waypoints = new List<Vector3>(waypoints);

            // Pre-compute 2D polygon for efficient checks
            m_polygon2D = new List<Vector2>(waypoints.Count);
            foreach (var wp in waypoints)
            {
                m_polygon2D.Add(new Vector2(wp.x, wp.z));
            }
        }

        public string GuardId => m_guardId;
        public Vector3 BedPosition => m_bedPosition;
        public IReadOnlyList<Vector3> Waypoints => m_waypoints;

        /// <summary>
        /// Check if a 3D position is inside the village area (XZ projection).
        /// Uses the ray casting algorithm for point-in-polygon testing.
        /// </summary>
        public bool IsInsideArea(Vector3 position)
        {
            if (m_polygon2D.Count < 3) return false;
            return IsPointInPolygon(new Vector2(position.x, position.z));
        }

        /// <summary>
        /// Check if a position is within a given distance of the village boundary.
        /// Used for enemy avoidance (20m buffer).
        /// </summary>
        public bool IsNearBoundary(Vector3 position, float radius)
        {
            if (m_polygon2D.Count < 3) return false;

            var point = new Vector2(position.x, position.z);
            float radiusSq = radius * radius;

            // Check distance to each polygon edge
            for (int i = 0; i < m_polygon2D.Count; i++)
            {
                int next = (i + 1) % m_polygon2D.Count;
                float distSq = PointToSegmentDistanceSq(point, m_polygon2D[i], m_polygon2D[next]);
                if (distSq <= radiusSq)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Ray casting algorithm for point-in-polygon test.
        /// Casts a ray from the point rightward and counts edge crossings.
        /// Odd crossings = inside, even = outside.
        /// </summary>
        private bool IsPointInPolygon(Vector2 point)
        {
            bool inside = false;
            int count = m_polygon2D.Count;

            for (int i = 0, j = count - 1; i < count; j = i++)
            {
                var vi = m_polygon2D[i];
                var vj = m_polygon2D[j];

                if ((vi.y > point.y) != (vj.y > point.y) &&
                    point.x < (vj.x - vi.x) * (point.y - vi.y) / (vj.y - vi.y) + vi.x)
                {
                    inside = !inside;
                }
            }

            return inside;
        }

        /// <summary>
        /// Squared distance from a point to a line segment.
        /// </summary>
        private static float PointToSegmentDistanceSq(Vector2 point, Vector2 segA, Vector2 segB)
        {
            var ab = segB - segA;
            var ap = point - segA;

            float abLenSq = ab.sqrMagnitude;
            if (abLenSq < 0.0001f)
                return ap.sqrMagnitude;

            float t = Mathf.Clamp01(Vector2.Dot(ap, ab) / abLenSq);
            var closest = segA + ab * t;
            return (point - closest).sqrMagnitude;
        }
    }
}
