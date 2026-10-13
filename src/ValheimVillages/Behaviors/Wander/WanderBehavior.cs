using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.Villages.Entity;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Behaviors.Wander
{
    /// <summary>
    ///     The default idle activity for EVERY villager with no workable task (auto-added in
    ///     <c>VillagerAI.RegisterBehaviors</c>). When idle, pick a random
    ///     walkable point within a short radius of the home anchor on the villager
    ///     (slot-31) navmesh and stroll to it, then idle and re-arm after a cooldown —
    ///     which reads as the villager mooching around the village instead of standing
    ///     frozen on its anchor.
    ///
    ///     <para>Idle filler: priority sits below relax (25, which takes over when a drive is
    ///     elevated), below patrol, and far below the reactive floor, so flee/combat still
    ///     preempt. Not an <see cref="IDirectedBehavior" />
    ///     — there is no scheduler task for it; it runs in the routine (step-3) slot, only
    ///     reached when the scheduler had nothing to dispatch. Tag: "wander".</para>
    /// </summary>
    [RegisterBehavior("wander")]
    public class WanderBehavior : IBehavior
    {
        private const float WanderInterval = 6f; // min idle seconds between strolls
        private const float WanderRadius = 12f;  // how far from the anchor to roam
        private const int SampleAttempts = 6;
        private const float SnapRadius = 2f;

        /// <summary>
        ///     How far a villager roams at night, centred on their own cot rather than the
        ///     anchor. Tighter than the day radius: the point is that they settle in around
        ///     where they sleep, not that they take a shorter walk somewhere else.
        ///
        ///     <para>An outer bound only — at night the binding constraint is the cot's REGION,
        ///     not this distance. A room larger than the radius is still fully available, and a
        ///     point this close but through a wall is not.</para>
        /// </summary>
        private const float NightWanderRadius = 7f;

        private readonly VillagerAI m_ai;
        private bool m_active;
        private float m_nextWanderTime;

        public WanderBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "wander";

        // Below relax(25) and patrol(30); idle filler. Reactive behaviors (flee/combat=100) preempt.
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

            var night = IsNight();
            var hasCot = TryGetCot(out var cot);

            // At night the disc is centred on the COT, not the anchor. Weighting alone is not
            // enough: the anchor is the village registry, and a cot can sit further from it than
            // the whole wander radius, so a villager scored toward their bed would still never
            // sample a point near it.
            var centre = night && hasCot ? cot : anchor;
            var radius = night && hasCot ? NightWanderRadius : WanderRadius;

            // At night a villager stays in the ROOM their cot is in. Region, not distance:
            // rooms are what the region graph already partitions the village into, so this
            // respects walls and doorways for free — a spot three metres away but through a
            // wall is correctly rejected, and a large room stays fully available.
            var confineToRegion = night && hasCot;
            var graph = confineToRegion ? VillageRegistry.GraphAt(cot) : null;
            string cotRegion = null;

            // NO FALLBACK. If the cot's room cannot be resolved there is no "roughly near the
            // bed" to retreat to — wandering unconfined around an unmapped cot is how a villager
            // ends up walking at a wall all night. Skip the stroll and stand still instead; the
            // unresolved region is already visible in vv_quarters.
            if (confineToRegion && !TryResolveCotRegion(graph, cot, out cotRegion))
            {
                m_nextWanderTime = Time.time + WanderInterval;
                return false;
            }

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            // Best of N rather than first-valid. Taking the first walkable sample gives the cot
            // preference nothing to express itself through — every candidate is equally
            // acceptable, so the villager drifts wherever the first probe happened to land.
            m_candidates.Clear();

            for (var i = 0; i < SampleAttempts; i++)
            {
                // Uniform point in the disc (sqrt keeps it from clustering at the centre).
                var ang = Random.value * Mathf.PI * 2f;
                var dist = Mathf.Sqrt(Random.value) * radius;
                var probe = centre + new Vector3(Mathf.Cos(ang) * dist, 0f, Mathf.Sin(ang) * dist);
                if (!NavMesh.SamplePosition(probe, out var hit, SnapRadius, filter)) continue;

                // Out of the bedroom — reject outright rather than merely scoring it down, so a
                // night with no in-room candidate leaves the villager where they are instead of
                // picking the least-bad spot outside.
                if (confineToRegion && graph.PointToRegionId(hit.position) != cotRegion) continue;

                m_candidates.Add((hit.position, Score(hit.position, hasCot, cot, night)));
            }

            m_nextWanderTime = Time.time + WanderInterval;
            if (m_candidates.Count == 0) return false;

            // Best first, then take the first one a COMPLETE path reaches. SamplePosition only
            // says a point is on the mesh, never that the villager can walk to it, and a partial
            // path is exactly how one ends up jogging into a wall and standing there pushing.
            // Usually the best candidate is reachable and this costs a single path query.
            m_candidates.Sort(static (a, b) => b.score.CompareTo(a.score));

            var best = Vector3.zero;
            var found = false;
            foreach (var candidate in m_candidates)
            {
                if (!IsReachable(candidate.position, filter)) continue;
                best = candidate.position;
                found = true;
                break;
            }

            if (!found) return false;

            m_active = true;
            if (m_ai.NavTo(best, BehaviorState.Wandering, "wander: stroll", snapToApproach: false))
            {
                m_ai.IsCasualTravel = true; // SetState cleared it — re-set after NavTo; walk, don't jog
                return true;
            }

            m_active = false;
            return false;
        }

        /// <summary>Candidate buffer, reused so a stroll every few seconds allocates nothing.</summary>
        private readonly System.Collections.Generic.List<(Vector3 position, float score)>
            m_candidates = new();

        /// <summary>Reused so each reachability test does not allocate a path object.</summary>
        private readonly NavMeshPath m_path = new();

        /// <summary>
        ///     Can the villager actually WALK there? A complete path only — a partial one means
        ///     the navmesh gets part of the way and then stops, which the agent happily steers
        ///     along until it is pressed against whatever ended it.
        /// </summary>
        private bool IsReachable(Vector3 target, NavMeshQueryFilter filter)
        {
            if (!NavMesh.CalculatePath(m_ai.Position, target, filter, m_path)) return false;
            return m_path.status == NavMeshPathStatus.PathComplete;
        }

        /// <summary>
        ///     How much a villager wants to stand here. Near their cot after dark, away from it
        ///     during the day — so a village looks like it turns in at night and gets on with
        ///     things by day, without anyone being pinned in place.
        ///
        ///     <para>At night this only orders candidates the region filter already accepted; it
        ///     is the filter, not this, that keeps them in the room.</para>
        ///
        ///     <para>Flat when the villager has no cot: with nothing to be near or away from,
        ///     every walkable point is equally good and the pick stays uniform.</para>
        /// </summary>
        private static float Score(Vector3 candidate, bool hasCot, Vector3 cot, bool night)
        {
            if (!hasCot) return 0f;

            var distance = Vector3.Distance(candidate, cot);
            return night ? -distance : distance;
        }

        /// <summary>
        ///     Which region the cot's ROOM is, resolved through the lookup grid.
        ///
        ///     <para>Asking <c>PointToRegionId</c> for the cot's own position does not work: a
        ///     piece's origin sits inside its own collider and off the walkable surface, so it
        ///     resolves to NO region — measured, with two of three test cots reporting none. The
        ///     night rule would then have silently fallen back to a plain radius and let
        ///     villagers wander through walls to get near their bed.</para>
        ///
        ///     <para><c>TryFindNearestLookupCell</c> is the canonical fix: lookup-grid cells are
        ///     by construction points that <c>PointToRegionId</c> agrees with.</para>
        /// </summary>
        private static bool TryResolveCotRegion(RegionGraph graph, Vector3 cot, out string regionId)
        {
            regionId = null;
            if (graph == null) return false;

            regionId = graph.PointToRegionId(cot);
            if (!string.IsNullOrEmpty(regionId)) return true;

            // Bounded: a cot whose room is further than this from any indexed cell is not in a
            // room the graph knows about, and confining to a region across the village would be
            // worse than not confining at all.
            if (!graph.TryFindNearestLookupCell(cot, null, out _, out regionId, CotRegionSearch))
                return false;

            return !string.IsNullOrEmpty(regionId);
        }

        /// <summary>How far from the cot to look for an indexed walkable cell.</summary>
        private const float CotRegionSearch = 4f;

        /// <summary>
        ///     This villager's cot, if one is assigned and loaded. Reads through the quarters
        ///     index, which is rebuilt at most every few seconds from one scene walk — wander
        ///     picks a destination at most every <see cref="WanderInterval" /> seconds, so this
        ///     costs a dictionary lookup.
        /// </summary>
        private bool TryGetCot(out Vector3 position)
        {
            position = Vector3.zero;
            var cot = Villager.Levels.VillagerQuarters.FindCot(m_ai.UniqueId);
            if (cot == null) return false;

            position = cot.transform.position;
            return true;
        }

        /// <summary>
        ///     Night by the mod's own thresholds, read straight from EnvMan.
        ///
        ///     <para>Deliberately NOT via <c>BehaviorLogic.GetTimeOfDay</c>, which reports night
        ///     as Evening on purpose ("sleep disabled for debugging") so villagers stay awake —
        ///     asking it would mean this preference never once fired after dark.</para>
        /// </summary>
        private static bool IsNight()
        {
            if (EnvMan.instance == null) return false;

            var fraction = EnvMan.instance.GetDayFraction();
            return fraction >= VillagerSettings.NightStart || fraction < VillagerSettings.MorningStart;
        }
    }
}
