using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;

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

            var myPos = m_ai.Position;
            var threatPos = m_threat.transform.position;
            var guard = FindNearestGuard();

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

            if (Time.time - m_lastRepathTime >= CombatSettings.ChaseRepathInterval)
            {
                m_lastRepathTime = Time.time;
                m_ai.NavTo(dest, BehaviorState.Alarmed, "flee", snapToApproach: false);
            }
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

        /// <summary>Nearest active combatant villager (one that has a <see cref="CombatBehavior"/>), or null.</summary>
        private VillagerAI FindNearestGuard()
        {
            var myPos = m_ai.Position;
            VillagerAI best = null;
            var bestSq = float.MaxValue;
            foreach (var kv in VillagerAIManager.ActiveVillagers)
            {
                var v = kv.Value;
                if (v == null || v == m_ai) continue;
                if (v.GetBehavior<CombatBehavior>() == null) continue;
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
            var bed = m_ai.HomeAnchor;
            if (bed != Vector3.zero && (bed - threatPos).sqrMagnitude > myDistSq)
                return bed; // home is farther from the threat — hole up indoors

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
