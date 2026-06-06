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