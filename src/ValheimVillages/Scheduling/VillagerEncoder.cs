using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Query tower of the dual encoder. Produces a villager's query embedding to match
    ///     against <see cref="TaskEncoder" /> output under a dot product:
    ///
    ///     <list type="bullet">
    ///       <item>dims 0..2 — <c>c</c>, the villager's triad-hop coordinates</item>
    ///       <item>dim 3 — constant <c>1</c> (pairs with the task's <c>-‖c‖²</c> MIPS term)</item>
    ///       <item>dim 4 — <c>w</c>, the weight applied to the task priority channel</item>
    ///     </list>
    ///
    ///     See <see cref="TaskEncoder" /> for why this layout makes the dot product rank by
    ///     triad-space proximity plus a priority bonus.
    /// </summary>
    public static class VillagerEncoder
    {
        public static float[] Encode(
            RegionGraph graph, IReadOnlyList<Vector3> triad, Vector3 pos, float priorityWeight)
        {
            var c = new float[TriadEncoding.Dims];
            TriadEncoding.Coords(graph, triad, pos, c);

            var emb = new float[TaskEncoder.Dim];
            emb[0] = c[0];
            emb[1] = c[1];
            emb[2] = c[2];
            emb[3] = 1f;
            emb[4] = priorityWeight;
            return emb;
        }
    }
}
