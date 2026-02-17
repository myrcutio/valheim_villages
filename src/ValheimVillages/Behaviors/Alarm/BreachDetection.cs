using UnityEngine;
using ValheimVillages.Villages;

namespace ValheimVillages.Behaviors.Alarm
{
    /// <summary>
    /// Detects breaches in the village wall at patrol waypoints.
    /// A breach exists when there is a direct unobstructed line from a waypoint
    /// to a wild, spawnable area (no wall blocking, no player structures nearby).
    /// </summary>
    public static class BreachDetection
    {
        /// <summary>Maximum raycast distance when checking for breaches.</summary>
        public const float BreachRaycastRange = 30f;

        /// <summary>
        /// Radius to check for player structures near the ray endpoint.
        /// If no structures are found, the area is considered "wild".
        /// </summary>
        public const float WildAreaCheckRadius = 15f;

        /// <summary>
        /// Check if there is a breach at the given waypoint.
        /// A breach means no wall is blocking the line of sight outward from the village,
        /// and the area beyond is a wild/spawnable zone.
        /// </summary>
        /// <param name="waypoint">The patrol waypoint position.</param>
        /// <param name="bedCenter">The village center (bed position).</param>
        /// <returns>True if a breach is detected.</returns>
        public static bool CheckForBreach(Vector3 waypoint, Vector3 bedCenter)
        {
            // Compute outward direction (away from bed center, XZ plane)
            var outward = waypoint - bedCenter;
            outward.y = 0f;
            if (outward.sqrMagnitude < 0.01f) return false;
            outward.Normalize();

            var rayOrigin = waypoint + Vector3.up * 1f;

            // Check if a wall exists between the waypoint and the wild area
            if (WallDetection.RaycastForWall(rayOrigin, outward, BreachRaycastRange, out _))
            {
                // Wall found -- no breach at this waypoint
                return false;
            }

            // No wall found -- check if the area beyond is truly wild
            var checkPoint = waypoint + outward * BreachRaycastRange;
            return IsWildArea(checkPoint);
        }

        /// <summary>
        /// Check if a position is in a "wild" area with no player structures nearby.
        /// A wild area has no crafting stations, fires, or workbenches within range.
        /// </summary>
        private static bool IsWildArea(Vector3 position)
        {
            var colliders = Physics.OverlapSphere(position, WildAreaCheckRadius);

            foreach (var col in colliders)
            {
                if (col == null || col.gameObject == null) continue;

                // Check for player-built structures that indicate a settled area
                var piece = col.GetComponentInParent<Piece>();
                if (piece != null && piece.IsPlacedByPlayer())
                    return false;
            }

            return true;
        }
    }
}
