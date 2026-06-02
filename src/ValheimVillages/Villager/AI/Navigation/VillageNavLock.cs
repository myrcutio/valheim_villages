using UnityEngine;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Global "hold" gate for villager movement around a navmesh / region-graph
    ///     rebuild.
    ///
    ///     <para>The region partition rebakes the slot-31 navmesh synchronously, so
    ///     no villager ticks DURING it — the hazard is the frames immediately
    ///     AFTER: a villager whose agent path was computed against the old mesh can
    ///     follow a now-invalid route off a ledge (the "train off a cliff") before
    ///     Unity's autoRepath corrects it. While the hold is active the AI tick
    ///     stops the mover and drops its stale path; the villager's behavior state
    ///     and target are left untouched, so it resumes its previous task on the
    ///     fresh mesh once the hold expires.</para>
    ///
    ///     <para>The hold is a fixed settle WINDOW (a future timestamp), not a
    ///     paired acquire/release. That is deliberate: a dropped, failed, or
    ///     timed-out rebuild can never leave the hold stuck on — it always expires
    ///     on its own. <see cref="RequestHold" /> only ever extends the window.</para>
    /// </summary>
    public static class VillageNavLock
    {
        private static float s_holdUntil;

        /// <summary>True while villager movement should be held for a rebuild settle.</summary>
        public static bool IsHeld => Time.time < s_holdUntil;

        /// <summary>Seconds remaining on the hold (0 when not held). For diagnostics.</summary>
        public static float SecondsRemaining => Mathf.Max(0f, s_holdUntil - Time.time);

        /// <summary>
        ///     Hold villager movement for at least <paramref name="seconds" /> from
        ///     now. Extends an existing hold; never shortens it.
        /// </summary>
        public static void RequestHold(float seconds)
        {
            var until = Time.time + seconds;
            if (until > s_holdUntil) s_holdUntil = until;
        }
    }
}
