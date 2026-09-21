using UnityEngine;
using ValheimVillages.Behaviors.Feasts;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI.Work;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Scheduling.Producers
{
    /// <summary>
    ///     Produces a <see cref="TaskKind.FeastRefresh" /> row when the village has an eaten-out
    ///     feast AND another one in store to replace it with.
    ///
    ///     <para>Both halves matter. Without the stock check the row would sit on the board
    ///     forever at a village that has no feasts left, and every villager offered it would
    ///     walk over, find nothing to lay out and come back — which is how a board row turns
    ///     into a treadmill.</para>
    /// </summary>
    public static class FeastTaskProducer
    {
        private const string Capability = "feast";

        public static void Scan(Village village, Vector3 center, float now)
        {
            if (village == null) return;
            var villageId = village.VillageId;
            if (string.IsNullOrEmpty(villageId)) return;

            var sourceId = "feast:" + villageId;

            var feast = FeastBehavior.FindEmptyFeast(center, WorkSettings.HaulScanRadius);
            if (feast == null)
            {
                TaskBoard.Remove(villageId, sourceId);
                return;
            }

            var containers = ContainerScanner.FindVillageContainers(
                center, WorkSettings.HaulScanRadius);
            if (!FeastBehavior.TryFindMaterial(feast, containers, out _))
            {
                TaskBoard.Remove(villageId, sourceId);
                return;
            }

            TaskBoard.Upsert(villageId, new CandidateTask
            {
                SourceId = sourceId,
                Kind = TaskKind.FeastRefresh,
                Position = feast.transform.position,
                // Comfort for the whole hall, and a short errand — but nothing spoils while it
                // waits, so it sits below rescuing food off a fire.
                Priority = 0.5f,
                ExpiresAt = 0f,
                RequiredCapability = Capability,
            });
        }
    }
}
