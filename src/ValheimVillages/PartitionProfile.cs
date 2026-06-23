using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;

namespace ValheimVillages
{
    /// <summary>
    ///     Lightweight, allocation-light profiler for ONE village's hna_partition pass.
    ///     <see cref="Begin" /> at <c>RegionPartitionHandler.Handle</c> entry, bracket each
    ///     stage with <see cref="Mark" />/<see cref="Since" />, increment the public counter
    ///     fields at hot native-query call sites (bare <c>++</c>, no gating), and
    ///     <see cref="Emit" /> before the partition returns. Produces a single structured
    ///     <c>DebugLog.Event("Region","PartitionProfile", ...)</c> line carrying per-stage
    ///     <c>*_ms</c> fields plus native-query call counts — the per-stage detail the existing
    ///     bake event (terrain_ms/piece_ms) doesn't cover.
    ///
    ///     <para>Inert until <see cref="Begin" />: counter increments and Mark/Since calls
    ///     outside an active partition are harmless (reset by the next Begin, never emitted).
    ///     Single-threaded by contract — the task queue runs Handle on the main thread, one
    ///     village at a time, so the static accumulator needs no locking.</para>
    /// </summary>
    internal static class PartitionProfile
    {
        private static bool s_active;
        private static readonly List<(string name, double ms)> s_stages = new();

        // Native-query counters, incremented at hot call sites. Reset by Begin();
        // only meaningful for the partition currently between Begin() and Emit().
        public static long SamplePos;
        public static long CheckCapsule;
        public static long CheckSphere;
        public static long OverlapBox;
        public static long GetGroundHeight;
        public static long CalcPath;

        /// <summary>Arm the profiler and reset all accumulators for a fresh partition.</summary>
        public static void Begin()
        {
            s_active = true;
            s_stages.Clear();
            SamplePos = 0;
            CheckCapsule = 0;
            CheckSphere = 0;
            OverlapBox = 0;
            GetGroundHeight = 0;
            CalcPath = 0;
        }

        /// <summary>High-resolution start token for a stage. Pair with <see cref="Since" />.</summary>
        public static long Mark()
        {
            return Stopwatch.GetTimestamp();
        }

        /// <summary>
        ///     Record the elapsed ms since <paramref name="mark" /> under <paramref name="name" />
        ///     (appended as <c>name_ms</c> on emit). No-op when not armed.
        /// </summary>
        public static void Since(string name, long mark)
        {
            if (!s_active) return;
            var ms = (Stopwatch.GetTimestamp() - mark) * 1000.0 / Stopwatch.Frequency;
            s_stages.Add((name, ms));
        }

        /// <summary>Flush the accumulated profile as one structured event, then go inert.</summary>
        public static void Emit(string villageKey)
        {
            if (!s_active) return;
            s_active = false;

            var kv = new List<(string, object)>(s_stages.Count + 7)
            {
                ("village", villageKey ?? ""),
            };
            foreach (var (name, ms) in s_stages)
                kv.Add((name + "_ms", ms.ToString("F2", CultureInfo.InvariantCulture)));
            kv.Add(("samplepos_calls", SamplePos));
            kv.Add(("checkcapsule_calls", CheckCapsule));
            kv.Add(("checksphere_calls", CheckSphere));
            kv.Add(("overlapbox_calls", OverlapBox));
            kv.Add(("getgroundheight_calls", GetGroundHeight));
            kv.Add(("calcpath_calls", CalcPath));

            DebugLog.Event("Region", "PartitionProfile", kv.ToArray());
        }
    }
}
