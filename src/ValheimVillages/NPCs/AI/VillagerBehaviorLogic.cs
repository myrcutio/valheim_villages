using System.Linq;
using UnityEngine;
using ValheimVillages;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Behavior decision logic for villager AI.
    /// Decides what the villager should do based on time of day, known locations, and context.
    /// </summary>
    public static class VillagerBehaviorLogic
    {
        /// <summary>
        /// Main behavior update: evaluate context and pick the next action.
        /// </summary>
        public static void UpdateBehavior(VillagerAI ai)
        {
            var context = GetCurrentContext(ai);

            // Worker NPCs: scan for work orders when idle during daytime
            if (ai.CraftingBehavior != null && context.TimeOfDay != TimeOfDay.Night
                && ai.CurrentState != BehaviorState.Working)
            {
                ai.CraftingBehavior.TryScanForWork();
            }

            // Handle ongoing exploration
            if (ai.CurrentState == BehaviorState.Exploring && VillagerExploration.ContinueExploration(ai))
                return;

            // Pick best known location for current context
            var bestLocation = SelectBestKnownLocation(ai, context);

            if (bestLocation != null)
            {
                if (VillagerExploration.ShouldExplore(ai, context))
                {
                    VillagerExploration.StartExploration(ai);
                    return;
                }

                TransitionToLocation(ai, bestLocation, context);
            }
            else if (VillagerExploration.ShouldExplore(ai, context))
            {
                VillagerExploration.StartExploration(ai);
            }
            else
            {
                ai.SetState(BehaviorState.Idle);
            }
        }

        /// <summary>
        /// End exploration (delegated from VillagerAI for convenience).
        /// </summary>
        public static void EndExploration(VillagerAI ai, string reason)
        {
            VillagerExploration.EndExploration(ai, reason);
        }

        /// <summary>
        /// Handle arrival at a destination based on current behavior state.
        /// </summary>
        public static void HandleArrival(VillagerAI ai)
        {
            switch (ai.CurrentState)
            {
                case BehaviorState.Sleeping:
                    ai.Instance.StopMoving();
                    ai.SetState(BehaviorState.Sleeping); // Keep sleeping, clear target
                    Plugin.Log?.LogDebug($"[AI:{ai.NpcName}] Sleeping at bed");
                    break;

                case BehaviorState.Patrolling:
                    var nextPatrol = ai.Memory.KnownLocations
                        .Where(l => l.Type == LocationType.Patrol && !l.IsSameLocation(ai.Position.ToVec3()))
                        .OrderBy(_ => Random.value)
                        .FirstOrDefault();
                    if (nextPatrol != null)
                    {
                        ai.SetState(BehaviorState.Patrolling, nextPatrol.Position.ToVector3());
                        Plugin.Log?.LogDebug($"[AI:{ai.NpcName}] Next patrol point");
                    }
                    else
                    {
                        ai.SetState(BehaviorState.Idle);
                    }
                    break;

                case BehaviorState.Exploring:
                    EndExploration(ai, "arrived");
                    break;

                default:
                    ai.SetState(BehaviorState.Idle);
                    break;
            }
        }

        /// <summary>
        /// Build current context from environment.
        /// </summary>
        public static BehaviorContext GetCurrentContext(VillagerAI ai)
        {
            var pos = ai.Character?.transform.position ?? Vector3.zero;
            float dayFraction = EnvMan.instance != null ? EnvMan.instance.GetDayFraction() : 0.5f;
            bool isRaining = EnvMan.instance != null && (EnvMan.IsWet() || EnvMan.instance.IsEnvironment("Rain"));

            return new BehaviorContext
            {
                CurrentPosition = pos.ToVec3(),
                TimeOfDay = GetTimeOfDay(dayFraction),
                IsRaining = isRaining,
                InShelter = CheckShelter(pos),
                CurrentComfort = 0f
            };
        }

        private static TimeOfDay GetTimeOfDay(float dayFraction)
        {
            // #region agent log
            // Sleep disabled for debugging — always return Day during night hours
            if (dayFraction >= VillagerSettings.NightStart || dayFraction < VillagerSettings.MorningStart)
                return TimeOfDay.Evening; // Treat night as evening so NPCs stay awake
            // #endregion
            if (dayFraction < VillagerSettings.DayStart)
                return TimeOfDay.Morning;
            if (dayFraction < VillagerSettings.EveningStart)
                return TimeOfDay.Day;
            return TimeOfDay.Evening;
        }

        /// <summary>
        /// Select the best known location for the current context.
        /// </summary>
        public static KnownLocation SelectBestKnownLocation(VillagerAI ai, BehaviorContext context)
        {
            var locations = ai.Memory.KnownLocations;
            if (locations.Count == 0) return null;

            return locations
                .Select(loc => new { Location = loc, Score = ScoreLocation(ai, loc, context) })
                .OrderByDescending(x => x.Score)
                .Select(x => x.Location)
                .FirstOrDefault();
        }

        private static float ScoreLocation(VillagerAI ai, KnownLocation loc, BehaviorContext context)
        {
            float score = 0f;
            float distance = Vector3.Distance(context.CurrentPosition.ToVector3(), loc.Position.ToVector3());

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
                case LocationType.Chair: score += 10f; break;
                case LocationType.Table: score += 8f; break;
                case LocationType.Bed: score += 5f; break;
                case LocationType.Shelter: score += 5f; break;
                case LocationType.Farm: score += 6f; break;
                case LocationType.Animals: score += 6f; break;
                case LocationType.Patrol: score += 3f; break;
            }

            score -= distance * 0.3f;
            score += loc.ComfortValue * 5f;

            if (ai.CurrentTarget.HasValue && loc.IsSameLocation(ai.CurrentTarget.Value.ToVec3()))
                score -= 20f;

            return score;
        }

        private static void TransitionToLocation(VillagerAI ai, KnownLocation location, BehaviorContext context)
        {
            BehaviorState newState;
            float distance = Vector3.Distance(ai.Position, location.Position.ToVector3());

            if (context.TimeOfDay == TimeOfDay.Night && location.Type == LocationType.Bed)
                newState = BehaviorState.Sleeping;
            else if (location.Type == LocationType.Patrol)
                newState = BehaviorState.Patrolling;
            else if (distance > VillagerSettings.ArrivalThreshold)
                newState = BehaviorState.Traveling;
            else
                newState = BehaviorState.Idle;

            ai.SetState(newState, location.Position.ToVector3());
        }

        /// <summary>
        /// Check if there is shelter overhead at the given position.
        /// </summary>
        public static bool CheckShelter(Vector3 position)
        {
            if (Physics.Raycast(position + Vector3.up * 0.5f, Vector3.up, out var hit, 20f))
            {
                if (hit.collider?.GetComponentInParent<Piece>() != null) return true;
                if (hit.collider != null && hit.collider.gameObject.isStatic) return true;
            }
            return false;
        }
    }
}
