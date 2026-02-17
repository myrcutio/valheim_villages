using UnityEngine;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// A navigation target with an associated pathing strategy id.
    /// The villager uses the strategy when moving toward this waypoint.
    /// </summary>
    public class VillagerWaypoint
    {
        /// <summary>World position to path to.</summary>
        public Vector3 Position { get; }

        /// <summary>Strategy id used to resolve pathing behavior (e.g. "default", "guard_patrol", "worker").</summary>
        public string StrategyId { get; }

        /// <summary>Optional label for debug display.</summary>
        public string Label { get; }

        /// <summary>
        /// Whether this waypoint is active in the patrol route.
        /// Inactive waypoints are retained for potential reactivation and debug display,
        /// but skipped during normal patrol traversal.
        /// </summary>
        public bool Active { get; set; } = true;

        public VillagerWaypoint(Vector3 position, string strategyId, string label = null)
        {
            Position = position;
            StrategyId = string.IsNullOrEmpty(strategyId) ? "default" : strategyId;
            Label = label ?? "";
        }

        /// <summary>Create a waypoint with the default pathing strategy.</summary>
        public static VillagerWaypoint WithDefault(Vector3 position, string label = null) =>
            new VillagerWaypoint(position, "default", label);
    }
}
