using UnityEngine;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Villager.Records
{
    /// <summary>
    ///     Revives a fallen (<see cref="RecordStatus.Dead" />) villager from its record:
    ///     re-spawns the NPC at its bed, flips the record back to Alive, and re-claims the
    ///     bed. No material cost for now — a global cooldown gates how often revives can
    ///     happen.
    /// </summary>
    public static class VillagerReviveService
    {
        public const float CooldownSeconds = 30f;
        private const float BedMatchRadius = 1f;
        private const string DefaultPrefab = "DvergerMage";

        // -CooldownSeconds so the first revive after load is immediately available.
        private static float s_lastReviveTime = -CooldownSeconds;

        public static bool IsOnCooldown => Time.time - s_lastReviveTime < CooldownSeconds;

        public static float CooldownRemaining =>
            Mathf.Max(0f, CooldownSeconds - (Time.time - s_lastReviveTime));

        /// <summary>
        ///     Revive the given Dead record. Returns false with a reason in
        ///     <paramref name="error" /> if it can't (not dead, on cooldown, bad data).
        /// </summary>
        public static bool Revive(VillagerRecord record, out string error)
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

            var bedPos = record.BedPosition;
            if (bedPos == Vector3.zero)
            {
                error = "record has no bed position to revive at";
                return false;
            }

            var prefabName = !string.IsNullOrEmpty(def.preferredPrefab) ? def.preferredPrefab : DefaultPrefab;

            // SpawnVillagerNpc re-activates the record in place (Status->Alive, re-link NPC).
            var r = record;
            var npc = VillagerPawnPatch.SpawnVillagerNpc(def, record.Type, prefabName, bedPos, ref r);
            if (npc == null)
            {
                error = "failed to spawn villager";
                return false;
            }

            var npcZdoId = npc.GetComponent<ZNetView>()?.GetZDO()?.m_uid ?? ZDOID.None;
            ClaimBedAt(bedPos, record.RecordId, record.Name, npcZdoId);

            s_lastReviveTime = Time.time;
            Plugin.Log?.LogInfo(
                $"[Revive] Revived '{record.Name}' ({record.Type}) record {record.RecordId} at {bedPos}");
            return true;
        }

        /// <summary>
        ///     Re-claim the bed at <paramref name="bedPos" /> for the revived villager (the
        ///     bed was released when it died). Best-effort: only loaded beds are found.
        /// </summary>
        private static void ClaimBedAt(Vector3 bedPos, string recordId, string villagerName, ZDOID npcZdoId)
        {
            var beds = Object.FindObjectsByType<Bed>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var bed in beds)
            {
                if (bed == null || bed.transform == null) continue;
                if (Vector3.Distance(bed.transform.position, bedPos) > BedMatchRadius) continue;

                var zdo = bed.GetComponent<ZNetView>()?.GetZDO();
                if (zdo == null) continue;

                zdo.Set("vv_bed_owner", recordId);
                zdo.Set("owner", npcZdoId != ZDOID.None ? npcZdoId.ID : 0L);
                zdo.Set("ownerName", villagerName);
                return;
            }
        }
    }
}
