using UnityEngine;

namespace ValheimVillages.Villager.AI.Pathfinding
{
    /// <summary>
    /// A navigation target. StrategyId is retained for serialization compatibility but pathing
    /// uses built-in MoveTo/FindPath only (no strategy registry).
    /// </summary>
    public class VillagerWaypoint
    {
        /// <summary>Single strategy id used for all waypoints (pathing strategy system removed).</summary>
        public const string DefaultStrategyId = "default";

        /// <summary>World position to path to.</summary>
        public Vector3 Position { get; }

        /// <summary>Strategy id (kept for persistence); movement always uses built-in pathing.</summary>
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

        /// <summary>Create a waypoint with the default strategy id.</summary>
        public static VillagerWaypoint WithDefault(Vector3 position, string label = null) =>
            new VillagerWaypoint(position, DefaultStrategyId, label);
    }
}
