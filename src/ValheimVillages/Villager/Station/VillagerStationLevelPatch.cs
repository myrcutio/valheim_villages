using HarmonyLib;
using UnityEngine;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.Levels;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.Villager.Station
{
    /// <summary>
    ///     Report a villager's quarters level as their virtual station's level, so Valheim's
    ///     own station-level icon shows the same number the mod does.
    ///
    ///     <para><c>CraftingStation.GetLevel()</c> is <c>1 + attached extensions</c> — a derived
    ///     value with no field to set — so a villager's station always read 1 while the status
    ///     panel said 3. Two numbers for one thing, both on screen at once.</para>
    ///
    ///     <para>SCOPED TIGHTLY to virtual villager stations ("$vv_*"). GetLevel also gates
    ///     which recipes a station may craft (<c>m_minStationLevel</c>), so widening this to
    ///     real workbenches and forges would quietly rewrite crafting rules — which is a much
    ///     bigger change than making an icon agree with a badge.</para>
    /// </summary>
    [HarmonyPatch(typeof(CraftingStation), nameof(CraftingStation.GetLevel))]
    public static class VillagerStationLevelPatch
    {
        [HarmonyPostfix]
        public static void Postfix(CraftingStation __instance, ref int __result)
        {
            if (__instance == null || !VillagerStation.IsVirtualStation(__instance.m_name)) return;

            var bridge = __instance.GetComponent<VillagerBehaviorBridge>();
            if (bridge == null) return;

            var record = VillagerRecordTable.FindById(bridge.UniqueId);
            if (record == null || !record.IsValid) return;

            // VillagerQuarters keeps a short-lived index, so this is a cached read rather than
            // a cover/comfort sweep — GetLevel runs every frame from UpdateRecipe.
            var quarters = VillagerQuarters.For(record);

            // Never below 1: the native icon is a "station level" readout and 0 would read as
            // a broken station rather than as "this villager has no room yet", which the
            // status panel says properly.
            __result = Mathf.Max(1, quarters.Level);
        }
    }
}
