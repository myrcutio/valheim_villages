using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Behaviors.Combat
{
    /// <summary>
    ///     Non-combatant reaction to danger. When a hostile comes within
    ///     <see cref="CombatSettings.FleeDangerRadius"/>, the villager panics and
    ///     runs — toward the nearest guard (any villager with a
    ///     <see cref="CombatBehavior"/>) if one is on the roster, otherwise directly
    ///     away from the threat. Calms (yields back to work/patrol) once the threat
    ///     is beyond <see cref="CombatSettings.FleeClearRadius"/>.
    ///
    ///     <para>Auto-added by <c>VillagerAI.RegisterBehaviors</c> to every villager
    ///     that is NOT itself a combatant, so "any non-guard flees" without each
    ///     definition having to opt in. Tag: "flee", Priority: 100 — preempts work
    ///     while in danger.</para>
    /// </summary>
    [RegisterBehavior("flee")]
    public class FleeBehavior : IBehavior
    {
        private const float FleeTickSec = 0.1f;

        private readonly VillagerAI m_ai;

        private float m_lastRepathTime;
        private float m_lastScanTime;
        private bool m_runningToGuard;
        private Character m_threat;

        public FleeBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "flee";

        // Preempts work/patrol while the villager is in danger.
        public int Priority => 100;

        public bool WantsControl(BehaviorContext ctx)
        {
            // Keep fleeing while the current threat is still within the (larger)
            // clear radius — hysteresis so panic doesn't flicker at the boundary.
            if (IsStillDangerous(m_threat))
                return true;

            if (Time.time - m_lastScanTime < CombatSettings.TargetRescanInterval)
                return false;
            m_lastScanTime = Time.time;

            m_threat = FindNearestThreat(CombatSettings.FleeDangerRadius);
            return m_threat != null;
        }

        public void Update(float dt)
        {
            if (!IsStillDangerous(m_threat))
            {
                Calm();
                return;
            }

            m_ai.RequestFastReselect(FleeTickSec);

            // Repath — and the non-trivial work that feeds it (village resolve, guard scope,
            // graph clamp) — only on the repath cadence, not on every 0.1s reselect tick.
            // During a raid many non-combatants flee at once; doing this 10x/s each was a
            // measurable hot path. Threat-clear (Calm, above) is still checked at 10Hz, so
            // reaction to safety stays snappy; only the pathing work is throttled.
            if (Time.time - m_lastRepathTime < CombatSettings.ChaseRepathInterval)
                return;
            m_lastRepathTime = Time.time;

            var myPos = m_ai.Position;
            var threatPos = m_threat.transform.position;

            // Resolve the fleer's OWN village once: scopes the guard search to this village
            // AND provides the graph the destination is clamped onto, so flee can never send
            // the villager toward another village's guard or to an off-graph SafeSpot, where
            // the agent would path across the unioned multi-village navmesh and strand it.
            var myVillage = Villages.Entity.VillageRegistry.GetVillageAt(m_ai.HomeAnchor);
            var graph = myVillage?.Graph;

            var guard = FindNearestGuard(graph);

            // Only run TO the guard when the guard is FARTHER from the enemy than we
            // are — then running to it moves us away from danger and behind its line.
            // If the guard is closer to the enemy than we are (it's already moving in
            // to engage), running to it would carry us toward the threat — so instead
            // retreat to a safe spot (home / directly away) and sit tight.
            Vector3 dest;
            if (guard != null &&
                (guard.Position - threatPos).sqrMagnitude > (myPos - threatPos).sqrMagnitude)
            {
                m_runningToGuard = true;
                dest = guard.Position;
            }
            else
            {
                m_runningToGuard = false;
                dest = SafeSpot(myPos, threatPos);
            }

            // Clamp the destination onto the fleer's village graph so the agent never paths
            // off-village. A same-village guard is already on-graph (no-op); SafeSpot may not
            // be. If the desired dest is too far from the graph, snap to a cell near the
            // villager's current position instead — anything on THIS graph beats an off-graph run.
            if (graph != null)
            {
                // Scope the clamp to anchor-REACHABLE cells so flee can never target a
                // disconnected far limb of the raw bake. Validator is null (unconstrained)
                // when the graph carries no committed classification — e.g. a blob-hydrated
                // client — so client-side behavior is unchanged.
                var reachable = graph.HasAnchorReachableClassification
                    ? (System.Func<Vector3, bool>)graph.IsAnchorReachableCell
                    : null;
                if (graph.TryFindNearestLookupCell(dest, reachable, out var onGraph, out _,
                        CombatSettings.FleeGraphClampRadius))
                    dest = onGraph;
                else if (graph.TryFindNearestLookupCell(myPos, reachable, out var hereCell, out _,
                             CombatSettings.FleeGraphClampRadius))
                    dest = hereCell;
            }

            m_ai.NavTo(dest, BehaviorState.Alarmed, "flee", snapToApproach: false);
        }

        public void OnArrival(float dt)
        {
            // No-op: fleeing is re-evaluated every tick against a moving threat.
        }

        public string GetStatusText()
        {
            if (m_threat == null) return "";
            return m_runningToGuard ? "Fleeing to a guard!" : "Hiding from danger!";
        }

        // --- helpers -------------------------------------------------------

        private bool IsStillDangerous(Character c)
        {
            if (c == null || c.IsDead()) return false;
            var clearSq = CombatSettings.FleeClearRadius * CombatSettings.FleeClearRadius;
            return (c.transform.position - m_ai.Position).sqrMagnitude <= clearSq;
        }

        private Character FindNearestThreat(float radius)
        {
            var me = m_ai.Character;
            var myPos = m_ai.Position;
            var radiusSq = radius * radius;

            Character best = null;
            var bestSq = float.MaxValue;
            foreach (var c in Character.GetAllCharacters())
            {
                if (c == null || c == me || c.IsDead()) continue;
                if (!BaseAI.IsEnemy(me, c)) continue;
                var dsq = (c.transform.position - myPos).sqrMagnitude;
                if (dsq > radiusSq) continue;
                if (dsq < bestSq)
                {
                    bestSq = dsq;
                    best = c;
                }
            }

            return best;
        }

        /// <summary>
        ///     Nearest active combatant villager (one with a <see cref="CombatBehavior"/>) in
        ///     the SAME village as the fleer, or null. Village-scoped so a panicked villager
        ///     never flees toward a guard in another village (which would path it off its own
        ///     graph). A guard is in-village when its stable home anchor resolves to a region
        ///     on the fleer's own <paramref name="graph"/>. If the fleer's graph can't be
        ///     resolved (<paramref name="graph"/> null), falls back to nearest-overall rather
        ///     than excluding every guard.
        /// </summary>
        private VillagerAI FindNearestGuard(RegionGraph graph)
        {
            var myPos = m_ai.Position;
            VillagerAI best = null;
            var bestSq = float.MaxValue;
            foreach (var kv in VillagerAIManager.ActiveVillagers)
            {
                var v = kv.Value;
                if (v == null || v == m_ai) continue;
                if (v.GetBehavior<CombatBehavior>() == null) continue;
                // Same-village only: a guard counts when its (stable) home anchor resolves to
                // a region on the fleer's OWN graph — one O(1) lookup-grid hit, vs re-scanning
                // all world ZDOs per guard (GetVillageAt). When the fleer's graph is
                // unresolved, fall back to nearest-overall rather than excluding every guard.
                if (graph != null && graph.PointToRegionId(v.HomeAnchor) == null) continue;

                var dsq = (v.Position - myPos).sqrMagnitude;
                if (dsq < bestSq)
                {
                    bestSq = dsq;
                    best = v;
                }
            }

            return best;
        }

        /// <summary>
        ///     A spot to retreat to when running to the guard isn't safe. Prefers
        ///     home (typically indoors) when that moves us AWAY from the threat;
        ///     otherwise backs directly away from it.
        /// </summary>
        private Vector3 SafeSpot(Vector3 myPos, Vector3 threatPos)
        {
            var myDistSq = (myPos - threatPos).sqrMagnitude;
            var anchor = m_ai.HomeAnchor;
            if (anchor != Vector3.zero && (anchor - threatPos).sqrMagnitude > myDistSq)
                return anchor; // home is farther from the threat — hole up indoors

            var away = myPos - threatPos;
            away.y = 0f;
            if (away.sqrMagnitude < 0.0001f) away = m_ai.Character.transform.forward;
            return myPos + away.normalized * CombatSettings.FleeDistance;
        }

        private void Calm()
        {
            m_threat = null;
            m_runningToGuard = false;
            if (m_ai.CurrentState != BehaviorState.Idle)
                m_ai.SetState(BehaviorState.Idle);
        }
    }
}
