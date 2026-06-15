using System;
using System.Linq;
using UnityEngine;
using ValheimVillages.Schemas;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.Records;
using ValheimVillages.Villager.Station;
using ValheimVillages.Villages.Entity;
using Object = UnityEngine.Object;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Instantiates and configures villager NPCs. Spawning is registry-only: the
    ///     registry station UI (Add tab), the vv_recruit dev command, and the Revive
    ///     service all call <see cref="SpawnVillagerNpc" />. The legacy pawn-on-bed spawn
    ///     path was removed — villagers are no longer anchored to or claimed on player beds.
    /// </summary>
    public static class VillagerSpawner
    {
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

        /// <summary>
        ///     Instantiate and fully configure a villager NPC at <paramref name="anchorPos" />:
        ///     identity + faction, dialog, native-component stripping, our component stack,
        ///     and the <c>vv_record_id</c> / <c>vv_home_position</c> stamps. Resolves the
        ///     authoritative record — mints a fresh Alive one when <paramref name="record" />
        ///     is null (fresh recruit), or re-activates the supplied record in place (revive).
        ///     Returns the NPC, or null on failure. Shared by the registry Add/recruit paths
        ///     and the Revive action so they stay in lock-step.
        /// </summary>
        internal static GameObject SpawnVillagerNpc(
            VillagerDef villagerDef, string villagerType, string villagerPrefab,
            Vector3 anchorPos, ref VillagerRecord record, string contextVillageId = null)
        {
            var prefab = ZNetScene.instance?.GetPrefab(villagerPrefab);
            if (prefab == null)
            {
                Plugin.Log?.LogError($"Failed to find prefab {villagerPrefab}");
                return null;
            }

            var npcObject = Object.Instantiate(prefab, anchorPos + Vector3.up * 0.7f, Quaternion.identity);
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
                    villageId, anchorPos, RecordStatus.Alive, npcZdoId);
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
                record.HomeAnchor = anchorPos;
            }

            npcZnetView.GetZDO().Set("vv_record_id", record.RecordId);
            npcZnetView.GetZDO().Set(Village.IdKey, villageId);
            npcZnetView.GetZDO().Set("vv_home_position", anchorPos);
            // Persist to the world save. ZDOMan.GetSaveClone writes every Persistent ZDO
            // regardless of owner, so this flag alone is sufficient (no ownership transfer).
            npcZnetView.GetZDO().Persistent = true;

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
