using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimVillages.Villager.Records
{
    /// <summary>
    ///     Static facade over the villager record store. The records live in
    ///     free-standing <c>vv_villager_record</c> ZDOs; this enumerates/queries them by
    ///     scanning <c>ZDOMan.m_objectsByID</c> (the same reflection idiom as
    ///     <c>VillagerAIManager.GetAllBedPositions</c>) filtered to the carrier prefab
    ///     hash, so the ZDO layer is always the source of truth — no in-memory cache to
    ///     keep in sync across hot reloads or world loads.
    /// </summary>
    public static class VillagerRecordTable
    {
        /// <summary>
        ///     Mint a new record ZDO. The villager's UniqueId is the returned record's
        ///     <see cref="VillagerRecord.RecordId" />.
        /// </summary>
        public static VillagerRecord Create(
            string type, string name, string villageKey,
            Vector3 bedPos, RecordStatus status, ZDOID npcZdoId)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null)
            {
                Plugin.Log?.LogError("[VillagerRecordTable] Cannot create record: ZDOMan not ready");
                return null;
            }

            var zdo = zdoMan.CreateNewZDO(bedPos, RecordPrefabFactory.RecordPrefabHash);
            // CreateNewZDO does NOT persist the prefab hash onto the ZDO (it only uses it
            // for a portal check) — set it explicitly so GetPrefab() returns the carrier
            // hash, which is how EnumerateAll finds records and how ZNetScene instantiates
            // the carrier clone.
            zdo.SetPrefab(RecordPrefabFactory.RecordPrefabHash);
            zdo.Persistent = true;
            // On a dedicated server the host must own the record ZDO so it's written to
            // the world .db (same fix SpawnPatch applies to villager ZDOs).
            if (ZNet.instance != null && ZNet.instance.IsDedicated())
                zdo.SetOwner(ZNet.GetUID());

            var id = Guid.NewGuid().ToString();
            zdo.Set(VillagerRecord.IdKey, id);

            var record = new VillagerRecord(zdo)
            {
                Type = type,
                Name = name,
                Village = villageKey,
                BedPosition = bedPos,
                Status = status,
                NpcZdoId = npcZdoId,
                EggPrefab = "",
            };

            Plugin.Log?.LogInfo(
                $"[VillagerRecordTable] Created record {id} ({status}) type={type} name={name} village={villageKey}");
            return record;
        }

        /// <summary>Every villager record in the world, regardless of village or status.</summary>
        public static IEnumerable<VillagerRecord> EnumerateAll()
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) yield break;

            var objectsByID = Traverse.Create(zdoMan)
                .Field<Dictionary<ZDOID, ZDO>>("m_objectsByID").Value;
            if (objectsByID == null) yield break;

            foreach (var zdo in objectsByID.Values)
            {
                if (zdo == null) continue;
                // Filter on the carrier prefab hash — NPC ZDOs also carry a vv_record_id
                // (their back-reference), so we must distinguish by prefab, not by the key.
                if (zdo.GetPrefab() != RecordPrefabFactory.RecordPrefabHash) continue;
                yield return new VillagerRecord(zdo);
            }
        }

        public static VillagerRecord FindById(string recordId)
        {
            if (string.IsNullOrEmpty(recordId)) return null;
            foreach (var rec in EnumerateAll())
                if (rec.RecordId == recordId)
                    return rec;
            return null;
        }

        public static IEnumerable<VillagerRecord> QueryByVillage(string villageKey)
        {
            foreach (var rec in EnumerateAll())
                if (KeysMatch(rec.Village, villageKey))
                    yield return rec;
        }

        public static IEnumerable<VillagerRecord> QueryByStatus(string villageKey, RecordStatus status)
        {
            foreach (var rec in EnumerateAll())
                if (rec.Status == status && KeysMatch(rec.Village, villageKey))
                    yield return rec;
        }

        public static void SetStatus(string recordId, RecordStatus status)
        {
            var rec = FindById(recordId);
            if (rec == null)
            {
                Plugin.Log?.LogWarning($"[VillagerRecordTable] SetStatus: record {recordId} not found");
                return;
            }

            rec.Status = status;
            Plugin.Log?.LogInfo($"[VillagerRecordTable] Record {recordId} -> {status}");
        }

        /// <summary>Permanently destroy a record's carrier ZDO.</summary>
        public static bool Delete(VillagerRecord rec)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null || rec?.Zdo == null) return false;

            var zdo = rec.Zdo;
            // DestroyZDO only acts for the ZDO's owner; claim it first.
            if (!zdo.IsOwner())
                zdo.SetOwner(ZDOMan.GetSessionID());
            zdoMan.DestroyZDO(zdo);
            Plugin.Log?.LogInfo($"[VillagerRecordTable] Deleted record {rec.RecordId}");
            return true;
        }

        public static bool Delete(string recordId)
        {
            var rec = FindById(recordId);
            if (rec == null)
            {
                Plugin.Log?.LogWarning($"[VillagerRecordTable] Delete: record {recordId} not found");
                return false;
            }

            return Delete(rec);
        }

        /// <summary>
        ///     Prune bogus <see cref="RecordStatus.Alive" /> records whose linked NPC
        ///     ZDO no longer exists — e.g. a villager removed without a clean death, or
        ///     a hot-reload artifact minted before death detection worked. Dead/Egg
        ///     records legitimately have no live NPC and are never pruned. (An Alive
        ///     villager that's merely unloaded still has its NPC ZDO in ZDOMan, so it
        ///     is NOT pruned.) Returns the number removed.
        /// </summary>
        public static int Reconcile()
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return 0;

            var stale = new List<VillagerRecord>();
            foreach (var rec in EnumerateAll())
            {
                if (rec.Status != RecordStatus.Alive) continue;
                var npc = rec.NpcZdoId;
                if (npc == ZDOID.None) continue;
                if (zdoMan.GetZDO(npc) == null) stale.Add(rec);
            }

            foreach (var rec in stale) Delete(rec);
            if (stale.Count > 0)
                Plugin.Log?.LogInfo(
                    $"[VillagerRecordTable] Reconcile pruned {stale.Count} orphaned Alive record(s)");
            return stale.Count;
        }

        /// <summary>
        ///     A record belongs to a village when their ids are exactly equal. With
        ///     durable, registry-anchored village ids (<c>vv_village_id</c>) there is no
        ///     coordinate bucket to straddle, so the old Manhattan-1 neighbour tolerance
        ///     is gone — an exact match is both necessary and sufficient.
        /// </summary>
        public static bool KeysMatch(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && a == b;
        }
    }
}
