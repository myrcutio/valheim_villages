using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>Default pathing: 1m arrival, fire avoidance, walkable destination.</summary>
    public sealed class DefaultPathing : VillagerPathing
    {
        protected override float GetArrivalThreshold() => VillagerSettings.ArrivalThreshold;
        protected override bool ShouldIgnoreFire() => false;
        protected override Vector3 AdjustDestination(Vector3 worldPosition) =>
            VillagerMovement.GetWalkableDestination(worldPosition);
    }

    /// <summary>Guard patrol pathing: NavMesh-based destination snap for reliable wall-top paths.</summary>
    public sealed class GuardPatrolPathing : VillagerPathing
    {
        protected override float GetArrivalThreshold() => VillagerSettings.ArrivalThreshold;
        protected override bool ShouldIgnoreFire() => false;
        protected override Vector3 AdjustDestination(Vector3 worldPosition)
        {
            var filter = new NavMeshQueryFilter();
            filter.agentTypeID = VillageNavMeshBake.ResolveValheimHumanoidAgentTypeID();
            filter.areaMask = NavMesh.AllAreas;

            if (NavMesh.SamplePosition(worldPosition, out NavMeshHit hit, 5f, filter))
                return hit.position;

            return VillagerMovement.GetWalkableDestination(worldPosition);
        }
    }

    /// <summary>Worker pathing: 2m arrival, ignore fire (stations), walkable destination.</summary>
    public sealed class WorkerPathing : VillagerPathing
    {
        protected override float GetArrivalThreshold() => WorkSettings.WorkArrivalThreshold;
        protected override bool ShouldIgnoreFire() => true;
        protected override Vector3 AdjustDestination(Vector3 worldPosition) =>
            VillagerMovement.GetWalkableDestination(worldPosition);
    }

    /// <summary>
    /// HNA* pathing: uses hierarchical region graph (village partition with doors/stairs as links).
    /// Same arrival/threshold as default for now; graph can be used later for intermediate waypoints.
    /// </summary>
    public sealed class HnaPathing : VillagerPathing
    {
        protected override float GetArrivalThreshold() => VillagerSettings.ArrivalThreshold;
        protected override bool ShouldIgnoreFire() => false;
        protected override Vector3 AdjustDestination(Vector3 worldPosition) =>
            VillagerMovement.GetWalkableDestination(worldPosition);
    }

    /// <summary>Registry of pathing strategies by id. Used by VillagerAI to resolve waypoint.StrategyId.</summary>
    public static class PathingStrategyRegistry
    {
        public const string DefaultId = "default";
        public const string GuardPatrolId = "guard_patrol";
        public const string WorkerId = "worker";
        public const string HnaId = "hna";

        /// <summary>Order for cycling on stuck: default → guard_patrol → worker → default.</summary>
        private static readonly string[] s_strategyOrder = { DefaultId, GuardPatrolId, WorkerId };

        private static readonly Dictionary<string, VillagerPathing> s_strategies = new Dictionary<string, VillagerPathing>
        {
            [DefaultId] = new DefaultPathing(),
            [GuardPatrolId] = new GuardPatrolPathing(),
            [WorkerId] = new WorkerPathing(),
            [HnaId] = new HnaPathing()
        };

        /// <summary>Get the pathing strategy for the given id. Returns default if unknown.</summary>
        public static VillagerPathing Get(string strategyId)
        {
            if (string.IsNullOrEmpty(strategyId) || !s_strategies.TryGetValue(strategyId, out var strategy))
                return s_strategies[DefaultId];
            return strategy;
        }

        /// <summary>Next strategy id when cycling on stuck. Order: default → guard_patrol → worker → default.</summary>
        public static string GetNextStrategyId(string currentId)
        {
            if (string.IsNullOrEmpty(currentId)) return DefaultId;
            for (int i = 0; i < s_strategyOrder.Length; i++)
            {
                if (s_strategyOrder[i] == currentId)
                    return s_strategyOrder[(i + 1) % s_strategyOrder.Length];
            }
            return DefaultId;
        }

        /// <summary>True when the next strategy in the cycle is default (i.e. we've wrapped and should give up).</summary>
        public static bool IsWrappedToBase(string currentId) => GetNextStrategyId(currentId) == DefaultId;
    }
}
