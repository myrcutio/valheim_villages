using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Fixed-capacity ring buffer of per-villager AI state mutations. Used
    ///     by the incident dump system to answer "what was this villager doing
    ///     in the last 30 seconds before the failure?" without log scraping.
    ///
    ///     <para>Events are recorded at the mutation site (TargetSet when AI
    ///     picks a new movement target, PathRecompute when TryFindPathCustom
    ///     runs, StateChange when CurrentState transitions). The ring is owned
    ///     per <c>VillagerAI</c>; the recorder doesn't try to coalesce events
    ///     across villagers — each one's history is its own thread.</para>
    ///
    ///     <para>Capacity is 256. At a worst-case tick of ~10 Hz with all event
    ///     types firing every tick, that's ~25s of history; in practice events
    ///     are sparser (TargetSet/StateChange fire on transitions, not every
    ///     tick) and the buffer covers well over 30s. Older events are silently
    ///     overwritten; the snapshot returns events in
    ///     oldest-first chronological order with monotonic <c>tSec</c> deltas
    ///     relative to a caller-supplied <c>nowSec</c>.</para>
    /// </summary>
    internal sealed class AiEventRing
    {
        private const int Capacity = 256;

        public enum EventKind
        {
            TargetSet,
            PathRecompute,
            StateChange,
        }

        public readonly struct Event
        {
            public readonly float TimeSec;       // Time.time at recording
            public readonly EventKind Kind;
            public readonly string Detail;       // structured payload, JSON-safe
            public readonly Vector3 PosA;        // event-dependent: target, currentPos, etc.
            public readonly Vector3 PosB;        // event-dependent: prev target, etc.
            public readonly int IntA;            // event-dependent: corner count, etc.

            public Event(float t, EventKind kind, string detail, Vector3 posA, Vector3 posB, int intA)
            {
                TimeSec = t;
                Kind = kind;
                Detail = detail;
                PosA = posA;
                PosB = posB;
                IntA = intA;
            }
        }

        private readonly Event[] m_buffer = new Event[Capacity];
        private int m_writeIdx;
        private int m_count;

        public int Count => m_count;

        /// <summary>
        ///     Record a "TargetSet" event. <paramref name="source"/> identifies
        ///     the caller (WorkScan, Patrol, Recovery, etc.) so the timeline
        ///     readback shows what KIND of code path picked the target.
        /// </summary>
        public void RecordTargetSet(Vector3 newTarget, Vector3 prevTarget, string source)
        {
            Append(new Event(Time.time, EventKind.TargetSet, source ?? "unknown",
                newTarget, prevTarget, 0));
        }

        /// <summary>
        ///     Record a "PathRecompute" event. <paramref name="result"/> is
        ///     the Unity NavMesh status as a string ("Complete", "Partial",
        ///     "Invalid", "Empty"); <paramref name="cornerCount"/> is the
        ///     number of corners in the resulting path (0 if none).
        /// </summary>
        public void RecordPathRecompute(string result, int cornerCount, Vector3 target)
        {
            Append(new Event(Time.time, EventKind.PathRecompute, result ?? "unknown",
                target, Vector3.zero, cornerCount));
        }

        /// <summary>
        ///     Record a "StateChange" event. <paramref name="reason"/> is a
        ///     short tag identifying the transition trigger (e.g. "Arrived",
        ///     "EnterRecovery", "AbandonWork").
        /// </summary>
        public void RecordStateChange(string prevState, string nextState, string reason)
        {
            // Pack the transition into Detail; PosA/PosB unused.
            var detail = $"{prevState}->{nextState}:{reason ?? ""}";
            Append(new Event(Time.time, EventKind.StateChange, detail,
                Vector3.zero, Vector3.zero, 0));
        }

        private void Append(Event ev)
        {
            m_buffer[m_writeIdx] = ev;
            m_writeIdx = (m_writeIdx + 1) % Capacity;
            if (m_count < Capacity) m_count++;
        }

        /// <summary>
        ///     Return the buffer's events in oldest-first chronological order,
        ///     filtered to those within <paramref name="lookbackSec"/> of
        ///     <paramref name="nowSec"/>. Caller-friendly for the incident
        ///     JSON writer.
        /// </summary>
        public List<Event> Snapshot(float nowSec, float lookbackSec)
        {
            var result = new List<Event>(m_count);
            // Oldest entry sits at (writeIdx - count) mod Capacity.
            var start = (m_writeIdx - m_count + Capacity) % Capacity;
            var cutoff = nowSec - lookbackSec;
            for (var i = 0; i < m_count; i++)
            {
                var ev = m_buffer[(start + i) % Capacity];
                if (ev.TimeSec >= cutoff) result.Add(ev);
            }
            return result;
        }
    }
}
