using UnityEngine;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Shell/hull classification helpers over a village's <see cref="RegionGraph" />
    ///     committed perimeter classification. "Outside" means beyond the outermost wall
    ///     layer (world-spawn rocks/ruins, ground past the palisade); boundary and interior
    ///     cells are NOT outside.
    /// </summary>
    public static class VillageShell
    {
        /// <summary>
        ///     True if <paramref name="pos" /> sits in a cell classified outside the village
        ///     hull. Boundary and interior cells return false. Callers should gate on
        ///     <see cref="RegionGraph.HasClassification" /> first — without a committed
        ///     classification this returns false ("unknown", not "outside").
        /// </summary>
        public static bool IsOutside(RegionGraph graph, Vector3 pos)
        {
            if (graph == null) return false;
            var gx = Mathf.FloorToInt(pos.x / RegionGraph.LookupCellSize);
            var gz = Mathf.FloorToInt(pos.z / RegionGraph.LookupCellSize);
            return graph.IsOutsideCell(gx, gz);
        }
    }
}
