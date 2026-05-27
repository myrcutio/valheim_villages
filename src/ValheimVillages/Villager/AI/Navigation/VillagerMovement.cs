using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Navigation helpers for VillagerAI.
    /// </summary>
    public static class VillagerMovement
    {
        /// <summary>
        ///     Single source of truth for "is the villager close enough to this
        ///     position to be considered there?" 3D distance against an
        ///     explicit threshold. Callers pass
        ///     <see cref="VillagerSettings.ArrivalThreshold"/> for final
        ///     arrival at stations / chests (generous, ~2m) and
        ///     <see cref="VillagerSettings.PathNodePopThreshold"/> for
        ///     intermediate path-node popping (tight, ~0.5m). Same shape, two
        ///     distinct semantics — using the generous arrival radius for
        ///     intermediate pops eats routing corners and strands the agent
        ///     against obstacles those corners were supposed to detour around.
        /// </summary>
        public static bool IsAtPosition(Vector3 villagerPos, Vector3 target, float threshold)
        {
            return Vector3.Distance(villagerPos, target) < threshold;
        }

        /// <summary>
        ///     Attempt to compute a COMPLETE path against the villager NavMesh
        ///     (slot 31). Returns true only when
        ///     <see cref="NavMeshPathStatus.PathComplete" /> — partial / invalid
        ///     paths are treated as "target unreachable" and the caller should
        ///     enter the recovery flow rather than walking a path that ends
        ///     short. Populates <paramref name="outPath" /> with
        ///     <c>navPath.corners</c> only on success; left empty on failure.
        /// </summary>
        public static bool TryFindCompletePath(Vector3 start, Vector3 end, List<Vector3> outPath)
        {
            outPath?.Clear();

            if (!VillagerAgentType.IsRegistered) return false;

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            if (!NavMesh.SamplePosition(start, out var startHit, 1f, filter)) return false;
            if (!NavMesh.SamplePosition(end, out var endHit, 1f, filter)) return false;

            var navPath = new NavMeshPath();
            if (!NavMesh.CalculatePath(startHit.position, endHit.position, filter, navPath))
                return false;
            if (navPath.status != NavMeshPathStatus.PathComplete) return false;

            if (outPath != null)
            {
                var corners = navPath.corners;
                for (var i = 0; i < corners.Length; i++) outPath.Add(corners[i]);
            }

            return true;
        }

        /// <summary>
        ///     Snap a world position to the walkable surface below it, if close.
        ///     Avoids snapping to roofs/ceilings in multi-story buildings.
        ///  </summary>
        public static Vector3 GetWalkableDestination(Vector3 worldPosition)
        {
            if (ZoneSystem.instance == null) return worldPosition;
            if (!ZoneSystem.instance.GetSolidHeight(worldPosition, out var h))
                return worldPosition;
            if (Mathf.Abs(h - worldPosition.y) < 2f)
                return new Vector3(worldPosition.x, h, worldPosition.z);
            return worldPosition;
        }

        /// <summary>
        ///     Find the nearest reachable point on the villager NavMesh within
        ///     <paramref name="maxRadius"/> of <paramref name="target"/>. Use this for stations
        ///     whose own transform sits on a non-walkable obstacle (e.g. Smelter, CharcoalKiln):
        ///     the villager can't path TO the obstacle, but they CAN path to a walkable cell
        ///     adjacent to it, which is close enough for RPC interaction (RPC_AddOre, RPC_AddFuel, …).
        ///     Returns true and writes the approach point on success; returns false (and leaves
        ///     <paramref name="approachPoint"/> = target) when nothing walkable is found within radius.
        /// </summary>
        public static bool TryFindReachableApproach(Vector3 target, float maxRadius, out Vector3 approachPoint)
        {
            approachPoint = target;
            if (!VillagerAgentType.IsRegistered) return false;

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            if (!NavMesh.SamplePosition(target, out var hit, maxRadius, filter)) return false;
            approachPoint = hit.position;
            return true;
        }

        /// <summary>
        ///     Resolve a navigable approach point for a target whose own centroid may sit on a
        ///     non-walkable obstacle (smelter on a foundation, chest on a shelf, etc). Probes the
        ///     centroid first, then expanding compass-offset rings, sampling the NavMesh at each
        ///     and validating with a complete-path check from <paramref name="pathSource"/>.
        ///     <para>Pass an optional <paramref name="hullPredicate"/> that returns false for
        ///     candidates outside the village hull — used by station lookup to reject points on
        ///     the wrong side of an outer wall. Pass null when hull filtering isn't applicable
        ///     (e.g. container targets — no hull data available at the workflow layer).</para>
        ///     Returns true and writes the approach on success. Returns false (writes
        ///     <paramref name="approach"/> = target) when no probe in the ring is both walkable
        ///     AND reachable from <paramref name="pathSource"/>.
        /// </summary>
        public static bool TryResolveApproach(
            Vector3 target,
            Vector3 pathSource,
            System.Func<Vector3, bool> hullPredicate,
            out Vector3 approach)
        {
            approach = target;
            var probes = s_probeOffsets;
            var pathBuffer = new List<Vector3>();
            for (var i = 0; i < probes.Length; i++)
            {
                var probe = target + probes[i];
                if (!TryFindReachableApproach(probe, ApproachProbeRadius, out var hit)) continue;
                if (hullPredicate != null && !hullPredicate(hit)) continue;
                if (!TryFindCompletePath(pathSource, hit, pathBuffer)) continue;
                approach = hit;
                return true;
            }
            return false;
        }

        private const float ApproachProbeRadius = 1.5f;

        private static readonly Vector3[] s_probeOffsets = BuildProbeOffsets();

        private static Vector3[] BuildProbeOffsets()
        {
            var list = new List<Vector3> { Vector3.zero };
            float[] rings = { 2f, 4f };
            foreach (var r in rings)
            {
                list.Add(new Vector3(0f, 0f, r));
                list.Add(new Vector3(r, 0f, 0f));
                list.Add(new Vector3(0f, 0f, -r));
                list.Add(new Vector3(-r, 0f, 0f));
                list.Add(new Vector3(r, 0f, r));
                list.Add(new Vector3(r, 0f, -r));
                list.Add(new Vector3(-r, 0f, -r));
                list.Add(new Vector3(-r, 0f, r));
            }
            return list.ToArray();
        }
    }
}