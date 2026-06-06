using HarmonyLib;
using UnityEngine;
using ValheimVillages.Villager.Records;
using Object = UnityEngine.Object;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     When a villager dies, clear the <c>owner</c> / <c>ownerName</c>
    ///     ZDO fields on the bed they were assigned to. SpawnPatch sets
    ///     these fields when a player uses a pawn item to spawn a villager
    ///     (so the bed appears claimed in Valheim's UI), but nothing
    ///     historically cleared them when the villager died — leaving the
    ///     bed permanently claimed by a now-dead villager and (depending
    ///     on Valheim version) preventing deconstruction with the hammer.
    ///     The cleanup runs on the unified Character.OnDeath path so it
    ///     catches every death cause (combat, fall damage, dev `kill`,
    ///     etc). Position-based bed lookup keyed on the villager's
    ///     persisted <c>vv_bed_position</c> rather than the truncated
    ///     <c>vv_assigned_bed</c> ZDOID-as-long, which loses the
    ///     userId portion of the ZDOID.
    /// </summary>
    [HarmonyPatch(typeof(Character), "OnDeath")]
    public static class VillagerDeathBedReleasePatch
    {
        /// <summary>Search radius (m) around the recorded BedPosition when locating the assigned Bed.</summary>
        private const float BedMatchRadius = 1f;

        [HarmonyPostfix]
        public static void Postfix(Character __instance)
        {
            if (__instance == null) return;

            var villager = __instance.GetComponent<Villager.Villager>();
            if (villager == null) return;

            // Flip the authoritative record to Dead so it persists (and shows on the
            // registry's Revive tab) even after the NPC is gone. Read the record id from
            // the cached component field (villager.uid), NOT the NPC ZDO: Character.OnDeath
            // calls ZNetScene.Destroy(gameObject) before this postfix runs, which already
            // removed the NPC ZDO — GetZDO() would be null here. The record's own carrier
            // ZDO is separate and survives, so SetStatus still works.
            var recordId = villager.uid;
            Plugin.Log?.LogInfo(
                $"[Death] OnDeath for villager '{villager.villagerName}' (record_id='{recordId}') — flipping to Dead");
            if (!string.IsNullOrEmpty(recordId))
                VillagerRecordTable.SetStatus(recordId, RecordStatus.Dead);
            else
                Plugin.Log?.LogWarning(
                    "[Death] dying villager has no cached record id (uid); cannot flip record to Dead");

            var bedPos = villager.BedPosition;
            if (bedPos == Vector3.zero)
            {
                Plugin.Log?.LogDebug(
                    $"[Bed] Villager '{villager.villagerName}' died with no BedPosition; nothing to release");
                return;
            }

            ReleaseBedOwnerAt(bedPos, villager.villagerName);
        }

        private static void ReleaseBedOwnerAt(Vector3 bedPos, string villagerName)
        {
            var beds = Object.FindObjectsByType<Bed>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var bed in beds)
            {
                if (bed == null || bed.transform == null) continue;
                if (Vector3.Distance(bed.transform.position, bedPos) > BedMatchRadius) continue;

                var nview = bed.GetComponent<ZNetView>();
                var zdo = nview?.GetZDO();
                if (zdo == null) continue;

                var priorOwner = zdo.GetLong("owner", 0L);
                var priorVillagerOwner = zdo.GetString("vv_bed_owner", "");
                if (priorOwner == 0L && string.IsNullOrEmpty(priorVillagerOwner))
                {
                    Plugin.Log?.LogDebug(
                        $"[Bed] Bed at {bed.transform.position} already unclaimed; '{villagerName}' death no-op");
                    return;
                }

                zdo.Set("vv_bed_owner", "");
                zdo.Set("owner", 0L);
                zdo.Set("ownerName", "");
                Plugin.Log?.LogInfo(
                    $"[Bed] Released owner on bed at {bed.transform.position} after '{villagerName}' died " +
                    $"(prior owner ZDOID={priorOwner}, villager='{priorVillagerOwner}')");
                return;
            }

            // No bed is expected now that villagers are anchored to the registry (the home
            // position is the registry, not a bed). This is also hit for a bed destroyed or
            // out of load range. Either way there's nothing to release — not a warning.
            Plugin.Log?.LogDebug(
                $"[Bed] Villager '{villagerName}' died at home={bedPos}; no bed to release " +
                "(registry-anchored villager, or bed gone).");
        }
    }
}
