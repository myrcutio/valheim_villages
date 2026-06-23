using UnityEngine;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.Registry;
using ValheimVillages.Villages;

namespace ValheimVillages.Villager.Records
{
    /// <summary>
    ///     Revives a fallen (<see cref="RecordStatus.Dead" />) villager from its record:
    ///     re-spawns the NPC at the registry anchor (or its stored home) and flips the
    ///     record back to Alive. The Lode Core cost is charged at the UI call site (ReviveTab)
    ///     and refunded if the host rejects the spawn; a global cooldown additionally gates
    ///     how often revives can happen. The <c>paid</c> flag is threaded to the spawn RPC so
    ///     the refund-on-failure path only fires for player-initiated (paid) revives, not the
    ///     vv_revive dev command.
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
            // 2-arg entry = the vv_revive dev command: no Lode Core cost (paid: false).
            return Revive(record, null, paid: false, out error);
        }

        /// <summary>
        ///     Revive the given Dead record at <paramref name="anchor" /> (the registry the
        ///     revive was triggered from), falling back to the record's stored home when
        ///     null. The anchor is resolved to a walkable seed so the villager lands inside
        ///     the village and the navmesh discovery seeds from a reachable cell — not a
        ///     stale home that may now sit outside a rebuilt village (which re-seeds a
        ///     degenerate partition).
        /// </summary>
        public static bool Revive(VillagerRecord record, Vector3? anchor, bool paid, out string error)
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

            // Resolve the owning village from the record so the spawn seed is found
            // against its founder-connected anchor triad, not the (possibly island)
            // revive anchor itself.
            var village = Villages.Entity.VillageRegistry.FindById(record.Village);
            if (village == null || village.IsInvalid)
            {
                error = "owning village is missing or invalid";
                Plugin.Log?.LogError(
                    $"[Revive] Village '{record.Village}' for '{record.Name}' is missing or invalid; " +
                    "aborting revive.");
                return false;
            }

            // Resolve an HNA-valid, approachable cell on the village (slot-31) graph
            // beside the anchor — the surface the villager actually walks on, Y-banded
            // so a roofed registry resolves to its own floor and not the structure
            // above. The seed is resolved against the village's anchor triad. This
            // becomes the spawn position AND the persisted home. No fallback by design:
            // if it fails (graph not settled, anchor now outside a rebuilt village, etc.)
            // surface a loud error and refuse the revive rather than drop the villager on
            // the raw anchor for the EnsureAgent warp to teleport upward.
            if (!Villages.Entity.VillageRegistry.TryResolveVillagerSeed(village, rawAnchor, out _))
            {
                error = "no reachable spawn location near the revive anchor";
                Plugin.Log?.LogError(
                    "[Revive] No reachable spawn location near anchor " +
                    $"({rawAnchor.x:F1},{rawAnchor.y:F1},{rawAnchor.z:F1}) for '{record.Name}'; " +
                    "aborting revive.");
                return false;
            }

            // Spawn on the HOST so the revived NPC is server-owned from birth (a client-spawned
            // NPC handed to the server despawns). The host re-resolves the seed near the anchor
            // against its own navmesh and re-activates the record (Status->Alive, re-link NPC).
            // Passing the record id makes the host reuse this record rather than mint a new one.
            VillagerRecruitRpc.RequestSpawn(record.Type, village.VillageId, rawAnchor, record.RecordId, paid);

            s_lastReviveTime = Time.time;
            Plugin.Log?.LogInfo(
                $"[Revive] Requested host-authoritative revive of '{record.Name}' ({record.Type}) " +
                $"record {record.RecordId} near ({rawAnchor.x:F1},{rawAnchor.y:F1},{rawAnchor.z:F1})");
            return true;
        }
    }
}
