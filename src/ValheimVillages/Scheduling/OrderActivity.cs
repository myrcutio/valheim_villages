using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.Scheduling
{
    /// <summary>
    ///     When each work order was last actually started, per village. Feeds the reranker's
    ///     staleness feature so a long-ignored order can gain ground on a perpetually-refilling
    ///     one — the anti-starvation signal, expressed as something the model can LEARN to weigh
    ///     rather than a hard-coded sort.
    ///
    ///     <para>In-memory only and deliberately so: it describes this session's dispatch history,
    ///     not durable village state, and a restart legitimately means "nothing worked recently".
    ///     An unknown order reports 0 ("never"), which the feature treats as maximally stale.</para>
    /// </summary>
    public static class OrderActivity
    {
        private static readonly Dictionary<string, float> s_lastWorked = new();

        private static string Key(string villageId, string itemPrefab) => villageId + ":" + itemPrefab;

        /// <summary>Record that work on this order actually began (not merely that it was offered).</summary>
        public static void MarkWorked(string villageId, string itemPrefab)
        {
            if (string.IsNullOrEmpty(villageId) || string.IsNullOrEmpty(itemPrefab)) return;
            s_lastWorked[Key(villageId, itemPrefab)] = Time.time;
        }

        /// <summary><c>Time.time</c> of the last start, or 0 if this order has never been worked.</summary>
        public static float LastWorked(string villageId, string itemPrefab)
        {
            if (string.IsNullOrEmpty(villageId) || string.IsNullOrEmpty(itemPrefab)) return 0f;
            return s_lastWorked.TryGetValue(Key(villageId, itemPrefab), out var t) ? t : 0f;
        }

        public static void Clear() => s_lastWorked.Clear();
    }
}
