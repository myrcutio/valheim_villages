using System;
using System.Linq;
using UnityEngine;
using ValheimVillages;

namespace ValheimVillages.NPCs.AI.Work
{
    /// <summary>
    /// Shared logic for resolving crafting/cooking stations from an NPC's known locations.
    /// Used by WorkOrderScanHandler and available for CraftingBehavior if needed.
    /// </summary>
    public static class StationFinder
    {
        private const float StationLookupRadius = 2f;

        /// <summary>
        /// Find the first station of type T at the NPC's known CraftStation locations,
        /// ordered by distance from bed. Returns position and component when filter passes.
        /// </summary>
        public static bool TryFindStationAtKnownLocations<T>(
            VillagerAI ai,
            Func<T, bool> filter,
            out Vector3 position,
            out T component) where T : Component
        {
            position = Vector3.zero;
            component = null;

            if (ai?.Memory?.KnownLocations == null) return false;

            var craftStations = ai.Memory.KnownLocations
                .Where(l => l.Type == LocationType.CraftStation || l.Type == LocationType.CookingStation)
                .OrderBy(l => Vector3.Distance(ai.Memory.BedPosition, l.Position))
                .ToList();

            foreach (var loc in craftStations)
            {
                var c = PhysicsHelper.GetFirstInRadius<T>(loc.Position, StationLookupRadius);
                if (c != null && (filter == null || filter(c)))
                {
                    position = c.transform.position;
                    component = c;
                    return true;
                }
            }

            return false;
        }
    }
}
