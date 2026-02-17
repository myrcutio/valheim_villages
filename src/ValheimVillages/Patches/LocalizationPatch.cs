using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ValheimVillages.Patches
{
    /// <summary>
    /// Registers custom localization tokens for virtual crafting station names.
    /// Valheim's Localization system resolves "$token" by looking up "token"
    /// in m_translations; unregistered tokens display as "[Token]".
    ///
    /// The Harmony postfix handles language changes / hot reloads.
    /// <see cref="RegisterTokens"/> is called from Plugin.Awake to cover the
    /// common case where SetupLanguage has already run before our plugin loads.
    /// </summary>
    [HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
    public static class LocalizationPatch
    {
        private static readonly Dictionary<string, string> Tokens = new()
        {
            { "vv_farmer", "Farmer" },
            { "vv_tavernkeeper", "Tavern Keeper" },
            { "vv_villager", "Villager" }
        };

        private static MethodInfo s_addWord;

        /// <summary>
        /// Immediately register tokens on the current Localization instance.
        /// Call from Plugin.Awake after PatchAll so tokens are available even
        /// if SetupLanguage already ran before our plugin loaded.
        /// </summary>
        public static void RegisterTokens()
        {
            var loc = Localization.instance;
            if (loc == null) return;
            AddTokens(loc);
        }

        [HarmonyPostfix]
        public static void Postfix(Localization __instance)
        {
            AddTokens(__instance);
        }

        private static void AddTokens(Localization instance)
        {
            s_addWord ??= AccessTools.Method(
                typeof(Localization), "AddWord",
                new[] { typeof(string), typeof(string) });

            if (s_addWord == null)
            {
                Plugin.Log?.LogWarning(
                    "LocalizationPatch: AddWord method not found");
                return;
            }

            foreach (var kv in Tokens)
            {
                s_addWord.Invoke(instance, new object[] { kv.Key, kv.Value });
            }

            Plugin.Log?.LogInfo(
                $"LocalizationPatch: Registered {Tokens.Count} custom tokens");
        }
    }
}
