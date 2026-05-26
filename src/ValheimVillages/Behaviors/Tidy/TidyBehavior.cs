using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Behaviors.Tidy
{
    /// <summary>
    ///     Scans nearby cooking stations for finished (Done) or burnt items and removes them
    ///     from the spit. Higher priority than crafting/farming so the NPC tidies before
    ///     starting new work. Removed items are deposited into the nearest container if one
    ///     exists; otherwise the physical drop is simply destroyed.
    ///     Tag: "tidy", Priority: 60.
    /// </summary>
    [RegisterBehavior("tidy")]
    public class TidyBehavior : IBehavior
    {
        private const float ScanInterval = 10f;
        private const float StationLookupRadius = 2f;
        private const float ItemPickupRadius = 3f;
        private readonly VillagerAI m_ai;
        private bool m_active;
        private float m_lastScanTime;
        private CookingStation m_targetStation;

        public TidyBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "tidy";
        public int Priority => 60;

        public bool WantsControl(BehaviorContext ctx)
        {
            if (m_active) return true;

            if (Time.time - m_lastScanTime < ScanInterval) return false;
            m_lastScanTime = Time.time;

            return FindDirtyStation();
        }

        public void Update(float dt)
        {
            if (m_active) return;

            if (m_targetStation == null) return;

            m_active = true;
            var pos = VillagerMovement.GetWalkableDestination(m_targetStation.transform.position);
            m_ai.SetState(BehaviorState.Traveling, pos);
        }

        public void OnArrival(float dt)
        {
            if (m_targetStation == null)
            {
                Reset();
                return;
            }

            CleanStation(m_targetStation);
            Reset();
        }

        public string GetStatusText()
        {
            if (m_active)
                return m_targetStation != null ? "Tidying cooking station" : "Looking for mess";
            return "";
        }

        private bool FindDirtyStation()
        {
            var memory = m_ai.GetMemory();
            if (memory == null) return false;

            foreach (var loc in memory.KnownLocations)
            {
                if (loc.Type != LocationType.CookingStation) continue;

                var station = PhysicsHelper.GetFirstInRadius<CookingStation>(
                    loc.Position, StationLookupRadius);

                if (station != null && HasDoneOrBurntItems(station))
                {
                    m_targetStation = station;
                    return true;
                }
            }

            return false;
        }

        private static bool HasDoneOrBurntItems(CookingStation station)
        {
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return false;

            var zdo = nview.GetZDO();
            var slotCount = station.m_slots != null ? station.m_slots.Length : 0;

            for (var i = 0; i < slotCount; i++)
            {
                var item = zdo.GetString("slot" + i);
                if (string.IsNullOrEmpty(item)) continue;

                var status = zdo.GetInt("slotstatus" + i);
                if (status >= 1) return true;
            }

            return false;
        }

        private void CleanStation(CookingStation station)
        {
            var nview = station.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return;

            var zdo = nview.GetZDO();
            var slotCount = station.m_slots != null ? station.m_slots.Length : 0;
            var removedItems = new List<string>();

            for (var i = 0; i < slotCount; i++)
            {
                var item = zdo.GetString("slot" + i);
                if (string.IsNullOrEmpty(item)) continue;

                var status = zdo.GetInt("slotstatus" + i);
                if (status < 1) continue;

                Plugin.Log?.LogInfo(
                    $"[Tidy:{m_ai.NpcName}] Removing slot {i} ({item}, status={status})");

                nview.InvokeRPC("RPC_RemoveDoneItem", station.transform.position, i);
                PickupDroppedItem(station, item);
                removedItems.Add(item);
            }

            if (removedItems.Count > 0)
                DepositToNearbyContainer(station, removedItems);
        }

        private static void PickupDroppedItem(CookingStation station, string slotItemName)
        {
            var spawnPos = station.m_spawnPoint != null
                ? station.m_spawnPoint.position
                : station.transform.position;

            var allDrops = PhysicsHelper.GetAllInRadius<ItemDrop>(spawnPos, ItemPickupRadius);

            ItemDrop closest = null;
            var closestDist = float.MaxValue;

            foreach (var drop in allDrops)
            {
                if (drop == null || drop.m_itemData == null) continue;
                var dropPrefab = drop.m_itemData.m_dropPrefab?.name
                                 ?? drop.gameObject.name.Replace("(Clone)", "").Trim();
                if (dropPrefab != slotItemName) continue;

                var dist = Vector3.Distance(drop.transform.position, spawnPos);
                if (dist < closestDist)
                {
                    closest = drop;
                    closestDist = dist;
                }
            }

            if (closest != null)
            {
                var dropNview = closest.GetComponent<ZNetView>();
                if (dropNview != null && dropNview.GetZDO() != null)
                    ZNetScene.instance.Destroy(closest.gameObject);
                else
                    Object.Destroy(closest.gameObject);
            }
        }

        private static void DepositToNearbyContainer(CookingStation station, List<string> itemNames)
        {
            var containers = ContainerScanner.FindNearbyContainers(
                station.transform.position, WorkSettings.ChestScanRadius);

            if (containers.Count == 0) return;

            foreach (var itemName in itemNames)
            foreach (var container in containers)
                if (ContainerScanner.TryDepositItem(container, itemName, 1))
                    break;
        }

        private void Reset()
        {
            m_active = false;
            m_targetStation = null;
            m_ai.SetState(BehaviorState.Idle);
        }
    }
}