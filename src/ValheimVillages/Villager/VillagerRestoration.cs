using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.Records;
using ValheimVillages.Villager.Registry;
using ValheimVillages.Villager.Station;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Restores villager state after hot reload or zone load.
    ///     Strips native Dvergr components (MonsterAI, NpcTalk, Tameable)
    ///     and ensures all mod components are present.
    /// </summary>
    public static class VillagerRestoration
    {
        private static readonly HashSet<string> s_restoredIds = new();

        /// <summary>
        ///     Restore villager state from ZDO using VillagerRegistry for definition lookup.
        ///     Strips native AI/talk/tame components and ensures all mod components are present.
        /// </summary>
        public static bool Restore(GameObject go, ZDO zdo)
        {
            if (go == null || zdo == null) return false;

            // Never re-init the player (or anything parented under it), and never treat a
            // record-carrier ZDO as an NPC — it shares the vv_record_id key with real
            // villagers, so the broad "is this a villager?" checks would otherwise match it.
            if (NativeNpcStripper.IsPlayerOwned(go))
            {
                Plugin.Log?.LogError(
                    $"[VillagerRestoration] Refusing to restore player-owned object '{go.name}'.");
                return false;
            }

            if (zdo.GetPrefab() == RecordPrefabFactory.RecordPrefabHash) return false;

            // Resolve the authoritative record. New villagers carry a vv_record_id
            // back-reference; legacy ones (saved before the record table) carry
            // vv_villager_id/type/name — migrate those into a fresh record.
            var record = ResolveOrMigrateRecord(zdo);
            if (record == null) return false;
            var recordId = record.RecordId;

            if (s_restoredIds.Contains(recordId)) return false;

            var typeStr = record.Type;
            var def = VillagerRegistry.Get(typeStr);
            if (def == null)
            {
                Plugin.Log?.LogWarning($"[VillagerRestoration] No definition for type '{typeStr}', skipping");
                return false;
            }

            // Re-assert world-save persistence. Legacy villagers spawned before the spawn-time
            // fix (or any whose prefab default was non-persistent) would otherwise be dropped from
            // the .db on the next save; setting it on every restore heals them in place.
            zdo.Persistent = true;

            StripNativeComponents(go);
            RestoreIdentity(go, def);
            RestoreComponents(go, recordId, typeStr);
            Dialog.ConfigureDialog(go, def);

            s_restoredIds.Add(recordId);

            Plugin.Log?.LogInfo(
                $"[VillagerRestoration] Restored {def.displayName} ({typeStr}) at {go.transform.position}");

            return true;
        }

        /// <summary>
        ///     Return this NPC's villager record, minting one from legacy
        ///     vv_villager_* keys if it has none (migration). Returns null if the ZDO
        ///     isn't one of ours or the record can't be created.
        /// </summary>
        private static VillagerRecord ResolveOrMigrateRecord(ZDO zdo)
        {
            var recordId = zdo.GetString("vv_record_id");
            if (!string.IsNullOrEmpty(recordId))
            {
                var existing = VillagerRecordTable.FindById(recordId);
                if (existing != null) return existing;
            }

            // No live record — migrate from legacy identity if present.
            var legacyType = zdo.GetString("vv_villager_type");
            if (string.IsNullOrEmpty(legacyType))
            {
                if (!string.IsNullOrEmpty(recordId))
                    Plugin.Log?.LogWarning(
                        $"[VillagerRestoration] vv_record_id '{recordId}' has no record and no legacy " +
                        "identity to migrate; skipping");
                return null;
            }

            var legacyName = zdo.GetString("vv_villager_name");
            var bedPos = zdo.GetVec3("vv_home_position", Vector3.zero);
            // Resolve (never mint) the village: stamped id, else existing graph coverage,
            // else registry-anchor proximity. A legacy villager that resolves to no village
            // is NOT migrated (villages are created only at a registry station).
            var stamped = zdo.GetString(Village.IdKey);
            var villageId = !string.IsNullOrEmpty(stamped)
                ? stamped
                : (VillageRegistry.GetVillageCovering(bedPos) ?? VillageRegistry.FindNearAnchor(bedPos))?.VillageId;
            if (string.IsNullOrEmpty(villageId))
            {
                Plugin.Log?.LogWarning(
                    $"[VillagerRestoration] legacy villager '{legacyName}' ({legacyType}) at {bedPos} " +
                    "resolves to no village; not migrating (villages are created only at a registry station).");
                return null;
            }

            var record = VillagerRecordTable.Create(
                legacyType,
                string.IsNullOrEmpty(legacyName) ? legacyType : legacyName,
                villageId, bedPos, RecordStatus.Alive, zdo.m_uid);
            if (record == null) return null;

            zdo.Set("vv_record_id", record.RecordId);
            zdo.Set(Village.IdKey, villageId);
            Plugin.Log?.LogInfo(
                $"[VillagerRestoration] Migrated legacy villager '{legacyName}' ({legacyType}) -> record {record.RecordId}");
            return record;
        }

        [RegisterCleanup]
        public static void ClearTracking()
        {
            s_restoredIds.Clear();
        }

        /// <summary>
        ///     Remove native Dvergr prefab components that conflict with our AI/talk/interaction.
        ///     Safe to call even if they've already been destroyed (e.g. after initial spawn).
        ///     Delegates to the shared, player-safe stripper, which also clears the native
        ///     BaseAI RPC registrations and dangling damage/death handlers (see
        ///     <see cref="NativeNpcStripper" />).
        /// </summary>
        private static void StripNativeComponents(GameObject go)
        {
            NativeNpcStripper.Strip(go);
        }

        private static void RestoreIdentity(GameObject go, VillagerDef definition)
        {
            var humanoid = go.GetComponent<Humanoid>();
            if (humanoid != null && !string.IsNullOrEmpty(definition?.displayName))
                humanoid.m_name = definition.displayName;

            // Only flip faction on a genuine villager — never on the player or its children.
            var character = go.GetComponent<Character>();
            if (character != null && !NativeNpcStripper.IsPlayerOwned(character))
                character.m_faction = Character.Faction.Players;
        }

        /// <summary>
        ///     Ensure all mod components are present on the NPC GameObject.
        ///     Idempotent -- skips components that already exist.
        /// </summary>
        private static void RestoreComponents(GameObject go, string uniqueId, string typeStr)
        {
            if (go.GetComponent<Villager>() == null)
                go.AddComponent<Villager>();

            if (go.GetComponent<VillagerTalk>() == null)
                go.AddComponent<VillagerTalk>();

            if (go.GetComponent<DoorHandler>() == null)
                go.AddComponent<DoorHandler>();

            var bridge = go.GetComponent<VillagerBehaviorBridge>();
            if (bridge == null)
            {
                bridge = go.AddComponent<VillagerBehaviorBridge>();
                bridge.villagerInstance = go.GetComponent<Villager>();
                bridge.Initialize(uniqueId);
            }

            if (go.GetComponent<VillagerInteract>() == null)
                go.AddComponent<VillagerInteract>();

            if (go.GetComponent<VillagerStation>() == null)
            {
                var vs = go.AddComponent<VillagerStation>();
                vs.Initialize(typeStr);
            }
        }
    }
}