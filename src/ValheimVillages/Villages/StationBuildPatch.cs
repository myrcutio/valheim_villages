using HarmonyLib;

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
        }
    }
}
