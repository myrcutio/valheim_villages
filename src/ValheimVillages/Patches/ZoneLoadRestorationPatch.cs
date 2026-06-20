using HarmonyLib;
using ValheimVillages.Villager;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     When Valheim loads a zone and spawns a Dvergr from ZDO, the raw prefab
    ///     gets MonsterAI/NpcTalk/Tameable but none of our mod components. This
    ///     postfix detects villager ZDOs and calls VillagerRestoration.Restore to
    ///     re-add Villager, VillagerAI, VillagerTalk, etc.
    ///     Safe alongside ZNetViewAwakeProtectionPatch (prefix): that prefix only
    ///     blocks template prefabs (vv_* without "(Clone)"). When the prefix returns
    ///     false, GetZDO() is null here, so we skip harmlessly.
    /// </summary>
    [HarmonyPatch(typeof(ZNetView), "Awake")]
    public static class ZoneLoadRestorationPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ZNetView __instance)
        {
            var zdo = __instance.GetZDO();
            if (zdo == null) return;

            // Host asserts SOLE ownership of village entities the instant their ZDO is
            // instantiated. This is required (not just a latency optimisation): a dedicated
            // server's reference position is the world origin, so ReleaseNearbyZDOS never
            // reaches a distant village on its own — without this, a client-recruited
            // villager / client-placed registry / village carrier stays client-owned after
            // a restart, a disconnect, or whenever no player stands in the village. Runs for
            // EVERY carrier kind (village/record/registry/villager), so it must precede the
            // record/player early-returns below. Idempotent (owner check), client = no-op.
            if (VillageOwnership.IsServerAuthority()
                && VillageOwnership.IsVillageZdo(zdo)
                && zdo.GetOwner() != ZDOMan.GetSessionID())
                zdo.SetOwner(ZDOMan.GetSessionID());

            // This postfix runs for EVERY ZNetView.Awake, so it must never act on the
            // wrong object. Two exclusions before the (intentionally broad) key check:
            //   - the record-carrier ZDO shares the vv_record_id key with real villagers;
            //   - the player (or anything parented under it) must never be restored.
            if (zdo.GetPrefab() == Villager.Records.RecordPrefabFactory.RecordPrefabHash) return;
            if (Villager.NativeNpcStripper.IsPlayerOwned(__instance.gameObject)) return;

            // Detect our NPCs by the new record back-reference or legacy identity key
            // (legacy ones get migrated to a record inside Restore).
            var isVillager = !string.IsNullOrEmpty(zdo.GetString("vv_record_id"))
                             || !string.IsNullOrEmpty(zdo.GetString("vv_villager_id"));
            if (!isVillager) return;

            if (__instance.GetComponent<Villager.Villager>() != null) return;

            VillagerRestoration.Restore(__instance.gameObject, zdo);
        }
    }
}