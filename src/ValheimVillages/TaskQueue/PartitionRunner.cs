using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.TaskQueue
{
    /// <summary>
    ///     Host for the village partition's long-running work, and the frame budget that
    ///     keeps it off the player's frame.
    ///     <para>
    ///     A partition costs 700-900ms per village and used to run to completion inside one
    ///     <c>GlobalTaskQueue.ProcessBatch</c> call — i.e. inside one <c>Update</c>, i.e. one
    ///     frame. The queue budgets by TASK COUNT (3 per tier per frame), which bounds how
    ///     many tasks start but nothing about how long one takes, so `vv_repartition` with
    ///     two villages froze the game for the best part of two seconds.
    ///     </para>
    ///     <para>
    ///     The work is now a coroutine that yields whenever <see cref="ShouldYield" /> says
    ///     this frame's budget is spent. Total wall-clock goes UP (a partition spreads over
    ///     a few seconds); the per-frame cost goes down to the budget. That trade is the
    ///     whole point: nobody notices a rebuild that takes three seconds in the background,
    ///     everybody notices one that takes 900ms in the foreground.
    ///     </para>
    /// </summary>
    [RegisterModObject(GameObjectName)]
    internal class PartitionRunner : MonoBehaviour
    {
        internal const string GameObjectName = "VV_PartitionRunner";

        /// <summary>
        ///     Milliseconds of partition work allowed per frame. At 60fps a frame is 16.7ms,
        ///     so 4ms is about a quarter of the budget — enough to make real progress without
        ///     being visible alongside everything else the game does in a frame.
        /// </summary>
        internal const double FrameBudgetMs = 4.0;

        private static PartitionRunner s_instance;

        /// <summary>
        ///     The village whose partition is in flight, or null. ONE AT A TIME, GLOBALLY —
        ///     not one per village.
        ///     <para>
        ///     The partition pipeline carries a lot of shared static scratch state that has
        ///     always assumed it runs to completion before the next one starts:
        ///     <c>PartitionProfile</c>'s accumulator (whose own doc says "single-threaded by
        ///     contract... one village at a time"), <c>NavMeshBakeManager</c>'s
        ///     <c>s_terrainSources</c>/<c>s_pieceSources</c>, <c>RubberBandPrune</c>'s
        ///     <c>Last*</c> diagnostic snapshot. Slicing across frames does not break that
        ///     contract; letting two villages slice CONCURRENTLY does. Measured when this was
        ///     briefly per-village: the two villages' stages interleaved into one profile
        ///     line and a village that should have committed 38 regions committed 10.
        ///     </para>
        /// </summary>
        private static string s_inFlight;

        private static long s_frameMark;

        /// <summary>
        ///     Increments once per partition. Lets a stage cache a whole-world scan for the
        ///     duration of ONE partition — which is already the contract the pipeline works
        ///     under: a partition reads a snapshot of the world, and a change to that world
        ///     enqueues a fresh partition rather than mutating the one in flight.
        /// </summary>
        internal static int Epoch { get; private set; }

        /// <summary>True while any village's partition is running.</summary>
        internal static bool IsAnyRunning => s_inFlight != null;

        /// <summary>The village currently partitioning, or null.</summary>
        internal static string RunningVillage => s_inFlight;

        /// <summary>
        ///     Start <paramref name="routine" /> as the one in-flight partition. Throws if
        ///     another is already running: the caller is expected to have checked
        ///     <see cref="IsAnyRunning" /> and deferred.
        /// </summary>
        internal static void Run(string villageId, IEnumerator routine)
        {
            if (string.IsNullOrEmpty(villageId))
                throw new ArgumentException(
                    "A partition coroutine must be keyed by village.", nameof(villageId));
            if (routine == null) throw new ArgumentNullException(nameof(routine));
            if (s_inFlight != null)
                throw new InvalidOperationException(
                    $"A partition for village {s_inFlight} is already in flight; the caller " +
                    "must defer rather than start a second one.");

            s_inFlight = villageId;
            Epoch++;
            // Arm the segment clock here. Left at 0 it would make the first ShouldYield
            // measure "now minus the epoch" and report a nonsense worst segment.
            s_lastYieldTs = Stopwatch.GetTimestamp();
            s_frameMark = s_lastYieldTs;
            Instance.StartCoroutine(Wrap(villageId, routine));
        }

        /// <summary>
        ///     Release the in-flight flag when the routine ends — including when it ends by
        ///     throwing. `finally` around a `yield` is legal in an iterator (a `catch` is
        ///     not), which is exactly what is wanted here: the exception still propagates and
        ///     is still logged by Unity, it just does not strand the village permanently.
        /// </summary>
        private static IEnumerator Wrap(string villageId, IEnumerator routine)
        {
            try
            {
                // Time each MoveNext: that IS the work done between two yields, so it
                // separates "the partition ran long" from "the frame was long for some
                // other reason" (GC, another system, a zone load). Without this split, a
                // worst-frame number cannot tell you which one you are looking at.
                while (routine.MoveNext()) yield return routine.Current;
            }
            finally
            {
                s_inFlight = null;
                ReportFrameWatch(villageId);
            }
        }

        /// <summary>Open a new frame's budget. Called from the runner's own Update.</summary>
        private static void ResetBudget()
        {
            s_frameMark = Stopwatch.GetTimestamp();
            s_lastYieldTs = s_frameMark;
        }

        /// <summary>
        ///     True once this frame's partition budget is spent. Callers yield on true; the
        ///     budget reopens on the next frame.
        /// </summary>
        internal static bool ShouldYield()
        {
            // No partition in flight means no frame budget to spend, so nothing to yield
            // for — the iterators that consult this are also driven straight through by
            // DrainNow and by the tests. Without this the stale s_frameMark (0 outside a
            // partition) made every call look like a massive overrun, which then took the
            // logging branch and touched Plugin.Log — forcing a BepInEx assembly load that
            // fails outright in a test host.
            if (s_inFlight == null) return false;

            var now = Stopwatch.GetTimestamp();
            var ms = (now - s_frameMark) * 1000.0 / Stopwatch.Frequency;
            if (ms < FrameBudgetMs) return false;

            // Work done since the previous yield was granted. Measured HERE rather than
            // around MoveNext because an inner iterator yielded up to Unity is driven by
            // Unity — Wrap's MoveNext never sees its individual steps, so timing there
            // reports the outer routine's stages only and misses the expensive ones.
            var segMs = (now - s_lastYieldTs) * 1000.0 / Stopwatch.Frequency;
            if (segMs > s_worstSegmentMs) s_worstSegmentMs = segMs;
            if (segMs >= OverrunReportMs)
            {
                s_overruns++;
                ReportOverrun(segMs);
            }
            s_segments++;
            s_lastYieldTs = now;
            return true;
        }

        /// <summary>
        ///     Name an overrun. Kept OUT of <see cref="ShouldYield" /> and never inlined:
        ///     the JIT resolves every type a method body mentions when it compiles that
        ///     method, so a <c>Plugin.Log</c> reference anywhere inside ShouldYield forces a
        ///     BepInEx assembly load the first time it runs — even on a path that never
        ///     executes. That is fine in game and fatal in a test host, where BepInEx is not
        ///     present and the whole flood then threw FileNotFoundException.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReportOverrun(double segMs)
        {
            // The offending work is whatever ran AFTER the named stage completed, which is
            // enough to locate the loop that still needs a yield inside it.
            Plugin.Log?.LogInfo(
                $"[PartitionRunner] budget overrun {segMs:F1}ms after stage " +
                $"'{PartitionProfile.LastStage}'");
        }

        /// <summary>
        ///     Budget check for the inside of a hot loop. Checking the stopwatch on every one
        ///     of 63,000 triangles would itself cost more than it saves, so the check is
        ///     rate-limited to every <paramref name="stride" />-th iteration.
        /// </summary>
        internal static bool ShouldYieldEvery(int iteration, int stride = 512)
        {
            return iteration % stride == 0 && ShouldYield();
        }

        /// <summary>
        ///     Create the host if it does not exist yet. Called every frame from
        ///     Plugin.Update so the runner's own Update is ticking BEFORE the first
        ///     partition — otherwise there is no idle frame-time baseline to compare a
        ///     partition's frames against, which is the number that says whether the
        ///     partition is what the player is feeling.
        /// </summary>
        internal static void EnsureHost()
        {
            _ = Instance;
        }

        private static PartitionRunner Instance
        {
            get
            {
                if (s_instance != null) return s_instance;

                // Adopt an orphan from a previous assembly by name before making a new one:
                // its MonoBehaviour is a different TYPE after a reload, so GetComponent<>()
                // cannot see it and a plain Find-or-create would stack hosts.
                var existing = GameObject.Find(GameObjectName);
                if (existing != null) UnityEngine.Object.Destroy(existing);

                var go = new GameObject(GameObjectName);
                DontDestroyOnLoad(go);
                s_instance = go.AddComponent<PartitionRunner>();
                return s_instance;
            }
        }

        // --- Frame-time watch ---------------------------------------------------
        //
        // The point of all this is the player's frame, so measure the player's frame
        // rather than inferring it from a sum of stage timings. While any partition is in
        // flight, track the worst frame seen; report it when the last one finishes.
        private static double s_worstFrameMs;
        private static int s_framesWatched;
        private static double s_watchedTotalMs;
        private static double s_worstSegmentMs;
        private static int s_segments;
        private static int s_overruns;
        private static long s_lastYieldTs;

        // Rolling idle baseline: what a frame costs when NO partition is running. Without
        // it, "mean frame 47ms during a partition" is unreadable — it could be the partition
        // or it could be what this machine does anyway.
        private static double s_idleTotalMs;
        private static int s_idleFrames;

        /// <summary>A segment this long or longer is a budget overrun worth counting.</summary>
        private const double OverrunReportMs = 16.0;

        internal static void ReportFrameWatch(string villageId)
        {
            if (s_framesWatched == 0) return;
            Plugin.Log?.LogInfo(
                $"[PartitionRunner] {villageId}: {s_segments} segment(s) over {s_framesWatched} frame(s); " +
                $"worst SEGMENT {s_worstSegmentMs:F1}ms, {s_overruns} over {OverrunReportMs:F0}ms; " +
                $"worst frame {s_worstFrameMs:F1}ms, mean frame {s_watchedTotalMs / s_framesWatched:F1}ms " +
                $"vs IDLE baseline {(s_idleFrames > 0 ? s_idleTotalMs / s_idleFrames : 0):F1}ms " +
                $"(budget {FrameBudgetMs:F1}ms)");
            s_worstFrameMs = 0;
            s_framesWatched = 0;
            s_watchedTotalMs = 0;
            s_worstSegmentMs = 0;
            s_segments = 0;
            s_overruns = 0;
        }

        private void Update()
        {
            var frameMs = Time.unscaledDeltaTime * 1000.0;
            if (s_inFlight != null)
            {
                if (frameMs > s_worstFrameMs) s_worstFrameMs = frameMs;
                s_watchedTotalMs += frameMs;
                s_framesWatched++;
            }
            else if (frameMs > 0.0)
            {
                // Keep the baseline to a recent window so it tracks the machine's current
                // state rather than averaging over the whole session.
                if (s_idleFrames >= 600)
                {
                    s_idleTotalMs *= 0.5;
                    s_idleFrames /= 2;
                }

                s_idleTotalMs += frameMs;
                s_idleFrames++;
            }

            ResetBudget();
        }

        [RegisterCleanup]
        public static void Cleanup()
        {
            if (s_instance != null)
            {
                s_instance.StopAllCoroutines();
                Destroy(s_instance.gameObject);
                s_instance = null;
            }

            s_inFlight = null;
        }
    }
}
