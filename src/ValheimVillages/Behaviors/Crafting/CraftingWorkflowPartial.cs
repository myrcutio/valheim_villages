using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Work;
using VillagerWaypoint = ValheimVillages.Villager.AI.Pathfinding.VillagerWaypoint;

namespace ValheimVillages.Behaviors.Crafting
{
    /// <summary>
    /// Workflow method stubs for CraftingBehavior (Villager path).
    /// Full implementation to be completed in later migration step.
    /// </summary>
    public partial class CraftingBehavior
    {
        private void AbandonWork(string reason)
        {
            m_context = null;
            m_subState = WorkSubState.Idle;
            if (m_ai != null)
                m_ai.SetState(BehaviorState.Idle, (VillagerWaypoint)null);
        }

        private void BeginFueling()
        {
            if (m_context?.FuelRequirement == null || m_context.FuelContainer == null)
            {
                BeginGatheringIngredients();
                return;
            }

            Plugin.Log?.LogInfo(
                $"[Work:{LogName}] Fueling required ({m_context.FuelRequirement.Value.FuelItemPrefab}), " +
                $"walking to fuel container");

            m_subState = WorkSubState.GatheringFuel;
            var fuelChestPos = VillagerMovement.GetWalkableDestination(
                m_context.FuelContainer.transform.position);
            m_ai.SetState(BehaviorState.Working,
                new VillagerWaypoint(fuelChestPos, VillagerWaypoint.DefaultStrategyId));
        }

        private void OnArrivedAtFuelContainer()
        {
            if (m_context == null || m_ai == null || m_context.FuelRequirement == null)
            {
                AbandonWork("lost fuel context");
                return;
            }

            var fuel = m_context.FuelRequirement.Value;
            var container = m_context.FuelContainer;
            if (container == null)
            {
                AbandonWork("fuel container inaccessible");
                return;
            }

            var inv = container.GetInventory();
            if (inv == null || ContainerScanner.CountByPrefab(inv, fuel.FuelItemPrefab) <= 0)
            {
                AbandonWork("fuel no longer available in container");
                return;
            }

            ContainerScanner.RemoveIngredients(new List<IngredientSource>
            {
                new IngredientSource
                {
                    PrefabName = fuel.FuelItemPrefab,
                    Amount = 1,
                    Container = container
                }
            });

            Plugin.Log?.LogInfo(
                $"[Work:{LogName}] Picked up 1x {fuel.FuelItemPrefab}, walking to fuel target");

            m_subState = WorkSubState.FuelingStation;
            var targetPos = VillagerMovement.GetWalkableDestination(fuel.FuelTargetPosition);
            m_ai.SetState(BehaviorState.Working,
                new VillagerWaypoint(targetPos, VillagerWaypoint.DefaultStrategyId));
        }

        private void OnArrivedAtFuelTarget()
        {
            if (m_context == null || m_context.FuelRequirement == null)
            {
                AbandonWork("lost fuel context at target");
                return;
            }

            var fuel = m_context.FuelRequirement.Value;

            if (fuel.FireplaceRef != null)
            {
                var nview = fuel.FireplaceRef.GetComponent<ZNetView>();
                if (nview != null && nview.GetZDO() != null)
                {
                    nview.InvokeRPC("RPC_AddFuel");
                    Plugin.Log?.LogInfo(
                        $"[Work:{LogName}] Added fuel to Fireplace");
                }
                else
                {
                    AbandonWork("fireplace ZNetView invalid");
                    return;
                }
            }
            else if (fuel.CookingStationRef != null)
            {
                var nview = fuel.CookingStationRef.GetComponent<ZNetView>();
                if (nview != null && nview.GetZDO() != null)
                {
                    nview.InvokeRPC("RPC_AddFuel");
                    Plugin.Log?.LogInfo(
                        $"[Work:{LogName}] Added fuel to CookingStation");
                }
                else
                {
                    AbandonWork("cooking station ZNetView invalid for fueling");
                    return;
                }
            }

            m_context.FuelRequirement = null;
            m_context.FuelContainer = null;
            BeginGatheringIngredients();
        }

        private void BeginGatheringIngredients()
        {
            if (m_context?.IngredientSources == null || m_context.IngredientSources.Count == 0)
            {
                BeginTravelingToStation();
                return;
            }
            m_context.CurrentIngredientIndex = 0;
            WalkToNextIngredientChest();
        }

        private bool TryPollCookingStation()
        {
            var station = m_context?.CookingStationRef;
            if (station == null) return false;
            if (m_context.CookingRemovalRequested) return true;

            if (!StationFinder.IsCookingStationReady(station))
            {
                AbandonWork("cooking station fire went out");
                return true;
            }

            var nview = station.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return false;

            int slotCount = station.m_slots != null ? station.m_slots.Length : 0;
            var zdo = nview.GetZDO();
            for (int i = 0; i < slotCount; i++)
            {
                string slotItem = zdo.GetString("slot" + i, "");
                if (string.IsNullOrEmpty(slotItem)) continue;

                int status = zdo.GetInt("slotstatus" + i, 0);
                // Status: 0=NotDone, 1=Done, 2=Burnt
                if (status >= 1)
                {
                    m_context.CookingRemovalRequested = true;
                    nview.InvokeRPC("RPC_RemoveDoneItem", station.transform.position, i);

                    TryPickupDroppedItem(station, slotItem);

                    m_context.CraftedCount++;
                    BeginReturningToChest();
                    return true;
                }
            }
            return true;
        }

        private void TryPickupDroppedItem(CookingStation station, string slotItemName)
        {
            var spawnPos = station.m_spawnPoint != null
                ? station.m_spawnPoint.position
                : station.transform.position;
            const float searchRadius = 3f;

            var allDrops = PhysicsHelper.GetAllInRadius<ItemDrop>(spawnPos, searchRadius);

            ItemDrop closest = null;
            float closestDist = float.MaxValue;
            foreach (var drop in allDrops)
            {
                if (drop == null || drop.m_itemData == null) continue;
                string dropPrefab = drop.m_itemData.m_dropPrefab?.name
                    ?? drop.gameObject.name.Replace("(Clone)", "").Trim();
                if (dropPrefab != slotItemName) continue;

                float dist = Vector3.Distance(drop.transform.position, spawnPos);
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

        private void CompleteCraft()
        {
            if (m_context == null)
            {
                AbandonWork("lost context in CompleteCraft");
                return;
            }

            if (m_context.CookingStationRef != null)
            {
                string outputPrefab = m_context.WorkOrder?.ItemPrefabName;
                if (!string.IsNullOrEmpty(outputPrefab) && m_context.SourceContainer != null)
                {
                    ContainerScanner.TryDepositItem(m_context.SourceContainer, outputPrefab, 1);
                    m_context.CookingItemAlreadyInChest = true;
                }
            }
            else
            {
                m_context.CraftedCount++;
            }

            BeginReturningToChest();
        }

        private void BeginReturningToChest()
        {
            if (m_context == null || m_ai == null)
            {
                AbandonWork("lost context in BeginReturningToChest");
                return;
            }

            if (m_context.SourceContainer == null)
            {
                FinishWork();
                return;
            }

            m_subState = WorkSubState.ReturningToChest;
            var chestPos = VillagerMovement.GetWalkableDestination(m_context.SourceContainer.transform.position);
            m_ai.SetState(BehaviorState.Working, new VillagerWaypoint(chestPos, VillagerWaypoint.DefaultStrategyId));
        }

        private void FinishWork()
        {
            m_context = null;
            m_subState = WorkSubState.Idle;
            if (m_ai != null)
                m_ai.SetState(BehaviorState.Idle, (VillagerWaypoint)null);
        }

        private void OnArrivedAtIngredientChest()
        {
            if (m_context == null || m_ai == null) return;
            if (m_context.CurrentIngredientIndex >= m_context.IngredientSources.Count)
            {
                BeginTravelingToStation();
                return;
            }

            var source = m_context.IngredientSources[m_context.CurrentIngredientIndex];
            if (source.Container == null)
            {
                AbandonWork("ingredient chest inaccessible");
                return;
            }

            var singleSource = new List<IngredientSource> { source };
            ContainerScanner.RemoveIngredients(singleSource);

            m_context.CurrentIngredientIndex++;
            WalkToNextIngredientChest();
        }

        private void OnArrivedAtStation()
        {
            if (m_context == null) return;

            m_context.CraftStartTime = Time.time;
            m_subState = WorkSubState.Crafting;

            var station = m_context.CookingStationRef;
            if (station != null && !string.IsNullOrEmpty(m_context.CookingInputItemName))
            {
                if (!StationFinder.IsCookingStationReady(station))
                {
                    AbandonWork("cooking station not ready (fire or fuel)");
                    return;
                }

                if (!StationFinder.HasFreeSlot(station))
                {
                    AbandonWork("cooking station full");
                    return;
                }

                var nview = station.GetComponent<ZNetView>();
                if (nview != null && nview.GetZDO() != null)
                {
                    nview.InvokeRPC("RPC_AddItem", m_context.CookingInputItemName);

                    var conversion = station.m_conversion?.Find(
                        c => c.m_from != null && c.m_from.gameObject.name == m_context.CookingInputItemName);
                    if (conversion != null)
                        m_context.CraftCookTimeSeconds = conversion.m_cookTime;
                }
                else
                {
                    AbandonWork("cooking station ZNetView invalid");
                }
            }
        }

        private void OnArrivedAtOutputChest()
        {
            if (m_context == null)
            {
                AbandonWork("lost context at output chest");
                return;
            }

            if (!m_context.CookingItemAlreadyInChest && m_context.SourceContainer != null)
            {
                string outputPrefab = m_context.WorkOrder?.ItemPrefabName;
                if (!string.IsNullOrEmpty(outputPrefab))
                    ContainerScanner.TryDepositItem(m_context.SourceContainer, outputPrefab, 1);
            }

            m_context.CookingItemAlreadyInChest = false;
            m_context.CookingRemovalRequested = false;

            int maxQuantity = m_context.WorkOrder?.MaxQuantity ?? 1;
            if (m_context.CraftedCount < maxQuantity)
            {
                BeginGatheringIngredients();
            }
            else
            {
                FinishWork();
            }
        }

        private void BeginTravelingToStation()
        {
            m_subState = WorkSubState.TravelingToStation;
            if (m_ai != null && m_context != null)
            {
                var stationTarget = VillagerMovement.GetWalkableDestination(m_context.CraftStationPosition);
                m_ai.SetState(BehaviorState.Working, new VillagerWaypoint(stationTarget, VillagerWaypoint.DefaultStrategyId));
            }
        }

        private void WalkToNextIngredientChest()
        {
            if (m_context == null || m_ai == null) return;
            if (m_context.CurrentIngredientIndex >= m_context.IngredientSources.Count)
            {
                BeginTravelingToStation();
                return;
            }
            var source = m_context.IngredientSources[m_context.CurrentIngredientIndex];
            if (source.Container == null)
            {
                AbandonWork("ingredient container destroyed");
                return;
            }
            m_subState = WorkSubState.GatheringIngredients;
            var targetPos = VillagerMovement.GetWalkableDestination(source.Container.transform.position);
            m_ai.SetState(BehaviorState.Working, new VillagerWaypoint(targetPos, VillagerWaypoint.DefaultStrategyId));
        }
    }
}
