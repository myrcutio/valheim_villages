using System.Linq;
using UnityEngine;
using ValheimVillages;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Exploration logic for villager AI.
    /// Handles deciding when to explore, generating targets, and managing exploration state.
    /// </summary>
    public static class VillagerExploration
    {
        /// <summary>
        /// Check if the NPC should explore to find new location types.
        /// </summary>
        public static bool ShouldExplore(VillagerAI ai, BehaviorContext context)
        {
            if (context.TimeOfDay == TimeOfDay.Night) return false;
            if (context.IsRaining && !context.InShelter) return false;
            if (ai.CurrentState == BehaviorState.Exploring) return false;
            if (!ai.Memory.ShouldExplore()) return false;
            return Random.value < ExplorationSettings.ExplorationChance;
        }

        /// <summary>
        /// Start exploring to find new location types.
        /// </summary>
        public static void StartExploration(VillagerAI ai)
        {
            Vector3? target = GenerateExplorationTarget(ai);
            if (target.HasValue)
            {
                ai.SetExplorationTarget(target.Value);
                ai.SetState(BehaviorState.Exploring, target);
                Plugin.Log?.LogDebug($"[AI:{ai.NpcName}] Starting exploration");
            }
        }

        /// <summary>
        /// Continue current exploration. Returns true if still exploring.
        /// </summary>
        public static bool ContinueExploration(VillagerAI ai)
        {
            if (!ai.ExplorationTarget.HasValue) return false;

            if (ai.ExplorationElapsed >= ExplorationSettings.ExplorationDuration)
            {
                EndExploration(ai, "timeout");
                return false;
            }

            if (!ai.Memory.ShouldExplore())
            {
                EndExploration(ai, "found locations");
                return false;
            }

            return true;
        }

        /// <summary>
        /// End the current exploration.
        /// </summary>
        public static void EndExploration(VillagerAI ai, string reason)
        {
            Plugin.Log?.LogDebug($"[AI:{ai.NpcName}] Ended exploration: {reason}");
            ai.ClearExploration();
            ai.SetState(BehaviorState.Idle);
        }

        /// <summary>
        /// Generate a random exploration target away from known locations.
        /// </summary>
        public static Vector3? GenerateExplorationTarget(VillagerAI ai)
        {
            var bedPos = ai.Memory.BedPosition;

            for (int attempt = 0; attempt < 10; attempt++)
            {
                float angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float dist = Random.Range(ExplorationSettings.MinDistanceFromKnown, ExplorationSettings.MaxExplorationRange);
                var direction = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
                var target = bedPos + direction * dist;

                bool tooClose = ai.Memory.KnownLocations.Any(l =>
                    Vector3.Distance(target, l.Position.ToVector3()) < ExplorationSettings.MinDistanceFromKnown);

                if (!tooClose)
                {
                    if (ZoneSystem.instance != null && ZoneSystem.instance.GetGroundHeight(target, out float h))
                        target.y = h;
                    return target;
                }
            }

            // Fallback
            float fbAngle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
            var fbDir = new Vector3(Mathf.Cos(fbAngle), 0, Mathf.Sin(fbAngle));
            return ai.Position + fbDir * ExplorationSettings.MinDistanceFromKnown * 2f;
        }
    }
}
