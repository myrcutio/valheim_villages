using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.Records;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    ///     Handles "villager_record_index" tasks. Runs once after world load to
    ///     eagerly migrate legacy villagers — NPC ZDOs carrying the old
    ///     <c>vv_villager_type</c> tag but no <c>vv_record_id</c> — by minting a
    ///     record and stamping the back-reference. Per-NPC restore also migrates
    ///     lazily; this makes the whole roster (and nav's anchor enumeration) consistent
    ///     up front. Priority: High.
    /// </summary>
    [RegisterTaskHandler]
    public class VillagerRecordIndexHandler : ITaskHandlerWithLog
    {
        public string TaskName => "villager_record_index";

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            var zdoMan = ZDOMan.instance;
            if (zdoMan == null)
                return TaskResult.Fail("ZDOMan not ready");

            var objectsByID = Traverse.Create(zdoMan)
                .Field<Dictionary<ZDOID, ZDO>>("m_objectsByID").Value;
            if (objectsByID == null)
                return TaskResult.Fail("m_objectsByID unavailable");

            // Snapshot legacy NPC ZDOs first: minting records calls CreateNewZDO, which
            // mutates m_objectsByID and would invalidate a live enumerator.
            var legacy = new List<ZDO>();
            foreach (var zdo in objectsByID.Values)
            {
                if (zdo == null) continue;
                if (!string.IsNullOrEmpty(zdo.GetString("vv_record_id"))) continue;
                if (string.IsNullOrEmpty(zdo.GetString("vv_villager_type"))) continue;
                legacy.Add(zdo);
            }

            var migrated = 0;
            foreach (var zdo in legacy)
            {
                var type = zdo.GetString("vv_villager_type");
                var name = zdo.GetString("vv_villager_name");
                var anchorPos = zdo.GetVec3("vv_home_position", Vector3.zero);
                // Resolve (never mint) the village; skip migration if none resolves
                // (villages are created only at a registry station).
                var stamped = zdo.GetString(Village.IdKey);
                var villageId = !string.IsNullOrEmpty(stamped)
                    ? stamped
                    : (VillageRegistry.GetVillageCovering(anchorPos) ?? VillageRegistry.FindNearAnchor(anchorPos))?.VillageId;
                if (string.IsNullOrEmpty(villageId))
                {
                    Plugin.Log?.LogWarning(
                        $"[villager_record_index] legacy villager '{name}' ({type}) at {anchorPos} resolves to " +
                        "no village; not migrating.");
                    continue;
                }

                var record = VillagerRecordTable.Create(
                    type,
                    string.IsNullOrEmpty(name) ? type : name,
                    villageId, anchorPos, RecordStatus.Alive, zdo.m_uid);
                if (record == null) continue;
                zdo.Set("vv_record_id", record.RecordId);
                zdo.Set(Village.IdKey, villageId);
                migrated++;
            }

            // Repair record -> NPC back-links BEFORE auditing. Valheim re-keys persisted ZDOs
            // into the "1:<n>" id space on world load, so a record's NpcZdoId — stamped at
            // spawn with the then-current session id (e.g. "2022516356:29536") — is dead after
            // the first save/reload. Without this, EVERY villager is reported ORPHANED on every
            // load while standing alive in front of the player, which trains you to ignore a
            // genuine-invariant-violation error.
            //
            // The NPC's own vv_record_id string IS stable across reloads, so it is the
            // authoritative side of the link; NpcZdoId is a cache of it. Re-pointing that cache
            // at the NPC that already claims the record is a link repair, not a record mutation:
            // status, identity, village and home are untouched, and no record is created or
            // deleted. (See the record invariants on AuditOrphans.)
            var relinked = RelinkNpcBackReferences(objectsByID);

            // Audit (never delete) Alive records whose NPC vanished — an orphan is a
            // loud invariant error to investigate, not a silent prune.
            var orphans = VillagerRecordTable.AuditOrphans();

            var total = VillagerRecordTable.EnumerateAll().Count();
            Plugin.Log?.LogInfo(
                $"[villager_record_index] records={total} migrated={migrated} relinked={relinked} orphans={orphans}");
            activityLog.Record(task.SourceId, TaskName, "index",
                $"records={total} migrated={migrated} relinked={relinked} orphans={orphans}");

            return TaskResult.Ok(new Dictionary<string, string>
            {
                { "records_total", total.ToString() },
                { "migrated", migrated.ToString() },
                { "relinked", relinked.ToString() },
                { "orphans", orphans.ToString() },
            });
        }

        /// <summary>
        ///     Point every record's <c>NpcZdoId</c> at the NPC ZDO that currently claims it via
        ///     <c>vv_record_id</c>. Returns how many links were stale and repaired.
        /// </summary>
        private static int RelinkNpcBackReferences(Dictionary<ZDOID, ZDO> objectsByID)
        {
            // Snapshot before touching anything: writing NpcZdoId sets a value on the record's
            // ZDO, and we must not be enumerating m_objectsByID if that ever grows it.
            var claims = new Dictionary<string, ZDO>();
            var duplicates = new List<string>();
            foreach (var zdo in objectsByID.Values)
            {
                if (zdo == null) continue;
                // The record carrier shares the vv_record_id key with the NPC; it is the record,
                // not the villager, so it must never become its own back-reference.
                if (zdo.GetPrefab() == RecordPrefabFactory.RecordPrefabHash) continue;

                var recordId = zdo.GetString("vv_record_id");
                if (string.IsNullOrEmpty(recordId)) continue;

                if (claims.ContainsKey(recordId)) duplicates.Add(recordId);
                else claims[recordId] = zdo;
            }

            // Two NPCs claiming one record is a real duplication bug — surface it rather than
            // silently binding to whichever we happened to see first.
            foreach (var dup in duplicates)
                Plugin.Log?.LogError(
                    $"[villager_record_index] record {dup} is claimed by MORE THAN ONE NPC ZDO. " +
                    "Back-link bound to the first seen; investigate the duplicate villager.");

            var relinked = 0;
            foreach (var kv in claims)
            {
                var record = VillagerRecordTable.FindById(kv.Key);
                if (record == null) continue;
                if (record.NpcZdoId == kv.Value.m_uid) continue;

                Plugin.Log?.LogInfo(
                    $"[villager_record_index] re-linked {record.Name} ({record.RecordId}): " +
                    $"{record.NpcZdoId} -> {kv.Value.m_uid} (ZDO re-keyed by world load)");
                record.NpcZdoId = kv.Value.m_uid;
                relinked++;
            }

            return relinked;
        }

    }
}
