using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Work;
using ValheimVillages.Villages;
using VillagerWaypoint = ValheimVillages.Villager.AI.Pathfinding.VillagerWaypoint;

namespace ValheimVillages.Behaviors.Crafting
{
    /// <summary>
    ///     Workflow method stubs for CraftingBehavior (Villager path).
    ///     Full implementation to be completed in later migration step.
    /// </summary>
    public partial class CraftingBehavior
    {
        /// <summary>
        ///     Resolve a world-space target to an HNA-valid approach point and set the villager
        ///     traveling to it in the given sub-state. Returns true on success. Returns false
        ///     (and abandons work with a clear reason) when no HNA region in the villager's
        ///     village has a complete path from the villager's current position to the target.
        ///     Single entry point for all workflow movement so when one path fails the diagnostic
        ///     applies to every other call site too.
        /// </summary>
        private bool TryWalkTo(Vector3 target, WorkSubState substate, string targetDescription)
        {
            if (m_ai == null) return false;
            if (!VillageStationRegistry.TryResolveApproach(target, m_ai.Position, out var approach))
            {
                AbandonWork($"no HNA-valid approach to {targetDescription} @ ({target.x:F1},{target.y:F1},{target.z:F1})");
                return false;
            }

            SubState = substate;
            m_ai.SetState(BehaviorState.Working,
                new VillagerWaypoint(approach, VillagerWaypoint.DefaultStrategyId));
            return true;
        }

        private static readonly MethodInfo s_smelterGetProcessedQueueSize = typeof(Smelter)
            .GetMethod("GetProcessedQueueSize", BindingFlags.NonPublic | BindingFlags.Instance);

        private static int GetSmelterProcessedQueueSize(Smelter smelter)
        {
            if (smelter == null || s_smelterGetProcessedQueueSize == null) return 0;
            try
            {
                return (int)s_smelterGetProcessedQueueSize.Invoke(smelter, null);
            }
            catch
            {
                return 0;
            }
        }

        private void AbandonWork(string reason)
        {
            m_context = null;
            SubState = WorkSubState.Idle;
            if (m_ai != null)
                m_ai.SetState(BehaviorState.Idle, (VillagerWaypoint)null);
        }

        /// <summary>
        ///     Public entry point for AbandonWork so external callers
        ///     (e.g. IPathUnreachableHandler dispatch on the adapter) can
        ///     abort the current work order without exposing the private
        ///     implementation surface.
        /// </summary>
        public void AbandonWorkPublic(string reason) => AbandonWork(reason);

        private void BeginFueling()
        {
            if (m_context?.FuelRequirement == null || m_context.FuelContainer == null)
            {
                BeginGatheringIngredients();
                return;
            }

            Plugin.Log?.LogInfo(
                $"[Work:{LogName}] Fueling required ({m_context.FuelRequirement.Value.FuelItemPrefab}), " +
                "walking to fuel container");

            TryWalkTo(m_context.FuelContainer.transform.position, WorkSubState.GatheringFuel, "fuel container");
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
                new()
                {
                    PrefabName = fuel.FuelItemPrefab,
                    Amount = 1,
                    Container = container,
                },
            });

            Plugin.Log?.LogInfo(
                $"[Work:{LogName}] Picked up 1x {fuel.FuelItemPrefab}, walking to fuel target");

            TryWalkTo(fuel.FuelTargetPosition, WorkSubState.FuelingStation, "fuel target");
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
            else if (fuel.SmelterRef != null)
            {
                var nview = fuel.SmelterRef.GetComponent<ZNetView>();
                if (nview != null && nview.GetZDO() != null)
                {
                    nview.InvokeRPC("RPC_AddFuel");
                    Plugin.Log?.LogInfo(
                        $"[Work:{LogName}] Added fuel to Smelter ({fuel.SmelterRef.gameObject.name})");
                }
                else
                {
                    AbandonWork("smelter ZNetView invalid for fueling");
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

            var slotCount = station.m_slots != null ? station.m_slots.Length : 0;
            var zdo = nview.GetZDO();
            for (var i = 0; i < slotCount; i++)
            {
                var slotItem = zdo.GetString("slot" + i);
                if (string.IsNullOrEmpty(slotItem)) continue;

                var status = zdo.GetInt("slotstatus" + i);
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

        private bool TryPollSmelter()
        {
            var smelter = m_context?.SmelterRef;
            if (smelter == null) return false;
            if (m_context.SmelterRemovalRequested) return true;

            if (!StationFinder.IsSmelterReady(smelter))
            {
                AbandonWork("smelter fuel ran out before output");
                return true;
            }

            var processedNow = GetSmelterProcessedQueueSize(smelter);
            if (processedNow <= m_context.SmelterProcessedAtStart) return true;

            var nview = smelter.GetComponent<ZNetView>();
            if (nview == null || nview.GetZDO() == null) return false;

            m_context.SmelterRemovalRequested = true;
            nview.InvokeRPC("RPC_EmptyProcessed");

            TryPickupSmelterOutput(smelter);

            m_context.CraftedCount++;
            BeginReturningToChest();
            return true;
        }

        private void TryPickupSmelterOutput(Smelter smelter)
        {
            var outputPos = smelter.m_outputPoint != null
                ? smelter.m_outputPoint.position
                : smelter.transform.position;
            const float searchRadius = 3f;

            var outputPrefab = m_context.WorkOrder?.ItemPrefabName;
            if (string.IsNullOrEmpty(outputPrefab)) return;

            var allDrops = PhysicsHelper.GetAllInRadius<ItemDrop>(outputPos, searchRadius);
            ItemDrop closest = null;
            var closestDist = float.MaxValue;
            foreach (var drop in allDrops)
            {
                if (drop == null || drop.m_itemData == null) continue;
                var dropPrefab = drop.m_itemData.m_dropPrefab?.name
                                 ?? drop.gameObject.name.Replace("(Clone)", "").Trim();
                if (dropPrefab != outputPrefab) continue;
                var dist = Vector3.Distance(drop.transform.position, outputPos);
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

                if (m_context.SourceContainer != null)
                {
                    ContainerScanner.TryDepositItem(m_context.SourceContainer, outputPrefab, 1);
                    m_context.SmelterItemAlreadyInChest = true;
                }
            }
        }

        private void TryPickupDroppedItem(CookingStation station, string slotItemName)
        {
            var spawnPos = station.m_spawnPoint != null
                ? station.m_spawnPoint.position
                : station.transform.position;
            const float searchRadius = 3f;

            var allDrops = PhysicsHelper.GetAllInRadius<ItemDrop>(spawnPos, searchRadius);

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

        private void CompleteCraft()
        {
            if (m_context == null)
            {
                AbandonWork("lost context in CompleteCraft");
                return;
            }

            if (m_context.CookingStationRef != null)
            {
                var outputPrefab = m_context.WorkOrder?.ItemPrefabName;
                if (!string.IsNullOrEmpty(outputPrefab) && m_context.SourceContainer != null)
                {
                    ContainerScanner.TryDepositItem(m_context.SourceContainer, outputPrefab, 1);
                    m_context.CookingItemAlreadyInChest = true;
                }
            }
            else if (m_context.SmelterRef != null)
            {
                // Smelter outputs are picked up in TryPollSmelter; this branch shouldn't normally trigger.
                // If it does (e.g. fallback timer), deposit directly to the source chest.
                var outputPrefab = m_context.WorkOrder?.ItemPrefabName;
                if (!string.IsNullOrEmpty(outputPrefab) && m_context.SourceContainer != null)
                {
                    ContainerScanner.TryDepositItem(m_context.SourceContainer, outputPrefab, 1);
                    m_context.SmelterItemAlreadyInChest = true;
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

            TryWalkTo(m_context.SourceContainer.transform.position, WorkSubState.ReturningToChest, "source chest");
        }

        private void FinishWork()
        {
            m_context = null;
            SubState = WorkSubState.Idle;
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
            SubState = WorkSubState.Crafting;

            var smelter = m_context.SmelterRef;
            if (smelter != null && !string.IsNullOrEmpty(m_context.SmelterInputItemName))
            {
                if (!StationFinder.IsSmelterReady(smelter))
                {
                    AbandonWork("smelter not ready (no fuel)");
                    return;
                }

                var smelterNview = smelter.GetComponent<ZNetView>();
                if (smelterNview == null || smelterNview.GetZDO() == null)
                {
                    AbandonWork("smelter ZNetView invalid");
                    return;
                }

                m_context.SmelterProcessedAtStart = GetSmelterProcessedQueueSize(smelter);
                smelterNview.InvokeRPC("RPC_AddOre", m_context.SmelterInputItemName);
                Plugin.Log?.LogInfo(
                    $"[Work:{LogName}] Added 1x {m_context.SmelterInputItemName} to Smelter ({smelter.gameObject.name})");
                return;
            }

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

                    var conversion = station.m_conversion?.Find(c =>
                        c.m_from != null && c.m_from.gameObject.name == m_context.CookingInputItemName);
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

            if (!m_context.CookingItemAlreadyInChest && !m_context.SmelterItemAlreadyInChest && m_context.SourceContainer != null)
            {
                var outputPrefab = m_context.WorkOrder?.ItemPrefabName;
                if (!string.IsNullOrEmpty(outputPrefab))
                    ContainerScanner.TryDepositItem(m_context.SourceContainer, outputPrefab, 1);
            }

            m_context.CookingItemAlreadyInChest = false;
            m_context.SmelterItemAlreadyInChest = false;
            m_context.CookingRemovalRequested = false;
            m_context.SmelterRemovalRequested = false;

            var maxQuantity = m_context.WorkOrder?.MaxQuantity ?? 1;
            if (m_context.CraftedCount < maxQuantity)
                BeginGatheringIngredients();
            else
                FinishWork();
        }

        private void BeginTravelingToStation()
        {
            if (m_ai != null && m_context != null)
            {
                TryWalkTo(m_context.CraftStationPosition, WorkSubState.TravelingToStation, "craft station");
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

            TryWalkTo(source.Container.transform.position, WorkSubState.GatheringIngredients, "ingredient chest");
        }
    }
}