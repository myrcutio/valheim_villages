using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Attributes;
using Object = UnityEngine.Object;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    ///     Registers and spawns the "Lode Core pedestal": a Dvergr core-stand Pickable (cloned
    ///     from the Mistlands black-core stand) whose pickup yields a generic Lode Core, so the
    ///     rescue-dungeon reward sits on a pedestal to pry loose rather than lying on the floor.
    ///     Its smash-drop is forced to Stone — the base stand would otherwise drop Mistlands
    ///     resources a fresh player can't yet use. Registered like the registry piece
    ///     (PieceFactory): a [RequireObjectDB] deferred task adds it to ZNetScene once ready.
    ///     EXPERIMENTAL — the cloned prefab's structure can't be verified without running the game.
    /// </summary>
    public static class LodeCorePedestal
    {
        public const string PrefabName = "vv_lode_core_pedestal";
        private const string BaseStand = "Pickable_BlackCoreStand"; // Dvergr black-core stand (Mistlands)

        private static GameObject _prefab;

        [RequireObjectDB]
        public static void RegisterDeferred()
        {
            Register(ZNetScene.instance);
        }

        public static void Register(ZNetScene zNetScene)
        {
            if (zNetScene == null) return;

            Ensure(zNetScene);
            if (_prefab == null) return;

            if (!zNetScene.m_prefabs.Contains(_prefab))
                zNetScene.m_prefabs.Add(_prefab);
            var named = GetPrivateDictionary(zNetScene, "m_namedPrefabs");
            if (named != null) named[PrefabName.GetStableHashCode()] = _prefab;

            Plugin.Log?.LogInfo($"[LodeCorePedestal] registered '{PrefabName}' in ZNetScene");
        }

        /// <summary>Spawn a pedestal at <paramref name="position" />. Returns the instance, or null if unavailable.</summary>
        public static GameObject SpawnAt(Vector3 position)
        {
            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(PrefabName) : null;
            if (prefab == null) return null;
            var rot = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            return Object.Instantiate(prefab, position, rot);
        }

        private static void Ensure(ZNetScene zNetScene)
        {
            if (_prefab != null) return;

            // Hot reload: a prior assembly may have left it registered — re-apply config.
            var existing = zNetScene.GetPrefab(PrefabName);
            if (existing != null)
            {
                Configure(existing);
                _prefab = existing;
                return;
            }

            var baseStand = zNetScene.GetPrefab(BaseStand);
            if (baseStand == null)
            {
                Plugin.Log?.LogError($"[LodeCorePedestal] base prefab '{BaseStand}' not found; pedestal unavailable");
                return;
            }

            var prefab = Clone(baseStand, PrefabName);
            Configure(prefab);
            _prefab = prefab;
            Plugin.Log?.LogInfo($"[LodeCorePedestal] built '{PrefabName}' from '{BaseStand}'");
        }

        private static void Configure(GameObject prefab)
        {
            var odb = ObjectDB.instance;
            var core = odb != null ? odb.GetItemPrefab(LodeCore.Prefab) : null;
            var stone = odb != null ? odb.GetItemPrefab("Stone") : null;

            // The core you pry off the stand is a generic Lode Core (and never respawns).
            foreach (var pickable in prefab.GetComponentsInChildren<Pickable>(true))
            {
                if (core != null) pickable.m_itemPrefab = core;
                pickable.m_amount = 1;
                pickable.m_respawnTimeMinutes = 0;
            }

            // Smashing the pedestal must only ever yield Stone — never the base stand's Mistlands
            // drops. Null the spawned mineable/resource object on every Destructible, then force the
            // drop-on-destroyed table to Stone (overwriting any existing ones; adding one if absent).
            var dods = new List<DropOnDestroyed>(prefab.GetComponentsInChildren<DropOnDestroyed>(true));
            foreach (var destructible in prefab.GetComponentsInChildren<Destructible>(true))
            {
                destructible.m_spawnWhenDestroyed = null;
                if (dods.Count == 0)
                    dods.Add(destructible.gameObject.AddComponent<DropOnDestroyed>());
            }

            if (stone == null)
            {
                Plugin.Log?.LogError("[LodeCorePedestal] 'Stone' prefab missing; cannot set the smash drop");
                return;
            }

            foreach (var dod in dods)
                dod.m_dropWhenDestroyed = new DropTable
                {
                    m_dropMin = 1,
                    m_dropMax = 1,
                    m_dropChance = 1f,
                    m_oneOfEach = false,
                    m_drops = new List<DropTable.DropData>
                    {
                        new() { m_item = stone, m_stackMin = 2, m_stackMax = 3, m_weight = 1f },
                    },
                };
        }

        private static GameObject Clone(GameObject basePrefab, string newName)
        {
            var wasActive = basePrefab.activeSelf;
            basePrefab.SetActive(false);
            var prefab = Object.Instantiate(basePrefab);
            basePrefab.SetActive(wasActive);
            prefab.name = newName;
            prefab.transform.SetParent(PrefabTemplates.Root, false);
            prefab.SetActive(true);
            return prefab;
        }

        private static Dictionary<int, GameObject> GetPrivateDictionary(object instance, string field)
        {
            var f = instance.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance);
            return f?.GetValue(instance) as Dictionary<int, GameObject>;
        }
    }
}
