using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     Keeps a live set of every <see cref="Door" /> in the scene, so the partition's
    ///     gate-sealing never has to scan for them.
    ///     <para>
    ///     <c>RubberBandPrune.GatherGateSeals</c> used to call
    ///     <c>FindObjectsByType&lt;Door&gt;(FindObjectsInactive.Include, ...)</c> — a
    ///     whole-scene walk that then discarded every door outside the caller's bounds, twice
    ///     per partition. Measured at 68-76ms per call, it was the largest single frame spike
    ///     left after the partition was sliced across frames, and it is not sliceable: it is
    ///     one engine call.
    ///     </para>
    ///     <para>
    ///     Caching the scan was the obvious alternative and each variant was unsound. Keyed
    ///     on a timer: a guess. Keyed on a structural change: misses doors that arrive by
    ///     zone streaming, where no structural-change event fires. Replaced by a local
    ///     <c>Physics.OverlapBox</c>: silently drops doors on inactive GameObjects, which the
    ///     original explicitly included. Recording each door as it awakes has none of those
    ///     holes — the set is exact by construction and costs nothing per frame.
    ///     </para>
    /// </summary>
    [HarmonyPatch]
    public static class DoorRegistryPatch
    {
        private static readonly List<Door> s_doors = new();

        /// <summary>
        ///     False until the one-off seeding scan has run. Needed because this assembly can
        ///     be hot-reloaded into a world whose doors awoke under the PREVIOUS assembly, so
        ///     their Awake already fired and will not fire again.
        /// </summary>
        private static bool s_seeded;

        [RegisterCleanup]
        public static void Reset()
        {
            s_doors.Clear();
            s_seeded = false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(Door), "Awake")]
        private static void OnDoorAwake(Door __instance)
        {
            if (__instance != null) s_doors.Add(__instance);
        }

        /// <summary>
        ///     Every live door. Destroyed doors compare null against Unity's overloaded
        ///     operator and are compacted out here rather than being unregistered: Door has
        ///     no OnDestroy to patch, and a piece can also vanish with its whole zone.
        /// </summary>
        internal static List<Door> LiveDoors()
        {
            if (!s_seeded)
            {
                // One scan per session/reload, not two per partition.
                var existing = Object.FindObjectsByType<Door>(
                    FindObjectsInactive.Include, FindObjectsSortMode.None);
                foreach (var d in existing)
                    if (d != null && !s_doors.Contains(d))
                        s_doors.Add(d);
                s_seeded = true;
                Plugin.Log?.LogInfo(
                    $"[DoorRegistry] Seeded with {s_doors.Count} existing door(s); " +
                    "subsequent doors register on Awake.");
            }

            var write = 0;
            for (var read = 0; read < s_doors.Count; read++)
            {
                var d = s_doors[read];
                if (d == null) continue;
                s_doors[write++] = d;
            }

            if (write < s_doors.Count) s_doors.RemoveRange(write, s_doors.Count - write);
            return s_doors;
        }
    }
}
