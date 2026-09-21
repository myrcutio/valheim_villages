using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
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
using ValheimVillages.Villages.Entity;
using Object = UnityEngine.Object;

namespace ValheimVillages.Behaviors.Forestry
{
    /// <summary>
    ///     The Lumberjack's woodlot round: plant saplings around the Forester's Post, fell
    ///     them once grown, break the logs they leave, and carry the wood home.
    ///
    ///     <para><b>One errand per assignment.</b> Like every other work behavior here the
    ///     scheduler owns WHETHER to work; this owns only WHICH bit of the woodlot needs
    ///     doing next, picked in <see cref="ChooseErrand" /> in drop-first order so wood on
    ///     the ground is never left behind while new trees go in.</para>
    ///
    ///     <para><b>Felling is aimed, not fenced.</b> The hit direction drops the trunk away
    ///     from the villager (see <see cref="TreeFelling" />). Beyond that, where a tree can
    ///     fall is the player's business — they placed the post — so there is deliberately no
    ///     clearance test on a planting spot.</para>
    ///
    ///     <para>Tag: "forestry", Priority: 45 — below craft(50) so work orders still win,
    ///     above repair(35) and patrol(30).</para>
    /// </summary>
    [RegisterBehavior("forestry")]
    public class ForestryBehavior : IBehavior, IDirectedBehavior
    {
        /// <summary>
        ///     Generous compared with in-village work (repair uses 20s) because the woodlot is
        ///     deliberately OUTSIDE the walls: a leg here is a walk to the far side of the
        ///     village, out through a gate and across the grove. At 30s the plant leg was
        ///     timing out mid-walk.
        /// </summary>
        private const float MaxLegSeconds = 60f;

        /// <summary>How many trees the woodlot is kept stocked at.</summary>
        private const int TargetTrees = 6;

        /// <summary>Close enough to act on a tree/log/drop without waiting for a path "arrival".</summary>
        private const float ReachRange = 3.5f;

        /// <summary>Minimum gap between a new sapling and anything already growing.</summary>
        private const float PlantSpacing = 4f;

        /// <summary>How far from a log/tree centre to look for standable ground.</summary>
        private const float BulkSnapRadius = 8f;

        // --- the charge ---

        /// <summary>How far back he squares up from the trunk before running at it.</summary>
        private const float ChargeBackoff = 5f;

        /// <summary>Seconds spent stamping and kicking up dust before the run.</summary>
        private const float WindupSeconds = 0.9f;

        /// <summary>Distance at which the charge counts as a hit.</summary>
        private const float ImpactRange = 1.6f;

        /// <summary>A charge that never connects must not strand the errand.</summary>
        private const float ChargeTimeout = 6f;

        /// <summary>
        ///     How much further than the run-up he may travel before the charge is called off.
        ///     He starts <see cref="ChargeBackoff" /> from the trunk and runs at it, so getting
        ///     FURTHER away than he began means the target is not where he is going.
        /// </summary>
        private const float ChargeOvershoot = 3f;

        /// <summary>
        ///     How long a tree or log is left alone after a charge at it was called off. Without
        ///     this the next errand pick chooses the same nearest target, charges it, aborts,
        ///     and repeats — the hot loop this codebase keeps rediscovering. Short enough that a
        ///     transient cause (someone standing in the way) clears on its own.
        /// </summary>
        private const float ChargeRetrySeconds = 120f;

        /// <summary>Instance ids of charge targets recently given up on → when to allow a retry.</summary>
        private readonly Dictionary<int, float> m_chargeFailures = new();

        /// <summary>Dust kicked up during the wind-up. Present in every world.</summary>
        private const string DustEffect = "vfx_SawDust";

        private ChargePhase m_charge;

        /// <summary>Hard deadline for the whole charge.</summary>
        private float m_chargeDeadline;

        /// <summary>When the current PHASE ends (only the wind-up uses it).</summary>
        private float m_windupUntil;

        private Vector3 m_chargeTarget;

        private readonly VillagerAI m_ai;

        private bool m_active;
        private Errand m_errand;
        private float m_legDeadline;
        private bool m_navIssued;
        private Vector3 m_target;
        private Vector3 m_grove;

        private TreeBase m_tree;
        private TreeLog m_log;
        private ItemDrop m_drop;
        private Container m_chest;

        /// <summary>Sapling to pull up: planted, paid for, and never going to grow.</summary>
        private Plant m_dud;

        /// <summary>Species chosen for the pending plant, and the chest slot paying for it.</summary>
        private TreeSpecies m_plantSpecies;
        private IngredientSource m_plantSeed;

        /// <summary>Where the sapling goes — NOT <c>m_target</c>, which is where he stands.</summary>
        private Vector3 m_plantSpot;

        public ForestryBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "forestry";

        public int Priority => 45;

        // Scheduler-driven only; must equal AssignmentActive (see IDirectedBehavior).
        public bool WantsControl(BehaviorContext ctx) => AssignmentActive;

        public bool CanExecute(TaskKind kind) => kind == TaskKind.Forestry;

        public bool AssignmentActive => m_active;

        public AssignmentResult BeginAssignment(CandidateTask task)
        {
            var village = VillageRegistry.GetVillageAt(m_ai.HomeAnchor);
            if (village == null || !village.TryGetAnchor(ForesterPost.AnchorName, out var grove))
                return AssignmentResult.NotActionable;

            m_grove = grove;

            // Nothing is worked while timber is still in the air. A felled trunk rolls and
            // slides for a few seconds; picking a new errand during that means either walking
            // at a moving collider or felling a second tree into the first one's path. Waiting
            // costs a couple of seconds and the assignment simply comes round again.
            if (TreeFelling.AnyLogInMotion(m_grove, ForesterPost.WorkRadius))
                return AssignmentResult.NotActionable;
            // Unreachable is reserved for the WOODLOT itself being cut off, because it blocks
            // this row until the next repartition. An individual errand target is transient —
            // a log rolls, a drop despawns — so a target we cannot approach means "try the
            // next errand", never "the woodlot is gone". Conflating the two blocked the whole
            // woodlot the first time a felled log landed somewhere awkward.
            if (!VillagerMovement.TryResolveApproach(m_grove, m_ai.Position, null, out _))
                return AssignmentResult.Unreachable;

            // Summarised rather than silent: when every candidate is skipped the villager just
            // looks idle next to a woodlot full of work, and there is no way to tell WHICH
            // stage could not be approached.
            var skipped = new List<string>();
            foreach (var candidate in CandidateErrands())
            {
                if (!VillagerMovement.TryResolveApproach(
                        candidate.target, m_ai.Position, null, out var approach))
                {
                    skipped.Add(candidate.errand.ToString());
                    continue;
                }

                m_errand = candidate.errand;
                m_tree = candidate.tree;
                m_log = candidate.log;
                m_drop = candidate.drop;
                m_dud = candidate.dud;
                m_plantSpecies = candidate.species;
                m_plantSeed = candidate.seed;
                // The SPOT, kept apart from the approach below. They are different points: the
                // approach is wherever the villager can stand, which the resolver picks for
                // walkability and nothing else. Planting at the approach point put saplings
                // wherever he happened to stop — beside boulders and walls, where they can
                // never grow — while the vetted spot went unused.
                m_plantSpot = candidate.target;
                m_target = approach;
                m_active = true;
                m_navIssued = false;
                m_legDeadline = Time.time + MaxLegSeconds;
                // Report skips even on SUCCESS. Only logging them when everything failed hid
                // the real problem: a higher-priority errand silently unreachable every single
                // time while a lower one kept succeeding, so the woodlot looked healthy while
                // logs piled up untouched.
                if (skipped.Count > 0)
                    Plugin.Log?.LogWarning(
                        $"[Forestry:{m_ai.NpcName}] doing {m_errand}; skipped unreachable " +
                        $"[{string.Join(",", skipped.ToArray())}]");
                return AssignmentResult.Accepted;
            }

            // Woodlot reachable, but nothing in it is actionable right now.
            if (skipped.Count > 0)
                Plugin.Log?.LogWarning(
                    $"[Forestry:{m_ai.NpcName}] nothing actionable: could not approach " +
                    $"{skipped.Count} candidate(s) [{string.Join(",", skipped.ToArray())}]");
            return AssignmentResult.NotActionable;
        }

        /// <summary>
        ///     Everything the woodlot could use right now, best first: SPLIT what is already
        ///     down, then gather, then fell something new, and only then replant a gap.
        ///
        ///     <para>Splitting outranks gathering deliberately. With gathering first the
        ///     woodlot livelocked: felling a tree flattens the undergrowth, undergrowth drops
        ///     wood, so there was always fresh wood on the ground and the logs were never
        ///     touched — observed as four deliveries with <c>logs=2</c> pinned the whole time.
        ///     Finishing the log first also matches how the job actually goes.</para>
        ///
        ///     <para>Yielded rather than returned so the caller can skip past any whose target
        ///     it cannot currently walk to.</para>
        /// </summary>
        private IEnumerable<Candidate> CandidateErrands()
        {
            // Harvest reaches further than planting: felled timber lands outward, so logs
            // and wood routinely come to rest beyond the planting ring.
            var r = ForesterPost.WorkRadius;

            // Several candidates per errand, nearest first — not one.
            //
            // These used to offer the single nearest tree and the single nearest log, so ONE
            // unusable target was indistinguishable from an empty woodlot: the assignment came
            // back NotActionable, the scheduler parked the row, and a Lumberjack stood idle
            // beside sixty trees. Any reason the nearest one fails — no approach, a charge that
            // just failed at it, a log wedged somewhere awkward — now simply moves to the next.
            // Nearest to HIM, not to the post: a log lying between the villager and his next
            // job is an obstacle the pathfinder has to detour around, and breaking it up is
            // both the job and the way through. Clearing the far side of the woodlot first
            // leaves him walking around the near one all day.
            foreach (var log in NearestLogs(m_ai.Position, r, AlternativesPerErrand))
                yield return new Candidate
                    { errand = Errand.BreakLog, target = BesideBulk(log.transform.position), log = log };

            foreach (var drop in AllHarvest(m_grove, r))
                yield return new Candidate
                    // Drops need the same treatment as logs: wood from a split log lands
                    // right beside (or on top of) the log that produced it, so its literal
                    // position is just as likely to be inside a collider.
                    { errand = Errand.Collect, target = BesideBulk(drop.transform.position), drop = drop };

            foreach (var tree in NearestTrees(m_grove, r, AlternativesPerErrand))
                yield return new Candidate
                    { errand = Errand.Fell, target = BesideBulk(tree.transform.position), tree = tree };

            // Before planting, not after: a dud holds a spot the next sapling could use, and
            // pulling it is the cheapest job in the woodlot.
            var dud = FindDudSapling(m_grove, ForesterPost.GroveRadius);
            if (dud != null)
                yield return new Candidate
                    { errand = Errand.Clear, target = BesideBulk(dud.transform.position), dud = dud };

            if (CountStock(m_grove, ForesterPost.GroveRadius) < TargetTrees
                && TryFindPlantSpot(m_grove, m_ai.HomeAnchor, WantedWoodTypes(),
                    out var spot, out var species, out var seed))
                yield return new Candidate
                    { errand = Errand.Plant, target = spot, species = species, seed = seed };
        }

        private struct Candidate
        {
            public Errand errand;
            public Vector3 target;
            public TreeBase tree;
            public TreeLog log;
            public ItemDrop drop;
            public Plant dud;
            public TreeSpecies species;
            public IngredientSource seed;
        }

        /// <summary>
        ///     A spot a villager can actually STAND next to a bulky object, rather than the
        ///     object's own centre.
        ///
        ///     <para>A felled log is ~4m of solid collider lying on the ground: measured, its
        ///     own cell reports <c>WallBlocks=TRUE hits=[log]</c> on all four neighbours and
        ///     the agent capsule at its centre is blocked by the log itself. Approaching the
        ///     centre therefore always failed to resolve, the errand was skipped every time,
        ///     and logs piled up in the woodlot un-split. Standing trees have the same shape
        ///     of problem, just narrower. Reuse the ring search built for the registry
        ///     station, which is the same "target sits in its own collider" case.</para>
        /// </summary>
        private static Vector3 BesideBulk(Vector3 centre)
        {
            // Ask the agent's OWN navmesh where the nearest standable spot is. That is the
            // authoritative answer and it is robust to the awkward cases: measured beside a
            // pair of stacked logs, the ring-and-capsule seed search struggled (every close
            // direction capsule-blocked, the log's own cell not even in the region graph)
            // while SamplePosition returned walkable ground 1.1m away. ReachRange then covers
            // the last step, so he never needs to stand ON the log.
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };
            if (NavMesh.SamplePosition(centre, out var hit, BulkSnapRadius, filter))
                return hit.position;

            // No mesh anywhere near it — fall back to the ring search before giving up.
            return RegistrySeedResolver.TryResolveWalkableSeed(centre, out var seed) ? seed : centre;
        }

        public void Update(float dt)
        {
            // A charge owns the villager outright until it lands or times out.
            if (m_charge != ChargePhase.None)
            {
                TickCharge();
                return;
            }

            if (Time.time > m_legDeadline)
            {
                Plugin.Log?.LogWarning(
                    $"[Forestry:{m_ai.NpcName}] gave up on {m_errand} (leg timed out)");
                Reset();
                return;
            }

            // Tick fast while travelling so the range check below lands precisely.
            m_ai.RequestFastReselect(0.25f);

            // Act on proximity, NOT on a PathComplete arrival — cross-region routes are
            // link-stitched (PathPartial) and never fire one.
            if ((m_ai.Position - m_target).sqrMagnitude <= ReachRange * ReachRange)
            {
                Act();
                return;
            }

            if (m_navIssued) return;
            if (!m_ai.NavTo(m_target, BehaviorState.Traveling, $"forestry: {m_errand}",
                    snapToApproach: false))
            {
                Plugin.Log?.LogWarning($"[Forestry:{m_ai.NpcName}] cannot path to {m_errand}");
                Reset();
                return;
            }

            m_navIssued = true;
        }

        public void OnArrival(float dt)
        {
            // A charge is already running the show — re-entering Act() here would call
            // BeginCharge() again, snap the phase back to BackingOff while he is ALREADY at
            // the standoff point, and re-announce the wind-up. That livelocked the charge and
            // spammed the log every tick.
            if (m_charge != ChargePhase.None) return;
            Act();
        }

        /// <summary>Names the errand, not just "working" — the woodlot round has four very
        /// different-looking stages and "Felling a tree" vs "Carrying wood home" is the
        /// difference between a villager that looks stuck and one that obviously is not.</summary>
        public string GetStatusText()
        {
            if (!m_active) return "";
            switch (m_errand)
            {
                case Errand.Plant: return "Planting a sapling";
                case Errand.Clear: return "Pulling up a dead sapling";
                case Errand.Fell: return "Felling a tree";
                case Errand.BreakLog: return "Splitting a log";
                case Errand.Collect: return "Gathering wood";
                case Errand.Deliver: return "Carrying wood home";
                default: return "Working the woodlot";
            }
        }

        private void Act()
        {
            switch (m_errand)
            {
                case Errand.Plant:
                    DoPlant();
                    Reset();
                    break;
                case Errand.Clear:
                    DoClear();
                    Reset();
                    break;
                case Errand.Fell:
                case Errand.BreakLog:
                    // He does not chop — he squares up and runs the thing down.
                    BeginCharge();
                    break;
                case Errand.Collect:
                    // Two legs: pick the wood up, then carry it to a chest.
                    if (!DoCollect()) Reset();
                    break;
                case Errand.Deliver:
                    DoDeliver();
                    Reset();
                    break;
                default:
                    Reset();
                    break;
            }
        }

        private void DoPlant()
        {
            if (m_plantSpecies == null)
            {
                Plugin.Log?.LogError($"[Forestry:{m_ai.NpcName}] plant errand with no species chosen");
                return;
            }

            if (!m_plantSpecies.TryPlantAt(m_plantSpot, m_plantSeed, out var failure))
            {
                // A loss worth seeing: he walked out to the spot for nothing, and if it is the
                // grow test talking then the spot passed when it was chosen and does not now.
                Plugin.Log?.LogWarning(
                    $"[Forestry:{m_ai.NpcName}] did not plant a {m_plantSpecies.Name}: {failure}");
                return;
            }

            Plugin.Log?.LogInfo(
                $"[Forestry:{m_ai.NpcName}] planted {m_plantSpecies.Name} " +
                $"(from {m_plantSeed.PrefabName}, for {m_plantSpecies.Wood}) at " +
                $"({m_plantSpot.x:F1},{m_plantSpot.y:F1},{m_plantSpot.z:F1})");
        }

        private void DoFell()
        {
            if (!TreeFelling.IsFellable(m_tree)) return;
            var name = m_tree.gameObject.name;
            // From the villager's position, so the trunk goes down away from them.
            TreeFelling.TryFell(m_tree, m_ai.Position);
            Plugin.Log?.LogInfo($"[Forestry:{m_ai.NpcName}] felled {name}");
        }

        /// <summary>
        ///     Pull up a sapling that cannot grow where it stands. The seed is already spent —
        ///     nothing recovers that — but the spot goes back into circulation instead of being
        ///     held forever by something that will never become a tree.
        /// </summary>
        private void DoClear()
        {
            if (m_dud == null) return;

            var status = m_dud.GetStatus();
            var name = m_dud.gameObject.name.Replace("(Clone)", "");
            var nview = m_dud.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return;

            nview.ClaimOwnership();
            nview.Destroy();
            Plugin.Log?.LogInfo(
                $"[Forestry:{m_ai.NpcName}] pulled up a {name} that could not grow there ({status})");
        }

        // --- the charge ---
        //
        // A Dvergr has no chopping animation (player weapon anims do not retarget onto the
        // rig — the same wall the crossbow work hit), so a Lumberjack who "chops" would stand
        // motionless while trees fell over for no visible reason. Instead he backs off, stamps
        // up some dust, and runs the trunk down shoulder-first: entirely built from movement,
        // which the rig does have.

        /// <summary>Back off from the target and square up for the run.</summary>
        private void BeginCharge()
        {
            m_chargeTarget = m_errand == Errand.Fell && m_tree != null
                ? m_tree.transform.position
                : m_log != null
                    ? m_log.transform.position
                    : m_target;

            // Stand off along the line he arrived on, so the run-up comes from the village
            // side and the trunk still goes down AWAY from him.
            var back = m_ai.Position - m_chargeTarget;
            back.y = 0f;
            if (back.sqrMagnitude < 0.01f) back = Vector3.forward;
            var standoff = m_chargeTarget + back.normalized * ChargeBackoff;

            // Only back off to somewhere he can actually stand.
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };
            if (NavMesh.SamplePosition(standoff, out var hit, 4f, filter))
                standoff = hit.position;

            m_target = standoff;
            m_navIssued = false;
            m_charge = ChargePhase.BackingOff;
            m_chargeDeadline = Time.time + ChargeTimeout;
        }

        private void TickCharge()
        {
            var targetGone = m_errand == Errand.Fell
                ? !TreeFelling.IsFellable(m_tree)
                : !TreeFelling.IsBreakable(m_log);
            // Deadline only — NOT the wind-up timer. Sharing one field between "the charge has
            // taken too long" and "the wind-up is over" made the wind-up's own expiry read as
            // a timeout, so every charge aborted ~1s after he squared up and never ran.
            if (targetGone || Time.time > m_chargeDeadline)
            {
                if (!targetGone)
                {
                    Plugin.Log?.LogWarning($"[Forestry:{m_ai.NpcName}] charge timed out short of the trunk");
                    NoteChargeFailure();
                }

                Reset();
                return;
            }

            m_ai.RequestFastReselect(0.1f);

            switch (m_charge)
            {
                case ChargePhase.BackingOff:
                    if ((m_ai.Position - m_target).sqrMagnitude <= 1.5f * 1.5f)
                    {
                        m_charge = ChargePhase.WindUp;
                        m_windupUntil = Time.time + WindupSeconds;
                        SpawnDust();
                        Plugin.Log?.LogInfo(
                            $"[Forestry:{m_ai.NpcName}] squares up at {ChargeBackoff:F0}m and paws the ground");
                        break;
                    }

                    if (!m_navIssued)
                        m_navIssued = m_ai.NavTo(m_target, BehaviorState.Traveling,
                            "forestry: back off for the charge", snapToApproach: false);
                    break;

                case ChargePhase.WindUp:
                    // Stand and face it. MoveTowards with a zero-length run would slide him,
                    // so just hold the facing until the wind-up expires.
                    FaceTarget();
                    if (Time.time >= m_windupUntil)
                    {
                        m_charge = ChargePhase.Running;
                        // Fresh deadline for the run itself.
                        m_chargeDeadline = Time.time + ChargeTimeout;
                    }

                    break;

                case ChargePhase.Running:
                    var toTarget = m_chargeTarget - m_ai.Position;
                    toTarget.y = 0f;
                    if (toTarget.magnitude <= ImpactRange)
                    {
                        Impact();
                        return;
                    }

                    // A charge is the one place this behaviour drives the villager with no
                    // path and no navmesh, so it is the one place a bad target means running
                    // into the wild. Bound it by DISTANCE as well as by time: at a run, the 6s
                    // deadline alone allows ~30m of unnavigated sprinting from a 5m start, and
                    // a trunk he cannot close on — across a gully, or blocked — buys every
                    // metre of that. Far enough to leave the navmesh, which is exactly where
                    // the normal movement code can no longer bring him back.
                    if (toTarget.magnitude > ChargeBackoff + ChargeOvershoot)
                    {
                        Plugin.Log?.LogWarning(
                            $"[Forestry:{m_ai.NpcName}] charge ran {toTarget.magnitude:F0}m and is " +
                            $"still short of the trunk — aborting before he leaves the woodlot.");
                        NoteChargeFailure();
                        Reset();
                        return;
                    }

                    // And stop the moment he is off the mesh: from there a straight-line sprint
                    // is unrecoverable, and every further metre makes it worse.
                    if (!OnNavMesh(m_ai.Position))
                    {
                        Plugin.Log?.LogWarning(
                            $"[Forestry:{m_ai.NpcName}] charge left the navmesh at " +
                            $"({m_ai.Position.x:F0},{m_ai.Position.z:F0}) — aborting.");
                        NoteChargeFailure();
                        Reset();
                        return;
                    }

                    // Straight-line sprint, not a nav path: it is a charge, and the last few
                    // metres are open ground he has already walked.
                    m_ai.DriveDirect(toTarget.normalized, true);
                    break;
            }
        }

        /// <summary>Remember the current charge target as one not to pick again for a while.</summary>
        private void NoteChargeFailure()
        {
            Object target = m_errand == Errand.Fell ? (Object)m_tree : m_log;
            if (target == null) return;
            m_chargeFailures[target.GetInstanceID()] = Time.time + ChargeRetrySeconds;
        }

        /// <summary>True while a charge at this target has recently been called off.</summary>
        private bool ChargeRecentlyFailed(Object target)
        {
            if (target == null) return false;
            var id = target.GetInstanceID();
            if (!m_chargeFailures.TryGetValue(id, out var until)) return false;
            if (Time.time < until) return true;
            m_chargeFailures.Remove(id);
            return false;
        }

        /// <summary>Is there villager navmesh under this point? Used to call off a charge.</summary>
        private static bool OnNavMesh(Vector3 pos)
        {
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };
            return NavMesh.SamplePosition(pos, out _, 2f, filter);
        }

        private void FaceTarget()
        {
            var dir = m_chargeTarget - m_ai.Position;
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.01f) m_ai.DriveDirect(dir.normalized * 0.001f, false);
        }

        private void Impact()
        {
            if (m_errand == Errand.Fell) DoFell();
            else DoBreakLog();
            Reset();
        }

        /// <summary>Dust at his feet during the wind-up.</summary>
        private void SpawnDust()
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(DustEffect) : null;
            if (prefab == null) return;
            Object.Instantiate(prefab, m_ai.Position, Quaternion.identity);
        }

        private void DoBreakLog()
        {
            if (!TreeFelling.IsBreakable(m_log)) return;
            var name = m_log.gameObject.name;
            TreeFelling.TryBreakLog(m_log, m_ai.Position);
            Plugin.Log?.LogInfo($"[Forestry:{m_ai.NpcName}] broke {name} into wood");
        }

        /// <summary>
        ///     Choose the chest and start the carry leg. The drop STAYS in the world until
        ///     the deposit succeeds — same rule haul uses — so an interruption mid-carry can
        ///     never destroy the wood.
        /// </summary>
        private bool DoCollect()
        {
            if (!IsDropValid(m_drop)) return false;

            // Gather the scatter into one armful before walking home. Breaking a log throws
            // wood across several metres as separate drops, and a trip per drop meant a dozen
            // walks back to the village for a few wood each — the felling is quick, the
            // carrying was the whole afternoon. Everything of the same kind within reach is
            // merged into the drop he is standing at, up to one full stack; whatever will not
            // fit stays where it is for the next trip.
            var gathered = ConsolidateNearby(m_drop, GatherRadius);
            if (gathered > 0)
                Plugin.Log?.LogInfo(
                    $"[Forestry:{m_ai.NpcName}] gathered {gathered} more into one armful " +
                    $"({m_drop.m_itemData.m_stack}x {m_drop.m_itemData.m_shared.m_name})");

            // Village footprint, not a radius around the villager: the woodlot is outside the
            // walls, so a radius centred on him reaches the wrong half of the settlement.
            var containers = ContainerScanner.FindVillageContainers(
                m_ai.HomeAnchor, WorkSettings.HaulScanRadius);
            m_chest = WorkOrderChestPolicy.ResolveDepositChest(
                containers, m_drop.m_itemData, m_drop.transform.position);
            if (m_chest == null)
            {
                Plugin.Log?.LogWarning(
                    $"[Forestry:{m_ai.NpcName}] no chest will take the wood; leaving it at the woodlot");
                return false;
            }

            if (!VillagerMovement.TryResolveApproach(
                    m_chest.transform.position, m_ai.Position, null, out var approach))
                return false;

            m_errand = Errand.Deliver;
            m_target = approach;
            m_navIssued = false;
            m_legDeadline = Time.time + MaxLegSeconds;
            return true;
        }

        /// <summary>How far around a drop he sweeps up more of the same. One tree's scatter.</summary>
        private const float GatherRadius = 10f;

        /// <summary>
        ///     Merge nearby drops of the same item into <paramref name="into" />, up to one full
        ///     stack. Returns how many units were absorbed.
        ///
        ///     <para>Valheim has its own <c>AutoStackItems</c>, but it is not usable here: it is
        ///     private, runs once per drop at spawn, reaches only 4m, skips any drop this peer
        ///     does not already own, and refuses a stack that will not fit ENTIRELY — so a
        ///     woodlot ends up with exactly the scattered piles it is meant to prevent. This
        ///     claims what it touches, takes partial stacks, and leaves the remainder as a
        ///     valid drop.</para>
        ///
        ///     <para>The survivor is grown and saved BEFORE the source is shrunk or destroyed.
        ///     If something interrupts between the two, the world has duplicated a little wood
        ///     rather than eaten it — the same bias the rest of this behaviour takes, where a
        ///     drop stays in the world until its deposit has actually succeeded.</para>
        /// </summary>
        private static int ConsolidateNearby(ItemDrop into, float radius)
        {
            if (into?.m_itemData?.m_shared == null) return 0;
            var max = into.m_itemData.m_shared.m_maxStackSize;
            if (max <= 1) return 0;

            var nview = into.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return 0;
            nview.ClaimOwnership();

            var absorbed = 0;
            foreach (var other in PhysicsHelper.GetAllInRadius<ItemDrop>(into.transform.position, radius))
            {
                var room = max - into.m_itemData.m_stack;
                if (room <= 0) break;

                if (other == null || other == into) continue;
                if (other.m_itemData?.m_shared == null) continue;
                if (other.m_itemData.m_shared.m_name != into.m_itemData.m_shared.m_name) continue;
                if (other.m_itemData.m_quality != into.m_itemData.m_quality) continue;
                // A placed piece is furniture, not litter — never absorb one.
                if (other.IsPiece()) continue;

                var otherView = other.GetComponent<ZNetView>();
                if (otherView == null || !otherView.IsValid()) continue;
                otherView.ClaimOwnership();

                var take = Mathf.Min(room, other.m_itemData.m_stack);
                if (take <= 0) continue;

                into.SetStack(into.m_itemData.m_stack + take);
                absorbed += take;

                var left = other.m_itemData.m_stack - take;
                if (left > 0) other.SetStack(left);
                else otherView.Destroy();
            }

            return absorbed;
        }

        private void DoDeliver()
        {
            if (!IsDropValid(m_drop) || m_chest == null) return;

            var payload = m_drop.m_itemData.Clone();
            if (!ContainerScanner.TryDepositItemData(m_chest, payload))
            {
                Plugin.Log?.LogWarning(
                    $"[Forestry:{m_ai.NpcName}] chest refused the wood (full?); leaving the drop");
                return;
            }

            Plugin.Log?.LogInfo(
                $"[Forestry:{m_ai.NpcName}] delivered {payload.m_stack}x {payload.m_shared.m_name} to a chest");
            var nview = m_drop.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid()) nview.Destroy();
        }

        // --- woodlot queries ---

        private static bool IsDropValid(ItemDrop drop)
        {
            if (drop == null || drop.m_itemData == null) return false;
            // Placed pieces are not litter — see HaulBehavior. A wood stack someone built is
            // furniture, not a pile of wood waiting to be carried in.
            if (drop.IsPiece()) return false;
            var nview = drop.GetComponent<ZNetView>();
            return nview != null && nview.IsValid();
        }

        /// <summary>
        ///     Every drop in the woodlot worth carrying home, nearest first — timber AND tree
        ///     seeds. All of them rather than just the nearest, so one drop the villager cannot
        ///     currently reach does not hide the rest behind it.
        ///
        ///     <para>Seeds matter as much as the wood: a felled tree drops the seed for its own
        ///     species, and planting spends seeds out of the village chests. Leaving them on the
        ///     forest floor breaks the loop — he fells everything, restocks nothing, and the
        ///     woodlot ends as bare ground.</para>
        /// </summary>
        private static List<ItemDrop> AllHarvest(Vector3 centre, float radius)
        {
            var found = new List<ItemDrop>();
            foreach (var drop in PhysicsHelper.GetAllInRadius<ItemDrop>(centre, radius))
            {
                if (!IsDropValid(drop)) continue;
                if (!IsHarvestable(drop)) continue;
                found.Add(drop);
            }

            // Seeds first, then nearest. Wood is the bulk output and there is always more of
            // it on the ground; seeds are the scarce input the next generation of trees comes
            // out of, and one errand is taken per assignment, so a pure distance sort leaves
            // seeds lying in the grove indefinitely while he ferries wood one stack at a time.
            found.Sort((a, b) =>
            {
                var seedRank = IsSeed(b).CompareTo(IsSeed(a));
                if (seedRank != 0) return seedRank;
                return (a.transform.position - centre).sqrMagnitude
                    .CompareTo((b.transform.position - centre).sqrMagnitude);
            });
            return found;
        }

        private static bool IsSeed(ItemDrop drop)
        {
            var prefab = drop.gameObject.name.Replace("(Clone)", "");
            foreach (var s in TreeSpecies.All)
                if (string.Equals(prefab, s.Seed, System.StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>
        ///     Timber (matched loosely on the display name, which covers Wood/FineWood/RoundLog/
        ///     ElderBark) or a seed of a species we can actually plant (matched EXACTLY on the
        ///     prefab, from the species table — "Acorn" and "PineCone" share no substring with
        ///     anything, so a name heuristic would miss them).
        /// </summary>
        private static bool IsHarvestable(ItemDrop drop)
        {
            if (IsSeed(drop)) return true;

            var name = drop.m_itemData.m_shared?.m_name ?? "";
            return name.IndexOf("wood", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("log", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("bark", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        ///     Nearest tree that is a TREE and not a sapling. A growing sapling has a Plant
        ///     component and no TreeBase, so anything with a TreeBase here is fair game.
        /// </summary>
        private static TreeBase FindGrownTree(
            Vector3 centre, float radius, System.Func<Object, bool> skip = null)
        {
            return TreeFelling.FindNearest(centre, radius, skip);
        }

        /// <summary>
        ///     How many alternatives to offer per errand. Enough that one awkward target does
        ///     not cost the errand, small enough that a failed assignment is still cheap — each
        ///     candidate costs an approach resolve.
        /// </summary>
        private const int AlternativesPerErrand = 4;

        /// <summary>The nearest fellable trees, closest first, skipping recent charge failures.</summary>
        private List<TreeBase> NearestTrees(Vector3 centre, float radius, int count)
        {
            var found = new List<TreeBase>();
            foreach (var tree in PhysicsHelper.GetAllInRadius<TreeBase>(centre, radius))
            {
                if (!TreeFelling.IsFellable(tree)) continue;
                if (ChargeRecentlyFailed(tree)) continue;
                found.Add(tree);
            }

            found.Sort((a, b) => (a.transform.position - centre).sqrMagnitude
                .CompareTo((b.transform.position - centre).sqrMagnitude));
            if (found.Count > count) found.RemoveRange(count, found.Count - count);
            return found;
        }

        /// <summary>The nearest breakable logs, closest first, skipping recent charge failures.</summary>
        /// <param name="sortFrom">
        ///     Where "nearest" is measured from — the VILLAGER for logs, so the one in his way
        ///     is the one he clears. The search itself still covers the whole woodlot.
        /// </param>
        private List<TreeLog> NearestLogs(Vector3 sortFrom, float radius, int count)
        {
            var found = new List<TreeLog>();
            foreach (var log in PhysicsHelper.GetAllInRadius<TreeLog>(m_grove, radius))
            {
                if (!TreeFelling.IsBreakable(log)) continue;
                if (!TreeFelling.IsSettled(log)) continue; // still rolling — leave it alone
                if (ChargeRecentlyFailed(log)) continue;
                found.Add(log);
            }

            found.Sort((a, b) => (a.transform.position - sortFrom).sqrMagnitude
                .CompareTo((b.transform.position - sortFrom).sqrMagnitude));
            if (found.Count > count) found.RemoveRange(count, found.Count - count);
            return found;
        }

        /// <summary>
        ///     Saplings + standing trees currently in the woodlot — the stock the stocking
        ///     target is measured against.
        ///
        ///     <para>A dud sapling (NoSpace, WrongBiome …) is NOT stock. Counting it kept the
        ///     woodlot reading "at target" while producing nothing, and the dud sits on its spot
        ///     forever; see the Clear errand, which pulls them.</para>
        /// </summary>
        private static int CountStock(Vector3 centre, float radius)
        {
            var n = 0;
            foreach (var plant in PhysicsHelper.GetAllInRadius<Plant>(centre, radius))
                if (plant != null && !PlantSpace.IsDud(plant))
                    n++;
            foreach (var tree in PhysicsHelper.GetAllInRadius<TreeBase>(centre, radius))
                if (TreeFelling.IsFellable(tree))
                    n++;
            return n;
        }

        /// <summary>
        ///     Nearest sapling in the grove that has judged itself unable to grow. It cost a
        ///     seed and it will never become a tree, so the only useful thing left to do with
        ///     it is clear the spot.
        /// </summary>
        private static Plant FindDudSapling(Vector3 centre, float radius)
        {
            Plant best = null;
            var bestSqr = float.MaxValue;
            foreach (var plant in PhysicsHelper.GetAllInRadius<Plant>(centre, radius))
            {
                if (plant == null || !PlantSpace.IsDud(plant)) continue;

                var sqr = (plant.transform.position - centre).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                best = plant;
                bestSqr = sqr;
            }

            return best;
        }

        /// <summary>
        ///     A clear, on-graph spot in the woodlot, the species that will actually grow there
        ///     and the seed that pays for it. Species follows the plot's biome because
        ///     <c>Plant.m_biome</c> gates growth — a Fir planted in the Meadows just sits at
        ///     WrongBiome forever.
        ///
        ///     <para>Static so <c>vv_forestry</c> can ask the exact question the villager asks,
        ///     with the same stock and the same spiral, rather than a lookalike.</para>
        /// </summary>
        internal static bool TryFindPlantSpot(
            Vector3 grove,
            Vector3 homeAnchor,
            HashSet<string> wantedWood,
            out Vector3 spot,
            out TreeSpecies species,
            out IngredientSource seed)
        {
            spot = Vector3.zero;
            species = null;
            seed = null;

            var graph = VillageRegistry.GraphAt(grove);
            var r = ForesterPost.GroveRadius;

            // Scanned once, not per candidate spot: the stock does not change as the spiral
            // walks outward, and FindVillageContainers walks every container in the world.
            var containers = ContainerScanner.FindVillageContainers(homeAnchor, WorkSettings.HaulScanRadius);

            // Deterministic spiral out from the post, so the woodlot fills evenly rather
            // than clustering wherever the first random throw landed.
            for (var ring = PlantSpacing; ring <= r; ring += PlantSpacing)
            {
                var steps = Mathf.Max(6, Mathf.RoundToInt(2f * Mathf.PI * ring / PlantSpacing));
                for (var i = 0; i < steps; i++)
                {
                    var a = 2f * Mathf.PI * i / steps;
                    var p = grove + new Vector3(Mathf.Cos(a) * ring, 0f, Mathf.Sin(a) * ring);
                    if (ZoneSystem.instance == null) return false;
                    p.y = ZoneSystem.instance.GetGroundHeight(p);

                    // Must be somewhere the villager can actually stand to plant it.
                    if (graph == null || string.IsNullOrEmpty(graph.PointToRegionId(p))) continue;

                    if (!TreeSpecies.Choose(p, wantedWood, containers, out species, out seed)) continue;

                    // The game's own grow test, against the chosen species' sapling: anything
                    // inside its grow radius — a boulder, a bush, a wall, not just another tree
                    // — means it would sit there as NoSpace forever, and the seed would be gone.
                    var prefab = ZNetScene.instance != null
                        ? ZNetScene.instance.GetPrefab(species.Sapling)
                        : null;
                    if (!PlantSpace.CanGrow(prefab, p, out _)) continue;

                    spot = p;
                    return true;
                }
            }

            return false;
        }

        /// <summary>Wood types the village currently has orders for, at this Lumberjack's station.</summary>
        private HashSet<string> WantedWoodTypes()
        {
            var wanted = new HashSet<string>();
            var village = VillageRegistry.GetVillageAt(m_ai.HomeAnchor);
            if (village == null) return wanted;

            foreach (var order in ContainerScanner.FindAllWorkOrders(village, m_ai.VillagerType))
                if (!string.IsNullOrEmpty(order.ItemPrefabName))
                    wanted.Add(order.ItemPrefabName);
            return wanted;
        }

        private void Reset()
        {
            m_active = false;
            m_errand = Errand.None;
            m_navIssued = false;
            m_charge = ChargePhase.None;
            m_tree = null;
            m_log = null;
            m_drop = null;
            m_chest = null;
            m_dud = null;
            m_plantSpecies = null;
            m_plantSeed = null;
            m_plantSpot = Vector3.zero;
            m_ai.SetState(BehaviorState.Idle);
        }

        /// <summary>Stages of one charge: back off, stamp, run it down.</summary>
        private enum ChargePhase
        {
            None,
            BackingOff,
            WindUp,
            Running,
        }

        private enum Errand
        {
            None,
            Plant,
            Clear,
            Fell,
            BreakLog,
            Collect,
            Deliver,
        }
    }
}
