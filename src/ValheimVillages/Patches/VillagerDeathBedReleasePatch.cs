using HarmonyLib;
using UnityEngine;
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

            Plugin.Log?.LogWarning(
                $"[Bed] Villager '{villagerName}' died at BedPosition={bedPos} but no Bed found within " +
                $"{BedMatchRadius:F1}m to release — bed may already be destroyed or out of load range");
        }
    }
}
