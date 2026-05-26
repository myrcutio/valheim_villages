using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    ///     Pruning checks for HNA flood-fill cells. Cases 1-2 are pure math
    ///     (unit-testable without Unity). Cases 3-4 wrap Unity Physics/NavMesh APIs.
    /// </summary>
    internal static class CellValidator
    {
        internal const float MaxLedgeDrop = 1.5f;
        internal const float MaxSlopeDeg = 30f;
        internal const float GroundCheckDist = 2f;

        /// <summary>Minimum NavMesh edge distance for <see cref="IsSurfaceWideEnough" />.</summary>
        internal const float MinEdgeDistance = 0.2f;

        /// <summary>Offset for multi-probe width check. Must exceed half the typical wall thickness (~0.15m).</summary>
        internal const float WidthProbeOffset = 0.35f;

        /// <summary>Minimum cardinal directions with NavMesh for <see cref="IsWideEnough" />.</summary>
        internal const int MinProbeHits = 3;

        private static readonly int GroundMask =
            LayerMask.GetMask("Default", "static_solid", "terrain", "piece");

        /// <summary>
        ///     Case 1: Rejects downward drops exceeding <see cref="MaxLedgeDrop" />.
        ///     Climbing up is never rejected by this check.
        /// </summary>
        internal static bool IsLedgeDrop(float fromY, float toY)
        {
            return fromY - toY > MaxLedgeDrop;
        }

        /// <summary>
        ///     Case 2: Rejects transitions steeper than <see cref="MaxSlopeDeg" /> in either direction.
        ///     Near-zero horizontal distance falls back to a step-height check against the live
        ///     villager agent climb. Returns <c>true</c> (cell rejected) if the agent climb is
        ///     not available (slot not registered yet) — no synthesised default. The caller may
        ///     also branch on the explicit value via <see cref="TryIsTooSteep" />.
        /// </summary>
        internal static bool IsTooSteep(float fromX, float fromY, float fromZ,
            float toX, float toY, float toZ)
        {
            if (TryIsTooSteep(fromX, fromY, fromZ, toX, toY, toZ, out var tooSteep))
                return tooSteep;

            Plugin.Log?.LogError(
                "[CellValidator] IsTooSteep: villager agent climb unavailable " +
                "(Pathfinding.instance not alive or agent slot 31 not registered); " +
                "rejecting cell transition fail-closed. Caller should defer cell validation.");
            return true;
        }

        /// <summary>
        ///     Try-pattern variant: returns false if slope/climb cannot be evaluated because the
        ///     villager agent slot is not yet registered. Callers that can defer work (flood-fill
        ///     drivers, tests) should branch on this rather than the eager <see cref="IsTooSteep" />.
        /// </summary>
        internal static bool TryIsTooSteep(float fromX, float fromY, float fromZ,
            float toX, float toY, float toZ,
            out bool tooSteep)
        {
            tooSteep = false;
            var dy = Mathf.Abs(toY - fromY);
            float dx = toX - fromX, dz = toZ - fromZ;
            var horiz = Mathf.Sqrt(dx * dx + dz * dz);
            if (horiz < 0.01f)
            {
                if (!VillagerAgentType.TryGetClimb(out var maxClimb))
                    return false;
                tooSteep = dy > maxClimb;
                return true;
            }

            tooSteep = Mathf.Atan2(dy, horiz) * Mathf.Rad2Deg > MaxSlopeDeg;
            return true;
        }

        /// <summary>
        ///     Case 3: Verifies solid geometry exists below <paramref name="pos" />.
        ///     Rejects cells on floating NavMesh fragments with no ground.
        /// </summary>
        internal static bool HasGroundBelow(Vector3 pos)
        {
            return Physics.Raycast(
                new Vector3(pos.x, pos.y + 0.2f, pos.z), Vector3.down,
                GroundCheckDist + 0.2f, GroundMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        ///     Case 4 (edge-distance variant): Verifies the NavMesh surface at <paramref name="pos" />
        ///     is at least <see cref="MinEdgeDistance" /> from the nearest edge.
        ///     Used by integration tests to audit the graph.
        /// </summary>
        internal static bool IsSurfaceWideEnough(Vector3 pos, NavMeshQueryFilter filter)
        {
            if (!NavMesh.FindClosestEdge(pos, out var hit, filter))
                return false;
            return hit.distance >= MinEdgeDistance;
        }

        /// <summary>
        ///     Case 4 (multi-probe variant): Samples NavMesh in 4 cardinal directions from
        ///     <paramref name="pos" />. A floor has NavMesh in all directions; a wall top only
        ///     along its axis. Requires at least <see cref="MinProbeHits" /> hits to pass.
        ///     Uses a tight sample radius so probes don't pick up adjacent floor NavMesh.
        /// </summary>
        internal static bool IsWideEnough(Vector3 pos, NavMeshQueryFilter filter)
        {
            const float tightRadius = 0.5f;
            var hits = 0;
            if (NavMesh.SamplePosition(pos + new Vector3(WidthProbeOffset, 0, 0), out _, tightRadius, filter)) hits++;
            if (NavMesh.SamplePosition(pos + new Vector3(-WidthProbeOffset, 0, 0), out _, tightRadius, filter)) hits++;
            if (NavMesh.SamplePosition(pos + new Vector3(0, 0, WidthProbeOffset), out _, tightRadius, filter)) hits++;
            if (NavMesh.SamplePosition(pos + new Vector3(0, 0, -WidthProbeOffset), out _, tightRadius, filter)) hits++;
            return hits >= MinProbeHits;
        }
    }
}