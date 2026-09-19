using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.Villager;
using ValheimVillages.Villager.Records;
using VillagerComponent = ValheimVillages.Villager.Villager;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    ///     Deferred re-try of a villager graft that arrived before its record did.
    ///     <para>
    ///         An NPC is turned into a villager by <see cref="VillagerRestoration.Restore" />
    ///         from <c>ZNetView.Awake</c> — a ONE-SHOT hook. Restore needs the NPC's record,
    ///         which lives in a free-standing <c>vv_villager_record</c> carrier ZDO that
    ///         <see cref="VillagerRecordTable.FindById" /> looks up by scanning
    ///         <c>ZDOMan.m_objectsByID</c>.
    ///     </para>
    ///     <para>
    ///         On a HOST that lookup can never miss: <c>ZDOMan.Load</c> populates
    ///         <c>m_objectsByID</c> with every persisted ZDO before anything is instantiated.
    ///         On a CLIENT it misses constantly — ZDOs stream in from the server in no
    ///         particular order, mixed into the same flood as the village's ~1500 piece ZDOs,
    ///         so an NPC whose ZDO lands before its record carrier found no record and was
    ///         skipped FOREVER. The Dvergr then stood in the village as an un-grafted native
    ///         mage (native name, native AI, no dialog) while the host happily simulated the
    ///         very same villager. That is the "my villagers turned back into Dvergr mages
    ///         after a dedicated-server reconnect" bug.
    ///     </para>
    ///     <para>
    ///         Waiting is the correct response to "not replicated yet", so this uses the same
    ///         deferred-precondition/backoff machinery as <see cref="VillagerSettleHandler" />
    ///         rather than a silent skip: the task parks until the record carrier shows up,
    ///         then grafts. A back-reference that is still dangling after
    ///         <see cref="RecordArrivalGraceSeconds" /> is a genuine invariant violation, so it
    ///         is reported as a loud error instead of being papered over.
    ///     </para>
    /// </summary>
    [RegisterTaskHandler]
    public class VillagerRestoreHandler : ITaskHandlerWithLog, ITaskPrecondition
    {
        public const string TaskNameConst = "villager_restore";

        /// <summary>
        ///     How long a pending graft waits for its record carrier to replicate before the
        ///     back-reference is declared dangling. Generous relative to ZDO replication (which
        ///     completes in seconds even behind a full village's worth of piece ZDOs), so
        ///     expiry means "this record does not exist", not "the network was slow".
        /// </summary>
        private const float RecordArrivalGraceSeconds = 60f;

        /// <summary>
        ///     NPC views awaiting their record, keyed by the record id they claim. Always
        ///     overwritten on re-enqueue: a zone round-trip destroys the Dvergr GameObject and
        ///     re-instantiates a fresh one for the same record, and the pending task must graft
        ///     the object that is actually in the world, not the destroyed one.
        /// </summary>
        private static readonly Dictionary<string, ZNetView> s_pending = new();

        public string TaskName => TaskNameConst;

        /// <summary>
        ///     Park <paramref name="nview" /> until the record it claims replicates.
        ///     Deduped by (name, recordId) in the queue; the pending view is refreshed
        ///     regardless so the newest object wins.
        /// </summary>
        public static void Enqueue(string recordId, ZNetView nview)
        {
            if (string.IsNullOrEmpty(recordId) || nview == null) return;

            s_pending[recordId] = nview;

            GlobalTaskQueue.Enqueue(new VillagerTask
            {
                Name = TaskNameConst,
                SourceId = recordId,
                Priority = TaskPriority.High,
                // The grace window is enforced in IsReady/Handle so the handler itself gets to
                // report a dangling back-reference. Keep the queue's own timeout out of the way
                // rather than letting it drop the task silently before Handle ever runs.
                TimeoutSeconds = float.MaxValue,
                Attributes = new Dictionary<string, string>(),
            });
        }

        /// <summary>
        ///     Ready once the record has replicated — or once there is nothing left to wait
        ///     for (object gone, already grafted, grace expired), so the task always completes
        ///     instead of spinning. Cheap and side-effect free, as the contract requires.
        /// </summary>
        public bool IsReady(VillagerTask task)
        {
            if (!s_pending.TryGetValue(task.SourceId, out var nview) || nview == null)
                return true; // despawned / zone-unloaded while waiting — let Handle no-op

            if (nview.GetComponent<VillagerComponent>() != null)
                return true; // grafted by another path already

            if (VillagerRecordTable.FindById(task.SourceId) != null)
                return true; // the record arrived — graft now

            // Still missing: hold until the grace window expires, then let Handle report it.
            return Time.time - task.CreatedAt > RecordArrivalGraceSeconds;
        }

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            if (!s_pending.TryGetValue(task.SourceId, out var nview)) return TaskResult.Ok();
            s_pending.Remove(task.SourceId);

            if (nview == null) return TaskResult.Ok();
            if (nview.GetComponent<VillagerComponent>() != null) return TaskResult.Ok();

            var zdo = nview.GetZDO();
            if (zdo == null) return TaskResult.Ok();

            if (VillagerRecordTable.FindById(task.SourceId) == null)
            {
                Plugin.Log?.LogError(
                    $"[villager_restore] NPC at {nview.transform.position} claims record " +
                    $"'{task.SourceId}' but no record carrier ZDO appeared within " +
                    $"{RecordArrivalGraceSeconds:F0}s. It was left un-grafted (native Dvergr) — " +
                    "this is a dangling back-reference to investigate, NOT a cue to mint a " +
                    "replacement record.");
                return TaskResult.Ok();
            }

            var restored = VillagerRestoration.Restore(nview.gameObject, zdo);
            activityLog.Record(task.SourceId, TaskName, "deferred_restore",
                restored ? "grafted after record replicated" : "no-op");
            return TaskResult.Ok();
        }

        /// <summary>Drop pending views on world unload / hot reload (they are about to die).</summary>
        [RegisterCleanup]
        public static void Clear()
        {
            s_pending.Clear();
        }
    }
}
