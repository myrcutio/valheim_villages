using UnityEngine;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Navigation helpers for VillagerAI.
    /// </summary>
    public static class VillagerMovement
    {
        /// <summary>
        ///     Snap a world position to the walkable surface below it, if close.
        ///     Avoids snapping to roofs/ceilings in multi-story buildings.
        /// </summary>
        public static Vector3 GetWalkableDestination(Vector3 worldPosition)
        {
            if (ZoneSystem.instance == null) return worldPosition;
            if (!ZoneSystem.instance.GetSolidHeight(worldPosition, out var h))
                return worldPosition;
            if (Mathf.Abs(h - worldPosition.y) < 2f)
                return new Vector3(worldPosition.x, h, worldPosition.z);
            return worldPosition;
        }
    }
}