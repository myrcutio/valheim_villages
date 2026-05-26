using HarmonyLib;
using UnityEngine;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Harmony patch on BaseAI.RandomMovement to make enemies and wildlife
    ///     prefer to avoid random wandering within 20m of a village area.
    ///     When an enemy picks a random movement target, if that target is near
    ///     a village boundary, the target is nudged away from the village.
    /// </summary>
    [HarmonyPatch(typeof(BaseAI), "RandomMovement")]
    public static class EnemyAvoidancePatch
    {
        /// <summary>Distance from village boundary that enemies should avoid.</summary>
        private const float AvoidanceRadius = 20f;

        /// <summary>Max attempts to re-roll a target before accepting.</summary>
        private const int MaxRerollAttempts = 3;

        /// <summary>
        ///     Prefix: check and modify the random move target field before movement.
        ///     If the AI's random target is near a village, nudge it away.
        ///     Only applies to non-player-faction creatures (enemies and wildlife).
        /// </summary>
        private static void Prefix(BaseAI __instance)
        {
            // No village areas -- skip entirely
            if (VillageAreaManager.AreaCount == 0) return;

            // Only affect hostile/wild creatures, not our villagers or tamed animals
            var character = __instance.GetComponent<Character>();
            if (character == null) return;
            if (character.m_faction == Character.Faction.Players) return;
            if (character.IsTamed()) return;

            // Access the random move target via reflection (it's private)
            var targetField = AccessTools.Field(typeof(BaseAI), "m_randomMoveTarget");
            if (targetField == null) return;

            var target = (Vector3)targetField.GetValue(__instance);

            // Check if the target is near or inside a village
            if (!VillageAreaManager.IsNearAnyVillage(target, AvoidanceRadius))
                return;

            // Try to find an alternative target away from the village
            var currentPos = __instance.transform.position;
            var moveRange = __instance.m_randomMoveRange;

            for (var attempt = 0; attempt < MaxRerollAttempts; attempt++)
            {
                // Pick a new random direction
                var angle = Random.Range(0f, 360f) * Mathf.Deg2Rad;
                var dist = Random.Range(moveRange * 0.3f, moveRange);
                var newTarget = currentPos + new Vector3(
                    Mathf.Cos(angle) * dist,
                    0f,
                    Mathf.Sin(angle) * dist
                );

                // Check if this new target is away from villages
                if (!VillageAreaManager.IsNearAnyVillage(newTarget, AvoidanceRadius))
                {
                    targetField.SetValue(__instance, newTarget);
                    return;
                }
            }

            // All attempts failed -- nudge away from the nearest village center
            // by reversing the direction toward the village
            var awayDir = (currentPos - target).normalized;
            var fallbackTarget = currentPos + awayDir * moveRange;
            targetField.SetValue(__instance, fallbackTarget);
        }
    }
}