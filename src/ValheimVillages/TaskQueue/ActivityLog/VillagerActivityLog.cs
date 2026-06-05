using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;

namespace ValheimVillages.TaskQueue.ActivityLog
{
    /// <summary>
    ///     Singleton per-villager activity/audit log for debugging. Records state-changing
    ///     actions taken by task handlers. Entries are persisted to ZDO on save; the log
    ///     is bounded and does not support replay.
    /// </summary>
    public class VillagerActivityLog
    {
        private const string ZdoKey = "vv_wal";
        private const char FieldSeparator = '\x1F'; // unit separator
        private const char EntrySeparator = '\x1E'; // record separator

        private static VillagerActivityLog s_instance;

        // Per-villager log storage
        private readonly Dictionary<string, List<ActivityLogEntry>> m_logs = new();

        /// <summary>Singleton instance.</summary>
        public static VillagerActivityLog Instance => s_instance ??= new VillagerActivityLog();

        /// <summary>
        ///     Record a state-changing action for a villager.
        /// </summary>
        public void Record(string villagerId, string taskName, string action, string description)
        {
            if (string.IsNullOrEmpty(villagerId)) return;

            if (!m_logs.TryGetValue(villagerId, out var entries))
            {
                entries = new List<ActivityLogEntry>();
                m_logs[villagerId] = entries;
            }

            // Trim if over capacity before adding
            if (entries.Count >= TaskSettings.MaxActivityLogEntriesPerVillager)
                TrimOldest(entries);

            entries.Add(new ActivityLogEntry
            {
                Timestamp = Time.time,
                VillagerId = villagerId,
                TaskName = taskName,
                Action = action,
                Description = description,
                Committed = false,
            });

            Plugin.Log?.LogDebug(
                $"[ActivityLog:{villagerId}] {taskName}/{action}: {description}");
        }

        /// <summary>
        ///     Record a "blocked" entry for a villager, deduped by
        ///     (villagerId, taskName, itemPrefab, stationName, reason). Existing matching
        ///     entries are removed before the new one is appended so the Info-tab
        ///     "Work Issues" view doesn't grow unbounded as scans repeat.
        /// </summary>
        public void RecordBlocked(string villagerId, string taskName, string itemPrefab, string stationName, string reason, Vector3? workOrderPosition)
        {
            if (string.IsNullOrEmpty(villagerId)) return;

            if (!m_logs.TryGetValue(villagerId, out var entries))
            {
                entries = new List<ActivityLogEntry>();
                m_logs[villagerId] = entries;
            }

            entries.RemoveAll(e =>
                e.VillagerId == villagerId &&
                e.TaskName == taskName &&
                e.Action == "blocked" &&
                e.ItemPrefab == itemPrefab &&
                e.StationName == stationName &&
                e.Reason == reason);

            if (entries.Count >= TaskSettings.MaxActivityLogEntriesPerVillager)
                TrimOldest(entries);

            // Build the audit description from only the parts that are present, so
            // non-work-order blockers (e.g. patrol, which has no item/station) read
            // cleanly. Output is identical to "{item} [{station}] — {reason}" when
            // both item and station are set.
            var prefix = string.Empty;
            if (!string.IsNullOrEmpty(itemPrefab)) prefix = itemPrefab;
            if (!string.IsNullOrEmpty(stationName)) prefix += $" [{stationName}]";
            prefix = prefix.Trim();
            var description = string.IsNullOrEmpty(prefix) ? reason : $"{prefix} — {reason}";
            entries.Add(new ActivityLogEntry
            {
                Timestamp = Time.time,
                VillagerId = villagerId,
                TaskName = taskName,
                Action = "blocked",
                Description = description,
                ItemPrefab = itemPrefab,
                StationName = stationName,
                Reason = reason,
                WorkOrderPosX = workOrderPosition?.x,
                WorkOrderPosY = workOrderPosition?.y,
                WorkOrderPosZ = workOrderPosition?.z,
                Committed = false,
            });

            Plugin.Log?.LogDebug(
                $"[ActivityLog:{villagerId}] {taskName}/blocked: {description}");
        }

        /// <summary>
        ///     Get all log entries for a villager (read-only snapshot).
        /// </summary>
        public IReadOnlyList<ActivityLogEntry> GetEntries(string villagerId)
        {
            return m_logs.TryGetValue(villagerId, out var entries)
                ? entries
                : (IReadOnlyList<ActivityLogEntry>)new List<ActivityLogEntry>();
        }

        /// <summary>
        ///     Get only uncommitted entries for a villager.
        /// </summary>
        public List<ActivityLogEntry> GetUncommitted(string villagerId)
        {
            if (!m_logs.TryGetValue(villagerId, out var entries))
                return new List<ActivityLogEntry>();

            return entries.Where(e => !e.Committed).ToList();
        }

        /// <summary>
        ///     Mark all entries for a villager as committed.
        ///     Called after a successful ZDO save.
        /// </summary>
        public void MarkCommitted(string villagerId)
        {
            if (!m_logs.TryGetValue(villagerId, out var entries)) return;

            foreach (var entry in entries)
                entry.Committed = true;
        }

        /// <summary>
        ///     Remove committed entries, keeping the most recent ones for debugging.
        ///     Keeps the last 10 committed entries as a rolling history.
        /// </summary>
        public void TrimCommitted(string villagerId)
        {
            if (!m_logs.TryGetValue(villagerId, out var entries)) return;

            const int keepCommitted = 10;
            var committed = entries.Where(e => e.Committed).ToList();

            if (committed.Count <= keepCommitted) return;

            var removeCount = committed.Count - keepCommitted;
            var toRemove = new HashSet<ActivityLogEntry>(committed.Take(removeCount));
            entries.RemoveAll(e => toRemove.Contains(e));
        }

        /// <summary>
        ///     Serialize uncommitted activity log entries to a ZDO string.
        /// </summary>
        public void SaveToZDO(string villagerId, ZDO zdo)
        {
            if (zdo == null) return;

            var uncommitted = GetUncommitted(villagerId);
            if (uncommitted.Count == 0)
            {
                zdo.Set(ZdoKey, "");
                return;
            }

            var sb = new StringBuilder();
            for (var i = 0; i < uncommitted.Count; i++)
            {
                var e = uncommitted[i];
                if (i > 0) sb.Append(EntrySeparator);
                sb.Append(e.Timestamp.ToString("F2"));
                sb.Append(FieldSeparator);
                sb.Append(e.TaskName ?? "");
                sb.Append(FieldSeparator);
                sb.Append(e.Action ?? "");
                sb.Append(FieldSeparator);
                sb.Append(e.Description ?? "");
                sb.Append(FieldSeparator);
                sb.Append(e.ItemPrefab ?? "");
                sb.Append(FieldSeparator);
                sb.Append(e.StationName ?? "");
                sb.Append(FieldSeparator);
                sb.Append(e.Reason ?? "");
                sb.Append(FieldSeparator);
                sb.Append(e.WorkOrderPosX.HasValue ? e.WorkOrderPosX.Value.ToString(CultureInfo.InvariantCulture) : "");
                sb.Append(FieldSeparator);
                sb.Append(e.WorkOrderPosY.HasValue ? e.WorkOrderPosY.Value.ToString(CultureInfo.InvariantCulture) : "");
                sb.Append(FieldSeparator);
                sb.Append(e.WorkOrderPosZ.HasValue ? e.WorkOrderPosZ.Value.ToString(CultureInfo.InvariantCulture) : "");
            }

            zdo.Set(ZdoKey, sb.ToString());
        }

        /// <summary>
        ///     Load activity log entries from a ZDO string. Loaded entries start as uncommitted
        ///     so they'll be re-saved on the next ZDO save cycle.
        /// </summary>
        public void LoadFromZDO(string villagerId, ZDO zdo)
        {
            if (zdo == null) return;

            var data = zdo.GetString(ZdoKey);
            if (string.IsNullOrEmpty(data)) return;

            if (!m_logs.TryGetValue(villagerId, out var entries))
            {
                entries = new List<ActivityLogEntry>();
                m_logs[villagerId] = entries;
            }

            var records = data.Split(EntrySeparator);
            foreach (var record in records)
            {
                var fields = record.Split(FieldSeparator);
                if (fields.Length < 4) continue;

                if (!float.TryParse(fields[0], out var timestamp))
                    continue;

                float? posX = null, posY = null, posZ = null;
                if (fields.Length >= 10)
                {
                    if (float.TryParse(fields[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) posX = x;
                    if (float.TryParse(fields[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) posY = y;
                    if (float.TryParse(fields[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) posZ = z;
                }

                entries.Add(new ActivityLogEntry
                {
                    Timestamp = timestamp,
                    VillagerId = villagerId,
                    TaskName = fields[1],
                    Action = fields[2],
                    Description = fields[3],
                    ItemPrefab = fields.Length >= 7 ? fields[4] : null,
                    StationName = fields.Length >= 7 ? fields[5] : null,
                    Reason = fields.Length >= 7 ? fields[6] : null,
                    WorkOrderPosX = posX,
                    WorkOrderPosY = posY,
                    WorkOrderPosZ = posZ,
                    Committed = false,
                });
            }

            Plugin.Log?.LogDebug(
                $"[ActivityLog:{villagerId}] Loaded {records.Length} entries from ZDO");
        }

        /// <summary>
        ///     Remove all entries for a villager (e.g. on villager death/removal).
        /// </summary>
        public void ClearVillager(string villagerId)
        {
            m_logs.Remove(villagerId);
        }

        /// <summary>
        ///     Clear all activity log data (e.g. on world unload).
        /// </summary>
        public void Clear()
        {
            m_logs.Clear();
        }

        /// <summary>
        ///     Reset the singleton instance (e.g. on hot reload so the new
        ///     assembly gets a fresh instance).
        /// </summary>
        [RegisterCleanup]
        public static void ResetInstance()
        {
            s_instance?.Clear();
            s_instance = null;
        }

        /// <summary>
        ///     Trim oldest entries when a villager's log exceeds capacity.
        /// </summary>
        private static void TrimOldest(List<ActivityLogEntry> entries)
        {
            var removeCount = entries.Count - TaskSettings.MaxActivityLogEntriesPerVillager + 1;
            if (removeCount > 0)
                entries.RemoveRange(0, removeCount);
        }
    }
}