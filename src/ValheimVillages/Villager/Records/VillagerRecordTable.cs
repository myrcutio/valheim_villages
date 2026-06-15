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
    ///     <c>VillagerAIManager.GetAllAnchorPositions</c>) filtered to the carrier prefab
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
            Vector3 anchorPos, RecordStatus status, ZDOID npcZdoId)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null)
            {
                Plugin.Log?.LogError("[VillagerRecordTable] Cannot create record: ZDOMan not ready");
                return null;
            }

            var zdo = zdoMan.CreateNewZDO(anchorPos, RecordPrefabFactory.RecordPrefabHash);
            // CreateNewZDO does NOT persist the prefab hash onto the ZDO (it only uses it
            // for a portal check) — set it explicitly so GetPrefab() returns the carrier
            // hash, which is how EnumerateAll finds records and how ZNetScene instantiates
            // the carrier clone.
            zdo.SetPrefab(RecordPrefabFactory.RecordPrefabHash);
            // ZDOMan.GetSaveClone writes every Persistent ZDO regardless of owner, so this
            // flag alone makes the record survive a world save/reload (ownership is irrelevant).
            zdo.Persistent = true;

            var id = Guid.NewGuid().ToString();
            zdo.Set(VillagerRecord.IdKey, id);

            var record = new VillagerRecord(zdo)
            {
                Type = type,
                Name = name,
                Village = villageKey,
                HomeAnchor = anchorPos,
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

        /// <summary>
        ///     Audit for <see cref="RecordStatus.Alive" /> records whose linked NPC ZDO
        ///     no longer exists in the world and log each as a loud error. An orphan is a
        ///     real invariant violation to investigate — NOT something to silently delete
        ///     or paper over. This method NEVER mutates or removes a record; only explicit
        ///     player actions may change records.
        ///     <para>
        ///         Reliable after world load: <see cref="ZDOMan.Load" /> populates
        ///         <c>m_objectsByID</c> with every persisted ZDO, so a null lookup here
        ///         means the NPC is genuinely gone — not merely in an unloaded zone.
        ///     </para>
        ///     Returns the number of orphans found.
        /// </summary>
        public static int AuditOrphans()
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null) return 0;

            var orphans = 0;
            foreach (var rec in EnumerateAll())
            {
                if (rec.Status != RecordStatus.Alive) continue;
                var npc = rec.NpcZdoId;
                if (npc == ZDOID.None) continue;
                if (zdoMan.GetZDO(npc) != null) continue;

                orphans++;
                Plugin.Log?.LogError(
                    $"[VillagerRecordTable] ORPHANED Alive record {rec.RecordId} " +
                    $"(type={rec.Type} name={rec.Name} village={rec.Village}): its linked NPC " +
                    $"ZDO {npc} no longer exists in the world. The record was left INTACT — this " +
                    "is an invariant violation, not a cue to auto-delete. Investigate why the NPC " +
                    "vanished (link broken on reload, removed without a clean death, etc.).");
            }

            if (orphans > 0)
                Plugin.Log?.LogError(
                    $"[VillagerRecordTable] {orphans} orphaned Alive record(s) detected (see errors " +
                    "above). Records were NOT modified.");
            return orphans;
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
