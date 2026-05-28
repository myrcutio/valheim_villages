using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Where to point the camera for a deterministic, generalizable village
    ///     screenshot. The anchor is derived from the active seed bed position so
    ///     PNG framing stays aligned with the sidecar JSON across captures and
    ///     across different villages — no hard-coded coordinates.
    ///
    ///     Yaw is fixed at 0° (north-up) and pitch at 90° (straight down). The
    ///     orthographic-feeling top-down view makes the on-screen X/Z axes line
    ///     up with the bake/BFS coordinates emitted by the sidecar so a human
    ///     can cross-reference without mental rotation.
    /// </summary>
    internal static class CaptureAnchor
    {
        /// <summary>
        ///     Vertical clearance (m) added above the seed-bed Y. 45m comfortably
        ///     frames a typical Meadows village (~35×35m) with the player camera's
        ///     default FOV — confirmed empirically against the current test
        ///     village whose passive PNG was captured at ~46m above bed-Y. Bump
        ///     this if a future village is too large to fit.
        ///
        ///     A plain const for now to match the surrounding settings idiom
        ///     (see Settings/Behavior.cs); flip to a BepInEx <c>ConfigEntry</c>
        ///     when tuning per-deploy becomes necessary.
        /// </summary>
        public const float VerticalClearance = 45f;

        /// <summary>
        ///     Resolved anchor pose. Pos/Yaw/Pitch are only meaningful when
        ///     <see cref="HasAnchor"/> is true; in the no-seed-bed case the
        ///     caller should fall back to a passive capture rather than guess
        ///     coordinates (per the project-wide "no silent fallbacks" rule).
        /// </summary>
        public readonly struct Result
        {
            public readonly bool HasAnchor;
            public readonly Vector3 Pos;
            public readonly float Yaw;
            public readonly float Pitch;
            public readonly string Reason; // populated only when HasAnchor is false

            public Result(bool hasAnchor, Vector3 pos, float yaw, float pitch, string reason)
            {
                HasAnchor = hasAnchor;
                Pos = pos;
                Yaw = yaw;
                Pitch = pitch;
                Reason = reason;
            }

            public static Result NoBed(string reason) =>
                new Result(false, Vector3.zero, 0f, 0f, reason);

            public static Result From(Vector3 bedPos) =>
                new Result(true, bedPos + Vector3.up * VerticalClearance, 0f, 90f, null);

            /// <summary>
            ///     Anchor at an arbitrary world position with a caller-provided
            ///     vertical clearance. Used for incident captures where the
            ///     anchor is a villager pos or destination pos rather than a
            ///     seed bed, and the framing scale wants to be tighter (10m)
            ///     than the village-overview default. Pos/Yaw/Pitch follow the
            ///     same convention as <see cref="From(Vector3)"/>.
            /// </summary>
            public static Result At(Vector3 anchorXZ, float clearance)
            {
                // Use the anchor's own Y as the floor, +clearance for the
                // camera height. If the caller passes a Y of 0 (XZ-only call
                // site like a console command), the camera lands at sea-level
                // + clearance, which is usually still useful — Valheim terrain
                // is rarely above 50m and 10m clearance over Y=0 still frames
                // the area, just from below ground if terrain rises sharply.
                // Callers with a real Y (incidents) get the expected behavior.
                return new Result(true, anchorXZ + Vector3.up * clearance, 0f, 90f, null);
            }
        }

        /// <summary>
        ///     Resolve the anchor pose against the seed bed nearest to
        ///     <paramref name="preferNear"/>. When <paramref name="preferNear"/>
        ///     is null, picks the first registered bed (single-village case is
        ///     trivial; multi-village resolution is left to A3's optional
        ///     village-id argument on the <c>vv_capture</c> console command).
        ///
        ///     Returns <see cref="Result.NoBed"/> when no seed bed is
        ///     registered — the calling capture path should log + degrade to a
        ///     passive snapshot rather than substitute fabricated coordinates.
        /// </summary>
        public static Result Resolve(Vector3? preferNear = null)
        {
            List<Vector3> beds;
            try
            {
                beds = VillagerAIManager.GetAllBedPositions();
            }
            catch
            {
                return Result.NoBed("VillagerAIManager threw resolving beds");
            }

            if (beds == null || beds.Count == 0)
                return Result.NoBed("no seed beds registered");

            if (preferNear == null) return Result.From(beds[0]);

            var bestBed = beds[0];
            var bestDistSq = float.MaxValue;
            foreach (var b in beds)
            {
                var dx = b.x - preferNear.Value.x;
                var dz = b.z - preferNear.Value.z;
                var d = dx * dx + dz * dz;
                if (d < bestDistSq)
                {
                    bestDistSq = d;
                    bestBed = b;
                }
            }

            return Result.From(bestBed);
        }

        /// <summary>
        ///     Resolve the anchor pose against a caller-supplied world position
        ///     with a caller-supplied vertical clearance. Used by the incident
        ///     recorder to anchor at villager pos and destination pos
        ///     independently. Yaw=0, pitch=90 matches the seed-bed overload so
        ///     PNG framing semantics stay consistent across capture trigger
        ///     types.
        /// </summary>
        public static Result ResolveAt(Vector3 worldPos, float clearance)
        {
            return Result.At(worldPos, clearance);
        }
    }
}
