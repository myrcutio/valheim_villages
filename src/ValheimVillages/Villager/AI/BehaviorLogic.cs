using UnityEngine;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.Settings;

namespace ValheimVillages.Villager.AI
{
    /// <summary>
    ///     Shared environment utilities for villager behaviors.
    ///     Context building and shelter detection used by multiple modules.
    /// </summary>
    public static class VillagerBehaviorLogic
    {
        /// <summary>
        ///     Build current context from environment.
        /// </summary>
        public static BehaviorContext GetCurrentContext(VillagerAI ai)
        {
            var pos = ai.Position;
            var dayFraction = EnvMan.instance != null ? EnvMan.instance.GetDayFraction() : 0.5f;
            var isRaining = EnvMan.instance != null && (EnvMan.IsWet() || EnvMan.instance.IsEnvironment("Rain"));

            return new BehaviorContext
            {
                CurrentPosition = pos,
                TimeOfDay = GetTimeOfDay(dayFraction),
                IsRaining = isRaining,
                InShelter = CheckShelter(pos),
                CurrentComfort = 0f,
            };
        }

        private static TimeOfDay GetTimeOfDay(float dayFraction)
        {
            // Sleep disabled for debugging — always return Day during night hours
            if (dayFraction >= VillagerSettings.NightStart || dayFraction < VillagerSettings.MorningStart)
                return TimeOfDay.Evening; // Treat night as evening so NPCs stay awake
            if (dayFraction < VillagerSettings.DayStart)
                return TimeOfDay.Morning;
            if (dayFraction < VillagerSettings.EveningStart)
                return TimeOfDay.Day;
            return TimeOfDay.Evening;
        }

        /// <summary>
        ///     Check if there is shelter overhead at the given position.
        /// </summary>
        public static bool CheckShelter(Vector3 position)
        {
            if (Physics.Raycast(position + Vector3.up * 0.5f, Vector3.up, out var hit, 50f))
            {
                if (hit.collider?.GetComponentInParent<Piece>() != null) return true;
                if (hit.collider != null && hit.collider.gameObject.isStatic) return true;
            }

            return false;
        }
    }
}