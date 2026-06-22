using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Behaviors.Wander
{
    /// <summary>
    ///     Low-priority idle ambling for villagers with no work role (e.g. the
    ///     Mountaineer, whose JSON has no work behaviors). When idle, pick a random
    ///     walkable point within a short radius of the home anchor on the villager
    ///     (slot-31) navmesh and stroll to it, then idle and re-arm after a cooldown —
    ///     which reads as the villager mooching around the village instead of standing
    ///     frozen on its anchor.
    ///
    ///     <para>Purely cosmetic idle filler: priority sits below patrol and far below the
    ///     reactive floor, so flee/combat still preempt. Not an <see cref="IDirectedBehavior" />
    ///     — there is no scheduler task for it; it runs in the routine (step-3) slot in
    ///     PrimaryMode and in the normal dispatch loop otherwise. Tag: "wander".</para>
    /// </summary>
    [RegisterBehavior("wander")]
    public class WanderBehavior : IBehavior
    {
        private const float WanderInterval = 6f; // min idle seconds between strolls
        private const float WanderRadius = 12f;  // how far from the anchor to roam
        private const int SampleAttempts = 6;
        private const float SnapRadius = 2f;

        private readonly VillagerAI m_ai;
        private bool m_active;
        private float m_nextWanderTime;

        public WanderBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "wander";

        // Below patrol(30); pure idle filler. Reactive behaviors (flee/combat=100) preempt.
        public int Priority => 20;

        public bool WantsControl(BehaviorContext ctx)
        {
            if (m_active) return true;
            if (m_ai.CurrentState != BehaviorState.Idle) return false;
            if (Time.time < m_nextWanderTime) return false;
            return TryPickDestination();
        }

        public void Update(float dt)
        {
            // The stroll is a single NavTo issued in TryPickDestination; the mover carries
            // it out. When the villager has settled back to Idle (arrived, or the mover
            // gave up), drop control so WantsControl re-arms after the cooldown.
            if (m_active && m_ai.CurrentState == BehaviorState.Idle)
                m_active = false;
        }

        public void OnArrival(float dt)
        {
            m_active = false;
            m_nextWanderTime = Time.time + WanderInterval;
            m_ai.SetState(BehaviorState.Idle);
        }

        public string GetStatusText()
        {
            return m_active ? "Wandering" : "";
        }

        /// <summary>
        ///     Sample a random walkable point in a disc around the home anchor and start
        ///     strolling to it. False when no walkable point is found (then back off so we
        ///     don't probe every tick).
        /// </summary>
        private bool TryPickDestination()
        {
            if (!VillagerAgentType.IsRegistered) return false;
            var anchor = m_ai.HomeAnchor;
            if (anchor == Vector3.zero) return false;

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            for (var i = 0; i < SampleAttempts; i++)
            {
                // Uniform point in the disc (sqrt keeps it from clustering at the centre).
                var ang = Random.value * Mathf.PI * 2f;
                var dist = Mathf.Sqrt(Random.value) * WanderRadius;
                var probe = anchor + new Vector3(Mathf.Cos(ang) * dist, 0f, Mathf.Sin(ang) * dist);
                if (!NavMesh.SamplePosition(probe, out var hit, SnapRadius, filter)) continue;

                m_active = true;
                m_nextWanderTime = Time.time + WanderInterval;
                if (m_ai.NavTo(hit.position, BehaviorState.Wandering, "wander: stroll",
                        snapToApproach: false))
                    return true;

                m_active = false;
                return false;
            }

            m_nextWanderTime = Time.time + WanderInterval;
            return false;
        }
    }
}
