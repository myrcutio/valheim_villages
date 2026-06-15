using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     Renders the per-task map for the Tasks tab. The map shows the village's
    ///     ACTUAL operable area — the region graph's lookup cells, the same points
    ///     <see cref="RegionGraph.PointToRegionId"/> resolves — identically for every
    ///     villager type, so the map faithfully represents where the villager can
    ///     operate (and surfaces any coverage holes / boundary-detection issues)
    ///     rather than a per-type derived shape. A patroller's route, the villager,
    ///     the anchor, gates, and task pins are drawn as overlays on top.
    /// </summary>
    public static class VillageMapPanel
    {
        // Detected gate/door markers on the village map. Cyan reads clearly
        // against the tan perimeter and the warmer task pins.
        private static readonly Color GatePinColor = new(0.25f, 0.85f, 0.95f, 1f);

        /// <summary>
        ///     Render a per-task map for a villager, with optional extra pins for task-relevant locations.
        ///     Returns null if no useful map can be drawn.
        /// </summary>
        public static Texture2D RenderForTask(
            VillagerBehaviorBridge villager,
            IReadOnlyList<(Vector3 position, Color color)> pins)
        {
            if (villager == null) return null;

            var ai = villager.AI;
            if (ai == null) return null;

            var anchor = ai.HomeAnchor;
            var graph = Villages.Entity.VillageRegistry.GraphAt(anchor);
            if (graph == null) return null; // no village graph here — no map to draw

            // Source of truth for the operable area: the graph's lookup cells.
            var cells = graph.Diagnostics.GetLookupCellCenters();

            // Patrol route (if any) is drawn as an OVERLAY, not the footprint.
            var waypoints = ai.GetBehavior<PerimeterPatrolBehavior>()?.PatrolWaypoints;
            var villagerPos = ai.Position;

            return PatrolMapRenderer.Render(
                waypoints,
                anchor,
                villagerPos,
                cells,
                cellSize: RegionGraph.LookupCellSize,
                extraPins: WithGatePins(villager, pins));
        }

        /// <summary>
        ///     Append a pin for every gate the partition sealed into this
        ///     villager's village boundary, so detected gates are visible on
        ///     the map alongside the task pins.
        /// </summary>
        private static IReadOnlyList<(Vector3 position, Color color)> WithGatePins(
            VillagerBehaviorBridge villager,
            IReadOnlyList<(Vector3 position, Color color)> pins)
        {
            var anchor = villager.AI?.HomeAnchor ?? Vector3.zero;
            var graph = Villages.Entity.VillageRegistry.GraphAt(anchor);
            var gates = graph?.GetGates();
            if (gates == null || gates.Count == 0) return pins;

            var merged = new List<(Vector3 position, Color color)>();
            if (pins != null) merged.AddRange(pins);
            foreach (var g in gates) merged.Add((g, GatePinColor));
            return merged;
        }
    }
}
