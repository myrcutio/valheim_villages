using UnityEngine;

namespace ValheimVillages.Behaviors.Relax
{
    /// <summary>
    ///     Replicates a villager's drive pressures from its owner to every other peer, so the
    ///     mood readout in the villager UI is the villager's actual state rather than the
    ///     reader's guess at it.
    ///
    ///     <para>Needed because <see cref="VillagerDrives" /> is an in-memory static keyed by
    ///     villager id, ticked from <c>VillagerAI.UpdateAI</c> — and <c>BaseAI.UpdateAI</c> runs
    ///     only on the ZDO owner. Villagers are server-owned from birth, so on a dedicated
    ///     server the drives live entirely on the host: a client calling
    ///     <see cref="VillagerDrives.For" /> would silently get a freshly-created neutral state
    ///     for every villager and the panel would report a uniform, invented mood.</para>
    ///
    ///     <para>This writes to the NPC's own ZDO, never the villager RECORD — drives are
    ///     transient live state that resets on reload, exactly what an NPC ZDO is for, and the
    ///     record invariants forbid implicit writes there. <see cref="VillagerDrives" /> itself
    ///     stays in-memory-only as documented; replication is this class's problem.</para>
    /// </summary>
    public static class VillagerDriveMirror
    {
        /// <summary>Four drives packed one per byte. One ZDO field instead of four.</summary>
        public const string Key = "vv_drives";

        /// <summary>
        ///     Owner-side publish cadence. A drive needs ~20s to cross from neutral to
        ///     actionable, so 2s granularity is invisible in a panel while keeping this off the
        ///     per-frame network path.
        /// </summary>
        public const float PublishInterval = 2f;

        private const long Absent = -1L;

        /// <summary>
        ///     Publish this villager's drives if we own it and the interval has elapsed.
        ///     <paramref name="nextPublish" /> is the caller's own timer, so no per-villager
        ///     state is kept here to leak when one despawns.
        /// </summary>
        public static void Publish(ZNetView nview, string villagerId, ref float nextPublish, float dt)
        {
            nextPublish -= dt;
            if (nextPublish > 0f) return;
            nextPublish = PublishInterval;

            // A non-owner write is silently dropped, so there is nothing to do but skip.
            if (nview == null || !nview.IsValid() || !nview.IsOwner()) return;

            var zdo = nview.GetZDO();
            if (zdo == null) return;

            zdo.Set(Key, Pack(VillagerDrives.For(villagerId)));
        }

        /// <summary>
        ///     The drives to show for this villager: the live ones when we own it (single
        ///     player, or the host), otherwise the owner's published mirror. Null when neither
        ///     is available — the caller says "not known here" rather than inventing a mood.
        /// </summary>
        public static DriveState ForDisplay(ZNetView nview, string villagerId)
        {
            if (nview != null && nview.IsValid() && nview.IsOwner())
                return VillagerDrives.For(villagerId);

            var packed = nview?.GetZDO()?.GetLong(Key, Absent) ?? Absent;
            return packed == Absent ? null : Unpack(packed);
        }

        internal static long Pack(DriveState d)
        {
            return (long)Quantise(d.Warmth)
                   | ((long)Quantise(d.Social) << 8)
                   | ((long)Quantise(d.Rest) << 16)
                   | ((long)Quantise(d.Curiosity) << 24);
        }

        internal static DriveState Unpack(long packed)
        {
            return new DriveState
            {
                Warmth = Dequantise((int)(packed & 0xFF)),
                Social = Dequantise((int)((packed >> 8) & 0xFF)),
                Rest = Dequantise((int)((packed >> 16) & 0xFF)),
                Curiosity = Dequantise((int)((packed >> 24) & 0xFF)),
            };
        }

        /// <summary>
        ///     Rounds UP, not to nearest. Nearest puts <see cref="VillagerDrives.ElevatedThreshold" />
        ///     (0.7 * 255 = 178.5) on an exact half, which round-half-to-even sends to 178 =
        ///     0.698 — so a drive that had just become worth acting on arrived at every remote
        ///     peer reading as not elevated, and the panel disagreed with what the villager was
        ///     about to do. Ceiling costs under 1/255 of upward bias and can only ever show a
        ///     want slightly early rather than hide one.
        /// </summary>
        private static int Quantise(float v)
        {
            return Mathf.Clamp(Mathf.CeilToInt(Mathf.Clamp01(v) * 255f), 0, 255);
        }

        private static float Dequantise(int b)
        {
            return b / 255f;
        }
    }
}
