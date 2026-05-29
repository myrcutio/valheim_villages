using UnityEngine;
using ValheimVillages.Interfaces;

namespace ValheimVillages.Villager.AI
{
    /// <summary>
    ///     Samples the comfort a villager is currently experiencing (shelter +
    ///     a nearby fire) and records the best seen into per-villager memory.
    ///     This is the only per-villager environmental sampling left after PoI
    ///     discovery moved to the village-level <c>VillagePoiRegistry</c>.
    /// </summary>
    public static class VillagerComfort
    {
        public static void UpdateExperiencedComfort(Transform transform, IVillagerMemory memory)
        {
            if (transform == null || memory == null) return;
            var pos = transform.position;
            memory.UpdateBestComfort(CalculateCurrentComfort(pos), pos);
        }

        private static float CalculateCurrentComfort(Vector3 position)
        {
            var comfort = 0f;

            if (VillagerBehaviorLogic.CheckShelter(position))
                comfort += 1f;

            var colliders = Physics.OverlapSphere(position, 10f);
            foreach (var col in colliders)
            {
                if (col == null) continue;
                if (col.GetComponent<Fireplace>() != null)
                {
                    comfort += 1f;
                    break;
                }
            }

            return comfort;
        }
    }
}
