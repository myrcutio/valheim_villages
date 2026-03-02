using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Abilities.MountainStride
{
    /// <summary>
    /// Registers the SE_MountainStride status effect with ObjectDB
    /// so it can be looked up by hash via SEMan.AddStatusEffect.
    /// </summary>
    [HarmonyPatch(typeof(ObjectDB), "Awake")]
    public static class MountainStride_ObjectDB_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ObjectDB __instance)
        {
            RegisterStatusEffect(__instance);
        }

        [RegisterObjectDB]
        public static void RegisterStatusEffect(ObjectDB db)
        {
            if (db == null) return;

            int hash = SE_MountainStride.EffectName.GetStableHashCode();

            // Don't register twice
            if (db.GetStatusEffect(hash) != null) return;

            var se = ScriptableObject.CreateInstance<SE_MountainStride>();
            se.name = SE_MountainStride.EffectName;

            db.m_StatusEffects.Add(se);
            Plugin.Log?.LogInfo("[Mountaineer] Registered SE_MountainStride status effect");
        }
    }

    /// <summary>
    /// Also register on CopyOtherDB (same pattern as ItemPatch).
    /// </summary>
    [HarmonyPatch(typeof(ObjectDB), "CopyOtherDB")]
    public static class MountainStride_ObjectDBCopy_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(ObjectDB __instance)
        {
            MountainStride_ObjectDB_Patch.RegisterStatusEffect(__instance);
        }
    }

    /// <summary>
    /// Tick the ability manager every frame via Player.Update.
    /// </summary>
    [HarmonyPatch(typeof(Player), "Update")]
    public static class MountainStride_PlayerUpdate_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Player __instance)
        {
            if (__instance != Player.m_localPlayer) return;
            VillagerAbilityManager.Update(Time.deltaTime);
        }
    }

    /// <summary>
    /// Harmony prefix on Character.ApplySlide to suppress sliding
    /// when the player has the Mountain Stride buff active.
    /// 
    /// ApplySlide is private, signature:
    ///   void ApplySlide(float dt, ref Vector3 currentVel, Vector3 bodyVel, bool running)
    /// 
    /// Also zeroes out m_slippage so that UpdateBodyFriction keeps full
    /// physics friction -- without this, the character still physically
    /// slides on near-vertical surfaces due to reduced collider friction.
    /// </summary>
    [HarmonyPatch(typeof(Character), "ApplySlide")]
    public static class MountainStride_ApplySlide_Patch
    {
        private static System.Reflection.FieldInfo s_slippageField;

        [HarmonyPrefix]
        public static bool Prefix(Character __instance)
        {
            if (!__instance.IsPlayer()) return true;

            var seman = __instance.GetSEMan();
            if (seman == null) return true;

            int hash = SE_MountainStride.EffectName.GetStableHashCode();
            if (!seman.HaveStatusEffect(hash))
                return true;

            // Zero out m_slippage so UpdateBodyFriction maintains full friction
            if (s_slippageField == null)
            {
                s_slippageField = typeof(Character).GetField("m_slippage",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            }
            s_slippageField?.SetValue(__instance, 0f);

            return false; // Skip ApplySlide
        }
    }
}
