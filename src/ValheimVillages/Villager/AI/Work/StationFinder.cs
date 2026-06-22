using System;
using System.Linq;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;

namespace ValheimVillages.Villager.AI.Work
{
    /// <summary>
    ///     Shared logic for resolving crafting/cooking stations from an NPC's known locations.
    ///     Used by WorkOrderScanHandler and available for CraftingBehavior if needed.
    /// </summary>
    public static class StationFinder
    {
        private const float StationLookupRadius = 2f;

        private static readonly MethodInfo s_isFireLit = typeof(CookingStation)
            .GetMethod("IsFireLit", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo s_getFuel = typeof(CookingStation)
            .GetMethod("GetFuel", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly MethodInfo s_smelterGetFuel = typeof(Smelter)
            .GetMethod("GetFuel", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        ///     Raw call to CookingStation.IsFireLit() via reflection.
        ///     Returns false if reflection fails or fire is not lit.
        /// </summary>
        private static bool InvokeIsFireLit(CookingStation station)
        {
            if (station == null || s_isFireLit == null) return false;
            try
            {
                return (bool)s_isFireLit.Invoke(station, null);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        ///     Returns true when the fire-underneath requirement is satisfied:
        ///     either fire is not required, or it is required and IsFireLit() returns true.
        ///     Used by StationFuelHelper.DiagnoseFuelNeed to check the fire condition independently.
        /// </summary>
        public static bool IsCookingStationFireLit(CookingStation station)
        {
            if (station == null) return false;
            if (!station.m_requireFire) return true;
            return InvokeIsFireLit(station);
        }

        /// <summary>
        ///     Matches the server's FixedUpdate ready check exactly:
        ///     isReady = (m_requireFire AND IsFireLit()) OR (m_useFuel AND GetFuel() > 0)
        ///     A station is ready when EITHER fire is lit (for fire-based stations)
        ///     OR it has internal fuel (for fuel-based stations).
        /// </summary>
        public static bool IsCookingStationReady(CookingStation station)
        {
            if (station == null) return false;
            if (station.m_requireFire && InvokeIsFireLit(station)) return true;
            if (station.m_useFuel && GetCookingStationFuel(station) > 0) return true;
            return false;
        }

        /// <summary>
        ///     Returns the Smelter component on the ZNetScene prefab with the given name, or null if the prefab
        ///     doesn't exist or doesn't carry a Smelter (e.g. it's a CraftingStation prefab like piece_forge).
        /// </summary>
        public static Smelter GetSmelterPrefab(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            var zns = ZNetScene.instance;
            if (zns?.m_prefabs == null) return null;
            for (var i = 0; i < zns.m_prefabs.Count; i++)
            {
                var go = zns.m_prefabs[i];
                if (go == null || go.name != prefabName) continue;
                return go.GetComponent<Smelter>();
            }
            return null;
        }

        /// <summary>
        ///     Smelter is ready when it has no fuel requirement (m_fuelItem null) OR currently has fuel.
        ///     Mirrors the cooking-station ready check at the smelter level.
        /// </summary>
        public static bool IsSmelterReady(Smelter station)
        {
            if (station == null) return false;
            if (station.m_fuelItem == null) return true;
            return GetSmelterFuel(station) > 0f;
        }

        /// <summary>
        ///     Returns the current fuel level of a Smelter via reflection (GetFuel is private).
        ///     Returns 0 on failure.
        /// </summary>
        public static float GetSmelterFuel(Smelter station)
        {
            if (station == null || s_smelterGetFuel == null) return 0f;
            try
            {
                return (float)s_smelterGetFuel.Invoke(station, null);
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        ///     Returns the current fuel level of a CookingStation via reflection (GetFuel is private).
        ///     Returns 0 on failure.
        /// </summary>
        public static float GetCookingStationFuel(CookingStation station)
        {
            if (station == null || s_getFuel == null) return 0f;
            try
            {
                return (float)s_getFuel.Invoke(station, null);
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        ///     Returns true if the cooking station has at least one empty slot.
        ///     Reads slot data from ZDO, matching the server's GetFreeSlot() logic.
        /// </summary>
        public static bool HasFreeSlot(CookingStation station)
        {
            if (station == null) return false;
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return false;

            var slotCount = station.m_slots != null ? station.m_slots.Length : 0;
            var zdo = nview.GetZDO();
            for (var i = 0; i < slotCount; i++)
                if (string.IsNullOrEmpty(zdo.GetString("slot" + i)))
                    return true;
            return false;
        }

        /// <summary>
        ///     Number of occupied (non-empty) slots on a cooking station — items currently
        ///     cooking, each of which becomes one finished output. Reads ZDO slot data,
        ///     mirroring <see cref="HasFreeSlot" />. Counted as pending output toward a
        ///     cooking work order's quota so the production pipeline can't overshoot.
        /// </summary>
        public static int CountOccupiedSlots(CookingStation station)
        {
            if (station == null) return 0;
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return 0;

            var slotCount = station.m_slots != null ? station.m_slots.Length : 0;
            var zdo = nview.GetZDO();
            var occupied = 0;
            for (var i = 0; i < slotCount; i++)
                if (!string.IsNullOrEmpty(zdo.GetString("slot" + i)))
                    occupied++;
            return occupied;
        }
    }
}