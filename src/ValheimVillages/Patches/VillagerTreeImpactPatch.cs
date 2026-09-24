using HarmonyLib;
using UnityEngine;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     Villagers are not hurt by falling trees or rolling logs.
    ///     <para>
    ///         A felled trunk deals its crush damage through <see cref="ImpactEffect" />, whose
    ///         only exemption is <c>m_damagePlayers</c> — players only. Villagers are Dvergr
    ///         NPCs, so a trunk landing on a Lumberjack (who charges the tree and is standing
    ///         at the stump when it goes) hurt him like any greyling. Monsters, weapons and
    ///         every other impact still land; this is scoped to tree and log bodies only.
    ///     </para>
    ///     <para>
    ///         Skips the whole collision for that pair, so the thud effect on the villager is
    ///         lost too. The log's self-damage from striking him goes with it, which only means
    ///         a villager is no longer a surface a log can shatter itself against.
    ///     </para>
    /// </summary>
    [HarmonyPatch(typeof(ImpactEffect), nameof(ImpactEffect.OnCollisionEnter))]
    public static class VillagerTreeImpactPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(ImpactEffect __instance, Collision info)
        {
            if (info == null || info.contacts.Length == 0) return true;
            if (__instance.GetComponent<TreeLog>() == null && __instance.GetComponent<TreeBase>() == null)
                return true;

            // Resolve the struck object exactly as ImpactEffect does before it calls Damage.
            var hit = Projectile.FindHitObject(info.contacts[0].otherCollider);
            if (hit == null || hit.GetComponent<Character>() == null) return true;

            // Villagers carry their record id on the NPC ZDO; players and creatures do not.
            // Read from the ZDO rather than looking for VillagerAI, which only exists on the
            // peer simulating the villager — the log may be owned by someone else.
            var zdo = hit.GetComponent<ZNetView>()?.GetZDO();
            return zdo == null || string.IsNullOrEmpty(zdo.GetString(VillagerRecord.IdKey));
        }
    }
}
