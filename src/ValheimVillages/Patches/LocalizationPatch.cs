using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Patches
{
    /// <summary>
    /// Registers custom localization tokens for virtual crafting station names.
    /// Valheim's Localization system resolves "$token" by looking up "token"
    /// in m_translations; unregistered tokens display as "[Token]".
    ///
    /// Tokens for virtual stations are generated from VillagerRegistry at runtime.
    /// The Harmony postfix handles language changes / hot reloads.
    /// <see cref="RegisterTokens"/> is called from Plugin.Awake to cover the
    /// common case where SetupLanguage has already run before our plugin loads.
    /// </summary>
    [HarmonyPatch(typeof(Localization), nameof(Localization.SetupLanguage))]
    public static class LocalizationPatch
    {
        private static Dictionary<string, string> s_tokens;

        /// <summary>
        /// Generic station token plus one per villager type with a virtual station.
        /// </summary>
        private static Dictionary<string, string> Tokens
        {
            get
            {
                if (s_tokens != null) return s_tokens;
                s_tokens = new Dictionary<string, string> { { "vv_villager", "Villager" } };
                foreach (var kv in VillagerRegistry.Definitions)
                {
                    if (!string.IsNullOrEmpty(kv.Value.stationName))
                    {
                        var token = kv.Value.stationName.TrimStart('$');
                        s_tokens[token] = kv.Value.displayName;
                    }
                }
                return s_tokens;
            }
        }

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
