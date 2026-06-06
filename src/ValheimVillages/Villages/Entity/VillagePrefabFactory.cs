using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimVillages.Villages.Entity
{
    /// <summary>
    ///     Registers the invisible <c>vv_village</c> carrier prefab — an empty
    ///     GameObject with a persistent <see cref="ZNetView" /> and nothing else. Each
    ///     durable village is a free-standing ZDO of this prefab (minted via
    ///     <see cref="VillageRegistry.Create" />), owning the serialized HNA region
    ///     graph independently of any in-world villager/registry GameObject.
    ///
    ///     <para>Direct mirror of <c>RecordPrefabFactory</c>: registering a real prefab
    ///     keeps <see cref="ZNetScene" /> from spamming "missing prefab" warnings and
    ///     lets it instantiate harmless empty <c>vv_village(Clone)</c> objects when a
    ///     village ZDO's sector loads. The <c>vv_</c> name means
    ///     <c>PrefabProtectionPatch</c> skips Awake on the TEMPLATE (so it never
    ///     self-registers a stray ZDO); clones run Awake normally. Idempotent — safe to
    ///     call again on hot reload.</para>
    /// </summary>
    public static class VillagePrefabFactory
    {
        public const string VillagePrefabName = "vv_village";

        public static readonly int VillagePrefabHash = VillagePrefabName.GetStableHashCode();

        private static GameObject _prefab;

        public static void RegisterInZNetScene(ZNetScene zNetScene)
        {
            if (zNetScene == null) return;

            // Hot reload may have left it registered — reuse the existing template.
            if (_prefab == null)
                _prefab = zNetScene.GetPrefab(VillagePrefabName);
            if (_prefab == null)
                _prefab = BuildPrefab();
            if (_prefab == null) return;

            if (!zNetScene.m_prefabs.Contains(_prefab))
                zNetScene.m_prefabs.Add(_prefab);

            var named = GetPrivateDictionary(zNetScene, "m_namedPrefabs");
            if (named != null)
                named[VillagePrefabHash] = _prefab;

            Plugin.Log?.LogInfo($"[VillagePrefabFactory] Registered '{VillagePrefabName}' carrier in ZNetScene");
        }

        private static GameObject BuildPrefab()
        {
            var go = new GameObject(VillagePrefabName);
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
