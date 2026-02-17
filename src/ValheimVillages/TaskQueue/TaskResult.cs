using System.Collections.Generic;

namespace ValheimVillages.TaskQueue
{
    /// <summary>
    /// Result returned by an ITaskHandler after processing a task.
    /// </summary>
    public class TaskResult
    {
        /// <summary>Whether the task completed successfully.</summary>
        public bool Success;

        /// <summary>Error message when Success is false; null otherwise.</summary>
        public string Error;

        /// <summary>Handler-specific output data, keyed by name.</summary>
        public Dictionary<string, string> Data;

        /// <summary>
        /// Optional typed payload for passing complex objects (e.g. WorkOrderContext)
        /// to callbacks without serializing to strings. Only valid during the
        /// synchronous callback invocation.
        /// </summary>
        public object Payload;

        /// <summary>Create a successful result with optional data.</summary>
        public static TaskResult Ok(Dictionary<string, string> data = null, object payload = null)
        {
            return new TaskResult { Success = true, Data = data, Payload = payload };
        }

        /// <summary>Create a failed result with an error message.</summary>
        public static TaskResult Fail(string error)
        {
            return new TaskResult { Success = false, Error = error };
        }
    }
}
