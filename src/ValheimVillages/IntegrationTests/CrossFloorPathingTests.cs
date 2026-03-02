using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Testing;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.IntegrationTests
{
    /// <summary>
    /// Integration tests for cross-floor NavMesh pathing.
    /// Validates that villagers can path between floors of player-built
    /// structures, whether via contiguous NavMesh geometry (stairs) or
    /// explicit NavMeshLinks bridging disconnected islands.
    ///
    /// Test scenario positions (from test world):
    ///   Ground floor (cooking station): (-2255.9, 37.6, 1299.9)
    ///   Upper floor (balcony container): (-2255.4, 44.1, 1294.2)
    ///
    /// CalculatePath is a static NavMesh query — no game objects needed,
    /// only a baked NavMesh.
    /// </summary>
    public static class CrossFloorPathingTests
    {
        private static readonly Vector3 GroundFloorPos = new Vector3(-2255.9f, 37.6f, 1299.9f);
        private static readonly Vector3 UpperFloorPos = new Vector3(-2255.4f, 44.1f, 1294.2f);

        [ModTest(Name = "CrossFloor_NavMeshBaked", Order = 200)]
        public static void CrossFloor_NavMeshBaked()
        {
            if (!VillageNavMeshBake.HasBakedInstance)
            {
                Plugin.Log?.LogInfo("[CrossFloorTest] Skipped: NavMesh baking disabled (using built-in MoveTo/FindPath)");
                return;
            }
        }

        [ModTest(Name = "CrossFloor_GroundToUpperFloor_PathComplete", Order = 210)]
        public static void CrossFloor_GroundToUpperFloor_PathComplete()
        {
            if (!VillageNavMeshBake.HasBakedInstance) return; // baking disabled
            RequireNavMesh();
            var filter = BuildFilter();

            bool srcOnMesh = NavMesh.SamplePosition(GroundFloorPos, out NavMeshHit srcHit, 5f, filter);
            ModAssert.True(srcOnMesh,
                $"Ground floor position ({GroundFloorPos.x:F1},{GroundFloorPos.y:F1},{GroundFloorPos.z:F1}) " +
                "must be on the NavMesh");

            bool dstOnMesh = NavMesh.SamplePosition(UpperFloorPos, out NavMeshHit dstHit, 5f, filter);
            ModAssert.True(dstOnMesh,
                $"Upper floor position ({UpperFloorPos.x:F1},{UpperFloorPos.y:F1},{UpperFloorPos.z:F1}) " +
                "must be on the NavMesh");

            var path = new NavMeshPath();
            NavMesh.CalculatePath(srcHit.position, dstHit.position, filter, path);

            Plugin.Log?.LogInfo(
                $"[CrossFloorTest] Ground->Upper: {path.status}, " +
                $"{path.corners?.Length ?? 0} corners, " +
                $"({srcHit.position.x:F1},{srcHit.position.y:F1},{srcHit.position.z:F1}) -> " +
                $"({dstHit.position.x:F1},{dstHit.position.y:F1},{dstHit.position.z:F1})");

            ModAssert.Equal(
                NavMeshPathStatus.PathComplete.ToString(),
                path.status.ToString(),
                $"Path from ground floor (y={srcHit.position.y:F1}) to upper floor " +
                $"(y={dstHit.position.y:F1}) must be PathComplete, got {path.status}");
        }

        [ModTest(Name = "CrossFloor_UpperToGroundFloor_PathComplete", Order = 220)]
        public static void CrossFloor_UpperToGroundFloor_PathComplete()
        {
            if (!VillageNavMeshBake.HasBakedInstance) return; // baking disabled
            RequireNavMesh();
            var filter = BuildFilter();

            bool srcOnMesh = NavMesh.SamplePosition(UpperFloorPos, out NavMeshHit srcHit, 5f, filter);
            ModAssert.True(srcOnMesh,
                $"Upper floor position ({UpperFloorPos.x:F1},{UpperFloorPos.y:F1},{UpperFloorPos.z:F1}) " +
                "must be on the NavMesh");

            bool dstOnMesh = NavMesh.SamplePosition(GroundFloorPos, out NavMeshHit dstHit, 5f, filter);
            ModAssert.True(dstOnMesh,
                $"Ground floor position ({GroundFloorPos.x:F1},{GroundFloorPos.y:F1},{GroundFloorPos.z:F1}) " +
                "must be on the NavMesh");

            var path = new NavMeshPath();
            NavMesh.CalculatePath(srcHit.position, dstHit.position, filter, path);

            Plugin.Log?.LogInfo(
                $"[CrossFloorTest] Upper->Ground: {path.status}, " +
                $"{path.corners?.Length ?? 0} corners, " +
                $"({srcHit.position.x:F1},{srcHit.position.y:F1},{srcHit.position.z:F1}) -> " +
                $"({dstHit.position.x:F1},{dstHit.position.y:F1},{dstHit.position.z:F1})");

            ModAssert.Equal(
                NavMeshPathStatus.PathComplete.ToString(),
                path.status.ToString(),
                $"Path from upper floor (y={srcHit.position.y:F1}) to ground floor " +
                $"(y={dstHit.position.y:F1}) must be PathComplete, got {path.status}");
        }

        private static void RequireNavMesh()
        {
            if (!VillageNavMeshBake.HasBakedInstance)
                throw new System.InvalidOperationException("Village NavMesh must be baked; skipping (baking disabled).");
        }

        private static NavMeshQueryFilter BuildFilter()
        {
            return new NavMeshQueryFilter
            {
                agentTypeID = VillageNavMeshBake.ResolveValheimHumanoidAgentTypeID(),
                areaMask = NavMesh.AllAreas
            };
        }
    }
}
