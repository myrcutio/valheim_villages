using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Behaviors.Repair
{
    /// <summary>
    ///     The one definition of "what in this village needs repair". Shared by the board
    ///     producer (is there ANY repair work?) and the carpenter (which piece next?), so the
    ///     two can never disagree about scope — a piece the board counts is a piece the
    ///     carpenter will consider, and vice versa.
    /// </summary>
    public static class VillageRepairs
    {
        /// <summary>How far from the village anchor structures are considered.</summary>
        public const float ScanRadius = 60f;

        /// <summary>Below full health (1.0); the small margin avoids float jitter at full.</summary>
        public const float DamagedThreshold = 0.99f;

        /// <summary>
        ///     Every damaged, networked structure in the village, excluding world-spawn pieces
        ///     outside the village's outer shell (ruins beyond the walls). The shell filter only
        ///     applies once the graph carries a classification; without one it would risk
        ///     filtering everything.
        /// </summary>
        public static List<(WearNTear piece, float health)> FindDamaged(Village village)
        {
            var result = new List<(WearNTear, float)>();
            if (village == null) return result;

            var graph = village.Graph;
            foreach (var wnt in PhysicsHelper.GetAllInRadius<WearNTear>(village.Anchor, ScanRadius))
            {
                var nview = wnt != null ? wnt.GetComponent<ZNetView>() : null;
                if (nview == null || !nview.IsValid() || nview.GetZDO() == null) continue;

                var pos = wnt.transform.position;
                if (graph != null && graph.HasClassification && VillageShell.IsOutside(graph, pos))
                    continue;

                // NOT GetHealthPercentage(): its cached value reads every intact piece as
                // damaged once a world is past world level 0. See PieceHealth.
                var hp = PieceHealth.Fraction(wnt);
                if (hp < DamagedThreshold) result.Add((wnt, hp));
            }

            return result;
        }
    }
}
