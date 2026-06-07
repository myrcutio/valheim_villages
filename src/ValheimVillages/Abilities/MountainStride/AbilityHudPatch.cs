using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using ValheimVillages.Attributes;
using ValheimVillages.UI.Core;

namespace ValheimVillages.Abilities.MountainStride
{
    /// <summary>
    ///     Renders a guardian-power-style HUD slot for the player's learned villager
    ///     ability, stacked just above the vanilla guardian power icon in the
    ///     bottom-left. The slot is cloned from the vanilla <c>m_gpRoot</c> element so
    ///     it matches the game's art and layout exactly, then driven from
    ///     <see cref="VillagerAbilityManager" />: icon + name show whenever the ability
    ///     is learned, the cooldown text counts down and reads "Ready" when available,
    ///     and the icon dims while on cooldown — mirroring <c>Hud.UpdateGuardianPower</c>.
    ///     Hidden entirely until the ability is learned.
    ///
    ///     The guardian-power name/cooldown labels are TMPro components; we reach them
    ///     by reflection and set their text via <see cref="VillagerUIFactory.SetTMPText" />
    ///     so this assembly keeps no compile-time TMPro dependency (matching the rest of
    ///     the mod's UI layer).
    /// </summary>
    public static class AbilityHud
    {
        private const string CloneName = "VV_AbilityHud";
        private static readonly Color CooldownTint = new(1f, 1f, 1f, 0.4f);

        private static GameObject s_root;
        private static Image s_icon;
        private static GameObject s_nameGO;
        private static GameObject s_cooldownGO;

        /// <summary>
        ///     Build the cloned slot once. Cheap no-op on every subsequent frame.
        /// </summary>
        private static void EnsureBuilt(Hud hud)
        {
            if (s_root != null) return;
            if (hud == null || hud.m_gpRoot == null || hud.m_gpIcon == null) return;

            var parent = hud.m_gpRoot.parent;

            // Drop any clone left behind by a previous (hot-reloaded) assembly before
            // building a fresh one against the new types.
            var stale = parent.Find(CloneName);
            if (stale != null) Object.DestroyImmediate(stale.gameObject);

            var clone = Object.Instantiate(hud.m_gpRoot, parent);
            clone.name = CloneName;
            // Stack directly above the guardian power slot, keeping its anchoring.
            clone.anchoredPosition = hud.m_gpRoot.anchoredPosition
                                     + new Vector2(hud.m_gpRoot.rect.width + 12f, 0f);

            s_root = clone.gameObject;
            s_icon = FindLike<Image>(hud.m_gpRoot, clone, hud.m_gpIcon.transform);

            var nameComp = AccessTools.Field(typeof(Hud), "m_gpName")?.GetValue(hud) as Component;
            var cdComp = AccessTools.Field(typeof(Hud), "m_gpCooldown")?.GetValue(hud) as Component;
            s_nameGO = FindChild(hud.m_gpRoot, clone, nameComp != null ? nameComp.transform : null);
            s_cooldownGO = FindChild(hud.m_gpRoot, clone, cdComp != null ? cdComp.transform : null);

            s_root.SetActive(false);
        }

        public static void Refresh(Hud hud)
        {
            EnsureBuilt(hud);
            if (s_root == null) return;

            if (!VillagerAbilityManager.HasLearnedMountainStride())
            {
                if (s_root.activeSelf) s_root.SetActive(false);
                return;
            }

            if (!s_root.activeSelf) s_root.SetActive(true);

            var cooldown = VillagerAbilityManager.GetCooldownRemaining();

            if (s_icon != null)
            {
                s_icon.sprite = SE_MountainStride.Icon;
                s_icon.color = cooldown > 0f ? CooldownTint : Color.white;
            }

            if (s_nameGO != null)
                VillagerUIFactory.SetTMPText(s_nameGO, "Mountain Stride");

            if (s_cooldownGO != null)
                VillagerUIFactory.SetTMPText(s_cooldownGO, cooldown > 0f
                    ? StatusEffect.GetTimeString(cooldown)
                    : Localization.instance.Localize("$hud_ready"));
        }

        /// <summary>Destroy the clone on hot-reload / world unload so it doesn't orphan.</summary>
        [RegisterCleanup]
        public static void Cleanup()
        {
            if (s_root != null) Object.Destroy(s_root);
            s_root = null;
            s_icon = null;
            s_nameGO = null;
            s_cooldownGO = null;
        }

        /// <summary>Find the component in <paramref name="cloneRoot" /> at the same
        /// relative path the original component occupies under its root.</summary>
        private static T FindLike<T>(Transform origRoot, Transform cloneRoot, Transform origChild)
            where T : Component
        {
            var go = FindChild(origRoot, cloneRoot, origChild);
            return go != null ? go.GetComponent<T>() : null;
        }

        private static GameObject FindChild(Transform origRoot, Transform cloneRoot, Transform origChild)
        {
            if (origChild == null) return null;
            var path = RelativePath(origRoot, origChild);
            var t = string.IsNullOrEmpty(path) ? cloneRoot : cloneRoot.Find(path);
            return t != null ? t.gameObject : null;
        }

        private static string RelativePath(Transform root, Transform child)
        {
            var parts = new List<string>();
            for (var cur = child; cur != null && cur != root; cur = cur.parent)
                parts.Insert(0, cur.name);
            return string.Join("/", parts);
        }
    }

    [HarmonyPatch(typeof(Hud), "Update")]
    public static class AbilityHud_Update_Patch
    {
        [HarmonyPostfix]
        public static void Postfix(Hud __instance)
        {
            AbilityHud.Refresh(__instance);
        }
    }
}
