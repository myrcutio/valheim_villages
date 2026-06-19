using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Task tower of the dual encoder. Produces a fixed-width embedding for a task,
    ///     structured (not learned) so retrieval is sensible while the model is untrained:
    ///
    ///     <list type="bullet">
    ///       <item>dims 0..2 — <c>2·c</c>, twice the triad-hop coordinates</item>
    ///       <item>dim 3 — <c>-‖c‖²</c>, the MIPS augmentation term</item>
    ///       <item>dim 4 — task priority</item>
    ///     </list>
    ///
    ///     Paired with <see cref="VillagerEncoder" />'s query layout <c>[c_q, 1, w]</c>, the
    ///     dot product evaluates to <c>2·c_q·c_t − ‖c_t‖² + w·priority</c>, which ranks tasks
    ///     by <c>−‖c_q − c_t‖²</c> (nearest in triad-coordinate space) plus a priority bonus.
    ///     That is the standard maximum-inner-product reduction of nearest-neighbour, so a
    ///     plain dot-product retrieval becomes spatially aware. Reserved trailing dims (if
    ///     <see cref="Dim" /> grows) are where a learned residual would later live.
    /// </summary>
    public static class TaskEncoder
    {
        public const int Dim = 5;

        public static float[] Encode(RegionGraph graph, IReadOnlyList<Vector3> triad, CandidateTask task)
        {
            var c = new float[TriadEncoding.Dims];
            TriadEncoding.Coords(graph, triad, task.Position, c);

            var emb = new float[Dim];
            emb[0] = 2f * c[0];
            emb[1] = 2f * c[1];
            emb[2] = 2f * c[2];
            emb[3] = -(c[0] * c[0] + c[1] * c[1] + c[2] * c[2]);
            emb[4] = task.Priority;
            return emb;
        }
    }
}
