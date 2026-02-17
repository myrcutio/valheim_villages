using UnityEngine;

namespace ValheimVillages.NPCs.AI.Work
{
    /// <summary>
    /// Partial class extension for CraftingBehavior that handles rescuing done
    /// cooked food from cooking stations (e.g. after save/load or finishing a craft).
    /// </summary>
    public partial class CraftingBehavior
    {
        /// <summary>
        /// Check nearby cooking stations for done items and start a rescue if found.
        /// Returns true if a rescue was started, false otherwise.
        /// </summary>
        private bool TryRescueCookedFood()
        {
            if (m_ai?.Memory == null) return false;

            var cookingLocations = m_ai.Memory.GetLocationsOfType(LocationType.CookingStation);

            foreach (var loc in cookingLocations)
            {
                float dist = Utils.DistanceXZ(m_ai.Position, loc.Position);
                if (dist > WorkSettings.ChestScanRadius) continue;

                var station = FindCookingStationAt(loc.Position);
                if (station == null) continue;

                int slotCount = CookingStationHelper.GetSlotCount(station);
                for (int i = 0; i < slotCount; i++)
                {
                    CookingStationHelper.GetSlot(station, i, out string itemName, out _, out int status);
                    if (status != CookingStationHelper.StatusDone) continue;
                    if (string.IsNullOrEmpty(itemName)) continue;

                    var context = new WorkOrderContext
                    {
                        CookingStationRef = station,
                        CraftStationPosition = loc.Position,
                        IsRescue = true,
                        WorkOrder = new WorkOrderMatch
                        {
                            ItemPrefabName = itemName,
                            MaxQuantity = 1
                        }
                    };

                    Plugin.Log?.LogInfo(
                        $"[Work:{m_ai.NpcName}] Rescuing done {itemName} from cooking station at {loc.Position}");

                    m_context = context;
                    m_subState = WorkSubState.TravelingToStation;
                    BeginTravelingToStation();
                    return true;
                }
            }

            return false;
        }

        private static CookingStation FindCookingStationAt(Vector3 position)
        {
            var colliders = Physics.OverlapSphere(position, 2f);
            foreach (var col in colliders)
            {
                var cs = col.GetComponentInParent<CookingStation>();
                if (cs != null) return cs;
            }
            return null;
        }
    }
}
