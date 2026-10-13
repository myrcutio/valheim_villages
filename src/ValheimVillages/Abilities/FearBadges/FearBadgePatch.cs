using HarmonyLib;
using UnityEngine;

namespace ValheimVillages.Abilities.FearBadges
{
    /// <summary>
    ///     Makes a warded creature run from the player carrying the badge for it.
    ///
    ///     <para>Modelled on the engine's crown-mode branch in <c>MonsterAI.UpdateAI</c>
    ///     (<c>MonsterAI.cs:387</c>), which is the proven insertion point for "this creature
    ///     should be fleeing this player instead of fighting them". Copying its shape gives two
    ///     things for free: it already excludes bosses, and it only fires once the creature has
    ///     ACQUIRED the player as a target — which is exactly "flees on detecting you", and
    ///     means a creature you corner still fights back.</para>
    ///
    ///     <para>What is deliberately NOT copied is crown mode itself. Its range is 9999f and it
    ///     is one global bool on the player, so setting it would make every non-boss creature in
    ///     the world flee forever. The ward is per-creature-type and uses
    ///     <see cref="FearBadgeStore.FearRange" />.</para>
    /// </summary>
    [HarmonyPatch(typeof(MonsterAI), nameof(MonsterAI.UpdateAI))]
    public static class FearBadgePatch
    {
        private static bool Prefix(
            MonsterAI __instance, float dt, ref bool __result,
            Character ___m_targetCreature, Character ___m_character)
        {
            if (___m_targetCreature is not Player player) return true;
            if (___m_character == null) return true;

            // Bosses are never afraid of anyone. Matches the engine's own exclusion.
            if (___m_character.GetFaction() == Character.Faction.Boss) return true;

            var creature = Utils.GetPrefabName(___m_character.gameObject);
            if (!FearBadgeStore.IsWarded(player, creature)) return true;

            // Same distance form as the crown-mode branch: edge-of-target, not centre, so a
            // large creature is not measured from inside itself.
            var distance = Vector3.Distance(player.transform.position, __instance.transform.position)
                           - player.GetRadius();
            if (distance >= FearBadgeStore.FearRange) return true;

            Flee(__instance, dt, player.transform.position);
            __result = true;
            return false;
        }

        /// <summary>
        ///     <c>BaseAI.Flee</c> is protected, so it is reached by reflection rather than
        ///     reimplemented — fleeing involves pathing, the lava check and a flee timer, and a
        ///     hand-rolled version would drift from the engine's.
        /// </summary>
        private static void Flee(MonsterAI instance, float dt, Vector3 from)
        {
            s_flee ??= AccessTools.Method(typeof(BaseAI), "Flee",
                new[] { typeof(float), typeof(Vector3) });
            s_flee?.Invoke(instance, new object[] { dt, from });
        }

        private static System.Reflection.MethodInfo s_flee;
    }
}
