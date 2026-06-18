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
using ValheimVillages.Villages;
using Object = UnityEngine.Object;

namespace ValheimVillages.Behaviors.Tidy
{
    /// <summary>
    ///     Picks up loose item drops left lying around the village (e.g. crafting
    ///     overflow dropped when a chest was full) and stores them in a chest.
    ///     Two legs: walk to the junk, then carry it to a chest that has room.
    ///     The ground drop stays in the world until the deposit actually succeeds,
    ///     so an interruption or despawn mid-carry can never destroy the item.
    ///     Only starts when idle, so it never interrupts active crafting/farming.
    ///     Tag: "haul", Priority: 55.
    /// </summary>
    [RegisterBehavior("haul")]
    public class HaulBehavior : IBehavior
    {
        private const float ScanInterval = 8f;
        private const float MaxLegSeconds = 20f;
        private const float UnreachableCooldown = 30f;

        private readonly VillagerAI m_ai;

        // Drops we couldn't reach recently, so we don't keep re-targeting them.
        private readonly Dictionary<ZDOID, float> m_skipUntil = new();

        private Container m_targetChest;
        private ItemDrop m_targetDrop;
        private float m_legDeadline;
        private float m_lastScanTime;
        private bool m_navIssued;
        private Phase m_phase = Phase.None;

        public HaulBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "haul";
        public int Priority => 55;

        public bool WantsControl(BehaviorContext ctx)
        {
            // Finish a haul already in progress.
            if (m_phase != Phase.None) return true;

            // Only start when idle so we never interrupt active work.
            if (m_ai.CurrentState != BehaviorState.Idle) return false;
            if (m_ai.IsInBackoff) return false;

            if (Time.time - m_lastScanTime < ScanInterval) return false;
            m_lastScanTime = Time.time;

            return FindHaulTarget();
        }

        public void Update(float dt)
        {
            if (Time.time > m_legDeadline && m_phase != Phase.None)
            {
                // Stuck on this leg too long — give up on this drop for a while.
                BlacklistTarget();
                Reset();
                return;
            }

            switch (m_phase)
            {
                case Phase.ToDrop:
                    if (!IsDropValid(m_targetDrop))
                    {
                        Reset();
                        return;
                    }

                    if (!m_navIssued)
                    {
                        if (!TryWalk(m_targetDrop.transform.position, "haul: go to junk"))
                        {
                            BlacklistTarget();
                            Reset();
                            return;
                        }

                        m_navIssued = true;
                    }

                    break;

                case Phase.ToChest:
                    if (m_targetChest == null)
                    {
                        Reset();
                        return;
                    }

                    if (!m_navIssued)
                    {
                        if (!TryWalk(m_targetChest.transform.position, "haul: carry to chest"))
                        {
                            Reset();
                            return;
                        }

                        m_navIssued = true;
                    }

                    break;
            }
        }

        /// <summary>
        ///     Walk to a world target via the village-graph approach resolver
        ///     (anchor-anchored, hull-checked) — the same path crafting uses to reach
        ///     chests/stations. NavTo's generic snap fails for objects sitting on
        ///     their own collider (chests), producing an unreachable "red" path.
        /// </summary>
        private bool TryWalk(Vector3 target, string label)
        {
            if (!VillagerMovement.TryResolveApproach(
                    target, m_ai.Position, null, out var approach))
                return false;

            return m_ai.NavTo(approach, BehaviorState.Traveling, label,
                snapToApproach: false);
        }

        public void OnArrival(float dt)
        {
            switch (m_phase)
            {
                case Phase.ToDrop:
                    ArriveAtDrop();
                    break;
                case Phase.ToChest:
                    ArriveAtChest();
                    break;
                default:
                    Reset();
                    break;
            }
        }

        public string GetStatusText()
        {
            return m_phase switch
            {
                Phase.ToDrop => "Collecting stray items",
                Phase.ToChest => "Storing items in a chest",
                _ => "",
            };
        }

        /// <summary>
        ///     Find the nearest reachable-ish ground drop that some nearby chest
        ///     can accept, and the chest to store it in. Requiring an accepting
        ///     chest up front means we never start a haul we can't finish (and
        ///     never loop on junk when every chest is full).
        /// </summary>
        private bool FindHaulTarget()
        {
            var center = m_ai.HomeAnchor;
            var radius = WorkSettings.HaulScanRadius;

            var containers = ContainerScanner.FindNearbyContainers(center, radius);
            if (containers.Count == 0) return false;

            var seen = new HashSet<ItemDrop>();
            var best = (ItemDrop)null;
            var bestChest = (Container)null;
            var bestDist = float.MaxValue;

            foreach (var drop in PhysicsHelper.GetAllInRadius<ItemDrop>(center, radius))
            {
                if (drop == null || !seen.Add(drop)) continue;
                if (!IsDropValid(drop)) continue;
                if (IsBlacklisted(drop)) continue;

                var dist = Vector3.Distance(drop.transform.position, center);
                if (dist >= bestDist) continue;

                var chest = FindAcceptingChest(containers, drop.m_itemData);
                if (chest == null) continue;

                best = drop;
                bestChest = chest;
                bestDist = dist;
            }

            if (best == null) return false;

            m_targetDrop = best;
            m_targetChest = bestChest;
            BeginLeg(Phase.ToDrop);
            return true;
        }

        private void ArriveAtDrop()
        {
            if (!IsDropValid(m_targetDrop))
            {
                Reset();
                return;
            }

            // Re-confirm a chest still has room (another villager may have filled
            // the one we picked); pick a fresh one if needed.
            if (m_targetChest == null ||
                !ContainerScanner.CanAcceptItemData(m_targetChest, m_targetDrop.m_itemData))
            {
                var containers = ContainerScanner.FindNearbyContainers(
                    m_ai.HomeAnchor, WorkSettings.HaulScanRadius);
                m_targetChest = FindAcceptingChest(containers, m_targetDrop.m_itemData);
            }

            if (m_targetChest == null)
            {
                Reset();
                return;
            }

            BeginLeg(Phase.ToChest);
        }

        private void ArriveAtChest()
        {
            // The drop is the source of truth — if it's gone (player took it),
            // deposit nothing so we can't duplicate it.
            if (!IsDropValid(m_targetDrop))
            {
                Reset();
                return;
            }

            var payload = m_targetDrop.m_itemData.Clone();
            var stored = ContainerScanner.TryDepositItemData(m_targetChest, payload);

            // Fall back to any other nearby chest with room.
            if (!stored)
            {
                var containers = ContainerScanner.FindNearbyContainers(
                    m_ai.HomeAnchor, WorkSettings.HaulScanRadius);
                foreach (var c in containers)
                {
                    if (!ContainerScanner.CanAcceptItemData(c, payload)) continue;
                    stored = ContainerScanner.TryDepositItemData(c, payload.Clone());
                    if (stored) break;
                }
            }

            if (stored)
            {
                Plugin.Log?.LogInfo(
                    $"[Haul:{m_ai.NpcName}] Stored {payload.m_stack}x " +
                    $"{DropName(m_targetDrop)} in a chest.");
                DestroyDrop(m_targetDrop);
            }
            // If still not stored, every chest is full — leave the drop be.

            Reset();
        }

        private static Container FindAcceptingChest(
            List<Container> containers, ItemDrop.ItemData item)
        {
            foreach (var c in containers)
                if (ContainerScanner.CanAcceptItemData(c, item))
                    return c;
            return null;
        }

        private void BeginLeg(Phase phase)
        {
            m_phase = phase;
            m_navIssued = false;
            m_legDeadline = Time.time + MaxLegSeconds;
        }

        private static bool IsDropValid(ItemDrop drop)
        {
            if (drop == null || drop.m_itemData == null) return false;
            var nview = drop.GetComponent<ZNetView>();
            return nview != null && nview.IsValid();
        }

        private static string DropName(ItemDrop drop)
        {
            return drop.m_itemData?.m_dropPrefab != null
                ? drop.m_itemData.m_dropPrefab.name
                : drop.gameObject.name.Replace("(Clone)", "").Trim();
        }

        private static void DestroyDrop(ItemDrop drop)
        {
            if (drop == null) return;
            var nview = drop.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid())
                nview.ClaimOwnership();
            if (nview != null && nview.IsValid())
                ZNetScene.instance.Destroy(drop.gameObject);
            else
                Object.Destroy(drop.gameObject);
        }

        private static ZDOID DropId(ItemDrop drop)
        {
            var nview = drop != null ? drop.GetComponent<ZNetView>() : null;
            return nview != null && nview.GetZDO() != null
                ? nview.GetZDO().m_uid
                : ZDOID.None;
        }

        private bool IsBlacklisted(ItemDrop drop)
        {
            var id = DropId(drop);
            if (id == ZDOID.None) return false;
            if (!m_skipUntil.TryGetValue(id, out var until)) return false;
            if (Time.time >= until)
            {
                m_skipUntil.Remove(id);
                return false;
            }

            return true;
        }

        private void BlacklistTarget()
        {
            var id = DropId(m_targetDrop);
            if (id != ZDOID.None)
                m_skipUntil[id] = Time.time + UnreachableCooldown;
        }

        private void Reset()
        {
            m_phase = Phase.None;
            m_targetDrop = null;
            m_targetChest = null;
            m_navIssued = false;
            m_ai.SetState(BehaviorState.Idle);
        }

        private enum Phase
        {
            None,
            ToDrop,
            ToChest,
        }
    }
}