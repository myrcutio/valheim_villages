using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;

namespace ValheimVillages.Behaviors.Relax
{
    /// <summary>
    ///     The four needs that decide what a villager feels like doing when nothing
    ///     productive wants control. Each is a pressure in [0,1]: 0 = fully satisfied,
    ///     1 = urgent.
    /// </summary>
    public enum Drive
    {
        /// <summary>Wants a fire, shelter, or the hot tub. Rises faster in rain/night.</summary>
        Warmth,

        /// <summary>Wants company — a table or fire another villager is already at.</summary>
        Social,

        /// <summary>Wants to stop moving. Rises with travel, falls while lingering.</summary>
        Rest,

        /// <summary>Wants novelty — the farm, the animals, a spot it hasn't visited lately.</summary>
        Curiosity,
    }

    /// <summary>Live drive pressures for one villager.</summary>
    public sealed class DriveState
    {
        public float Warmth;
        public float Social;
        public float Rest;
        public float Curiosity;

        public float Get(Drive d) => d switch
        {
            Drive.Warmth => Warmth,
            Drive.Social => Social,
            Drive.Rest => Rest,
            Drive.Curiosity => Curiosity,
            _ => 0f,
        };

        public void Set(Drive d, float v)
        {
            v = Mathf.Clamp01(v);
            switch (d)
            {
                case Drive.Warmth: Warmth = v; break;
                case Drive.Social: Social = v; break;
                case Drive.Rest: Rest = v; break;
                case Drive.Curiosity: Curiosity = v; break;
            }
        }

        public override string ToString() =>
            $"warmth={Warmth:F2} social={Social:F2} rest={Rest:F2} curiosity={Curiosity:F2}";
    }

    /// <summary>
    ///     Per-villager drive pressures, ticked from the idle behavior.
    ///
    ///     <para>These are deliberately <b>in-memory only</b>. They are a flavour signal for
    ///     choosing an idle spot, not authoritative villager state, so they must never reach a
    ///     record ZDO — see the record invariants (records change only on explicit player
    ///     action). A reload resets everybody to the neutral midpoint, which is harmless.</para>
    ///
    ///     <para>Drives rise on their own and fall only while the villager is actually at a
    ///     spot that serves them. That asymmetry is what produces rotation: sitting at the
    ///     fire drains Warmth, which lets Social or Curiosity become the top pressure and
    ///     sends the villager somewhere else next session, rather than re-picking the nearest
    ///     spot forever.</para>
    /// </summary>
    public static class VillagerDrives
    {
        /// <summary>Baseline pressure a villager starts (and reloads) at.</summary>
        private const float Neutral = 0.5f;

        /// <summary>Pressure gained per second while the drive is unmet.</summary>
        private const float RisePerSecond = 0.010f;

        /// <summary>Pressure shed per second while at a spot that serves the drive.</summary>
        private const float FallPerSecond = 0.055f;

        /// <summary>Extra Warmth pressure per second when it is raining or dark.</summary>
        private const float ColdBonusPerSecond = 0.014f;

        /// <summary>Largest elapsed slice honoured in one tick, so a stall can't jump drives.</summary>
        private const float MaxElapsedSeconds = 2f;

        /// <summary>
        ///     Pressure at which a drive is worth acting on. Below it an idle villager just
        ///     wanders; at or above it, relax takes over and sends it to a spot that serves the
        ///     drive. From <see cref="Neutral" /> an unmet drive reaches this in ~20s.
        /// </summary>
        public const float ElevatedThreshold = 0.7f;

        private static readonly Dictionary<string, DriveState> s_byVillager = new();

        /// <summary>Wall-clock time of each villager's last tick.</summary>
        private static readonly Dictionary<string, float> s_lastTick = new();

        /// <summary>Drive state for a villager, created at the neutral midpoint on first use.</summary>
        public static DriveState For(string villagerId)
        {
            if (string.IsNullOrEmpty(villagerId)) return new DriveState();
            if (s_byVillager.TryGetValue(villagerId, out var d)) return d;

            d = new DriveState
            {
                Warmth = Neutral,
                Social = Neutral,
                Rest = Neutral,
                // Start curiosity high so a freshly-settled villager explores its village
                // before it settles into the comfort loop.
                Curiosity = 0.75f,
            };
            s_byVillager[villagerId] = d;
            return d;
        }

        /// <summary>
        ///     Advance the villager's drives. <paramref name="servedBy" /> is the spot it is
        ///     currently lingering at, or null while travelling / working.
        ///
        ///     <para>Rates are per wall-clock second and the elapsed slice is measured here,
        ///     NOT taken from the caller's <c>dt</c>. The AI tick this is driven from does not
        ///     run once per frame, so trusting its <c>dt</c> made drives advance roughly an
        ///     order of magnitude slower than the constants say and effectively froze the idle
        ///     rotation. Measuring elapsed time keeps the tuning meaningful no matter how often
        ///     the caller ticks.</para>
        /// </summary>
        public static void Tick(string villagerId, float _, LocationType? servedBy, bool travelling)
        {
            if (string.IsNullOrEmpty(villagerId)) return;

            var now = Time.time;
            if (!s_lastTick.TryGetValue(villagerId, out var last))
            {
                s_lastTick[villagerId] = now;
                return; // first sighting — establish the baseline, advance nothing
            }

            var dt = Mathf.Min(now - last, MaxElapsedSeconds);
            if (dt <= 0f) return;
            s_lastTick[villagerId] = now;

            var d = For(villagerId);

            var cold = IsCold();
            foreach (Drive drive in System.Enum.GetValues(typeof(Drive)))
            {
                var gain = servedBy.HasValue
                    ? LeisureAppeal.Gain(servedBy.Value, drive)
                    : 0f;

                float delta;
                if (gain > 0f)
                    delta = -FallPerSecond * gain * dt;
                else
                    delta = RisePerSecond * dt;

                // Travelling is tiring; standing still is not.
                if (drive == Drive.Rest && travelling)
                    delta = RisePerSecond * 1.8f * dt;

                if (drive == Drive.Warmth && cold)
                    delta += ColdBonusPerSecond * dt;

                d.Set(drive, d.Get(drive) + delta);
            }
        }

        /// <summary>
        ///     True when the weather or the hour makes warmth matter — drives the
        ///     shelter-seeking preference in <see cref="LeisureAppeal" />.
        /// </summary>
        public static bool IsCold()
        {
            // These are statics that read EnvMan.instance internally, so guard the singleton
            // before calling them (they run before the world is up during load).
            if (EnvMan.instance == null) return false;
            return EnvMan.IsWet() || EnvMan.IsCold() || EnvMan.IsFreezing() || !EnvMan.IsDay();
        }

        /// <summary>The drive under the most pressure right now (for status text / diagnostics).</summary>
        public static Drive Dominant(string villagerId)
        {
            var d = For(villagerId);
            var best = Drive.Warmth;
            var bestVal = -1f;
            foreach (Drive drive in System.Enum.GetValues(typeof(Drive)))
            {
                var v = d.Get(drive);
                if (v <= bestVal) continue;
                bestVal = v;
                best = drive;
            }

            return best;
        }

        /// <summary>
        ///     True when any of this villager's drives has reached
        ///     <see cref="ElevatedThreshold" /> — i.e. it has a reason to go relax rather than wander.
        /// </summary>
        public static bool IsAnyElevated(string villagerId)
        {
            var d = For(villagerId);
            foreach (Drive drive in System.Enum.GetValues(typeof(Drive)))
                if (d.Get(drive) >= ElevatedThreshold)
                    return true;
            return false;
        }

        /// <summary>Drop one villager's drives (death / despawn).</summary>
        public static void Forget(string villagerId)
        {
            if (string.IsNullOrEmpty(villagerId)) return;
            s_byVillager.Remove(villagerId);
            s_lastTick.Remove(villagerId);
        }

        [RegisterCleanup]
        public static void Clear()
        {
            s_byVillager.Clear();
            s_lastTick.Clear();
        }
    }
}
