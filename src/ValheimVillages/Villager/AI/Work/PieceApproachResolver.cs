using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Villager.AI.Work
{
    /// <summary>
    ///     Resolves a walkable spot beside a PLAIN world piece — a beehive, a berry bush, a
    ///     wild mushroom — through the region LOOKUP GRID rather than the station approach
    ///     resolver.
    ///
    ///     <para>A station has a standoff pad the station resolver knows how to land on. A plain
    ///     piece does not: its own position sits inside its own collider, so
    ///     <c>PointToRegionId</c> at the piece comes back unresolved even with walkable floor a
    ///     metre away. Observed on a hive 17m from the anchor, which the station resolver
    ///     rejected outright and reported as "No station 'Beehive' in village" — the same shape
    ///     as the perimeter-piece failure that made the carpenter ignore damaged walls.</para>
    ///
    ///     <para>The remedy is the same one the carpenter got: walk the lookup grid, which only
    ///     ever returns cells <c>PointToRegionId</c> agrees with, and take the nearest one within
    ///     reach that the agent can actually stand on. Shared rather than copied per harvester —
    ///     every "stand next to a thing that isn't a station" caller needs exactly this, and a
    ///     second copy would drift from the first the next time the grid's rules change.</para>
    /// </summary>
    public static class PieceApproachResolver
    {
        /// <summary>
        ///     Snap radius when landing a resolved region cell onto the agent navmesh.
        ///     Deliberately small so the approach stays inside the cell we chose.
        /// </summary>
        private const float ApproachSnapRadius = 2f;

        /// <summary>
        ///     A standing spot within <paramref name="reach" /> of <paramref name="piecePos" />
        ///     that belongs to the village graph at <paramref name="anchor" /> and that the
        ///     villager agent can path onto. False when the piece is out of the graph's reach —
        ///     the caller must then treat the piece as un-workable rather than walk at it.
        /// </summary>
        public static bool TryResolve(Vector3 anchor, Vector3 piecePos, float reach, out Vector3 approach)
        {
            approach = Vector3.zero;
            if (!VillagerAgentType.IsRegistered) return false;

            var graph = VillageRegistry.GraphAt(anchor);
            if (graph == null) return false;

            if (!graph.TryFindNearestLookupCell(
                    piecePos,
                    pos => NavMesh.SamplePosition(pos, out _, ApproachSnapRadius, AgentFilter()),
                    out var cell,
                    out _,
                    reach))
                return false;

            if (!NavMesh.SamplePosition(cell, out var near, ApproachSnapRadius, AgentFilter()))
                return false;

            approach = near.position;
            return true;
        }

        private static NavMeshQueryFilter AgentFilter()
        {
            return new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };
        }
    }
}
