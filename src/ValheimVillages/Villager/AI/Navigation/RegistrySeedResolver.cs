using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Resolves a <b>walkable</b> seed point near a village anchor (a registry
    ///     station, or a stored villager home) that an agent can actually stand on.
    ///
    ///     <para>Why this exists: the registry station's anchor position sits inside
    ///     its own piece colliders (tabletop + legs). Used verbatim it is NOT a
    ///     walkable cell — the slot-31 agent navmesh is carved away there, and the
    ///     Pass-1 reachability flood seeds from a cell the station walls off (N/S
    ///     neighbours blocked), so the flood reaches ~2 cells and the whole region
    ///     graph / boundary / patrol degenerates. Callers must resolve a clear seed
    ///     BEFORE spawning a villager at the registry or seeding the slot-31 navmesh
    ///     discovery.</para>
    ///
    ///     <para>Deliberately independent of the slot-31 navmesh (which is what the
    ///     discovery is about to (re)bake and may be stale/empty): walkability is
    ///     judged from real geometry — a ground raycast, the agent-body capsule
    ///     clearance check the bake uses for <c>rej_blocked</c>, and a confirmation
    ///     against the always-present vanilla Humanoid navmesh.</para>
    /// </summary>
    public static class RegistrySeedResolver
    {
        // Agent-body capsule — matches the constants the terrain bake pass uses for
        // rej_blocked (MeshProbe.ReportCapsuleHits), so "clear" here means the same
        // thing the navmesh bake means by "not blocked by a piece".
        private const float CapsuleRadius = 0.3f;
        private const float CapsuleHeight = 1.4f;
        private const float CapsuleLift = 0.25f;

        // Vertical raycast window for finding the standing surface at a probe XZ.
        private const float RaycastUp = 3f;
        private const float RaycastDown = 8f;

        // How far the resolved seed may snap onto the vanilla navmesh.
        private const float HumanoidSnapRadius = 1f;

        /// <summary>
        ///     Generous one-off probe used only to decide whether Valheim's navmesh exists at
        ///     all near the anchor. Much wider than <see cref="HumanoidSnapRadius" /> so a
        ///     merely awkward anchor is not mistaken for a missing mesh.
        /// </summary>
        private const float HumanoidProbeRadius = 12f;

        /// <summary>
        ///     The "no Humanoid navmesh" notice is a standing property of the host, not an
        ///     event, and this resolver runs per anchor per partition — so say it once rather
        ///     than a dozen times a rebuild.
        /// </summary>
        private static bool s_warnedHumanoidAbsent;

        [Attributes.RegisterCleanup]
        public static void ResetWarnings()
        {
            s_warnedHumanoidAbsent = false;
        }

        // Ring search: radius 0 first (return the anchor unchanged when it is already
        // clear — idempotent for good seeds), then expanding rings to step just
        // outside the station footprint. Closest qualifying point on the smallest
        // ring that yields any hit wins.
        private static readonly float[] SearchRadii = { 0f, 1.5f, 2f, 2.5f, 3f, 4f };
        private const int Directions = 12;

        /// <summary>
        ///     Find the closest walkable, capsule-clear point near <paramref name="anchor" />.
        ///     Returns false (and leaves <paramref name="seed" /> = anchor) when nothing
        ///     within range qualifies — callers decide; this never silently hands back
        ///     the blocked anchor as if it were valid.
        /// </summary>
        /// <param name="minRadius">
        ///     Smallest ring to consider, so a caller whose own piece is a big solid lump can
        ///     refuse a seed standing ON it. Measured on a live server: a Forester's Post seed
        ///     landed on top of its own woodpile — the ground ray hits the pile, the capsule
        ///     above it is clear, so radius 0 "qualifies" — and the reachability flood then
        ///     found all four neighbours blocked by that same pile (WallBlocks=TRUE
        ///     hits=[log;log]). The anchor cell became an island: no region, no path, no
        ///     woodlot.
        /// </param>
        public static bool TryResolveWalkableSeed(Vector3 anchor, out Vector3 seed, float minRadius = 0f)
        {
            seed = anchor;

            var humanoidId = VillagerAgentType.ResolveValheimHumanoidAgentTypeID();
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = humanoidId,
                areaMask = NavMesh.AllAreas,
            };

            // Is Valheim's own navmesh actually present here? On a DEDICATED SERVER it is
            // not baked at all (measured: Humanoid SamplePosition MISSes everywhere,
            // including open terrain), while the agent type id still resolves non-zero. The
            // confirmation below therefore rejected every candidate and this method returned
            // false for every anchor on a headless host — silently, since callers only see
            // "no walkable seed found". Probe once, wide, and treat "no mesh anywhere" as
            // "cannot confirm" rather than "not walkable": the ground raycast and the
            // capsule clearance are still real evidence on their own.
            var humanoidAvailable = humanoidId != 0
                && NavMesh.SamplePosition(anchor, out _, HumanoidProbeRadius, filter);
            if (humanoidId != 0 && !humanoidAvailable && !s_warnedHumanoidAbsent)
            {
                s_warnedHumanoidAbsent = true;
                Plugin.Log?.LogInfo(
                    "[SeedResolver] Valheim's Humanoid navmesh is absent here (dedicated server); " +
                    "confirming seeds from ground + capsule clearance only");
            }

            foreach (var radius in SearchRadii)
            {
                if (radius < minRadius) continue;
                var count = radius == 0f ? 1 : Directions;
                Vector3? bestOnRing = null;
                var bestDistSq = float.MaxValue;

                for (var i = 0; i < count; i++)
                {
                    var angle = 360f / count * i * Mathf.Deg2Rad;
                    var probe = anchor + new Vector3(
                        Mathf.Cos(angle) * radius, 0f, Mathf.Sin(angle) * radius);

                    if (!TryGroundPoint(probe, anchor.y, out var ground)) continue;
                    if (!CapsuleClear(ground)) continue;

                    // Confirm against the vanilla navmesh and snap onto it — but only where
                    // that mesh exists (see humanoidAvailable above).
                    if (humanoidAvailable)
                    {
                        if (!NavMesh.SamplePosition(ground, out var hit, HumanoidSnapRadius, filter))
                            continue;
                        ground = hit.position;
                    }

                    var distSq = (ground - anchor).sqrMagnitude;
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        bestOnRing = ground;
                    }
                }

                // Take the closest qualifying point on the smallest ring that has one.
                if (bestOnRing.HasValue)
                {
                    seed = bestOnRing.Value;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        ///     Locate the standing surface at <paramref name="xz" /> by raycasting down
        ///     from just above <paramref name="refY" />. Falls back to terrain height.
        /// </summary>
        private static bool TryGroundPoint(Vector3 xz, float refY, out Vector3 ground)
        {
            ground = xz;
            var mask = LayerMask.GetMask("Default", "static_solid", "piece", "terrain");
            var origin = new Vector3(xz.x, refY + RaycastUp, xz.z);
            if (Physics.Raycast(origin, Vector3.down, out var hit, RaycastUp + RaycastDown,
                    mask, QueryTriggerInteraction.Ignore))
            {
                ground = hit.point;
                return true;
            }

            if (ZoneSystem.instance != null)
            {
                ground = new Vector3(xz.x, ZoneSystem.instance.GetGroundHeight(xz), xz.z);
                return true;
            }

            return false;
        }

        /// <summary>
        ///     True when the agent-body capsule standing on <paramref name="ground" /> is
        ///     clear of piece/static colliders — i.e. NOT inside the station footprint.
        /// </summary>
        private static bool CapsuleClear(Vector3 ground)
        {
            var blockMask = LayerMask.GetMask("Default", "static_solid", "piece");
            var p0 = ground + Vector3.up * (CapsuleLift + CapsuleRadius);
            var p1 = ground + Vector3.up * (CapsuleLift + CapsuleHeight - CapsuleRadius);
            return !Physics.CheckCapsule(p0, p1, CapsuleRadius, blockMask, QueryTriggerInteraction.Ignore);
        }
    }
}
