using ValheimVillages.Schemas;

namespace ValheimVillages.TaskQueue
{
    /// <summary>
    ///     Optional contract for task handlers that must not run until the engine /
    ///     world is in a particular state. When a handler implements this, the queue
    ///     calls <see cref="IsReady" /> before dispatching; a false result defers the
    ///     task (re-enqueued with backoff) instead of running it. This is distinct from
    ///     <see cref="VillagerTask.NotBefore" /> (a fixed wall-clock delay): readiness
    ///     is a live precondition re-evaluated each drain, so a task waits exactly as
    ///     long as the world takes to settle and no longer.
    /// </summary>
    public interface ITaskPrecondition
    {
        /// <summary>
        ///     Return true when the handler can run <paramref name="task" /> now. Return
        ///     false to defer it. Must be cheap and side-effect free — it is polled.
        /// </summary>
        bool IsReady(VillagerTask task);
    }
}
