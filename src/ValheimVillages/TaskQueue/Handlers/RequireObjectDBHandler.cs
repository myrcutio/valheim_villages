using ValheimVillages.Attributes;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    ///     Runs a method annotated with <see cref="RequireObjectDBAttribute" /> once the
    ///     ObjectDB is alive. One task is enqueued per annotated method (SourceId = the
    ///     method key); this handler defers each via <see cref="ITaskPrecondition" /> until
    ///     ObjectDB is ready, then invokes it through <see cref="AttributeScanner" />.
    /// </summary>
    [RegisterTaskHandler]
    public class RequireObjectDBHandler : ITaskHandlerWithLog, ITaskPrecondition
    {
        public const string TaskNameConst = "require_objectdb";

        public string TaskName => TaskNameConst;

        /// <summary>
        ///     Ready once the ObjectDB is populated AND the scene exists. A populated
        ///     ObjectDB guarantees ItemFactory.RegisterAll has run (so custom item prefabs
        ///     exist) and that vanilla prefabs the annotated methods depend on — the Hammer
        ///     and its build PieceTable, the Wood/Resin resource items — are present.
        ///     ZNetScene is also required because every current consumer mirrors a prefab
        ///     into ZNetScene; the task is enqueued at ZNetScene.Awake so this is normally
        ///     already true, and only the ObjectDB half is actually awaited on the race.
        /// </summary>
        public bool IsReady(VillagerTask task)
        {
            return ObjectDB.instance != null
                   && ObjectDB.instance.m_items != null
                   && ObjectDB.instance.m_items.Count > 0
                   && ZNetScene.instance != null;
        }

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            return AttributeScanner.InvokeRequireObjectDB(task.SourceId)
                ? TaskResult.Ok()
                : TaskResult.Fail($"[RequireObjectDB] no method registered for key '{task.SourceId}'");
        }
    }
}
