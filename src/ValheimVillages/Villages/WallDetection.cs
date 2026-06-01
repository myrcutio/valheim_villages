using UnityEngine;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Utility for identifying wall and door pieces in the game world.
    ///     Used by patrollers to detect village boundaries during patrol route discovery.
    /// </summary>
    public static class WallDetection
    {
        /// <summary>
        ///     Known wall prefab name prefixes. Doors also count as walls (open or closed).
        /// </summary>
        private static readonly string[] WallPrefixes =
        {
            "wood_wall",
            "stone_wall",
            "stakewall",
            "darkwood_gate",
            "iron_wall",
            "dungeon_wall",
            "goblin_wall",
            "dvergr_wall",
            "piece_wall",
        };

        private static readonly string[] DoorPrefixes =
        {
            "wood_door",
            "iron_door",
            "darkwood_door",
            "dvergr_door",
            "door",
        };

        /// <summary>
        ///     Layer mask for piece colliders (used in raycasts).
        ///     Valheim pieces typically use the "piece" or "static_solid" layers.
        /// </summary>
        private static int? s_pieceMask;

        private static int PieceMask
        {
            get
            {
                s_pieceMask ??= LayerMask.GetMask("piece", "static_solid", "Default");
                return s_pieceMask.Value;
            }
        }

        /// <summary>
        ///     Check if a GameObject is a wall or door piece.
        /// </summary>
        public static bool IsWallPiece(GameObject obj)
        {
            if (obj == null) return false;

            var piece = obj.GetComponentInParent<Piece>();
            if (piece == null) return false;

            var name = obj.name.ToLower();
            return IsWallName(name) || IsDoorName(name);
        }

        /// <summary>
        ///     Raycast to find the nearest wall piece in a given direction.
        ///     Returns true if a wall was found, with hit info.
        /// </summary>
        public static bool RaycastForWall(Vector3 origin, Vector3 direction, float maxDist, out RaycastHit wallHit)
        {
            wallHit = default;
            var hits = Physics.RaycastAll(origin, direction.normalized, maxDist, PieceMask);

            var closestDist = float.MaxValue;
            var found = false;

            foreach (var hit in hits)
            {
                if (hit.collider == null) continue;
                if (!IsWallPiece(hit.collider.gameObject)) continue;

                if (hit.distance < closestDist)
                {
                    closestDist = hit.distance;
                    wallHit = hit;
                    found = true;
                }
            }

            return found;
        }

        private static bool IsWallName(string lowerName)
        {
            foreach (var prefix in WallPrefixes)
                if (lowerName.Contains(prefix))
                    return true;

            // Fallback: any piece with "wall" in the name
            return lowerName.Contains("wall");
        }

        private static bool IsDoorName(string lowerName)
        {
            foreach (var prefix in DoorPrefixes)
                if (lowerName.Contains(prefix))
                    return true;

            return false;
        }
    }
}