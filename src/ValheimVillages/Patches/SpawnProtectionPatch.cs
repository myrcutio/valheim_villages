using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Villages;

namespace ValheimVillages.Patches
{
    /// <summary>
    /// Harmony patch on SpawnSystem.UpdateSpawnList to prevent non-raid enemy spawns
    /// inside protected village areas.
    /// 
    /// SpawnSystem.UpdateSpawnList is called with eventSpawners=false for normal spawns
    /// and eventSpawners=true for raid/event spawns. We only suppress normal spawns.
    /// </summary>
    [HarmonyPatch(typeof(SpawnSystem), "UpdateSpawnList")]
    public static class SpawnProtectionPatch
    {
        /// <summary>
        /// Prefix: if this is NOT an event spawn, check if we should suppress spawning
        /// by temporarily filtering the spawn list to remove spawners whose spawn position
        /// would fall inside a village area.
        /// 
        /// We use a Postfix-compatible approach: we don't skip the method, we just
        /// set m_nospawn temporarily for spawns inside villages.
        /// </summary>
        static void Prefix(
            List<SpawnSystem.SpawnData> spawners,
            System.DateTime currentTime,
            bool eventSpawners,
            SpawnSystem __instance)
        {
            // Never interfere with event/raid spawns (rule 4)
            if (eventSpawners) return;

            // No village areas registered -- nothing to protect
            if (VillageAreaManager.AreaCount == 0) return;

            // Store the original enabled state and disable spawners whose positions
            // would be inside a village area. We check the player positions since
            // spawns occur near players.
            s_disabledSpawnerIndices.Clear();

            var players = Player.GetAllPlayers();
            if (players == null || players.Count == 0) return;

            // Check if any player is inside a village area
            bool anyPlayerInVillage = false;
            foreach (var player in players)
            {
                if (player == null) continue;
                if (VillageAreaManager.IsInsideAnyVillage(player.transform.position))
                {
                    anyPlayerInVillage = true;
                    break;
                }
            }

            if (!anyPlayerInVillage) return;

            // Temporarily disable all non-event spawners when a player is in a village.
            // SpawnSystem spawns at random points 40-80m from players, so we check
            // sample spawn positions against village areas.
            for (int i = 0; i < spawners.Count; i++)
            {
                var spawner = spawners[i];
                if (spawner == null || !spawner.m_enabled) continue;

                // Check spawn positions around each player inside a village
                bool shouldDisable = true;
                foreach (var player in players)
                {
                    if (player == null) continue;
                    var playerPos = player.transform.position;

                    // If the player is inside a village, spawns near them should be suppressed
                    if (VillageAreaManager.IsInsideAnyVillage(playerPos))
                    {
                        // Sample several potential spawn positions around the player
                        // Spawns occur 40-80m from players, so check at those distances
                        if (!AllSpawnPositionsInsideVillage(playerPos))
                        {
                            shouldDisable = false;
                            break;
                        }
                    }
                    else
                    {
                        shouldDisable = false;
                        break;
                    }
                }

                if (shouldDisable)
                {
                    spawner.m_enabled = false;
                    s_disabledSpawnerIndices.Add(i);
                }
            }

            s_lastSpawners = spawners;
        }

        /// <summary>
        /// Postfix: re-enable any spawners we temporarily disabled.
        /// </summary>
        static void Postfix()
        {
            if (s_lastSpawners == null || s_disabledSpawnerIndices.Count == 0) return;

            foreach (int index in s_disabledSpawnerIndices)
            {
                if (index < s_lastSpawners.Count && s_lastSpawners[index] != null)
                    s_lastSpawners[index].m_enabled = true;
            }

            s_disabledSpawnerIndices.Clear();
            s_lastSpawners = null;
        }

        private static readonly List<int> s_disabledSpawnerIndices = new();
        private static List<SpawnSystem.SpawnData> s_lastSpawners;

        /// <summary>
        /// Check if all typical spawn positions around a player would be inside a village.
        /// Samples 8 directions at spawn distance to determine coverage.
        /// </summary>
        private static bool AllSpawnPositionsInsideVillage(Vector3 playerPos)
        {
            float spawnDist = 60f; // Midpoint of 40-80m spawn range
            int insideCount = 0;

            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f * Mathf.Deg2Rad;
                var samplePos = playerPos + new Vector3(
                    Mathf.Cos(angle) * spawnDist,
                    0f,
                    Mathf.Sin(angle) * spawnDist
                );

                if (VillageAreaManager.IsInsideAnyVillage(samplePos))
                    insideCount++;
            }

            // If most spawn positions are inside the village, suppress spawning
            return insideCount >= 6;
        }
    }
}
