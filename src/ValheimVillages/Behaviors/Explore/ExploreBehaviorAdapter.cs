using System.Linq;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Behaviors.Explore
{
    /// <summary>
    ///     Universal lowest-priority behavior handling idle, wander, and location-seeking.
    ///     Auto-injected for every NPC; only runs when no higher-priority behavior wants control.
    ///     Scores known locations by context (time, weather, shelter) and moves the NPC
    ///     toward the best one. Also triggers work-order scanning for crafting NPCs.
    /// </summary>
    [RegisterBehavior("explore")]
    public class ExploreBehaviorAdapter : IBehavior
    {
        private readonly VillagerAI m_ai;

        public ExploreBehaviorAdapter(VillagerAI ai)
        {
            m_ai = ai;
        }

        public string Tag => "explore";
        public int Priority => 20;

        public bool WantsControl(BehaviorContext ctx)
        {
            return true;
        }

        public void Update(float dt)
        {
            var context = VillagerBehaviorLogic.GetCurrentContext(m_ai);

            var workScanner = m_ai.GetWorkScanner();
            if (workScanner != null && m_ai.CurrentState != BehaviorState.Working)
                workScanner.TryScanForWork();

            if (m_ai.CurrentState == BehaviorState.Exploring)
            {
                m_ai.SetState(BehaviorState.Idle);
                return;
            }

            // Linger window from a recently-finished work step: the villager
            // is parked next to a station that's still processing (smelter,
            // cooking) and will need them back shortly. Suppress location-
            // seeking so the villager doesn't visibly walk to the fire and
            // immediately walk back. The work scanner above still runs —
            // if a new work order matches, it preempts the linger.
            if (m_ai.IsLingering)
            {
                m_ai.SetState(BehaviorState.Idle);
                return;
            }

            var bestLocation = SelectBestKnownLocation(context);
            if (bestLocation != null)
                TransitionToLocation(bestLocation);
            else
                m_ai.SetState(BehaviorState.Idle);
        }

        public void OnArrival(float dt)
        {
            m_ai.SetState(BehaviorState.Idle);
        }

        public string GetStatusText()
        {
            return m_ai.CurrentState switch
            {
                BehaviorState.Traveling => "Walking...",
                BehaviorState.Idle => "Idle",
                _ => "",
            };
        }

        public void Save(ZDO zdo)
        {
        }

        public void Load(ZDO zdo)
        {
        }

        private KnownLocation SelectBestKnownLocation(BehaviorContext context)
        {
            var locations = m_ai.GetMemory().KnownLocations;
            if (locations.Count == 0) return null;

            return locations
                .Select(loc => new { Location = loc, Score = ScoreLocation(loc, context) })
                .OrderByDescending(x => x.Score)
                .Select(x => x.Location)
                .FirstOrDefault();
        }

        private float ScoreLocation(KnownLocation loc, BehaviorContext context)
        {
            var score = 0f;
            var distance = Vector3.Distance(context.CurrentPosition, loc.Position);

            if (context.TimeOfDay == TimeOfDay.Night)
            {
                if (loc.Type == LocationType.Bed) score += 100f;
                else if (loc.HasShelter) score += 10f;
                else return -100f;
            }

            if (context.IsRaining && !context.InShelter)
            {
                if (loc.HasShelter) score += 30f;
                else score -= 20f;
            }

            switch (loc.Type)
            {
                case LocationType.Fire: score += 12f; break;
                case LocationType.Table: score += 8f; break;
                case LocationType.Bed: score += 5f; break;
                case LocationType.Shelter: score += 5f; break;
                case LocationType.Farm: score += 6f; break;
                case LocationType.Animals: score += 6f; break;
            }

            score -= distance * 0.3f;
            score += loc.ComfortValue * 5f;

            if (m_ai.CurrentTarget.HasValue && loc.IsSameLocation(m_ai.CurrentTarget.Value))
                score -= 20f;

            return score;
        }

        private void TransitionToLocation(KnownLocation location)
        {
            if (!VillagerMovement.IsAtPosition(m_ai.Position, location.Position, VillagerSettings.ArrivalThreshold))
            {
                m_ai.SetState(BehaviorState.Traveling, location.Position);
                // Mark this as casual (Explore-initiated) travel so the
                // path-follow loop walks instead of runs — visual cue
                // that the villager isn't on a work errand. SetState
                // clears IsCasualTravel by default, so this re-arms it
                // for the freshly-set waypoint only.
                m_ai.IsCasualTravel = true;
            }
        }
    }
}