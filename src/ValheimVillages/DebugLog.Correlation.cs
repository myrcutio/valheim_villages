using System;

namespace ValheimVillages
{
    internal static partial class DebugLog
    {
        /// <summary>
        ///     Compact correlation token: first 8 hex chars of a GUID.
        ///     Use as the value of a `vid=` key on every line referring to a
        ///     villager, so a single grep can trace one NPC end-to-end without
        ///     dragging full 36-char UUIDs through the log.
        ///     Plugin.Log.LogInfo($"[VillagerAI] state_change vid={DebugLog.Vid(npcId)} from=patrol to=tidy");
        /// </summary>
        public static string Vid(Guid id)
        {
            // GUID "N" format = 32 hex chars, no dashes. Take first 8.
            var s = id.ToString("N");
            return s.Length >= 8 ? s.Substring(0, 8) : s;
        }

        /// <summary>
        ///     Same as Vid(Guid) but accepts a string GUID. Tolerant of dashed
        ///     or undashed input; returns "unknown" if parse fails.
        /// </summary>
        public static string Vid(string id)
        {
            if (string.IsNullOrEmpty(id)) return "unknown";
            if (Guid.TryParse(id, out var g)) return Vid(g);
            // Fall back: strip dashes and take first 8 hex-ish chars.
            var stripped = id.Replace("-", "");
            return stripped.Length >= 8 ? stripped.Substring(0, 8) : stripped;
        }

        /// <summary>
        ///     Compact correlation token for a queued task: name + short hash of
        ///     the task instance (or numeric id if provided). Companion to Vid().
        /// </summary>
        public static string Tid(string taskName, int instanceId)
        {
            return (taskName ?? "task") + "#" + instanceId;
        }
    }
}