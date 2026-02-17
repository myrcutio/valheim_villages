using System.Collections.Generic;
using ValheimVillages.Core.Attributes;

namespace ValheimVillages.TaskQueue
{
    /// <summary>
    /// Static registry that maps task names to their ITaskHandler implementations.
    /// Handlers are registered once at plugin startup.
    /// </summary>
    public static class TaskHandlerRegistry
    {
        private static readonly Dictionary<string, ITaskHandler> s_handlers = new();

        /// <summary>
        /// Register a handler. Uses the handler's TaskName as the key.
        /// </summary>
        public static void Register(ITaskHandler handler)
        {
            if (handler == null) return;

            if (s_handlers.ContainsKey(handler.TaskName))
            {
                Plugin.Log?.LogWarning(
                    $"[TaskRegistry] Replacing existing handler for '{handler.TaskName}'");
            }

            s_handlers[handler.TaskName] = handler;
            Plugin.Log?.LogDebug($"[TaskRegistry] Registered handler: {handler.TaskName}");
        }

        /// <summary>
        /// Look up a handler by task name. Returns null if not found.
        /// </summary>
        public static ITaskHandler Get(string taskName)
        {
            s_handlers.TryGetValue(taskName, out var handler);
            return handler;
        }

        /// <summary>
        /// Clear all registered handlers (e.g. on hot reload).
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            s_handlers.Clear();
        }
    }
}
