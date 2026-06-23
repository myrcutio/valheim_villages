using HarmonyLib;
using UnityEngine;
using ValheimVillages.Items;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     The one legitimate automatic record mutation: a confirmed in-world death.
    ///     <see cref="Character.OnDeath" /> fires only on actual death (driven by
    ///     <c>CheckDeath</c> when health hits zero) — NOT on zone-unload/eviction, which
    ///     goes through GameObject destroy + <c>VillagerAI.OnDestroy</c>. That distinction
    ///     is exactly what lets us flip a villager's record to <see cref="RecordStatus.Dead" />
    ///     on death without wrongly "killing" a villager that merely streamed out of range.
    ///     <para>
    ///         Without this, no code path ever transitions a combat-killed villager to Dead,
    ///         so every listing surface (vv_records, the registry Roster) keeps reporting it
    ///         Alive forever. <see cref="Humanoid" /> does not override OnDeath, so patching
    ///         <see cref="Character" /> catches villagers (Dvergr/Humanoid).
    ///     </para>
    /// </summary>
    [HarmonyPatch(typeof(Character), "OnDeath")]
    public static class VillagerDeathPatch
    {
        // PREFIX, not postfix: Character.OnDeath destroys the NPC GameObject before it
        // returns (its owner path calls ZNetScene.Destroy -> ZNetView.ResetZDO, which nulls
        // m_zdo, and the host also DestroyZDO's it). By postfix time GetZDO() is already null,
        // so the owner guard would always trip and the transition would never fire. At prefix
        // time the NPC ZDO is still live and IsOwner() is still meaningful. Returns void so it
        // never suppresses OnDeath (drops/ragdoll/ZDO-destroy must still run).
        [HarmonyPrefix]
        private static void Prefix(Character __instance)
        {
            // Only the OWNER of the NPC flips its record. Villagers are server-owned, so on
            // the host this is true; on a client it's false (and a client write to the
            // host-owned record ZDO would be vetoed anyway). Avoids cross-peer double-writes.
            var znv = __instance != null ? __instance.GetComponent<ZNetView>() : null;
            var zdo = znv != null ? znv.GetZDO() : null;
            if (zdo == null || !zdo.IsOwner()) return;

            // Our villagers carry the record back-reference on their NPC ZDO. Players and
            // vanilla creatures don't, so this is the villager filter. (The record CARRIER
            // ZDO also has this key but is an invisible empty GameObject with no Character,
            // so it never reaches OnDeath.)
            var recordId = zdo.GetString(VillagerRecord.IdKey);
            if (string.IsNullOrEmpty(recordId)) return;

            var record = VillagerRecordTable.FindById(recordId);
            if (record == null)
            {
                Plugin.Log?.LogWarning(
                    $"[VillagerDeathPatch] villager {recordId} died but no matching record was found; " +
                    "nothing to mark Dead.");
                return;
            }

            if (record.Status == RecordStatus.Dead) return; // idempotent

            record.Status = RecordStatus.Dead;
            // The NPC ZDO is about to be destroyed (ragdoll replaces it); clear the now-dead
            // back-link so liveness/orphan checks read true and the record presents as Unlinked.
            record.NpcZdoId = ZDOID.None;

            // Return the villager's Lode Core to the world so it can be recovered and spent to
            // recruit/revive — closing the recruit↔death loop. Host-only (we're inside the
            // IsOwner guard) so there's no duplicate drop. Use transform.position rather than
            // GetCenterPoint(): the latter dereferences m_collider, and an NRE here would
            // propagate out of this OnDeath prefix and SUPPRESS the native death sequence
            // (ragdoll/ZDO-destroy). Nudged up so the core doesn't sink into the ground.
            LodeCore.DropAt(__instance.transform.position + Vector3.up * 0.5f);

            Plugin.Log?.LogInfo(
                $"[VillagerDeathPatch] '{record.Name}' ({record.Type}) died -> record {recordId} marked Dead " +
                "(NPC back-link cleared, Lode Core dropped). Revive from the registry to restore.");
        }
    }
}
