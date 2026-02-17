using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Builds NavMesh data for a village bounding box using Unity's public APIs.
    /// Caller is responsible for computing bounds; this class only performs collect + build + add.
    /// Tracks the last added instance so a subsequent bake can remove it before adding new data.
    /// </summary>
    public static class VillageNavMeshBake
    {
        public const float DefaultAgentRadius = 0.5f;
        public const float DefaultAgentHeight = 2f;
        public const float DefaultAgentClimb = 0.4f;

        /// <summary>Default ledge drop height in meters for auto-link generation.</summary>
        public const float DefaultLedgeDropHeight = 1f;

        /// <summary>Default max jump-across distance for auto-link generation.</summary>
        public const float DefaultMaxJumpAcrossDistance = 0.4f;

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
                var pf = Pathfinding.instance;
                if (pf == null) return 0;

                var listField = typeof(Pathfinding).GetField("m_agentSettings",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (listField == null) return 0;

                var list = listField.GetValue(pf) as System.Collections.IList;
                if (list == null) return 0;

                var asType = typeof(Pathfinding).GetNestedType("AgentSettings",
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
                settings.agentSlope = 85f;
                settings.ledgeDropHeight = DefaultLedgeDropHeight;
                settings.maxJumpAcrossDistance = DefaultMaxJumpAcrossDistance;
                s_villageAgentTypeID = settings.agentTypeID;
                Plugin.Log?.LogInfo($"[NavMesh] Created custom village agent type: {s_villageAgentTypeID}");
                return s_villageAgentTypeID;
            }
        }

        /// <summary>
        /// Bake NavMesh for the given XZ bounds (world space). Uses physics colliders for geometry.
        /// Removes any previously added village NavMesh data first.
        /// Uses a custom agent type so the overlay doesn't interfere with Valheim's
        /// humanoid NavMesh (which has stair connections for NPC movement).
        /// </summary>
        public static bool Bake(
            float minX, float minZ, float maxX, float maxZ,
            float agentRadius, float agentHeight, float agentClimb,
            out string error)
        {
            error = null;

            if (minX >= maxX || minZ >= maxZ)
            {
                error = "Invalid bounds: min >= max";
                return false;
            }

            RemovePreviousInstance();

            float centerX = (minX + maxX) * 0.5f;
            float centerZ = (minZ + maxZ) * 0.5f;
            var center = new Vector3(centerX, BoundsHeight * 0.5f, centerZ);
            var size = new Vector3(maxX - minX, BoundsHeight, maxZ - minZ);
            var worldBounds = new Bounds(center, size);

            var sources = new List<NavMeshBuildSource>();
            var markups = BuildDoorExclusionMarkups(worldBounds);

            try
            {
                NavMeshBuilder.CollectSources(
                    worldBounds,
                    -1, // all layers
                    NavMeshCollectGeometry.PhysicsColliders,
                    0,  // default area
                    true, // generateLinksByDefault: enable auto-link generation
                    markups,
                    false, // includeOnlyMarkedObjects
                    sources);
            }
            catch (System.Exception ex)
            {
                error = "CollectSources: " + ex.Message;
                return false;
            }

            if (sources.Count == 0)
            {
                error = "No NavMesh sources in bounds";
                return false;
            }

            // Bake with Valheim's humanoid agent type so our overlay merges into the
            // same NavMesh layer that NPC pathfinding queries. This fills gaps in
            // Valheim's tile-based NavMesh (e.g. upper-floor staircase connectivity).
            int humanoidAgentType = ResolveValheimHumanoidAgentTypeID();
            var buildSettings = NavMesh.GetSettingsByID(humanoidAgentType);
            buildSettings.agentRadius = agentRadius;
            buildSettings.agentHeight = agentHeight;
            buildSettings.agentClimb = agentClimb;
            buildSettings.ledgeDropHeight = DefaultLedgeDropHeight;
            buildSettings.maxJumpAcrossDistance = DefaultMaxJumpAcrossDistance;

            var localCenter = new Vector3(0f, BoundsHeight * 0.5f, 0f);
            var localBounds = new Bounds(localCenter, size);
            NavMeshData data;
            try
            {
                data = NavMeshBuilder.BuildNavMeshData(
                    buildSettings,
                    sources,
                    localBounds,
                    Vector3.zero,
                    Quaternion.identity);
            }
            catch (System.Exception ex)
            {
                error = "BuildNavMeshData: " + ex.Message;
                return false;
            }

            if (data == null)
            {
                error = "BuildNavMeshData returned null";
                return false;
            }

            try
            {
                s_lastInstance = NavMesh.AddNavMeshData(
                    data, new Vector3(centerX, 0f, centerZ), Quaternion.identity);
                s_hasInstance = s_lastInstance.valid;
            }
            catch (System.Exception ex)
            {
                error = "AddNavMeshData: " + ex.Message;
                Object.Destroy(data);
                return false;
            }

            if (!s_hasInstance)
            {
                Object.Destroy(data);
                error = "AddNavMeshData returned invalid instance";
                return false;
            }

            Plugin.Log?.LogInfo(
                $"[NavMesh] Bake OK: {sources.Count} sources, humanoidAgent={humanoidAgentType}, " +
                $"bounds ({minX:F0},{minZ:F0})-({maxX:F0},{maxZ:F0})");

            // #region agent log
            try
            {
                int diagHumanoidType = ResolveValheimHumanoidAgentTypeID();
                var diagFilter = new NavMeshQueryFilter();
                diagFilter.agentTypeID = diagHumanoidType;
                diagFilter.areaMask = NavMesh.AllAreas;

                // H1/H2/H6: Probe NavMesh at center at multiple Y heights to find islands
                // Use bed positions to find the actual terrain height, then probe relative to that
                var probeResults = new System.Text.StringBuilder();
                probeResults.Append("[");
                var hitPositions = new List<Vector3>();
                float probeCenterX = (minX + maxX) * 0.5f;
                float probeCenterZ = (minZ + maxZ) * 0.5f;
                float baseY = 0f;
                var beds = VillagerAIManager.GetAllBedPositions();
                if (beds != null && beds.Count > 0) baseY = beds[0].y;
                bool first = true;
                for (float yOff = baseY - 5f; yOff <= baseY + 20f; yOff += 1.5f)
                {
                    var probe = new Vector3(probeCenterX, yOff, probeCenterZ);
                    bool hit = NavMesh.SamplePosition(probe, out NavMeshHit navHit, 2f, diagFilter);
                    if (!first) probeResults.Append(",");
                    first = false;
                    probeResults.Append($"{{\"probeY\":{yOff:F1},\"hit\":{hit.ToString().ToLower()}");
                    if (hit)
                    {
                        probeResults.Append($",\"snapPos\":\"{navHit.position.x:F2},{navHit.position.y:F2},{navHit.position.z:F2}\"");
                        hitPositions.Add(navHit.position);
                    }
                    probeResults.Append("}");
                }
                probeResults.Append("]");

                string probeData = $"{{\"humanoidAgent\":{diagHumanoidType},\"centerX\":{probeCenterX:F1},\"centerZ\":{probeCenterZ:F1},\"probes\":{probeResults},\"hitCount\":{hitPositions.Count}}}";
                long ts1 = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                System.IO.File.AppendAllText("/home/benny/Projects/valheim_villages/.cursor/debug.log",
                    $"{{\"hypothesisId\":\"H10\",\"location\":\"VillageNavMeshBake.cs:PostBakeProbe\",\"message\":\"NavMesh probe with HUMANOID agent (not village overlay)\",\"data\":{probeData},\"timestamp\":{ts1}}}\n");

                // H3: Test cross-island CalculatePath between each pair of found positions
                if (hitPositions.Count >= 2)
                {
                    var pathResults = new System.Text.StringBuilder();
                    pathResults.Append("[");
                    bool firstPath = true;
                    for (int i = 0; i < hitPositions.Count; i++)
                    {
                        for (int j = i + 1; j < hitPositions.Count; j++)
                        {
                            if (Mathf.Abs(hitPositions[i].y - hitPositions[j].y) < 1f) continue;
                            var testPath = new NavMeshPath();
                            NavMesh.CalculatePath(hitPositions[i], hitPositions[j], diagFilter, testPath);
                            if (!firstPath) pathResults.Append(",");
                            firstPath = false;
                            pathResults.Append($"{{\"from\":\"{hitPositions[i].x:F1},{hitPositions[i].y:F1},{hitPositions[i].z:F1}\"");
                            pathResults.Append($",\"to\":\"{hitPositions[j].x:F1},{hitPositions[j].y:F1},{hitPositions[j].z:F1}\"");
                            pathResults.Append($",\"yDiff\":{Mathf.Abs(hitPositions[i].y - hitPositions[j].y):F1}");
                            pathResults.Append($",\"status\":\"{testPath.status}\"");
                            pathResults.Append($",\"corners\":{(testPath.corners != null ? testPath.corners.Length : 0)}}}");
                        }
                    }
                    pathResults.Append("]");

                    long ts2 = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    System.IO.File.AppendAllText("/home/benny/Projects/valheim_villages/.cursor/debug.log",
                        $"{{\"hypothesisId\":\"H10_path\",\"location\":\"VillageNavMeshBake.cs:CrossIslandPath\",\"message\":\"Cross-floor path with HUMANOID agent (no overlay interference)\",\"data\":{{\"pairs\":{pathResults}}},\"timestamp\":{ts2}}}\n");
                }
                else
                {
                    long ts2 = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    System.IO.File.AppendAllText("/home/benny/Projects/valheim_villages/.cursor/debug.log",
                        $"{{\"hypothesisId\":\"H10_path\",\"location\":\"VillageNavMeshBake.cs:CrossIslandPath\",\"message\":\"Not enough height-separated surfaces to test cross-island pathing\",\"data\":{{\"hitCount\":{hitPositions.Count}}},\"timestamp\":{ts2}}}\n");
                }
            }
            catch (System.Exception ex)
            {
                try
                {
                    long tsErr = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    System.IO.File.AppendAllText("/home/benny/Projects/valheim_villages/.cursor/debug.log",
                        $"{{\"hypothesisId\":\"H1_H2_H3\",\"location\":\"VillageNavMeshBake.cs:DiagError\",\"message\":\"Post-bake diagnostic failed\",\"data\":{{\"error\":\"{ex.Message.Replace("\"", "'")}\"}},\"timestamp\":{tsErr}}}\n");
                }
                catch { }
            }
            // #endregion

            return true;
        }

        /// <summary>
        /// Find all Door objects within the bake bounds and create NavMeshBuildMarkup entries
        /// that exclude them from the NavMesh. This makes the mesh continuous through doorways
        /// so pathfinding can plan routes through doors. The DoorHandler component handles
        /// actually opening doors when the NPC approaches.
        /// </summary>
        private static List<NavMeshBuildMarkup> BuildDoorExclusionMarkups(Bounds worldBounds)
        {
            var markups = new List<NavMeshBuildMarkup>();
            var allDoors = Object.FindObjectsByType<Door>(FindObjectsSortMode.None);
            int excluded = 0;

            foreach (var door in allDoors)
            {
                if (door == null) continue;
                if (!worldBounds.Contains(door.transform.position)) continue;

                markups.Add(new NavMeshBuildMarkup
                {
                    root = door.transform,
                    overrideArea = false,
                    ignoreFromBuild = true
                });
                excluded++;
            }

            if (excluded > 0)
                Plugin.Log?.LogInfo(
                    $"[NavMesh] Excluded {excluded} doors from bake for pathable doorways");

            return markups;
        }

        /// <summary>
        /// Remove the village NavMesh data and links. Call on world unload.
        /// </summary>
        public static void RemovePreviousInstance()
        {
            NavMeshLinkPlacer.RemoveAllLinks();
            if (s_hasInstance && s_lastInstance.valid)
            {
                s_lastInstance.Remove();
                s_hasInstance = false;
            }
        }
    }
}
