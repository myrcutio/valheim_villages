using UnityEngine;
using ValheimVillages.Networking;

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
            _prefab = CarrierPrefab.Register(zNetScene, VillagePrefabName, _prefab, "VillagePrefabFactory");
        }
    }
}
