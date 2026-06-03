using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.Registry;
using ValheimVillages.Villager.Station;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Single spawn path: handles using pawn items to spawn villagers at unclaimed beds.
    ///     Supports vv_pawn (random type), vv_farmer_pawn, vv_guard_pawn, vv_mountaineer_pawn, etc.
    ///     Adds Villager component and registers with Villager.AI.VillagerAIManager.
    /// </summary>
    [HarmonyPatch(typeof(Humanoid), nameof(Humanoid.UseItem))]
    public static class VillagerPawnPatch
    {
        private const string VillagerTypeKey = "vv_spawns_villager_type";
        private const string VillagerPrefabKey = "vv_spawns_villager_prefab";
        private const string DefaultPrefab = "DvergerMage";

        private static Dictionary<string, string> s_pawnItemToType;

        /// <summary>
        ///     Map of pawn item names to villager type, built from VillagerRegistry.
        ///     "vv_pawn" maps to null (random type).
        /// </summary>
        private static Dictionary<string, string> PawnItemToType
        {
            get
            {
                if (s_pawnItemToType != null) return s_pawnItemToType;
                s_pawnItemToType = new Dictionary<string, string> { { "vv_pawn", null } };
                foreach (var kv in VillagerRegistry.Definitions)
                    s_pawnItemToType[$"vv_{kv.Key.ToLower()}_pawn"] = kv.Key;
                return s_pawnItemToType;
            }
        }

        /// <summary>
        ///     Logs all prefabs containing "dvergr" (case insensitive) for debugging.
        /// </summary>
        public static void LogAvailableDvergrPrefabs()
        {
            if (ZNetScene.instance == null)
            {
                Plugin.Log?.LogWarning("ZNetScene not available for prefab listing");
                return;
            }

            var dvergrPrefabs = ZNetScene.instance.m_prefabs
                .Where(p => p != null && p.name.IndexOf("dvergr", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(p => p.name)
                .OrderBy(n => n)
                .ToList();

            Plugin.Log?.LogInfo($"Found {dvergrPrefabs.Count} Dvergr prefabs:");
            foreach (var name in dvergrPrefabs) Plugin.Log?.LogInfo($"  - {name}");
        }

        [HarmonyPrefix]
        public static bool Prefix(Humanoid __instance, ItemDrop.ItemData item)
        {
            // Only works for players
            if (__instance is not Player player)
                return true;

            // Only intercept our pawn items (by name or custom data)
            var itemName = item?.m_dropPrefab?.name;
            if (itemName == null)
                return true;
            var hasCustomType = item.m_customData != null && item.m_customData.ContainsKey(VillagerTypeKey);
            if (!hasCustomType && !PawnItemToType.ContainsKey(itemName))
                return true;

            // Must be looking at something
            var hoverObject = player.GetHoverObject();
            if (hoverObject == null)
                return true;

            // Check if it's a bed
            var bed = hoverObject.GetComponentInParent<Bed>();
            if (bed == null)
                return true;

            // Check if the bed is unclaimed (owner stored in ZDO)
            var bedZdoId = bed.GetComponent<ZNetView>()?.GetZDO()?.GetLong("owner") ?? 0L;
            if (bedZdoId != 0L)
            {
                player.Message(MessageHud.MessageType.Center, "This bed is already claimed");
                return false;
            }

            // Spawn a villager NPC at the bed (handles bed assignment and item consumption)
            SpawnVillagerAtBed(player, bed, item);

            return false;
        }

        private static bool SpawnVillagerAtBed(Player player, Bed bed, ItemDrop.ItemData item)
        {
            string villagerType;
            string villagerPrefab;
            if (item.m_customData != null && item.m_customData.ContainsKey(VillagerTypeKey))
            {
                villagerType = item.m_customData[VillagerTypeKey];
                villagerPrefab =
                    item.m_customData != null && item.m_customData.TryGetValue(VillagerPrefabKey, out var p)
                        ? p
                        : DefaultPrefab;
            }
            else
            {
                PawnItemToType.TryGetValue(item?.m_dropPrefab?.name ?? "", out var mappedType);
                if (mappedType != null)
                {
                    villagerType = mappedType;
                }
                else
                {
                    var types = VillagerRegistry.Definitions.Keys.ToList();
                    if (types.Count == 0)
                    {
                        Plugin.Log?.LogError("No villager types in registry");
                        return false;
                    }

                    villagerType = types[Random.Range(0, types.Count)];
                }

                var def = VillagerRegistry.Get(villagerType);
                villagerPrefab = !string.IsNullOrEmpty(def?.preferredPrefab) ? def.preferredPrefab : DefaultPrefab;
            }

            var villagerDef = VillagerRegistry.Get(villagerType);
            if (villagerDef == null)
            {
                Plugin.Log?.LogError($"Failed to get definition for villager type {villagerType}");
                return false;
            }

            var prefab = ZNetScene.instance?.GetPrefab(villagerPrefab);
            if (prefab == null)
            {
                Plugin.Log?.LogError($"Failed to find prefab {villagerPrefab}");
                player.Message(MessageHud.MessageType.Center, "Failed to spawn villager (prefab not found)");
                return false;
            }

            // Spawn position at the bed
            var spawnPos = bed.transform.position + Vector3.up * 0.7f;
            var spawnRot = bed.transform.rotation;

            // Instantiate the NPC
            var npcObject = Object.Instantiate(prefab, spawnPos, spawnRot);
            if (npcObject == null)
            {
                Plugin.Log?.LogError("Failed to instantiate NPC");
                return false;
            }

            // Set up the NPC
            var humanoid = npcObject.GetComponent<Humanoid>();
            if (humanoid != null)
            {
                humanoid.m_name = villagerDef.displayName;

                var character = npcObject.GetComponent<Character>();
                if (character != null)
                    character.m_faction = Character.Faction.Players;
            }

            // Add VillagerTalk and configure dialog lines from JSON definition
            npcObject.AddComponent<VillagerTalk>();
            npcObject.AddComponent<DoorHandler>();
            Dialog.ConfigureDialog(npcObject, villagerDef);

            // ZDO must be set before adding Villager so LoadFromZDO can read identity
            var npcZnetView = npcObject.GetComponent<ZNetView>();
            if (npcZnetView == null || npcZnetView.GetZDO() == null)
            {
                Plugin.Log?.LogError("Spawned NPC has no ZNetView/ZDO");
                Object.Destroy(npcObject);
                return false;
            }

            var npcZdoId = npcZnetView.GetZDO().m_uid;
            var uniqueId = Guid.NewGuid().ToString();
            npcZnetView.GetZDO().Set("vv_villager_id", uniqueId);
            npcZnetView.GetZDO().Set("vv_villager_type", villagerType);
            npcZnetView.GetZDO().Set("vv_villager_name", villagerDef.displayName);
            npcZnetView.GetZDO().Set("vv_bed_position", bed.transform.position);

            // Persist to the world save, not just this session. ZDOMan only writes ZDOs whose
            // Persistent flag is set; relying on the prefab default leaves villagers liable to be
            // orphaned (never written to the .db) on reload. Assert it explicitly, like Tameable.
            npcZnetView.GetZDO().Persistent = true;

            if (ZNet.instance != null && ZNet.instance.IsDedicated())
                npcZnetView.GetZDO().SetOwner(ZNet.GetUID());

            var bedZnetView = bed.GetComponent<ZNetView>();
            if (bedZnetView != null && bedZnetView.GetZDO() != null)
            {
                var bedZdoId = bedZnetView.GetZDO().m_uid;
                npcZnetView.GetZDO().Set("vv_assigned_bed", bedZdoId != ZDOID.None ? bedZdoId.ID : 0L);
                bedZnetView.GetZDO().Set("owner", npcZdoId != ZDOID.None ? npcZdoId.ID : 0L);
                bedZnetView.GetZDO().Set("ownerName", villagerDef.displayName);
                Plugin.Log?.LogInfo($"Assigned bed to villager '{villagerDef.displayName}' with ZDO ID: {npcZdoId}");
            }

            // Strip native AI/interaction components; our VillagerAI, VillagerTalk, and
            // VillagerInteract replace them entirely.
            var monsterAI = npcObject.GetComponent<MonsterAI>();
            if (monsterAI != null)
                Object.DestroyImmediate(monsterAI);

            var npcTalk = npcObject.GetComponent<NpcTalk>();
            if (npcTalk != null)
                Object.DestroyImmediate(npcTalk);

            var tameable = npcObject.GetComponent<Tameable>();
            if (tameable != null)
                Object.DestroyImmediate(tameable);

            // Add Villager component (Awake adds VillagerAI and registers with VillagerAIManager)
            npcObject.AddComponent<Villager>();

            // Bridge needs villagerInstance set for UI
            var bridge = npcObject.AddComponent<VillagerBehaviorBridge>();
            bridge.villagerInstance = npcObject.GetComponent<Villager>();
            bridge.Initialize(uniqueId);

            // Add interaction component for dialog menu
            npcObject.AddComponent<VillagerInteract>();

            // Add virtual station for the crafting/tab UI panel
            var villagerStation = npcObject.AddComponent<VillagerStation>();
            villagerStation.Initialize(villagerType);

            // Consume the pawn item from player's inventory
            try
            {
                var playerInventory = player.GetInventory();
                if (playerInventory != null && item != null)
                {
                    if (playerInventory.RemoveOneItem(item))
                        Plugin.Log?.LogInfo("Consumed vv_pawn item from inventory");
                    else
                        Plugin.Log?.LogWarning("Failed to remove vv_pawn item from inventory");
                }
                else
                {
                    Plugin.Log?.LogWarning(
                        $"Cannot remove item: playerInventory={playerInventory != null}, item={item != null}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"Exception while removing item: {ex.Message}");
            }

            Plugin.Log?.LogInfo($"Spawned {villagerDef.displayName} at bed ({bed.transform.position})");
            player.Message(MessageHud.MessageType.Center, $"Assigned {villagerDef.displayName} to bed");

            return true;
        }
    }
}