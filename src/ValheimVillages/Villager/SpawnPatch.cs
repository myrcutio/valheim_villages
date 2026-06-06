using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Schemas;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.Records;
using ValheimVillages.Villager.Registry;
using ValheimVillages.Villager.Station;
using ValheimVillages.Villages.Entity;
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

            // Check if the bed is unclaimed. The authoritative claim is our own player-independent
            // vv_bed_owner key; vanilla "owner" is also honored so a bed a real player has set their
            // spawn at can't be hijacked by a villager.
            var existingBedZdo = bed.GetComponent<ZNetView>()?.GetZDO();
            var vanillaOwner = existingBedZdo?.GetLong("owner") ?? 0L;
            var villagerOwner = existingBedZdo?.GetString("vv_bed_owner") ?? "";
            if (vanillaOwner != 0L || !string.IsNullOrEmpty(villagerOwner))
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

            // Instantiate + configure the NPC and mint its authoritative record. Passing a
            // null record makes SpawnVillagerNpc mint a fresh Alive one (the revive path
            // passes an existing record to re-activate instead).
            VillagerRecord record = null;
            // Resolve (never mint) the village this bed sits in.
            var bedVillageId = (VillageRegistry.GetVillageCovering(bed.transform.position)
                                ?? VillageRegistry.FindNearAnchor(bed.transform.position))?.VillageId;
            var npcObject = SpawnVillagerNpc(villagerDef, villagerType, villagerPrefab,
                bed.transform.position, ref record, bedVillageId);
            if (npcObject == null)
            {
                player.Message(MessageHud.MessageType.Center, "Failed to spawn villager");
                return false;
            }

            var uniqueId = record.RecordId;
            var npcZnetView = npcObject.GetComponent<ZNetView>();
            var npcZdoId = npcZnetView.GetZDO().m_uid;

            var bedZnetView = bed.GetComponent<ZNetView>();
            if (bedZnetView != null && bedZnetView.GetZDO() != null)
            {
                var bedZdoId = bedZnetView.GetZDO().m_uid;
                // Reverse link: store the full bed ZDOID, not the truncated .ID (which dropped the
                // userId half and could collide across sessions).
                npcZnetView.GetZDO().Set("vv_assigned_bed", bedZdoId);
                // Authoritative, player-independent claim under our own namespace. Valheim's spawn-point
                // system only writes the vanilla "owner" key, so it can never silently clobber this.
                bedZnetView.GetZDO().Set("vv_bed_owner", uniqueId);
                // Mirror vanilla owner/ownerName so the bed still reads as claimed in vanilla UI and
                // resists casual deconstruction. This mirror is cosmetic; vv_bed_owner is the truth.
                bedZnetView.GetZDO().Set("owner", npcZdoId != ZDOID.None ? npcZdoId.ID : 0L);
                bedZnetView.GetZDO().Set("ownerName", villagerDef.displayName);
                Plugin.Log?.LogInfo($"Assigned bed to villager '{villagerDef.displayName}' with ZDO ID: {npcZdoId}");
            }

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

        /// <summary>
        ///     Instantiate and fully configure a villager NPC at <paramref name="bedPos" />:
        ///     identity + faction, dialog, native-component stripping, our component stack,
        ///     and the <c>vv_record_id</c> / <c>vv_bed_position</c> stamps. Resolves the
        ///     authoritative record — mints a fresh Alive one when <paramref name="record" />
        ///     is null (pawn spawn), or re-activates the supplied record in place (revive).
        ///     Returns the NPC, or null on failure. Shared by the pawn-spawn path and the
        ///     registry Revive action so both stay in lock-step.
        /// </summary>
        internal static GameObject SpawnVillagerNpc(
            VillagerDef villagerDef, string villagerType, string villagerPrefab,
            Vector3 bedPos, ref VillagerRecord record, string contextVillageId = null)
        {
            var prefab = ZNetScene.instance?.GetPrefab(villagerPrefab);
            if (prefab == null)
            {
                Plugin.Log?.LogError($"Failed to find prefab {villagerPrefab}");
                return null;
            }

            var npcObject = Object.Instantiate(prefab, bedPos + Vector3.up * 0.7f, Quaternion.identity);
            if (npcObject == null)
            {
                Plugin.Log?.LogError("Failed to instantiate NPC");
                return null;
            }

            // Safety: a villager prefab lookup must never resolve to the player. If it
            // somehow did (bad preferredPrefab, prefab-table corruption), abort rather
            // than strip the player's components or flip its faction.
            if (NativeNpcStripper.IsPlayerOwned(npcObject))
            {
                Plugin.Log?.LogError(
                    $"[SpawnVillagerNpc] Prefab '{villagerPrefab}' resolved to a player-owned object; aborting spawn.");
                Object.Destroy(npcObject);
                return null;
            }

            var humanoid = npcObject.GetComponent<Humanoid>();
            if (humanoid != null)
            {
                humanoid.m_name = villagerDef.displayName;
                var character = npcObject.GetComponent<Character>();
                if (character != null)
                    character.m_faction = Character.Faction.Players;
            }

            // Add VillagerTalk and configure dialog lines from the JSON definition.
            npcObject.AddComponent<VillagerTalk>();
            npcObject.AddComponent<DoorHandler>();
            Dialog.ConfigureDialog(npcObject, villagerDef);

            var npcZnetView = npcObject.GetComponent<ZNetView>();
            if (npcZnetView == null || npcZnetView.GetZDO() == null)
            {
                Plugin.Log?.LogError("Spawned NPC has no ZNetView/ZDO");
                Object.Destroy(npcObject);
                return null;
            }

            var npcZdoId = npcZnetView.GetZDO().m_uid;

            // Resolve the durable village this villager belongs to — NEVER mint here.
            // A revive reuses the record's village; a fresh spawn uses the village the
            // caller resolved (the registry it was recruited from). Villages are created
            // only at registry placement, so a villager with no resolvable village is an
            // error, not a cue to fabricate one.
            var villageId = record != null && !string.IsNullOrEmpty(record.Village)
                ? record.Village
                : contextVillageId;
            if (string.IsNullOrEmpty(villageId))
            {
                Plugin.Log?.LogError(
                    "[SpawnVillagerNpc] No village to spawn into (villages are created only at a " +
                    "registry station). Aborting spawn.");
                Object.Destroy(npcObject);
                return null;
            }

            // Resolve the authoritative record. The record owns identity
            // (type/name/status/village); the NPC keeps only vv_record_id + vv_village_id
            // back-references, and its UniqueId IS the record id.
            if (record == null)
            {
                record = VillagerRecordTable.Create(
                    villagerType, villagerDef.displayName,
                    villageId, bedPos, RecordStatus.Alive, npcZdoId);
                if (record == null)
                {
                    Plugin.Log?.LogError("Failed to mint villager record; aborting spawn");
                    Object.Destroy(npcObject);
                    return null;
                }
            }
            else
            {
                // Revive: re-activate the existing record and re-link the fresh NPC.
                record.Status = RecordStatus.Alive;
                record.NpcZdoId = npcZdoId;
                record.BedPosition = bedPos;
            }

            npcZnetView.GetZDO().Set("vv_record_id", record.RecordId);
            npcZnetView.GetZDO().Set(Village.IdKey, villageId);
            npcZnetView.GetZDO().Set("vv_bed_position", bedPos);
            // Persist to the world save (ZDOMan only writes Persistent ZDOs).
            npcZnetView.GetZDO().Persistent = true;
            if (ZNet.instance != null && ZNet.instance.IsDedicated())
                npcZnetView.GetZDO().SetOwner(ZNet.GetUID());

            // Strip native AI/interaction components; our VillagerAI/VillagerTalk/VillagerInteract
            // replace them. This also unregisters the native BaseAI RPCs ("Alert" etc.) and detaches
            // its OnDamaged/OnDeath handlers, so the VillagerAI (also a BaseAI) can re-register without
            // an "item with the same key has already been added" abort in BaseAI.Awake.
            NativeNpcStripper.Strip(npcObject);

            // Add Villager (Awake reads identity from the record + registers VillagerAI),
            // then the bridge / interaction / virtual-station components.
            npcObject.AddComponent<Villager>();
            var bridge = npcObject.AddComponent<VillagerBehaviorBridge>();
            bridge.villagerInstance = npcObject.GetComponent<Villager>();
            bridge.Initialize(record.RecordId);
            npcObject.AddComponent<VillagerInteract>();
            var villagerStation = npcObject.AddComponent<VillagerStation>();
            villagerStation.Initialize(villagerType);

            return npcObject;
        }
    }
}