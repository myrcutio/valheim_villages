using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     Triad-landmark coordinates: region-hop distance from a point to each of the
    ///     village's (up to 3) triad anchors. This is a Lipschitz embedding of the graph
    ///     metric — by the triangle inequality, points with similar coordinate vectors are
    ///     close in graph distance — which is what lets a dot-product (with the MIPS
    ///     augmentation in <see cref="TaskEncoder" />) approximate spatial proximity during
    ///     retrieval. The exact distance is recomputed in the rerank stage; this is only a
    ///     coarse recall signal.
    ///
    ///     <para>An unreachable anchor yields a large sentinel so the point reads as "far"
    ///     in every retrieval comparison rather than collapsing to zero.</para>
    /// </summary>
    public static class TriadEncoding
    {
        public const int Dims = 3;

        /// <summary>Hop value used when an anchor is unreachable or absent.</summary>
        public const float UnreachableHops = 32f;

        public static void Coords(
            RegionGraph graph, IReadOnlyList<Vector3> triad, Vector3 pos, float[] outCoords)
        {
            for (var i = 0; i < Dims; i++)
            {
                if (triad != null && i < triad.Count)
                {
                    var h = RegionHopDistance.Hops(graph, pos, triad[i]);
                    outCoords[i] = h < 0 ? UnreachableHops : h;
                }
                else
                {
                    outCoords[i] = UnreachableHops;
                }
            }
        }
    }
}
