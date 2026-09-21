using HarmonyLib;
using UnityEngine;
using ValheimVillages.Villages;

namespace ValheimVillages.Abilities.SpawnBlock
{
    /// <summary>
    ///     Keeps ordinary monster spawns out of a village, by vetoing the SPAWN POINT.
    ///
    ///     <para><c>SpawnSystem.Spawn</c> is the last step before the creature is
    ///     instantiated, and it is handed the final position and whether this is an event
    ///     spawn — so a prefix here can refuse exactly the spawns that would land inside the
    ///     walls and leave every other spawn in the world alone.</para>
    ///
    ///     <para><b>Why not the spawner list.</b> This used to patch
    ///     <c>UpdateSpawnList</c> and disable whole spawners while a player stood in a
    ///     village. That was both too blunt and, in practice, inert: it required six of eight
    ///     sample points on a 60m ring around the player to be inside the village area, and
    ///     the area is the patrol hull — the walls — so a 60m ring from anywhere inside a
    ///     normal village mostly falls outside it and nothing was ever disabled. Greylings
    ///     went on appearing between the houses. Testing the position that is actually about
    ///     to be used needs no sampling and cannot miss.</para>
    ///
    ///     <para>Raids and events are deliberately untouched: a village that cannot be raided
    ///     is not defended, it is just safe.</para>
    /// </summary>
    [HarmonyPatch(typeof(SpawnSystem), "Spawn")]
    public static class SpawnProtectionPatch
    {
        /// <summary>
        ///     Extra buffer (m) beyond the village boundary that still counts as protected, so
        ///     nothing spawns pressed up against the outside of the wall.
        /// </summary>
        private const float SpawnBlockMargin = 10f;

        /// <summary>How often the running total of blocked spawns is reported.</summary>
        private const float ReportIntervalSeconds = 60f;

        private static int s_blocked;
        private static float s_nextReport;

        private static bool Prefix(Vector3 spawnPoint, bool eventSpawner)
        {
            // Raids come through with eventSpawner=true and are none of our business.
            if (eventSpawner) return true;
            if (VillageAreaManager.AreaCount == 0) return true;
            if (!VillageAreaManager.IsNearAnyVillage(spawnPoint, SpawnBlockMargin)) return true;

            // Counted and reported periodically rather than logged per spawn: this runs on
            // every spawn attempt in the world, and a line each would bury the log — the
            // failure mode a dedicated server cannot afford.
            s_blocked++;
            if (Time.time >= s_nextReport)
            {
                s_nextReport = Time.time + ReportIntervalSeconds;
                Plugin.Log?.LogInfo(
                    $"[SpawnBlock] Refused {s_blocked} spawn(s) inside a village so far " +
                    $"(margin {SpawnBlockMargin:F0}m; raids are never blocked)");
            }

            return false;
        }
    }
}
