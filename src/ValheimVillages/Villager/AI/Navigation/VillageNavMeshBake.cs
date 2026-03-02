using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    /// Builds NavMesh data for a village bounding box using Unity's public APIs.
    /// Caller is responsible for computing bounds; this class only performs collect + build + add.
    /// Tracks the last added instance so a subsequent bake can remove it before adding new data.
    /// </summary>
    public static class VillageNavMeshBake
    {
        public const float DefaultAgentRadius = 0.4f;
        public const float DefaultAgentHeight = 1.2f;
        public const float DefaultAgentClimb = 0.3f;

        /// <summary>Max walkable slope in degrees. Valheim defaults to 85 which treats
        /// building walls as walkable surface. 28 excludes walls but includes staircases
        /// (typical staircase is ~25 degrees).</summary>
        public const float DefaultAgentSlope = 27f;

        /// <summary>Default ledge drop height in meters for auto-link generation.</summary>
        public const float DefaultLedgeDropHeight = 1f;

        /// <summary>Default max jump-across distance for auto-link generation.</summary>
        public const float DefaultMaxJumpAcrossDistance = 0.2f;

        /// <summary>NavMesh area index used for our overlay surfaces and links.
        /// Separate from area 0 (Valheim tiles) so we can bias pathfinding
        /// to prefer our walkable overlay over Valheim's 85-degree wall surfaces.</summary>
        public const int OverlayAreaIndex = 3;

        private static int s_cachedAgentTypeID = -1;
        private const float BoundsHeight = 500f;
        private static NavMeshDataInstance s_lastInstance;
        private static bool s_hasInstance;

        /// <summary>True if a village NavMesh overlay has been baked and added.</summary>
        public static bool HasBakedInstance => s_hasInstance;
        private static int s_villageAgentTypeID = -1;

        /// <summary>
        /// Resolves Valheim's Humanoid agent type ID from the Pathfinding singleton via reflection.
        /// Caches the result after the first successful lookup. Falls back to 0 if resolution fails.
        /// </summary>
        public static int ResolveValheimHumanoidAgentTypeID()
        {
            if (s_cachedAgentTypeID >= 0) return s_cachedAgentTypeID;

            try
            {
                var pf = global::Pathfinding.instance;
                if (pf == null) return 0;

                var listField = typeof(global::Pathfinding).GetField("m_agentSettings",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (listField == null) return 0;

                var list = listField.GetValue(pf) as System.Collections.IList;
                if (list == null) return 0;

                var asType = typeof(global::Pathfinding).GetNestedType("AgentSettings",
                    System.Reflection.BindingFlags.NonPublic);
                var typeF = asType?.GetField("m_agentType");
                var buildF = asType?.GetField("m_build");
                if (typeF == null || buildF == null) return 0;

                foreach (var ag in list)
                {
                    if (ag == null) continue;
                    if ((int)typeF.GetValue(ag) == 1) // Pathfinding.AgentType.Humanoid
                    {
                        var bs = (NavMeshBuildSettings)buildF.GetValue(ag);
                        s_cachedAgentTypeID = bs.agentTypeID;
                        return s_cachedAgentTypeID;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Plugin.Log?.LogWarning(
                    $"[NavMeshBake] Failed to resolve Humanoid agentTypeID: {ex.Message}");
            }

            return 0;
        }

        /// <summary>
        /// Returns a custom agent type ID used exclusively for the village NavMesh overlay.
        /// Created via NavMesh.CreateSettings so it doesn't interfere with Valheim's
        /// humanoid agent type (which controls NPC movement through Valheim's tile-based NavMesh).
        /// Guard discovery/planning queries use this; NPC movement uses Valheim's humanoid type.
        /// </summary>
        public static int VillageAgentTypeID
        {
            get
            {
                if (s_villageAgentTypeID >= 0) return s_villageAgentTypeID;
                var settings = NavMesh.CreateSettings();
                settings.agentRadius = DefaultAgentRadius;
                settings.agentHeight = DefaultAgentHeight;
                settings.agentClimb = DefaultAgentClimb;
                settings.agentSlope = DefaultAgentSlope;
                settings.ledgeDropHeight = DefaultLedgeDropHeight;
                settings.maxJumpAcrossDistance = DefaultMaxJumpAcrossDistance;
                s_villageAgentTypeID = settings.agentTypeID;
                Plugin.Log?.LogInfo($"[NavMesh] Created custom village agent type: {s_villageAgentTypeID}");
                return s_villageAgentTypeID;
            }
        }

        /// <summary>
        /// No-op: baking disabled; experiments use built-in MoveTo/FindPath only.
        /// </summary>
        public static bool Bake(
            float minX, float minZ, float maxX, float maxZ,
            float agentRadius, float agentHeight, float agentClimb,
            out string error)
        {
            error = null;
            return true;
        }

        /// <summary>
        /// No-op: no overlay to remove.
        /// </summary>
        public static void RemovePreviousInstance()
        {
        }
    }
}
