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
using ValheimVillages.Villager.AI.Pathfinding;
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

        /// <summary>Speed (m/s) above which a drop counts as still falling/rolling — not a target yet.</summary>
        private const float MovingSpeed = 0.5f;

        /// <summary>How close to the chest a villager must be to put the item in it.</summary>
        private const float ChestReach = 4f;

        /// <summary>
        ///     Leg deadline = path length at this pessimistic speed + <see cref="LegSlackSeconds" />.
        ///     A flat 20 s timed out villagers a few metres short on multi-level routes (upstairs).
        /// </summary>
        private const float PlanningSpeed = 1.5f;

        private const float LegSlackSeconds = 10f;

        /// <summary>
        ///     Identical drops within this distance of the chosen one are carried with it as a
        ///     single stack (one trip, one reservation) — a spilled pile shouldn't cost one
        ///     round trip per item.
        /// </summary>
        private const float GroupRadius = 2.5f;

        /// <summary>How far from where it stands a villager can gather group members at pickup.</summary>
        private const float PickupReach = 3.5f;

        /// <summary>
        ///     Which group reservation each drop belongs to. A group is ONE TaskBoard claim
        ///     (keyed by its first drop); this maps every member to that key so other villagers
        ///     see the whole pile as taken.
        /// </summary>
        private static readonly Dictionary<ZDOID, string> s_memberClaim = new();

        [RegisterCleanup]
        public static void ClearStatic()
        {
            s_memberClaim.Clear();
        }

        private readonly VillagerAI m_ai;

        // Drops we couldn't reach recently, so we don't keep re-targeting them.
        private readonly Dictionary<ZDOID, float> m_skipUntil = new();

        private Container m_targetChest;
        private ItemDrop m_targetDrop;

        /// <summary>The item in the villager's hands (mirrored on the NPC ZDO via <see cref="HaulCarry" />).</summary>
        private ItemDrop.ItemData m_carried;

        /// <summary>TaskBoard reservation on the target drop, so two villagers never chase one item.</summary>
        private string m_claimId;

        /// <summary>The drops this trip will pick up together (first = the one walked to).</summary>
        private readonly List<ItemDrop> m_group = new();

        /// <summary>ZDO ids registered in <see cref="s_memberClaim" /> for this trip (captured at
        /// claim time — a picked-up drop no longer has a ZDO to ask).</summary>
        private readonly List<ZDOID> m_groupIds = new();

        /// <summary>Set once the NPC ZDO has been checked for an item carried across a reload/unload.</summary>
        private bool m_carryRestoreChecked;
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
            // An item still recorded as carried from before a hot reload / zone unload belongs
            // on the ground, not in a hand that no longer knows it holds it. Owner only.
            if (!m_carryRestoreChecked)
            {
                m_carryRestoreChecked = true;
                var zdo = NpcZdo();
                if (zdo != null && zdo.IsOwner())
                    HaulCarry.DropIfCarrying(zdo, m_ai.Position);
            }

            // Finish a haul already in progress.
            if (m_phase != Phase.None) return true;

            // Only start when idle — or on idle TRAVEL (a wander stroll, a walk to a relax spot),
            // which is leisure, not work. Requiring strict Idle meant a wandering villager only
            // looked for litter in the few seconds between strolls. Never interrupts real work.
            if (m_ai.CurrentState != BehaviorState.Idle && !m_ai.IsCasualTravel)
                return Gate("busy", m_ai.CurrentState);
            if (m_ai.IsInBackoff) return Gate("stuck_backoff", null);

            if (Time.time - m_lastScanTime < ScanInterval)
                return Gate("scan_interval", Time.time - m_lastScanTime);
            m_lastScanTime = Time.time;

            return FindHaulTarget();
        }

        /// <summary>
        ///     Telemetry: why WantsControl declined. Throttled per villager AND per reason, so
        ///     every distinct reason still surfaces while a hot gate (scan_interval) can't flood.
        /// </summary>
        private bool Gate(string reason, object detail)
        {
            if (!LogSettings.VerboseHaul) return false;
            DebugLog.ThrottledWindow(
                $"haul_gate:{m_ai.NpcName}:{reason}", System.TimeSpan.FromSeconds(10f),
                "Haul", "gate_declined",
                ("villager", m_ai.NpcName), ("reason", reason), ("detail", detail),
                ("active", m_ai.ActiveBehavior?.Tag ?? "none"));
            return false;
        }

        public void Update(float dt)
        {
            // Keep the reservation alive while we're still heading for the drop (the TTL is
            // shorter than a long walk).
            if (m_claimId != null)
                TaskBoard.Claim(m_claimId, m_ai.UniqueId, Time.time);

            if (Time.time > m_legDeadline && m_phase != Phase.None)
            {
                // Stuck on this leg too long — give up on this drop for a while.
                LogLeg("leg_timeout");
                BlacklistTarget();
                Reset();
                return;
            }

            switch (m_phase)
            {
                case Phase.ToDrop:
                    if (!IsDropValid(m_targetDrop))
                    {
                        LogLeg("drop_gone");
                        Reset();
                        return;
                    }

                    if (!m_navIssued)
                    {
                        if (!TryWalk(m_targetDrop.transform.position, "haul: go to junk"))
                        {
                            LogLeg("walk_to_drop_failed");
                            BlacklistTarget();
                            Reset();
                            return;
                        }

                        LogLeg("walking_to_drop");
                        m_navIssued = true;
                    }

                    break;

                case Phase.ToChest:
                    if (m_targetChest == null)
                    {
                        LogLeg("chest_gone");
                        Reset();
                        return;
                    }

                    if (!m_navIssued)
                    {
                        if (!TryWalk(m_targetChest.transform.position, "haul: carry to chest"))
                        {
                            LogLeg("walk_to_chest_failed");
                            Reset();
                            return;
                        }

                        LogLeg("walking_to_chest");
                        m_navIssued = true;
                    }

                    break;
            }
        }

        /// <summary>Telemetry: a haul leg changed or ended, with the drop/chest it concerns.</summary>
        private void LogLeg(string what)
        {
            if (!LogSettings.VerboseHaul) return;
            var dp = m_targetDrop != null ? m_targetDrop.transform.position : Vector3.zero;
            DebugLog.Event("Haul", "leg",
                ("villager", m_ai.NpcName), ("what", what), ("phase", m_phase),
                ("drop", m_targetDrop != null ? DropName(m_targetDrop) : "null"), ("drop_pos", dp),
                ("chest_pos", m_targetChest != null ? m_targetChest.transform.position : Vector3.zero),
                ("villager_pos", m_ai.Position), ("state", m_ai.CurrentState));
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

            if (!m_ai.NavTo(approach, BehaviorState.Traveling, label, snapToApproach: false))
                return false;

            // Size this leg's deadline to the route actually being walked.
            var legSeconds = EstimateLegSeconds(m_ai.Position, approach);
            m_legDeadline = Time.time + legSeconds;
            return true;
        }

        /// <summary>
        ///     Seconds a leg may take: route length at <see cref="PlanningSpeed" /> plus slack,
        ///     never less than the old flat <see cref="MaxLegSeconds" />. Falls back to that
        ///     flat value when no complete path can be measured (the walk itself will then
        ///     decide whether it's reachable).
        /// </summary>
        private static float EstimateLegSeconds(Vector3 from, Vector3 to)
        {
            var filter = new UnityEngine.AI.NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = UnityEngine.AI.NavMesh.AllAreas,
            };
            var path = new UnityEngine.AI.NavMeshPath();
            if (!UnityEngine.AI.NavMesh.CalculatePath(from, to, filter, path) ||
                path.status != UnityEngine.AI.NavMeshPathStatus.PathComplete)
                return MaxLegSeconds;

            var length = 0f;
            var corners = path.corners;
            for (var i = 1; i < corners.Length; i++)
                length += Vector3.Distance(corners[i - 1], corners[i]);

            return Mathf.Max(MaxLegSeconds, length / PlanningSpeed + LegSlackSeconds);
        }

        public void OnArrival(float dt)
        {
            // Telemetry: where the villager actually is when an arrival fires, relative to
            // the drop and the chest — an arrival "at the chest" metres away from it is a
            // remote deposit.
            if (LogSettings.VerboseHaul)
                DebugLog.Event("Haul", "arrival",
                    ("villager", m_ai.NpcName), ("phase", m_phase), ("nav_issued", m_navIssued),
                    ("state", m_ai.CurrentState),
                    ("dist_to_drop", m_targetDrop != null
                        ? Vector3.Distance(m_ai.Position, m_targetDrop.transform.position) : -1f),
                    ("dist_to_chest", m_targetChest != null
                        ? Vector3.Distance(m_ai.Position, m_targetChest.transform.position) : -1f));

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
        ///     Find the nearest ground drop that the villager can actually reach and some
        ///     nearby chest can accept, and the chest to store it in. Requiring an accepting
        ///     chest up front means we never start a haul we can't finish (and never loop on
        ///     junk when every chest is full).
        ///
        ///     <para>Candidates are tried nearest-first and the first REACHABLE one wins. The
        ///     old scan took only the single nearest and discovered it was unreachable when the
        ///     walk failed — so one bad drop (underground, on a roof, still falling) was chosen
        ///     every scan and starved every other drop in the village. Unreachable ones are now
        ///     skipped here and set aside for <see cref="UnreachableCooldown" />.</para>
        /// </summary>
        private bool FindHaulTarget()
        {
            var center = m_ai.HomeAnchor;
            var radius = WorkSettings.HaulScanRadius;

            var containers = ContainerScanner.FindNearbyContainers(center, radius);
            var colliderHits = PhysicsHelper.GetAllInRadius<ItemDrop>(center, radius);
            if (containers.Count == 0)
            {
                LogScan(center, radius, 0, colliderHits.Count, null, "no_containers");
                return false;
            }

            var seen = new HashSet<ItemDrop>();
            // Per-drop verdicts are telemetry (vv_log haul). Null when off, and every write is
            // verdicts?.Add(...), which skips building the string at all.
            var verbose = LogSettings.VerboseHaul;
            var verdicts = verbose ? new List<string>() : null;
            var candidates = new List<(ItemDrop drop, Container chest, float dist, string tagged)>();

            foreach (var drop in colliderHits)
            {
                if (drop == null || !seen.Add(drop)) continue;
                var p = drop.transform.position;
                var tagged = verbose
                    ? $"{(drop.m_itemData != null ? DropName(drop) : drop.gameObject.name)}" +
                      $"@({p.x:F1},{p.y:F1},{p.z:F1}){DropFacts(drop)}"
                    : null;

                if (!IsDropValid(drop))
                {
                    verdicts?.Add(tagged + ":" + InvalidReason(drop));
                    continue;
                }

                if (IsBlacklisted(drop))
                {
                    verdicts?.Add(tagged + ":blacklisted");
                    continue;
                }

                // Still falling or rolling: where it will end up isn't known yet, and the
                // approach resolved against a mid-air position fails. Look again next scan.
                var body = drop.GetComponent<Rigidbody>();
                if (body != null && !body.isKinematic && body.linearVelocity.sqrMagnitude > MovingSpeed * MovingSpeed)
                {
                    verdicts?.Add(tagged + ":moving");
                    continue;
                }

                var chest = WorkOrderChestPolicy.ResolveDepositChest(
                    containers, drop.m_itemData, drop.transform.position);
                if (chest == null)
                {
                    verdicts?.Add(tagged + ":no_accepting_chest");
                    continue;
                }

                candidates.Add((drop, chest, Vector3.Distance(drop.transform.position, center), tagged));
            }

            // Nearest first; take the first one the villager can actually stand beside.
            candidates.Sort((a, b) => a.dist.CompareTo(b.dist));
            ItemDrop best = null;
            Container bestChest = null;
            foreach (var c in candidates)
            {
                if (best != null)
                {
                    verdicts?.Add(c.tagged + $":not_needed({c.dist:F1}m)");
                    continue;
                }

                if (TaskBoard.IsClaimedByOther(ClaimKeyFor(c.drop), m_ai.UniqueId, Time.time))
                {
                    verdicts?.Add(c.tagged + ":claimed_by_other");
                    continue;
                }

                if (!VillagerMovement.TryResolveApproach(
                        c.drop.transform.position, m_ai.Position, null, out _))
                {
                    Blacklist(c.drop);
                    verdicts?.Add(c.tagged + $":unreachable({c.dist:F1}m)");
                    continue;
                }

                verdicts?.Add(c.tagged + $":chosen({c.dist:F1}m)");
                best = c.drop;
                bestChest = c.chest;
            }

            // Gather identical drops lying beside the chosen one into the same trip.
            m_group.Clear();
            if (best != null)
            {
                m_group.Add(best);
                var max = Mathf.Max(1, best.m_itemData.m_shared.m_maxStackSize);
                var total = best.m_itemData.m_stack;
                var near = new List<(ItemDrop drop, float d)>();
                foreach (var c in candidates)
                {
                    if (c.drop == best || !StacksWith(best, c.drop)) continue;
                    var d = Vector3.Distance(c.drop.transform.position, best.transform.position);
                    if (d > GroupRadius) continue;
                    if (TaskBoard.IsClaimedByOther(ClaimKeyFor(c.drop), m_ai.UniqueId, Time.time)) continue;
                    near.Add((c.drop, d));
                }

                near.Sort((a, b) => a.d.CompareTo(b.d));
                foreach (var n in near)
                {
                    if (total >= max) break;
                    m_group.Add(n.drop);
                    total += n.drop.m_itemData.m_stack;
                }

                if (m_group.Count > 1)
                    verdicts?.Add($"group={m_group.Count}x{DropName(best)} stack={Mathf.Min(total, max)}/{max}");
            }

            LogScan(center, radius, containers.Count, colliderHits.Count, verdicts,
                best != null ? "chose " + DropName(best) : "nothing_to_haul");
            if (best == null) return false;

            m_targetDrop = best;
            m_targetChest = bestChest;
            m_claimId = ClaimId(best);
            TaskBoard.Claim(m_claimId, m_ai.UniqueId, Time.time);
            foreach (var member in m_group)
            {
                var id = DropId(member);
                if (id == ZDOID.None) continue;
                s_memberClaim[id] = m_claimId;
                m_groupIds.Add(id);
            }

            BeginLeg(Phase.ToDrop);
            return true;
        }

        /// <summary>Telemetry: one line per scan with every drop the scan saw and its verdict.</summary>
        private void LogScan(Vector3 center, float radius, int chests, int colliderHits,
            List<string> verdicts, string outcome)
        {
            if (!LogSettings.VerboseHaul) return;
            DebugLog.Event("Haul", "scan",
                ("villager", m_ai.NpcName), ("center", center), ("radius", radius),
                ("chests", chests), ("item_colliders", colliderHits),
                ("drops", verdicts == null ? "" : string.Join(";", verdicts)),
                ("outcome", outcome));
        }

        /// <summary>
        ///     Telemetry: the network facts of one drop instance — who owns its ZDO, whether this
        ///     peer is that owner, where the ZDO says it is (vs the transform), whether THIS
        ///     GameObject is the instance ZNetScene has registered for the ZDO, and whether its
        ///     body is kinematic.
        /// </summary>
        private static string DropFacts(ItemDrop drop)
        {
            var nview = drop.GetComponent<ZNetView>();
            var zdo = nview != null ? nview.GetZDO() : null;
            if (zdo == null) return "[zdo=null]";
            var registered = ZNetScene.instance != null && ZNetScene.instance.FindInstance(zdo) == nview;
            var body = drop.GetComponent<Rigidbody>();
            return $"[uid={zdo.m_uid} own={zdo.GetOwner()} me={zdo.IsOwner()} " +
                   $"zY={zdo.GetPosition().y:F1} reg={registered} " +
                   $"kin={(body != null ? body.isKinematic.ToString() : "nobody")}]";
        }

        private static string InvalidReason(ItemDrop drop)
        {
            if (drop.m_itemData == null) return "no_item_data";
            if (drop.IsPiece()) return "is_piece";
            if (drop.GetComponent<Feast>() != null) return "is_feast";
            var nview = drop.GetComponent<ZNetView>();
            if (nview == null) return "no_znetview";
            return nview.IsValid() ? "valid?" : "znetview_invalid";
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
                !CanStoreIn(m_targetChest, m_targetDrop.m_itemData))
            {
                var containers = ContainerScanner.FindNearbyContainers(
                    m_ai.HomeAnchor, WorkSettings.HaulScanRadius);
                m_targetChest = WorkOrderChestPolicy.ResolveDepositChest(
                    containers, m_targetDrop.m_itemData, m_targetDrop.transform.position);
            }

            if (m_targetChest == null)
            {
                Reset();
                return;
            }

            // Pick it up: record it on the villager FIRST (so nothing in between can lose it),
            // then take the ground copy away on every peer. From here the item exists only in
            // the villager's hands until the chest.
            var npc = NpcZdo();
            if (npc == null || !npc.IsOwner())
            {
                // Can't record the carry durably on this peer — leave the item on the ground.
                LogLeg("cannot_carry_not_owner");
                Reset();
                return;
            }

            // Plan the whole pickup — the drop we walked to plus any identical drops still
            // within reach — as ONE stack, record it, then take it off the ground.
            var max = Mathf.Max(1, m_targetDrop.m_itemData.m_shared.m_maxStackSize);
            var plan = new List<(ItemDrop drop, int take)>();
            var total = 0;
            foreach (var d in m_group)
            {
                if (total >= max) break;
                if (!IsDropValid(d) || !StacksWith(m_targetDrop, d)) continue;
                if (d != m_targetDrop &&
                    Vector3.Distance(m_ai.Position, d.transform.position) > PickupReach) continue;
                var take = Mathf.Min(d.m_itemData.m_stack, max - total);
                if (take <= 0) continue;
                plan.Add((d, take));
                total += take;
            }

            m_carried = m_targetDrop.m_itemData.Clone();
            m_carried.m_stack = total;
            HaulCarry.Store(npc, m_carried);
            foreach (var p in plan)
                GroundDrops.Take(p.drop, p.take);

            if (LogSettings.VerboseHaul)
                DebugLog.Event("Haul", "picked_up",
                    ("villager", m_ai.NpcName), ("item", DropName(m_targetDrop)),
                    ("drops", plan.Count), ("stack", total), ("max", max));

            m_targetDrop = null;
            ReleaseClaim();

            BeginLeg(Phase.ToChest);

            // Issue the walk NOW, not on the next Update. Update runs only on reselect ticks,
            // but the arrival check runs every frame — with the villager still standing on the
            // waypoint it just reached, the very next frame fired OnArrival again in the
            // ToChest phase and deposited on the spot, metres (and floors) from the chest.
            if (!TryWalk(m_targetChest.transform.position, "haul: carry to chest"))
            {
                LogLeg("walk_to_chest_failed");
                Reset(); // drops the carried item at the villager's feet
                return;
            }

            LogLeg("walking_to_chest");
            m_navIssued = true;
        }

        private void ArriveAtChest()
        {
            if (m_carried == null || m_targetChest == null)
            {
                Reset();
                return;
            }

            // Only ever deposit AT the chest. An arrival anywhere else (a stale waypoint, an
            // agent that gave up short) walks on instead of reaching into a chest from afar.
            var distToChest = Vector3.Distance(m_ai.Position, m_targetChest.transform.position);
            if (distToChest > ChestReach)
            {
                LogLeg($"arrived_short_of_chest({distToChest:F1}m)");
                if (!TryWalk(m_targetChest.transform.position, "haul: carry to chest")) Reset();
                return;
            }

            if (CanStoreIn(m_targetChest, m_carried)
                && ContainerScanner.TryDepositItemData(m_targetChest, m_carried))
            {
                Plugin.Log?.LogInfo(
                    $"[Haul:{m_ai.NpcName}] Stored {m_carried.m_stack}x {m_carried.m_dropPrefab?.name} in a chest.");
                LogLeg("deposited");
                HaulCarry.Clear(NpcZdo());
                m_carried = null;
                Reset();
                return;
            }

            // This chest filled up (or its order changed) while we walked: carry it to another
            // one — on foot, never remotely. None left → put it down here.
            var containers = ContainerScanner.FindNearbyContainers(
                m_ai.HomeAnchor, WorkSettings.HaulScanRadius);
            var alternate = WorkOrderChestPolicy.ResolveDepositChest(
                containers, m_carried, m_ai.Position);
            if (alternate == null || alternate == m_targetChest)
            {
                LogLeg("no_chest_with_room");
                Reset(); // drops the carried item here
                return;
            }

            m_targetChest = alternate;
            BeginLeg(Phase.ToChest);
            if (!TryWalk(m_targetChest.transform.position, "haul: carry to chest"))
            {
                Reset();
                return;
            }

            LogLeg("walking_to_other_chest");
            m_navIssued = true;
        }

        /// <summary>
        ///     Room AND permission — the re-confirmation test only, for a chest already chosen by
        ///     <see cref="WorkOrderChestPolicy.ResolveDepositChest" /> that another villager may
        ///     have filled since. Choosing is the resolver's job: it files a drop into the chest
        ///     holding the work order for that very item before considering anything else, so a
        ///     harvested crop picked up off the ground lands with its order rather than in
        ///     whatever box the enumeration happened to reach first.
        ///
        ///     <para>A chest holding a work order is reserved for that order's output, ingredients
        ///     and fuel — dumping unrelated salvage in it is what eats the slots the order's output
        ///     needs, so hauling steps around those chests entirely. If none of the remaining
        ///     chests will take the drop, it stays on the ground.</para>
        /// </summary>
        private static bool CanStoreIn(Container container, ItemDrop.ItemData item)
        {
            if (!ContainerScanner.CanAcceptItemData(container, item)) return false;

            if (WorkOrderChestPolicy.Allows(container, item)) return true;

            Plugin.Log?.LogDebug(
                $"[Haul] Skipping reserved work-order chest '{container.m_name}' for " +
                $"{item?.m_dropPrefab?.name ?? "?"} — not part of its order.");
            return false;
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
            if (IsPlaced(drop)) return false;
            var nview = drop.GetComponent<ZNetView>();
            return nview != null && nview.IsValid();
        }

        /// <summary>
        ///     Something the player PUT there, not litter someone dropped.
        ///
        ///     <para>A placed feast is an <c>ItemDrop</c>, a <c>Piece</c> and a
        ///     <c>WearNTear</c> on one object with no rigidbody — which is exactly what
        ///     <c>ItemDrop.IsPiece()</c> tests, and it is how the game itself tells the two
        ///     apart (its own despawn timer skips pieces). To a plain "ItemDrop within the haul
        ///     radius" scan it looked like a dropped stack, so villagers carried feasts,
        ///     platters and anything else set out for show off to a chest. Loose drops keep
        ///     their rigidbody, so nothing that IS litter is caught by this.</para>
        /// </summary>
        private static bool IsPlaced(ItemDrop drop)
        {
            return drop.IsPiece() || drop.GetComponent<Feast>() != null;
        }

        private static string DropName(ItemDrop drop)
        {
            return drop.m_itemData?.m_dropPrefab != null
                ? drop.m_itemData.m_dropPrefab.name
                : drop.gameObject.name.Replace("(Clone)", "").Trim();
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
            Blacklist(m_targetDrop);
        }

        private void Blacklist(ItemDrop drop)
        {
            var id = DropId(drop);
            if (id != ZDOID.None)
                m_skipUntil[id] = Time.time + UnreachableCooldown;
        }

        private void Reset()
        {
            // Never leave the item in limbo: anything still in hand goes down where we stand.
            if (m_carried != null)
            {
                HaulCarry.DropIfCarrying(NpcZdo(), m_ai.Position);
                m_carried = null;
            }

            ReleaseClaim();
            m_phase = Phase.None;
            m_targetDrop = null;
            m_targetChest = null;
            m_navIssued = false;
            m_ai.SetState(BehaviorState.Idle);
        }

        private ZDO NpcZdo()
        {
            var nview = m_ai.Character != null ? m_ai.Character.GetComponent<ZNetView>() : null;
            return nview != null ? nview.GetZDO() : null;
        }

        private static string ClaimId(ItemDrop drop)
        {
            return "haul:" + DropId(drop);
        }

        /// <summary>The reservation key covering <paramref name="drop" />: its group's, else its own.</summary>
        private static string ClaimKeyFor(ItemDrop drop)
        {
            var id = DropId(drop);
            return id != ZDOID.None && s_memberClaim.TryGetValue(id, out var group) ? group : ClaimId(drop);
        }

        /// <summary>Would these two drops merge into one stack (same item, quality and variant)?</summary>
        private static bool StacksWith(ItemDrop a, ItemDrop b)
        {
            if (a?.m_itemData == null || b?.m_itemData == null) return false;
            return a.m_itemData.m_dropPrefab == b.m_itemData.m_dropPrefab
                   && a.m_itemData.m_quality == b.m_itemData.m_quality
                   && a.m_itemData.m_variant == b.m_itemData.m_variant;
        }

        private void ReleaseClaim()
        {
            foreach (var id in m_groupIds)
                if (s_memberClaim.TryGetValue(id, out var key) && key == m_claimId)
                    s_memberClaim.Remove(id);

            m_groupIds.Clear();
            m_group.Clear();
            if (m_claimId == null) return;
            TaskBoard.Release(m_claimId);
            m_claimId = null;
        }

        private enum Phase
        {
            None,
            ToDrop,
            ToChest,
        }
    }
}