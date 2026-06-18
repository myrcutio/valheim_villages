using System;

namespace ValheimVillages.Attributes
{
    /// <summary>
    ///     Marks a static parameterless method that must not run until the
    ///     <see cref="ObjectDB" /> is alive (populated) in a loaded world.
    ///     <para>
    ///     <see cref="AttributeScanner.EnqueueObjectDBDependentTasks" /> enqueues a
    ///     high-priority <c>require_objectdb</c> task for each annotated method when the
    ///     scene comes up. The task's precondition (RequireObjectDBHandler.IsReady) polls
    ///     ObjectDB readiness and the queue defers the task with a ~1.5s backoff until it
    ///     is ready, then invokes the method.
    ///     </para>
    ///     <para>
    ///     Use this instead of an inline "ObjectDB not ready → bail" gate: a bail loses the
    ///     work for the whole session (the symptom that hid the Village Registry recipe when
    ///     <c>ZNetScene.Awake</c> won the race against <c>ObjectDB.Awake</c>), whereas a
    ///     deferred task is retried until the dependency is satisfied.
    ///     </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RequireObjectDBAttribute : Attribute
    {
    }
}
