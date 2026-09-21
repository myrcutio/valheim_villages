using UnityEngine;

namespace ValheimVillages.Behaviors.Forestry
{
    /// <summary>
    ///     Will a sapling of this prefab actually grow at this spot — asked BEFORE anything is
    ///     planted.
    ///
    ///     <para>Why this exists: reading <c>Plant.GetStatus()</c> off a sapling you just
    ///     instantiated tells you nothing. It returns a cached field that only
    ///     <c>UpdateHealth</c> writes, on the 10-second SlowUpdate tick, and that method
    ///     short-circuits to <c>Healthy</c> for the first 10 seconds of a plant's life. So a
    ///     doomed sapling reports Healthy at the moment you plant it and turns NoSpace a few
    ///     seconds later — which is exactly how a woodlot ends up with saplings wedged against
    ///     rocks and bushes that will never mature, each one having cost a seed.</para>
    ///
    ///     <para>The checks below mirror <c>Plant.UpdateHealth</c>/<c>HaveGrowSpace</c>/
    ///     <c>HaveRoof</c> against the PREFAB's own settings (grow radius, biome mask,
    ///     cultivation, heat/cold tolerance), so the answer is the game's answer rather than an
    ///     approximation of it. Note how strict the space test really is: ANY collider on
    ///     Default/static_solid/Default_small/piece/piece_nonsolid within the grow radius
    ///     blocks, not just trees — a boulder, a bush, a stake wall. Only another plant that is
    ///     itself unhealthy is forgiven.</para>
    /// </summary>
    internal static class PlantSpace
    {
        private static readonly Collider[] s_hits = new Collider[64];
        private static int s_spaceMask;
        private static int s_roofMask;

        /// <summary>
        ///     True when a <paramref name="saplingPrefab" /> planted at <paramref name="pos" />
        ///     would come up Healthy. <paramref name="reason" /> names the blocking condition
        ///     otherwise, in <c>Plant.Status</c> terms.
        /// </summary>
        public static bool CanGrow(GameObject saplingPrefab, Vector3 pos, out string reason)
        {
            reason = null;
            var plant = saplingPrefab != null ? saplingPrefab.GetComponent<Plant>() : null;
            if (plant == null)
            {
                reason = $"'{(saplingPrefab != null ? saplingPrefab.name : "null")}' is not a Plant";
                return false;
            }

            var heightmap = Heightmap.FindHeightmap(pos);
            if (heightmap != null)
            {
                var biome = heightmap.GetBiome(pos);
                if ((biome & plant.m_biome) == 0)
                {
                    reason = Plant.Status.WrongBiome.ToString();
                    return false;
                }

                if (plant.m_needCultivatedGround && !heightmap.IsCultivated(pos))
                {
                    reason = Plant.Status.NotCultivated.ToString();
                    return false;
                }

                if (!plant.m_tolerateHeat && biome == Heightmap.Biome.AshLands &&
                    !ShieldGenerator.IsInsideShield(pos))
                {
                    reason = Plant.Status.TooHot.ToString();
                    return false;
                }

                if (!plant.m_tolerateCold &&
                    (biome == Heightmap.Biome.DeepNorth || biome == Heightmap.Biome.Mountain) &&
                    !ShieldGenerator.IsInsideShield(pos))
                {
                    reason = Plant.Status.TooCold.ToString();
                    return false;
                }
            }

            if (s_roofMask == 0) s_roofMask = LayerMask.GetMask("Default", "static_solid", "piece");
            if (Physics.Raycast(pos, Vector3.up, 100f, s_roofMask))
            {
                reason = Plant.Status.NoSun.ToString();
                return false;
            }

            if (s_spaceMask == 0)
                s_spaceMask = LayerMask.GetMask(
                    "Default", "static_solid", "Default_small", "piece", "piece_nonsolid");

            var count = Physics.OverlapSphereNonAlloc(pos, plant.m_growRadius, s_hits, s_spaceMask);
            for (var i = 0; i < count; i++)
            {
                var other = s_hits[i].GetComponent<Plant>();
                if (other == null || other.GetStatus() == Plant.Status.Healthy)
                {
                    reason = $"{Plant.Status.NoSpace} ({s_hits[i].gameObject.name} " +
                             $"within {plant.m_growRadius:F1}m)";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        ///     A sapling that has been in the ground long enough to have judged itself, and
        ///     judged badly: it will never mature and it is holding a planting spot.
        /// </summary>
        public static bool IsDud(Plant plant)
        {
            return plant != null && plant.GetStatus() != Plant.Status.Healthy;
        }
    }
}
