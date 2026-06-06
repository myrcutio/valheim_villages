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
    ///     lazily; this makes the whole roster (and nav's bed enumeration) consistent
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
                var bedPos = zdo.GetVec3("vv_bed_position", Vector3.zero);
                // Resolve (never mint) the village; skip migration if none resolves
                // (villages are created only at a registry station).
                var stamped = zdo.GetString(Village.IdKey);
                var villageId = !string.IsNullOrEmpty(stamped)
                    ? stamped
                    : (VillageRegistry.GetVillageCovering(bedPos) ?? VillageRegistry.FindNearAnchor(bedPos))?.VillageId;
                if (string.IsNullOrEmpty(villageId))
                {
                    Plugin.Log?.LogWarning(
                        $"[villager_record_index] legacy villager '{name}' ({type}) at {bedPos} resolves to " +
                        "no village; not migrating.");
                    continue;
                }

                var record = VillagerRecordTable.Create(
                    type,
                    string.IsNullOrEmpty(name) ? type : name,
                    villageId, bedPos, RecordStatus.Alive, zdo.m_uid);
                if (record == null) continue;
                zdo.Set("vv_record_id", record.RecordId);
                zdo.Set(Village.IdKey, villageId);
                migrated++;
            }

            // Prune bogus Alive records whose NPC vanished (hot-reload artifacts, or
            // villagers removed without a clean death) so the roster reflects reality.
            var pruned = VillagerRecordTable.Reconcile();

            var total = VillagerRecordTable.EnumerateAll().Count();
            Plugin.Log?.LogInfo(
                $"[villager_record_index] records={total} migrated={migrated} pruned={pruned}");
            activityLog.Record(task.SourceId, TaskName, "index",
                $"records={total} migrated={migrated} pruned={pruned}");

            return TaskResult.Ok(new Dictionary<string, string>
            {
                { "records_total", total.ToString() },
                { "migrated", migrated.ToString() },
                { "pruned", pruned.ToString() },
            });
        }
    }
}
