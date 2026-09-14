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
        /// <param name="targetAlreadyResolved">
        ///     True when <paramref name="target" /> is ALREADY a resolved, walkable approach
        ///     point and must be walked to as-is.
        ///
        ///     <para>Needed because the station resolver below cannot resolve an approach for a
        ///     plain PIECE: a beehive sits inside its own collider and PointToRegionId comes
        ///     back unresolved at it, so re-resolving a hive approach that the scan had already
        ///     found via the region lookup grid threw the good point away and abandoned the
        ///     work. The villager then re-scanned, matched the same order, and abandoned again
        ///     — a silent loop. Resolve once, at selection time, then walk it.</para>
        /// </param>
        private bool TryWalkTo(Vector3 target, WorkSubState substate, string targetDescription,
            bool targetAlreadyResolved = false)
        {
            if (m_ai == null) return false;

            var approach = target;
            // Anchor the village on the BED, not m_ai.Position: if the agent has
            // been bumped off the graph, its current position may resolve to no
            // village (or the wrong one), but its home village is always known.
            // Current position is still the path start (2nd arg).
            if (!targetAlreadyResolved &&
                !VillagerMovement.TryResolveApproach(
                    target, m_ai.Position, null, out approach))
            {
                AbandonWork($"no HNA-valid approach to {targetDescription} @ ({target.x:F1},{target.y:F1},{target.z:F1})");
                return false;
            }

            SubState = substate;
            // We already resolved a station-aware approach above (anchor-anchored
            // village + hull check), so skip NavTo's generic snap and route the
            // state change + path/agent reset through the single entry point.
            return m_ai.NavTo(approach, BehaviorState.Working, targetDescription,
                snapToApproach: false);
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
            SetWorkNote($"abandon[{SubState}]: {reason} @ t={Time.time:F0}");
            // Log, don't just stash a note. A silent abandon looks exactly like a workflow
            // that never started: the villager re-scans, matches the same order, abandons
            // again, and the only visible symptom is "starting work on X" repeating forever
            // with no reason anywhere. Cost a full diagnosis round-trip on the Honey order.
            Plugin.Log?.LogWarning($"[Work:{LogName}] abandon[{SubState}]: {reason}");
            // Roll back any items we removed from chests but didn't manage
            // to commit to a station — without this, a stall mid-walk
            // drains source chests of items that vanish (the workflow
            // restarts, pulls fresh items, repeats). User-observed:
            // Farmer cycling raw meat from a chest into the void during
            // pathing failures. See WorkOrderContext.HeldItems doc.
            if (m_context != null && m_context.HeldItems.Count > 0)
                RollbackHeldItems(m_context.HeldItems, reason);

            m_context = null;
            SubState = WorkSubState.Idle;
            if (m_ai != null)
                m_ai.SetState(BehaviorState.Idle, (VillagerWaypoint)null);
        }

        /// <summary>
        ///     Restore items from <see cref="WorkOrderContext.HeldItems"/> to
        ///     their source containers. If a source container is destroyed
        ///     or full, drop the items at the villager's feet — better that
        ///     the player can pick them up than they vanish into context-
        ///     reset limbo. Per the no-silent-fallbacks rule: log every
        ///     branch (success / drop-on-ground / source-destroyed) so a
        ///     leaked transaction is visible in the log.
        /// </summary>
        private void RollbackHeldItems(List<HeldItem> held, string reason)
        {
            foreach (var item in held)
            {
                if (item == null || string.IsNullOrEmpty(item.PrefabName) || item.Amount <= 0)
                    continue;

                var deposited = item.SourceContainer != null &&
                                ContainerScanner.TryDepositItem(
                                    item.SourceContainer, item.PrefabName, item.Amount);
                if (deposited)
                {
                    Plugin.Log?.LogInfo(
                        $"[Work:{LogName}] Rollback ({reason}): returned {item.Amount}x " +
                        $"{item.PrefabName} to source container.");
                    continue;
                }

                // Source container full or destroyed — drop at villager's
                // feet so the player can recover the items. ItemDrop.DropItem
                // would be the ideal API but we don't have a reliable
                // reference handy here; using ItemDrop spawn via prefab.
                var pos = m_ai != null && m_ai.transform != null
                    ? m_ai.transform.position
                    : Vector3.zero;
                var prefab = ZNetScene.instance?.GetPrefab(item.PrefabName);
                if (prefab == null)
                {
                    Plugin.Log?.LogError(
                        $"[Work:{LogName}] Rollback ({reason}): LOST {item.Amount}x " +
                        $"{item.PrefabName} — prefab not found and source unavailable.");
                    continue;
                }

                for (var i = 0; i < item.Amount; i++)
                {
                    var dropPos = pos + new Vector3(
                        UnityEngine.Random.Range(-0.3f, 0.3f),
                        0.3f,
                        UnityEngine.Random.Range(-0.3f, 0.3f));
                    Object.Instantiate(prefab, dropPos, Quaternion.identity);
                }
                Plugin.Log?.LogWarning(
                    $"[Work:{LogName}] Rollback ({reason}): source container unavailable for " +
                    $"{item.Amount}x {item.PrefabName}; dropped at villager position " +
                    $"({pos.x:F1},{pos.y:F1},{pos.z:F1}).");
            }

            held.Clear();
        }

        /// <summary>
        ///     Public entry point for AbandonWork so external callers
        ///     (e.g. IPathUnreachableHandler dispatch on the adapter) can
        ///     abort the current work order without exposing the private
        ///     implementation surface.
        /// </summary>
        public void AbandonWorkPublic(string reason) => AbandonWork(reason);

        /// <summary>
        ///     Re-diagnose a cooking station that has gone cold on arrival and, if the fuel it
        ///     wants is in reach, start the fuelling leg. Returns false when there is genuinely
        ///     nothing the villager can do (no fuel need identified, or no fuel in any chest),
        ///     in which case the caller abandons as before.
        /// </summary>
        private bool TryDivertToFueling(CookingStation station)
        {
            if (m_context == null || m_ai == null) return false;

            if (!StationFuelHelper.DiagnoseFuelNeed(station, out var need)) return false;

            var containers = ContainerScanner.FindVillageContainers(
                m_ai.HomeAnchor, Settings.WorkSettings.ChestScanRadius);
            if (!StationFuelHelper.FindFuelInContainers(containers, need.FuelItemPrefab, out var fc))
                return false;

            m_context.FuelRequirement = need;
            m_context.FuelContainer = fc;
            // Diverting mid-cycle — the fuelling leg must return to the station rather than
            // re-running the gather (which would pull a second set of ingredients).
            m_context.ResumeAtStationAfterFueling = true;

            Plugin.Log?.LogInfo(
                $"[Work:{LogName}] Cooking station went cold on arrival — fetching " +
                $"{need.FuelItemPrefab} to relight instead of abandoning.");

            BeginFueling();
            return true;
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
            // Track this pickup as in-transit until the RPC_AddFuel
            // commits it. If we stall en route, AbandonWork's rollback
            // returns this to the source chest instead of letting it
            // vanish.
            m_context.HeldItems.Add(new HeldItem
            {
                SourceContainer = container,
                PrefabName = fuel.FuelItemPrefab,
                Amount = 1,
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
            // Fuel was successfully committed to the station — clear the
            // matching HeldItem entry so AbandonWork down the line doesn't
            // try to roll back already-committed items.
            ClearHeldItem(fuel.FuelItemPrefab, 1);

            if (m_context.ResumeAtStationAfterFueling)
            {
                m_context.ResumeAtStationAfterFueling = false;
                BeginTravelingToStation();
                return;
            }

            BeginGatheringIngredients();
        }

        /// <summary>
        ///     Remove an entry from <see cref="WorkOrderContext.HeldItems"/>
        ///     after a successful commit to a station. Matches by prefab
        ///     name + amount; if multiple matching entries exist (e.g. two
        ///     pickups of the same prefab), removes the first. Caller
        ///     should pass the exact prefab + amount that was committed.
        /// </summary>
        private void ClearHeldItem(string prefabName, int amount)
        {
            if (m_context == null || string.IsNullOrEmpty(prefabName)) return;
            for (var i = 0; i < m_context.HeldItems.Count; i++)
            {
                var h = m_context.HeldItems[i];
                if (h == null || h.PrefabName != prefabName || h.Amount != amount) continue;
                m_context.HeldItems.RemoveAt(i);
                return;
            }
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
                // Fire died with the food still on the station. Fetch fuel and come back
                // rather than abandoning the cook — the item stays on the station meanwhile.
                if (TryDivertToFueling(station)) return true;

                AbandonWork("cooking station fire went out and no fuel available");
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

        /// <summary>Seconds to wait for a hive's extracted honey to hit the ground.</summary>
        private const float BeehiveHarvestTimeoutSec = 10f;

        /// <summary>
        ///     Beekeeping leg of the Crafting sub-state. A hive has no conversion to wait on —
        ///     the honey is already in it — so this extracts once and then sweeps the ground
        ///     for the drops, which is the same thing the smelter/kiln poll does because those
        ///     stations also spit their output onto the floor.
        /// </summary>
        private bool TryPollBeehive()
        {
            var hive = m_context?.BeehiveRef;
            if (hive == null) return false;

            if (!m_context.BeehiveExtractRequested)
            {
                var level = BeehiveHelper.GetHoneyLevel(hive);
                if (level <= 0)
                {
                    // Drained between the scan offering it and the villager walking over —
                    // another villager (or the player) got there first.
                    AbandonWork("beehive empty on arrival");
                    return true;
                }

                if (!BeehiveHelper.Extract(hive))
                {
                    AbandonWork("beehive ZNetView invalid");
                    return true;
                }

                m_context.BeehiveExtractRequested = true;
                Plugin.Log?.LogInfo(
                    $"[Work:{LogName}] Extracted {level}x Honey from beehive " +
                    $"({hive.gameObject.name})");
                return true; // drops arrive with the RPC; sweep on the next poll
            }

            var collected = CollectGroundOutput(BeehiveHelper.OutputPoint(hive), OutputBatchPerTrip);
            if (collected > 0)
            {
                m_context.CraftedCount += collected;
                BeginReturningToChest();
                return true;
            }

            // Bounded wait. Unlike a smelter there is nothing still cooking, so if the honey
            // hasn't landed by now it isn't coming (extract lost, drops despawned, someone
            // else swept them) and waiting forever would pin the villager at the hive.
            if (Time.time - m_context.CraftStartTime > BeehiveHarvestTimeoutSec)
                AbandonWork("no honey appeared after extracting");

            return true;
        }

        private bool TryPollSmelter()
        {
            var smelter = m_context?.SmelterRef;
            if (smelter == null) return false;
            if (m_context.SmelterRemovalRequested) return true;

            // Fuel-burning smelters (regular smelter, blast furnace) must still
            // have fuel to finish the current product; the charcoal kiln
            // (m_fuelItem == null) consumes its input as fuel and never runs dry
            // that way.
            if (smelter.m_fuelItem != null && !StationFinder.IsSmelterReady(smelter))
            {
                AbandonWork("smelter fuel ran out before output");
                return true;
            }

            // BOTH the charcoal kiln AND the regular smelter spawn their finished
            // product as ground item-drops at the output point — there's no
            // reliable "extract" step (the old processed-queue trigger missed
            // them, so bars/coal piled up uncollected while the villager waited
            // for a queue that never advanced). So while we wait at the station,
            // tidy the output point: collect a batch of the ground-spawned output
            // (bounded by chest room) and carry it back. CollectGroundOutput
            // returns 0 until the station actually spits something out, so this
            // naturally polls.
            var smelterOutputPos = smelter.m_outputPoint != null
                ? smelter.m_outputPoint.position
                : smelter.transform.position;
            var collected = CollectGroundOutput(smelterOutputPos, OutputBatchPerTrip);
            if (collected > 0)
            {
                m_context.SmelterRemovalRequested = true;
                m_context.CraftedCount += collected;
                BeginReturningToChest();
            }

            return true; // keep waiting/polling until output appears
        }

        /// <summary>Max output items the villager will carry back per return trip.</summary>
        private const int OutputBatchPerTrip = 5;

        /// <summary>
        ///     Collect up to <paramref name="maxItems" /> ground-spawned output
        ///     items of the work order's output prefab from around the station's
        ///     output point, bounded by how many the destination chest can still
        ///     hold. Destroys the collected drops and records them as a single
        ///     carried <see cref="HeldItem" /> (so an abandon mid-return rolls
        ///     them back to the chest); the actual deposit happens on arrival in
        ///     <see cref="OnArrivedAtOutputChest" />. Returns the count collected.
        /// </summary>
        private int CollectGroundOutput(Vector3 outputPos, int maxItems)
        {
            const float searchRadius = 7f;

            var outputPrefab = m_context.WorkOrder?.ItemPrefabName;
            if (string.IsNullOrEmpty(outputPrefab)) return 0;

            var allDrops = PhysicsHelper.GetAllInRadius<ItemDrop>(outputPos, searchRadius);
            var matches = new List<(ItemDrop drop, float dist)>();
            foreach (var drop in allDrops)
            {
                if (drop == null || drop.m_itemData == null) continue;
                var dropPrefab = drop.m_itemData.m_dropPrefab?.name
                                 ?? drop.gameObject.name.Replace("(Clone)", "").Trim();
                if (dropPrefab != outputPrefab) continue;
                matches.Add((drop, Vector3.Distance(drop.transform.position, outputPos)));
            }

            if (matches.Count == 0) return 0;
            matches.Sort((a, b) => a.dist.CompareTo(b.dist));

            // Cap by batch size and by remaining chest capacity — don't carry
            // more than will fit, so we never strand a deposit on arrival.
            var want = Mathf.Min(maxItems, matches.Count);
            var room = want;
            if (m_context.SourceContainer == null)
                room = 0;
            else
                while (room > 0 && !ContainerScanner.CanAcceptItem(
                           m_context.SourceContainer, outputPrefab, room))
                    room--;
            if (room <= 0) return 0;

            var collected = 0;
            for (var i = 0; i < matches.Count && collected < room; i++)
            {
                var drop = matches[i].drop;
                var dropNview = drop.GetComponent<ZNetView>();
                if (dropNview != null && dropNview.GetZDO() != null)
                    ZNetScene.instance.Destroy(drop.gameObject);
                else
                    Object.Destroy(drop.gameObject);
                collected++;
            }

            if (collected <= 0) return 0;

            // Carry the whole batch as one tracked entry — deposit on arrival.
            m_context.HeldItems.Add(new HeldItem
            {
                SourceContainer = m_context.SourceContainer,
                PrefabName = outputPrefab,
                Amount = collected,
            });
            return collected;
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
                // Plain crafting station (workbench): there is no station to load the inputs
                // into, so the craft itself consumes them. Clear the in-transit entries here,
                // at the moment the product exists.
                ConsumeIngredientHeldItems();
                m_context.CraftedCount++;
            }

            BeginReturningToChest();
        }

        /// <summary>
        ///     Mark this craft cycle's ingredient pickups as consumed.
        ///
        ///     <para>The cooking and smelter paths call <see cref="ClearHeldItem" /> when their
        ///     RPC commits the input to a station. A plain crafting station has no such commit,
        ///     so nothing cleared them and they survived to <see cref="FinishWork" />, which
        ///     rolled them back into the chest — the villager kept the output AND returned the
        ///     inputs, crafting for free. (Observed: a carpenter making 5 torches ended with 10
        ///     uncommitted Wood/Resin entries, all refunded.)</para>
        /// </summary>
        private void ConsumeIngredientHeldItems()
        {
            if (m_context?.IngredientSources == null) return;

            foreach (var src in m_context.IngredientSources)
            {
                if (src == null || string.IsNullOrEmpty(src.PrefabName)) continue;
                ClearHeldItem(src.PrefabName, src.Amount);
            }
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
            // Arm a "linger near station" window so Explore doesn't immediately
            // drag the villager off to the village fire. Smelters/cooking
            // stations need time to process; without this, the villager
            // walks to fire and 30s later walks right back. Polish for #29.
            if (m_ai != null && m_context != null && m_context.CraftStationPosition != Vector3.zero)
            {
                m_ai.LingerUntilTime = Time.time + Settings.VillagerSettings.PostWorkLingerSec;
                m_ai.LingerAtPos = m_context.CraftStationPosition;
            }

            // Defensive: any HeldItems left at the end of a "successful"
            // work cycle is a workflow bug — items pulled from a chest
            // but never committed at a station. Roll them back rather
            // than silently leaking. With every commit site calling
            // ClearHeldItem, this list should be empty here; if it isn't,
            // the warning + rollback both flag the regression and
            // preserve the items.
            if (m_context != null && m_context.HeldItems.Count > 0)
            {
                Plugin.Log?.LogWarning(
                    $"[Work:{LogName}] FinishWork with {m_context.HeldItems.Count} uncommitted " +
                    "HeldItems — workflow regression, rolling back as if AbandonWork.");
                RollbackHeldItems(m_context.HeldItems, "finish_with_uncommitted");
            }

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
            // Only record the held item if the withdrawal actually took the full amount.
            // If the ingredient ran out since the scan, RemoveIngredients removes nothing
            // and returns false — abort rather than fabricate a held item we never got
            // (which would later be loaded onto the station, manifesting output from
            // ingredients that aren't there). AbandonWork rolls back any earlier pickups.
            if (!ContainerScanner.RemoveIngredients(singleSource))
            {
                AbandonWork($"ingredient {source.PrefabName} no longer available");
                return;
            }

            // Track this pickup as in-transit (see fuel branch + AbandonWork
            // rollback for the why).
            m_context.HeldItems.Add(new HeldItem
            {
                SourceContainer = source.Container,
                PrefabName = source.PrefabName,
                Amount = source.Amount,
            });

            m_context.CurrentIngredientIndex++;
            WalkToNextIngredientChest();
        }

        private void OnArrivedAtStation()
        {
            if (m_context == null) return;

            m_context.CraftStartTime = Time.time;
            SubState = WorkSubState.Crafting;
            // Stop here and drop the movement waypoint. We've arrived; the
            // crafting wait is stationary (poll the smelter/cooking station).
            // Without this the per-frame mover keeps re-detecting "arrived" at
            // the station waypoint and re-firing OnArrival, which lands in the
            // HandleWorkArrival default ("unexpected arrival in Crafting") and
            // abandons the order — dropping the just-loaded ore/fuel on the
            // ground. Stay in Working (so UpdateWorkAI keeps polling) but with
            // no waypoint.
            m_ai?.ClearWaypoint();
            SetWorkNote($"crafting @ station t={Time.time:F0}");

            // Each trip to the hive is its own extract.
            m_context.BeehiveExtractRequested = false;

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
                // The trailing bool is the engine's "cheated" flag (added by the 2026-09-09
                // update, signature: RPC_AddOre(long sender, string name, bool cheated)); it is
                // stored on the slot to mark cheat-spawned input. The villager removed this ore
                // from a chest for real, so it is false. Omitting it throws EndOfStreamException
                // inside ZRpc.Deserialize and the work order stalls forever at the station.
                smelterNview.InvokeRPC("RPC_AddOre", m_context.SmelterInputItemName, false);
                Plugin.Log?.LogInfo(
                    $"[Work:{LogName}] Added 1x {m_context.SmelterInputItemName} to Smelter ({smelter.gameObject.name})");
                // Ore committed — clear the matching in-transit entry.
                ClearHeldItem(m_context.SmelterInputItemName, 1);
                return;
            }

            var station = m_context.CookingStationRef;
            if (station != null && !string.IsNullOrEmpty(m_context.CookingInputItemName))
            {
                if (!StationFinder.IsCookingStationReady(station))
                {
                    // The fire was lit when the scan picked this station, but has since gone
                    // out — fires burn down while the villager walks over. Giving up here is
                    // what made a villager stand at a cold cooking station doing nothing, then
                    // re-scan and repeat. Divert to the fuelling leg instead: it is the same
                    // work the scan would have queued had the fire already been out on arrival.
                    if (TryDivertToFueling(station)) return;

                    AbandonWork("cooking station not ready and no fuel available");
                    return;
                }

                // Mirror CookingStation.OnUseItem's own gate (fire + free slot) rather than the
                // looser progress check. RPC_AddItem does no fire check of its own — it is the
                // trusted inner call the engine's gated path makes — so this is what stops a
                // villager doing what a player is refused: loading food onto a cold station.
                if (!StationFinder.CanAcceptItem(station))
                {
                    if (!StationFinder.HasFreeSlot(station))
                    {
                        AbandonWork("cooking station full");
                        return;
                    }

                    if (TryDivertToFueling(station)) return;
                    AbandonWork("cooking station has no fire and no fuel available");
                    return;
                }

                var nview = station.GetComponent<ZNetView>();
                if (nview != null && nview.GetZDO() != null)
                {
                    // Trailing "cheated" bool, as for RPC_AddOre above:
                    // RPC_AddItem(long sender, string itemName, bool cheated).
                    nview.InvokeRPC("RPC_AddItem", m_context.CookingInputItemName, false);
                    // Cooking input committed — clear the matching in-transit entry.
                    ClearHeldItem(m_context.CookingInputItemName, 1);

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
                {
                    // Deposit the WHOLE carried batch now that we've actually
                    // arrived at the chest. The carried amount is the sum of the
                    // HeldItem entries for this prefab (CollectGroundOutput adds
                    // one batch entry); fall back to 1 for the legacy path where
                    // nothing was tracked. Clear the entries only on a successful
                    // deposit so a full chest leaves them to roll back.
                    var carried = 0;
                    foreach (var h in m_context.HeldItems)
                        if (h != null && h.PrefabName == outputPrefab)
                            carried += h.Amount;
                    if (carried <= 0) carried = 1;
                    if (ContainerScanner.TryDepositItem(m_context.SourceContainer, outputPrefab, carried))
                        m_context.HeldItems.RemoveAll(h => h != null && h.PrefabName == outputPrefab);
                }
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
                // A hive's CraftStationPosition is already a lookup-grid approach resolved at
                // scan time (BeehiveHelper.TryResolveHiveApproach); re-resolving it through the
                // station resolver fails and abandons the work. See TryWalkTo.
                var preResolved = m_context.BeehiveRef != null;
                TryWalkTo(m_context.CraftStationPosition, WorkSubState.TravelingToStation,
                    "craft station", preResolved);
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