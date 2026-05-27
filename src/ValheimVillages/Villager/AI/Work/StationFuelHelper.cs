using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Villager.AI.Work
{
    /// <summary>
    ///     Fuel requirement for a cooking station: either fire underneath (Fireplace)
    ///     or internal fuel (CookingStation.m_useFuel).
    /// </summary>
    public struct FuelNeed
    {
        public Vector3 FuelTargetPosition;
        public string FuelItemPrefab;
        public Fireplace FireplaceRef;
        public CookingStation CookingStationRef;
        public Smelter SmelterRef;
        public bool NeedsFireUnderneath;
        public bool NeedsInternalFuel;
    }

    /// <summary>
    ///     Helpers for diagnosing and resolving fuel requirements on cooking stations.
    /// </summary>
    public static class StationFuelHelper
    {
        private const float FireplaceSearchRadius = 3f;

        /// <summary>
        ///     Determines whether the station needs fueling (fire underneath, internal fuel, or both).
        ///     Returns true when at least one fuel need was identified.
        /// </summary>
        /// <summary>
        ///     Determines whether the station needs fueling (fire underneath, internal fuel, or both).
        ///     Server ready check is: (m_requireFire AND IsFireLit) OR (m_useFuel AND GetFuel > 0).
        ///     Either condition alone makes the station ready, so we try fire first and fall back to
        ///     internal fuel if the fireplace can't be found.
        /// </summary>
        public static bool DiagnoseFuelNeed(CookingStation station, out FuelNeed need)
        {
            need = default;
            if (station == null) return false;

            var needsFire = station.m_requireFire && !StationFinder.IsCookingStationFireLit(station);
            var needsInternalFuel = station.m_useFuel && StationFinder.GetCookingStationFuel(station) <= 0;

            if (!needsFire && !needsInternalFuel) return false;

            need.CookingStationRef = station;

            if (needsFire && TryFindFireplaceNear(station, out var fireplace))
            {
                need.NeedsFireUnderneath = true;
                need.FireplaceRef = fireplace;
                need.FuelTargetPosition = fireplace.transform.position;
                need.FuelItemPrefab = fireplace.m_fuelItem?.gameObject?.name;
            }
            else if (needsInternalFuel)
            {
                need.NeedsInternalFuel = true;
                need.FuelTargetPosition = station.transform.position;
                need.FuelItemPrefab = station.m_fuelItem?.gameObject?.name;
            }
            else
            {
                return false;
            }

            return !string.IsNullOrEmpty(need.FuelItemPrefab);
        }

        /// <summary>
        ///     Determines whether a Smelter needs internal fueling. Smelters with no m_fuelItem
        ///     (charcoal_kiln, windmill, piece_spinningwheel) never need separate fuel — Wood/Barley/Flax
        ///     is the input and there's no parallel fuel slot.
        /// </summary>
        public static bool DiagnoseFuelNeed(Smelter station, out FuelNeed need)
        {
            need = default;
            if (station == null) return false;
            if (station.m_fuelItem == null) return false;
            if (StationFinder.GetSmelterFuel(station) > 0f) return false;

            need.SmelterRef = station;
            need.NeedsInternalFuel = true;
            need.FuelTargetPosition = station.transform.position;
            need.FuelItemPrefab = station.m_fuelItem.gameObject?.name;
            return !string.IsNullOrEmpty(need.FuelItemPrefab);
        }

        /// <summary>
        ///     Finds the Fireplace near a CookingStation that can accept fuel.
        ///     Searches around m_fireCheckPoints first, then around the station transform.
        /// </summary>
        public static bool TryFindFireplaceNear(CookingStation station, out Fireplace fireplace)
        {
            fireplace = null;
            if (station == null) return false;

            var searchPoints = new List<Vector3>();
            if (station.m_fireCheckPoints != null)
                foreach (var pt in station.m_fireCheckPoints)
                    if (pt != null)
                        searchPoints.Add(pt.position);

            if (searchPoints.Count == 0)
                searchPoints.Add(station.transform.position);

            foreach (var center in searchPoints)
            {
                var candidates = PhysicsHelper.GetAllInRadius<Fireplace>(center, FireplaceSearchRadius);
                foreach (var fp in candidates)
                {
                    if (fp == null) continue;
                    if (fp.m_infiniteFuel) continue;
                    fireplace = fp;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     Searches containers for at least 1 unit of the required fuel item.
        /// </summary>
        public static bool FindFuelInContainers(
            List<Container> containers, string fuelPrefabName, out Container fuelContainer)
        {
            fuelContainer = null;
            if (containers == null || string.IsNullOrEmpty(fuelPrefabName)) return false;

            foreach (var container in containers)
            {
                var inv = container?.GetInventory();
                if (inv == null) continue;

                if (ContainerScanner.CountByPrefab(inv, fuelPrefabName) > 0)
                {
                    fuelContainer = container;
                    return true;
                }
            }

            return false;
        }
    }
}