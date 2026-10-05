using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimVillages.Networking
{
    /// <summary>
    ///     Registers an invisible "carrier" prefab — an empty GameObject with a persistent
    ///     <see cref="ZNetView" /> and nothing else — whose ZDOs hold mod data that must live
    ///     in the world save independently of any in-world object (village graphs, villager
    ///     records, rescue quests). Registering a real prefab (rather than minting ZDOs with an
    ///     unknown hash) keeps <see cref="ZNetScene" /> from spamming "missing prefab" warnings
    ///     and lets it instantiate harmless empty <c>(Clone)</c> objects when a carrier's sector
    ///     loads. Carrier names start with <c>vv_</c>, so <c>ZNetViewAwakeProtectionPatch</c>
    ///     skips Awake on the TEMPLATE (it never mints a stray ZDO of its own); clones run Awake
    ///     normally and bind to the pre-created ZDO. Idempotent — safe to call again on hot reload.
    /// </summary>
    internal static class CarrierPrefab
    {
        /// <summary>
        ///     Ensures the carrier <paramref name="name" /> is registered in
        ///     <paramref name="zNetScene" />, reusing <paramref name="prefab" /> or a template a
        ///     previous (hot-reloaded) assembly left behind. Returns the template to cache.
        /// </summary>
        internal static GameObject Register(ZNetScene zNetScene, string name, GameObject prefab, string logTag)
        {
            if (zNetScene == null) return prefab;

            // Hot reload may have left it registered — reuse the existing template.
            if (prefab == null)
                prefab = zNetScene.GetPrefab(name);
            if (prefab == null)
                prefab = Build(name);

            if (!zNetScene.m_prefabs.Contains(prefab))
                zNetScene.m_prefabs.Add(prefab);

            var named = GetPrivateDictionary(zNetScene, "m_namedPrefabs");
            if (named != null)
                named[name.GetStableHashCode()] = prefab;

            Plugin.Log?.LogInfo($"[{logTag}] Registered '{name}' carrier in ZNetScene");
            return prefab;
        }

        private static GameObject Build(string name)
        {
            var go = new GameObject(name);
            go.SetActive(false);

            var nview = go.AddComponent<ZNetView>();
            nview.m_persistent = true;
            nview.m_type = ZDO.ObjectType.Default;
            nview.m_distant = false;

            Object.DontDestroyOnLoad(go);
            // Active template like vanilla prefabs: ZNetScene instantiates active clones
            // (so their ZNetView.Awake binds the ZDO). The protection patch skips Awake on
            // this template by name, so it never mints a stray ZDO of its own.
            go.SetActive(true);
            return go;
        }

        private static Dictionary<int, GameObject> GetPrivateDictionary<T>(T instance, string fieldName)
        {
            var field = typeof(T).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(instance) as Dictionary<int, GameObject>;
        }
    }
}
