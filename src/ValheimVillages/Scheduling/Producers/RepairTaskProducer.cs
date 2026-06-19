using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Scheduling.Producers
{
    /// <summary>
    ///     Produces <see cref="TaskKind.RepairPiece" /> tasks from damaged structures
    ///     (any <see cref="WearNTear" /> below full health). No deadline — durability
    ///     loss is gradual — so priority is the damage fraction and the reranker trades
    ///     it off against distance. Reachability is left to the reranker (a piece whose
    ///     position resolves to no region scores as unreachable and is skipped).
    /// </summary>
    public static class RepairTaskProducer
    {
        private const float ScanRadius = 60f;

        // Below full health (1.0); the small margin avoids float jitter at full.
        private const float DamagedThreshold = 0.99f;
        private const string Capability = "repair";

        public static void Scan(Village village, Vector3 center, float now)
        {
            if (village == null) return;
            var villageId = village.VillageId;
            var graph = village.Graph;

            foreach (var wnt in PhysicsHelper.GetAllInRadius<WearNTear>(center, ScanRadius))
            {
                var nview = wnt != null ? wnt.GetComponent<ZNetView>() : null;
                if (nview == null || !nview.IsValid() || nview.GetZDO() == null) continue;

                var sourceId = nview.GetZDO().m_uid.ToString();
                var pos = wnt.transform.position;

                // Don't burn cycles repairing world-spawn pieces outside the village's
                // outer shell (rocks/ruins beyond the walls). Boundary + interior cells
                // are kept (IsOutsideCell is false for them). Only when a classification
                // exists, else we'd risk filtering everything.
                if (graph != null && graph.HasClassification && VillageShell.IsOutside(graph, pos))
                {
                    TaskBoard.Remove(villageId, sourceId);
                    continue;
                }

                var hp = wnt.GetHealthPercentage();
                if (hp >= DamagedThreshold)
                {
                    // Healthy (or freshly repaired) — drop any stale task.
                    TaskBoard.Remove(villageId, sourceId);
                    continue;
                }

                TaskBoard.Upsert(villageId, new CandidateTask
                {
                    SourceId = sourceId,
                    Kind = TaskKind.RepairPiece,
                    Position = pos,
                    Priority = 1f - hp, // more damaged = higher base importance
                    ExpiresAt = 0f, // no deadline
                    RequiredCapability = Capability,
                });
            }
        }
    }
}
