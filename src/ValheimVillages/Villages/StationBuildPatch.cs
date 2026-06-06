using HarmonyLib;
using ValheimVillages.Items;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Villages
{
    /// <summary>
    ///     Registers a freshly-built station with its village the moment it is placed,
    ///     so villagers can use it without waiting for the next partition rescan.
    ///     <para><see cref="Piece.SetCreator" /> runs exactly once when a player places a
    ///     real piece (it is NOT called on world load, which restores the creator from the
    ///     ZDO), which makes it a clean "on build" signal. Station-ness is decided
    ///     generically by <see cref="VillageStationRegistry.TryClassifyStation" /> — no
    ///     hard-coded prefab/type list — so this picks up modded stations too.</para>
    ///     <para>The per-partition <see cref="VillageStationRegistry.RefreshFor" /> rescan
    ///     remains the authority that prunes stale/destroyed entries, so no destroy hook
    ///     is needed.</para>
    /// </summary>
    [HarmonyPatch(typeof(Piece), nameof(Piece.SetCreator))]
    public static class StationBuildPatch
    {
        [HarmonyPostfix]
        public static void Postfix(Piece __instance)
        {
            if (__instance == null) return;
            VillageStationRegistry.RegisterStation(__instance.gameObject);

            // When the placed piece IS a registry station, mint (or re-link) its
            // durable village and stamp the back-reference onto the registry ZDO.
            // This is the single point a new village is born.
            var zdo = __instance.GetComponent<ZNetView>()?.GetZDO();
            if (zdo != null && zdo.GetPrefab() == PieceFactory.RegistryPrefabName.GetStableHashCode())
                LinkRegistryToVillage(zdo);
        }

        private static void LinkRegistryToVillage(ZDO registryZdo)
        {
            if (!string.IsNullOrEmpty(registryZdo.GetString(Village.IdKey))) return; // already linked

            var pos = registryZdo.GetPosition();
            // A registry INSIDE an existing village's graph joins it; otherwise this is a
            // new village (GetOrCreateAt re-links a re-placed registry by anchor, else mints).
            // This registry-placement path is the ONLY place a village is created.
            var village = VillageRegistry.GetVillageCovering(pos) ?? VillageRegistry.GetOrCreateAt(pos);
            if (village == null) return;

            registryZdo.Set(Village.IdKey, village.VillageId);
            registryZdo.Persistent = true;
            Plugin.Log?.LogInfo(
                $"[StationBuildPatch] Registry at ({pos.x:F1},{pos.y:F1},{pos.z:F1}) -> village {village.VillageId}");
        }
    }
}
