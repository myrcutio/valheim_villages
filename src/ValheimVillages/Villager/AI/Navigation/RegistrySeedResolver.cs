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
        public static bool TryResolveWalkableSeed(Vector3 anchor, out Vector3 seed)
        {
            seed = anchor;

            var humanoidId = VillagerAgentType.ResolveValheimHumanoidAgentTypeID();
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = humanoidId,
                areaMask = NavMesh.AllAreas,
            };

            foreach (var radius in SearchRadii)
            {
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

                    // Confirm the surface is genuinely reachable ground per the vanilla
                    // navmesh (which, unlike slot 31, is always baked). Snap onto it.
                    if (humanoidId != 0)
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
