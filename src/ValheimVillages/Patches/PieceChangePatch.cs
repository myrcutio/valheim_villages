using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Patches
{
    /// <summary>
    /// Detects structural changes (piece placement/removal) and sets a dirty
    /// flag so the HNA region graph can be rebuilt after a debounce period.
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

        private static void MarkDirty()
        {
            IsDirty = true;
            LastStructureChangeTime = Time.realtimeSinceStartup;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Piece), "SetCreator")]
        private static void OnPiecePlaced() => MarkDirty();

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WearNTear), "Remove")]
        private static void OnPieceRemoved() => MarkDirty();
    }
}
