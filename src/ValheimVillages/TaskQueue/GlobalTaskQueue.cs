using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Core.Attributes;
using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.TaskQueue
{
    /// <summary>
    /// Global singleton task queue with tiered priority levels.
    /// Each priority tier has its own FIFO queue. The priority value (1-3)
    /// determines how many messages that tier can pop per tick.
    /// When higher tiers are empty, unused capacity backfills to lower tiers.
    /// </summary>
    public static class GlobalTaskQueue
    {
        // One FIFO queue per priority tier
        private static readonly Dictionary<TaskPriority, Queue<VillagerTask>> s_queues = new()
        {
            { TaskPriority.High, new Queue<VillagerTask>() },
            { TaskPriority.Medium, new Queue<VillagerTask>() },
            { TaskPriority.Low, new Queue<VillagerTask>() }
        };

        // Priority tiers in descending order for iteration
        private static readonly TaskPriority[] s_tiersDescending =
            { TaskPriority.High, TaskPriority.Medium, TaskPriority.Low };

        // O(1) dedup: tracks pending (Name, SourceId) pairs
        private static readonly HashSet<(string, string)> s_pendingKeys = new();

        // Dead letter queue for tasks that exhausted retries
        private static readonly List<VillagerTask> s_deadLetters = new();

        /// <summary>Read-only access to the dead letter queue for debug inspection.</summary>
        public static IReadOnlyList<VillagerTask> DeadLetters => s_deadLetters;

        /// <summary>
        /// Enqueue a task into the appropriate priority tier.
        /// Silently drops duplicates (same Name + SourceId already pending).
        /// </summary>
        public static bool Enqueue(VillagerTask task)
        {
            if (task == null) return false;

            var key = (task.Name, task.SourceId);
            if (s_pendingKeys.Contains(key))
            {
                Plugin.Log?.LogDebug(
                    $"[TaskQueue] Dedup: skipping duplicate {task.Name} for {task.SourceId}");
                return false;
            }

            if (!s_queues.ContainsKey(task.Priority))
            {
                Plugin.Log?.LogWarning(
                    $"[TaskQueue] Unknown priority {task.Priority}, dropping task {task.Name}");
                return false;
            }

            task.CreatedAt = Time.time;
            s_queues[task.Priority].Enqueue(task);
            s_pendingKeys.Add(key);
            return true;
        }

        /// <summary>
        /// Process a batch of tasks. Called once per frame from Plugin.Update().
        /// First pass: each tier gets its guaranteed allocation (priority value).
        /// Second pass: backfill lower tiers if total processed is under the
        /// minimum throughput floor (highest priority value = 3).
        /// </summary>
        public static void ProcessBatch()
        {
            int totalProcessed = 0;
            int minThroughput = (int)s_tiersDescending[0]; // 3

            // First pass: guaranteed allocation per tier
            foreach (var tier in s_tiersDescending)
            {
                int allocation = (int)tier;
                int processed = DrainQueue(tier, allocation);
                totalProcessed += processed;
            }

            // Second pass: backfill if under minimum throughput
            if (totalProcessed < minThroughput)
            {
                int remaining = minThroughput - totalProcessed;
                foreach (var tier in s_tiersDescending)
                {
                    if (remaining <= 0) break;
                    int processed = DrainQueue(tier, remaining);
                    totalProcessed += processed;
                    remaining -= processed;
                }
            }
        }

        /// <summary>
        /// Drain up to maxCount tasks from the specified tier's queue.
        /// Returns the number of tasks successfully processed (not timed out).
        /// </summary>
        private static int DrainQueue(TaskPriority tier, int maxCount)
        {
            var queue = s_queues[tier];
            int processed = 0;

            while (processed < maxCount && queue.Count > 0)
            {
                var task = queue.Dequeue();
                RemovePendingKey(task);

                // Check timeout
                if (Time.time - task.CreatedAt > task.TimeoutSeconds)
                {
                    Plugin.Log?.LogWarning(
                        $"[TaskQueue] Task '{task.Name}' for {task.SourceId} timed out " +
                        $"after {Time.time - task.CreatedAt:F1}s (limit {task.TimeoutSeconds}s)");
                    // Timed-out tasks don't count against allocation
                    continue;
                }

                // Look up handler
                var handler = TaskHandlerRegistry.Get(task.Name);
                if (handler == null)
                {
                    Plugin.Log?.LogWarning(
                        $"[TaskQueue] No handler registered for '{task.Name}', dead-lettering");
                    AddToDeadLetters(task);
                    continue;
                }

                // Execute handler
                try
                {
                    var result = ((ITaskHandlerWithLog)handler).Handle(task, VillagerActivityLog.Instance);

                    if (result.Success)
                    {
                        task.Callback?.Invoke(result);
                        processed++;
                    }
                    else
                    {
                        HandleFailure(task, result.Error);
                        processed++;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log?.LogError(
                        $"[TaskQueue] Handler '{task.Name}' threw exception: {ex.Message}");
                    HandleFailure(task, ex.Message);
                    processed++;
                }
            }

            return processed;
        }

        /// <summary>
        /// Handle a failed task: retry or dead-letter.
        /// </summary>
        private static void HandleFailure(VillagerTask task, string error)
        {
            task.RetryCount++;

            if (task.RetryCount < TaskSettings.MaxRetries)
            {
                Plugin.Log?.LogDebug(
                    $"[TaskQueue] Retrying '{task.Name}' for {task.SourceId} " +
                    $"(attempt {task.RetryCount + 1}/{TaskSettings.MaxRetries}): {error}");

                // Re-enqueue at back of its tier (bypass dedup since key was removed)
                s_queues[task.Priority].Enqueue(task);
                s_pendingKeys.Add((task.Name, task.SourceId));
            }
            else
            {
                Plugin.Log?.LogWarning(
                    $"[TaskQueue] Dead-lettering '{task.Name}' for {task.SourceId} " +
                    $"after {task.RetryCount} retries: {error}");
                AddToDeadLetters(task);
            }
        }

        /// <summary>
        /// Add a task to the dead letter queue, evicting oldest if at capacity.
        /// </summary>
        private static void AddToDeadLetters(VillagerTask task)
        {
            if (s_deadLetters.Count >= TaskSettings.MaxDeadLetterSize)
                s_deadLetters.RemoveAt(0);

            s_deadLetters.Add(task);
        }

        /// <summary>
        /// Remove a task's dedup key from the pending set.
        /// </summary>
        private static void RemovePendingKey(VillagerTask task)
        {
            s_pendingKeys.Remove((task.Name, task.SourceId));
        }

        /// <summary>
        /// Get the total number of pending tasks across all tiers.
        /// </summary>
        public static int PendingCount =>
            s_queues.Values.Sum(q => q.Count);

        /// <summary>
        /// Get the pending count for a specific priority tier.
        /// </summary>
        public static int PendingCount_ForTier(TaskPriority tier) =>
            s_queues.TryGetValue(tier, out var q) ? q.Count : 0;

        /// <summary>
        /// Clear all queues, pending keys, and dead letters (e.g. on world unload).
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            foreach (var q in s_queues.Values)
                q.Clear();

            s_pendingKeys.Clear();
            s_deadLetters.Clear();
        }
    }
}
