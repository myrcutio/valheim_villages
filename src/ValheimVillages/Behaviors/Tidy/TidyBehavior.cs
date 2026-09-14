using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Scheduling;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Work;
using ValheimVillages.Villages;

namespace ValheimVillages.Behaviors.Tidy
{
    /// <summary>
    ///     Clears finished (Done) or burnt items off a cooking station's spit. Removed items
    ///     are deposited into the nearest container if one exists; otherwise the physical drop
    ///     is simply destroyed.
    ///
    ///     <para>Never self-discovers: the station to clear arrives as a
    ///     <see cref="TaskKind.CookRescue" /> assignment from the scheduler, produced by
    ///     <see cref="Scheduling.Producers.CookRescueProducer" />. Tag: "tidy", Priority: 60.</para>
    /// </summary>
    [RegisterBehavior("tidy")]
    public class TidyBehavior : IBehavior, IDirectedBehavior
    {
        private const float ItemPickupRadius = 3f;
        private readonly VillagerAI m_ai;
        private bool m_active;
        private CookingStation m_targetStation;

        public TidyBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "tidy";
        public int Priority => 60;

        // The scheduler owns target selection — act only on an assignment, never
        // self-discover. Deliberately identical to AssignmentActive: the dispatcher holds
        // a claim while that is true, so if the two could disagree the villager would be
        // "busy" to the dispatcher and idle to the selector, and never get reassigned.
        public bool WantsControl(BehaviorContext ctx) => AssignmentActive;

        // --- IDirectedBehavior: scheduler-assigned execution ---

        public bool CanExecute(TaskKind kind) => kind == TaskKind.CookRescue;

        public bool AssignmentActive => m_active || m_targetStation != null;

        public bool BeginAssignment(CandidateTask task)
        {
            var station = FindStationNear(task.Position);
            if (station == null) return false; // nothing actionable (no Done/burnt item)
            m_targetStation = station;
            return true;
        }

        /// <summary>Nearest cooking station near a point that has a Done/burnt item to clear.</summary>
        private static CookingStation FindStationNear(Vector3 pos)
        {
            CookingStation best = null;
            var bestSq = float.MaxValue;
            foreach (var s in PhysicsHelper.GetAllInRadius<CookingStation>(pos, ItemPickupRadius * 2f))
            {
                if (s == null || !HasDoneOrBurntItems(s)) continue;
                var d = (s.transform.position - pos).sqrMagnitude;
                if (d < bestSq)
                {
                    bestSq = d;
                    best = s;
                }
            }

            return best;
        }

        public void Update(float dt)
        {
            if (m_active) return;

            if (m_targetStation == null) return;

            m_active = true;
            // Route through the single NavTo entry point so the target is
            // snapped onto a reachable agent-navmesh cell. A cooking station's
            // transform sits ON its non-walkable collider; a raw SetState target
            // there lands off-mesh and the NavMeshAgent can't reach it — the
            // villager never arrives, so OnArrival → CleanStation (which pops the
            // finished item off the spit, then picks it up) never runs. NavTo
            // also clears the prior path and re-plans the agent.
            if (!m_ai.NavTo(m_targetStation.transform.position, BehaviorState.Traveling,
                    "tidy cooking station"))
                // No reachable approach to this station — release control instead
                // of holding it forever and starving other behaviors.
                Reset();
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
            {
                // A chest holding a work order is reserved for that order's own goods; burnt
                // meat swept off a station is exactly the kind of filler that would eat the
                // slots the order's output needs. The resolver skips those chests unless the
                // sweep is carrying something the order actually uses — and when the swept item
                // IS an order's output (a finished dish left on the station), it goes to that
                // order's own chest rather than to the first box in range.
                var target = WorkOrderChestPolicy.ResolveDepositChest(
                    containers, itemName, null, 1, station.transform.position);
                if (target == null) continue;
                ContainerScanner.TryDepositItem(target, itemName, 1);
            }
        }

        private void Reset()
        {
            m_active = false;
            m_targetStation = null;
            m_ai.SetState(BehaviorState.Idle);
        }
    }
}