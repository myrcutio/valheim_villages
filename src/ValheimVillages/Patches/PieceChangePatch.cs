using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     Detects structural changes (piece placement/removal/destruction and terrain
    ///     edits) and sets a dirty flag so the HNA region graph is rebuilt after a
    ///     debounce period (see <c>Plugin.Update</c>). The bake is combined terrain+piece,
    ///     so terrain edits must dirty it too.
    /// </summary>
    [HarmonyPatch]
    public static class PieceChangePatch
    {
        /// <summary>Realtime timestamp of the last structural change.</summary>
        internal static float LastStructureChangeTime { get; private set; }

        /// <summary>True when a structural change has occurred but the graph hasn't been rebuilt yet.</summary>
        internal static bool IsDirty { get; set; }

        [RegisterCleanup]
        public static void Reset()
        {
            IsDirty = false;
            LastStructureChangeTime = 0f;
        }

        private static void MarkDirty(string source)
        {
            IsDirty = true;
            LastStructureChangeTime = Time.realtimeSinceStartup;
            Plugin.Log?.LogInfo($"[PieceChange] dirty via {source}; rebake will enqueue ~10s after settle");
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Piece), "SetCreator")]
        private static void OnPiecePlaced()
        {
            MarkDirty("place");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WearNTear), "Remove")]
        private static void OnPieceRemoved(WearNTear __instance)
        {
            MarkDirty("hammer-remove");
            Villages.VillageCleanupRpc.OnRegistryRemoved(__instance);
        }

        // Pieces destroyed by damage/decay go through the private WearNTear.Destroy
        // (→ m_onDestroyed), NOT Remove — so this is needed for boar-smashed walls etc.
        [HarmonyPrefix]
        [HarmonyPatch(typeof(WearNTear), "Destroy")]
        private static void OnPieceDestroyed(WearNTear __instance)
        {
            MarkDirty("destroyed");
            Villages.VillageCleanupRpc.OnRegistryRemoved(__instance);
        }

        // Terrain edits (hoe/pickaxe/cultivator/raise/level) — the combined bake includes
        // terrain, so these must dirty the graph too. ApplyOperation runs once per edit.
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TerrainComp), nameof(TerrainComp.ApplyOperation))]
        private static void OnTerrainModified()
        {
            MarkDirty("terrain");
        }
    }
}
