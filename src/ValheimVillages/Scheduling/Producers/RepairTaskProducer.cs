using UnityEngine;
using ValheimVillages.Behaviors.Repair;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Scheduling.Producers
{
    /// <summary>
    ///     Posts ONE <see cref="TaskKind.RepairPiece" /> task per village while anything in it
    ///     is damaged, and removes it once nothing is. Which piece to fix is the carpenter's
    ///     call (<see cref="RepairBehavior" /> walks the village until everything reachable is
    ///     patched), not the board's.
    ///
    ///     <para>It used to post one row per damaged piece. With a few dozen of them the board
    ///     was mostly repair rows every other villager had to score and skip, and a piece the
    ///     carpenter could not actually reach came straight back every time its short
    ///     blacklist lapsed — three unreachable wall pieces kept him walking a 20-second loop
    ///     indefinitely without repairing anything.</para>
    /// </summary>
    public static class RepairTaskProducer
    {
        private const string Capability = "repair";

        public static void Scan(Village village, Vector3 center, float now)
        {
            if (village == null) return;
            var villageId = village.VillageId;
            var sourceId = SourceIdFor(villageId);

            var damaged = VillageRepairs.FindDamaged(village);
            if (damaged.Count == 0)
            {
                TaskBoard.Remove(villageId, sourceId);
                return;
            }

            // Position and priority come from the worst piece: position only feeds the
            // reranker's travel estimate, and the carpenter chooses its own route on arrival.
            var worst = damaged[0];
            foreach (var d in damaged)
                if (d.health < worst.health)
                    worst = d;

            TaskBoard.Upsert(villageId, new CandidateTask
            {
                SourceId = sourceId,
                Kind = TaskKind.RepairPiece,
                Position = worst.piece.transform.position,
                Priority = 1f - worst.health, // more damaged = higher base importance
                ExpiresAt = 0f, // no deadline
                RequiredCapability = Capability,
            });
        }

        private static string SourceIdFor(string villageId) => $"repair:{villageId}";
    }
}
