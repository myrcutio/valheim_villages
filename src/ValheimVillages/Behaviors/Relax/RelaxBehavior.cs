using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Scheduling;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villages;

namespace ValheimVillages.Behaviors.Relax
{
    /// <summary>
    ///     Lowest-priority idle filler: when a villager has nothing better to do it ambles
    ///     over to a comfort spot — a fire, a table, a seat ("sit"), or the hot tub — and
    ///     relaxes there until real work calls.
    ///
    ///     <para>Modeled on <see cref="Wander.WanderBehavior" />: a routine
    ///     <see cref="IBehavior" /> (NOT an <see cref="Interfaces.IDirectedBehavior" />), so
    ///     it is never dispatched by the reranker and always sits in the step-3 routine slot
    ///     in PrimaryMode / last-by-priority otherwise. Its priority (10) is below wander (20)
    ///     and patrol (30) and far below the reactive floor (100), so ANY real work — reactive
    ///     (combat/flee) or scheduler work (craft/repair/cook/farm) — preempts it on the next
    ///     reselect tick. That is the "always interruptible for any real work" contract: relax
    ///     is only ever selected when nothing else wanted control.</para>
    ///
    ///     <para>Relax spots come from the village-level <see cref="VillagePoiRegistry" />,
    ///     which already classifies Fire/Table/Chair and (now) the hot tub. The chosen spot is
    ///     reserved on the shared <see cref="TaskBoard" /> (the villager work table) so
    ///     villagers spread across spots instead of stacking on one; the soft TTL claim is
    ///     released when the villager moves on, and auto-expires if it gets pulled into work.</para>
    ///
    ///     <para>Villagers do not literally sit — the chair-attach / sit-emote pipeline is
    ///     Player-only in the engine (empty <c>Character</c> stubs) and the Dvergr rig has no
    ///     sit pose — so "relax" means navigate to the spot and idle beside it.</para>
    /// </summary>
    [RegisterBehavior("relax")]
    public class RelaxBehavior : IBehavior
    {
        // Min idle seconds between relax sessions (and the back-off when no spot is found),
        // so a village with no comfort furniture doesn't re-scan every reselect tick.
        private const float RelaxCooldown = 8f;

        // How long to linger at a spot before releasing it and re-arming, so villagers
        // drift between spots over time and a freed spot can rotate to someone else.
        private const float DwellSeconds = 30f;

        // If the villager ends up this far from its relax spot while supposedly lingering
        // (i.e. it was preempted by work and returned to idle elsewhere), drop the session.
        private const float DisplacedRadius = 6f;

        private readonly VillagerAI m_ai;

        private bool m_active;          // a relax session is in progress (traveling or lingering)
        private bool m_arrived;         // reached the spot and now lingering
        private float m_nextRelaxTime;  // cooldown gate
        private float m_dwellUntil;     // when the current lingering session ends
        private Vector3 m_approach;     // resolved navmesh point we travel to / linger at
        private Vector3 m_spot;         // the PoI centre (for status text)
        private LocationType m_spotType;
        private string m_claimId;       // TaskBoard reservation key for the chosen spot

        public RelaxBehavior(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "relax";

        // The absolute floor of the routine tier: below wander(20)/patrol(30) and far below
        // the reactive floor(100). Everything else a villager could do outranks relaxing.
        public int Priority => 10;

        public bool WantsControl(BehaviorContext ctx)
        {
            if (m_active) return true;
            if (m_ai.CurrentState != BehaviorState.Idle) return false;
            if (m_ai.IsInBackoff) return false;
            if (Time.time < m_nextRelaxTime) return false;
            return TryPickSpot();
        }

        public void Update(float dt)
        {
            if (!m_active) return;

            // Keep the work-board reservation fresh while we hold the spot. ClaimTtl (20s) is
            // shorter than DwellSeconds (30s), so without refreshing, a lingering villager's
            // claim would lapse mid-relax and another villager could double-book the spot.
            if (m_claimId != null)
                TaskBoard.Claim(m_claimId, m_ai.UniqueId, Time.time);

            var nearSpot = Vector3.Distance(m_ai.Position, m_approach) <= DisplacedRadius;

            // Mover settled back to Idle after travel. (OnArrival is the normal arrival path;
            // this covers the mover-gave-up case.) If we're at the spot, start lingering; if
            // it gave up far away, drop the session so the next idle re-picks cleanly.
            if (!m_arrived && m_ai.CurrentState == BehaviorState.Idle)
            {
                if (nearSpot) BeginDwell();
                else { EndSession(); return; }
            }

            if (!m_arrived) return;

            // Pulled away from the spot (preempted by work, then returned to idle elsewhere):
            // end the session so the next idle tick re-picks cleanly from the current position.
            if (!nearSpot)
            {
                EndSession();
                return;
            }

            // Dwell elapsed → release the spot and back off so the villager can drift to a
            // different spot next time (and so any real work can slot in between sessions).
            if (Time.time >= m_dwellUntil)
                EndSession();
        }

        public void OnArrival(float dt)
        {
            // Reached the approach point — stop and linger, don't bounce onward.
            m_ai.SetState(BehaviorState.Idle);
            BeginDwell();
        }

        public string GetStatusText()
        {
            if (!m_active) return "";
            var where = WhereText(m_spotType);
            return m_arrived ? $"Relaxing {where}" : $"Off to relax {where}";
        }

        /// <summary>
        ///     Find the nearest reachable comfort spot in this villager's village, preferring
        ///     one no other villager has reserved, reserve it on the work board, and amble
        ///     over. Backs off (no spam) when there's nothing to relax at.
        /// </summary>
        private bool TryPickSpot()
        {
            var anchor = m_ai.HomeAnchor;
            if (anchor == Vector3.zero)
            {
                BackOff();
                return false;
            }

            var pois = VillagePoiRegistry.GetPois(anchor);
            if (pois == null || pois.Count == 0)
            {
                BackOff();
                return false;
            }

            var from = m_ai.Position;
            var candidates = new List<KnownLocation>();
            foreach (var p in pois)
                if (IsRelaxType(p.Type))
                    candidates.Add(p);

            if (candidates.Count == 0)
            {
                BackOff();
                return false;
            }

            candidates.Sort((a, b) =>
                (a.Position - from).sqrMagnitude.CompareTo((b.Position - from).sqrMagnitude));

            var me = m_ai.UniqueId;
            var now = Time.time;

            // Pass 0: nearest unreserved spot. Pass 1: nearest of any (so a lone spot is
            // still used when every spot is already taken — relaxing together is fine).
            for (var pass = 0; pass < 2; pass++)
            {
                var preferUnclaimed = pass == 0;
                foreach (var poi in candidates)
                {
                    var id = SpotId(poi);
                    if (preferUnclaimed && TaskBoard.IsClaimedByOther(id, me, now)) continue;
                    if (!VillagerMovement.TryResolveApproach(poi.Position, from, null, out var approach))
                        continue;

                    m_spot = poi.Position;
                    m_spotType = poi.Type;
                    m_approach = approach;
                    m_claimId = id;
                    TaskBoard.Claim(id, me, now);

                    // Pre-resolved approach → snapToApproach:false (NavTo still snaps the
                    // point onto the agent mesh). Casual travel = amble, not sprint.
                    if (m_ai.NavTo(approach, BehaviorState.Traveling, $"relax: {poi.Type}",
                            snapToApproach: false))
                    {
                        m_active = true;
                        m_arrived = false;
                        m_ai.IsCasualTravel = true; // SetState cleared it — re-set after NavTo
                        return true;
                    }

                    // NavTo declined — release the reservation and keep looking.
                    TaskBoard.Release(id);
                    m_claimId = null;
                }
            }

            BackOff();
            return false;
        }

        private void BeginDwell()
        {
            m_arrived = true;
            m_dwellUntil = Time.time + DwellSeconds;
        }

        private void EndSession()
        {
            if (m_claimId != null)
            {
                TaskBoard.Release(m_claimId);
                m_claimId = null;
            }

            m_active = false;
            m_arrived = false;
            m_nextRelaxTime = Time.time + RelaxCooldown;
            if (m_ai.CurrentState != BehaviorState.Idle)
                m_ai.SetState(BehaviorState.Idle);
        }

        private void BackOff()
        {
            m_nextRelaxTime = Time.time + RelaxCooldown;
        }

        private static bool IsRelaxType(LocationType t)
        {
            return t == LocationType.Fire || t == LocationType.Table ||
                   t == LocationType.Chair || t == LocationType.HotTub;
        }

        private static string WhereText(LocationType t)
        {
            return t switch
            {
                LocationType.Fire => "by the fire",
                LocationType.HotTub => "in the hot tub",
                LocationType.Table => "at the table",
                LocationType.Chair => "on a seat",
                _ => "",
            };
        }

        // Stable per-spot reservation key (PoIs have no ZDOID; quantised XZ is stable across
        // re-scans because the dedup in VillagePoiRegistry keeps each spot at a fixed point).
        private static string SpotId(KnownLocation poi)
        {
            return $"relax:{poi.Type}:{poi.Position.x:F0}:{poi.Position.z:F0}";
        }
    }
}
