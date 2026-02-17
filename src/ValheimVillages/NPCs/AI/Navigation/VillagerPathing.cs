using UnityEngine;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Base class for pluggable pathing strategies. Uses Valheim pathfinding (BaseAI MoveTo/MoveAndAvoid)
    /// with strategy-specific arrival threshold, fire handling, and destination adjustment.
    /// </summary>
    public abstract class VillagerPathing
    {
        /// <summary>Arrival distance in meters (3D). Subclasses override.</summary>
        protected virtual float GetArrivalThreshold() => VillagerSettings.ArrivalThreshold;

        /// <summary>Whether to skip fire avoidance (e.g. when heading to a forge).</summary>
        protected virtual bool ShouldIgnoreFire() => false;

        /// <summary>Adjust destination for pathfinding (e.g. snap to walkable surface).</summary>
        protected virtual Vector3 AdjustDestination(Vector3 worldPosition) =>
            VillagerMovement.GetWalkableDestination(worldPosition);

        /// <summary>
        /// Perform one tick of movement toward the destination. Returns true if arrived (within threshold).
        /// </summary>
        public bool MoveToward(MonsterAI instance, Vector3 destination, float dt)
        {
            Vector3 adjusted = AdjustDestination(destination);
            float threshold = GetArrivalThreshold();
            bool ignoreFire = ShouldIgnoreFire();
            return VillagerMovement.ExecutePathingTick(instance, adjusted, dt, threshold, ignoreFire);
        }
    }
}
