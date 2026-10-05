using UnityEngine;
using ValheimVillages.Networking;

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
            _prefab = CarrierPrefab.Register(zNetScene, RecordPrefabName, _prefab, "RecordPrefabFactory");
        }
    }
}
