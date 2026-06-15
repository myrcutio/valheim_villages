using UnityEngine;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Villager.Records
{
    /// <summary>
    ///     Revives a fallen (<see cref="RecordStatus.Dead" />) villager from its record:
    ///     re-spawns the NPC at the registry anchor (or its stored home) and flips the
    ///     record back to Alive. No material cost for now — a global cooldown gates how
    ///     often revives can happen.
    /// </summary>
    public static class VillagerReviveService
    {
        public const float CooldownSeconds = 30f;
        private const string DefaultPrefab = "DvergerMage";

        // -CooldownSeconds so the first revive after load is immediately available.
        private static float s_lastReviveTime = -CooldownSeconds;

        public static bool IsOnCooldown => Time.time - s_lastReviveTime < CooldownSeconds;

        public static float CooldownRemaining =>
            Mathf.Max(0f, CooldownSeconds - (Time.time - s_lastReviveTime));

        /// <summary>
        ///     Revive the given Dead record at its recorded home. Returns false with a
        ///     reason in <paramref name="error" /> if it can't (not dead, on cooldown, bad
        ///     data). Prefer the <paramref name="anchor" /> overload when reviving from a
        ///     registry — a stale home may now sit outside a rebuilt village.
        /// </summary>
        public static bool Revive(VillagerRecord record, out string error)
        {
            return Revive(record, null, out error);
        }

        /// <summary>
        ///     Revive the given Dead record at <paramref name="anchor" /> (the registry the
        ///     revive was triggered from), falling back to the record's stored home when
        ///     null. The anchor is resolved to a walkable seed so the villager lands inside
        ///     the village and the navmesh discovery seeds from a reachable cell — not a
        ///     stale home that may now sit outside a rebuilt village (which re-seeds a
        ///     degenerate partition).
        /// </summary>
        public static bool Revive(VillagerRecord record, Vector3? anchor, out string error)
        {
            error = null;

            if (record == null)
            {
                error = "no record";
                return false;
            }

            if (record.Status != RecordStatus.Dead)
            {
                error = "villager is not fallen";
                return false;
            }

            if (IsOnCooldown)
            {
                error = $"revive on cooldown ({CooldownRemaining:F0}s)";
                return false;
            }

            var def = VillagerRegistry.Get(record.Type);
            if (def == null)
            {
                error = $"unknown villager type '{record.Type}'";
                return false;
            }

            var rawAnchor = anchor ?? record.HomeAnchor;
            if (rawAnchor == Vector3.zero)
            {
                error = "no anchor/home position to revive at";
                return false;
            }

            // Snap to a walkable seed near the anchor (the registry). This becomes the
            // spawn position AND the persisted home (SpawnVillagerNpc writes anchorPos onto
            // the record), so the villager lands inside the village and the partition
            // seeds from a reachable cell instead of a stale, now-outside home.
            var spawnPos = rawAnchor;
            if (RegistrySeedResolver.TryResolveWalkableSeed(rawAnchor, out var seed))
                spawnPos = seed;
            else
                Plugin.Log?.LogWarning(
                    $"[Revive] No walkable seed near anchor ({rawAnchor.x:F1},{rawAnchor.y:F1},{rawAnchor.z:F1}) " +
                    $"for '{record.Name}'; reviving at the anchor as-is.");

            var prefabName = !string.IsNullOrEmpty(def.preferredPrefab) ? def.preferredPrefab : DefaultPrefab;

            // SpawnVillagerNpc re-activates the record in place (Status->Alive, re-link NPC).
            var r = record;
            var npc = VillagerSpawner.SpawnVillagerNpc(def, record.Type, prefabName, spawnPos, ref r);
            if (npc == null)
            {
                error = "failed to spawn villager";
                return false;
            }

            s_lastReviveTime = Time.time;
            Plugin.Log?.LogInfo(
                $"[Revive] Revived '{record.Name}' ({record.Type}) record {record.RecordId} at " +
                $"({spawnPos.x:F1},{spawnPos.y:F1},{spawnPos.z:F1})");
            return true;
        }
    }
}
