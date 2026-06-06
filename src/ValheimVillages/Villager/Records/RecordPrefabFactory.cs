using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ValheimVillages.Villager.Records
{
    /// <summary>
    ///     Registers the invisible <c>vv_villager_record</c> carrier prefab — an empty
    ///     GameObject with a persistent <see cref="ZNetView" /> and nothing else. Each
    ///     villager record is a free-standing ZDO of this prefab (minted via
    ///     <see cref="VillagerRecordTable.Create" />). Registering a real prefab (rather
    ///     than minting ZDOs with an unknown hash) keeps <see cref="ZNetScene" /> from
    ///     spamming "missing prefab" warnings and lets it instantiate harmless empty
    ///     <c>vv_villager_record(Clone)</c> objects when a record's sector is loaded.
    ///     The <c>vv_</c> name means <see cref="Patches.ZNetViewAwakeProtectionPatch" />
    ///     skips Awake on the TEMPLATE (so it never self-registers a ZDO); clones run
    ///     Awake normally and bind to the pre-created record ZDO. Idempotent — safe to
    ///     call again on hot reload (mirrors <c>PieceFactory.RegisterAllInZNetScene</c>).
    /// </summary>
    public static class RecordPrefabFactory
    {
        public const string RecordPrefabName = "vv_villager_record";

        public static readonly int RecordPrefabHash = RecordPrefabName.GetStableHashCode();

        private static GameObject _prefab;

        public static void RegisterInZNetScene(ZNetScene zNetScene)
        {
            if (zNetScene == null) return;

            // Hot reload may have left it registered — reuse the existing template.
            if (_prefab == null)
                _prefab = zNetScene.GetPrefab(RecordPrefabName);
            if (_prefab == null)
                _prefab = BuildPrefab();
            if (_prefab == null) return;

            if (!zNetScene.m_prefabs.Contains(_prefab))
                zNetScene.m_prefabs.Add(_prefab);

            var named = GetPrivateDictionary(zNetScene, "m_namedPrefabs");
            if (named != null)
                named[RecordPrefabHash] = _prefab;

            Plugin.Log?.LogInfo($"[RecordPrefabFactory] Registered '{RecordPrefabName}' carrier in ZNetScene");
        }

        private static GameObject BuildPrefab()
        {
            var go = new GameObject(RecordPrefabName);
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
