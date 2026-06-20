using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Diagnostic capture for villager despawns. The dedicated server's BepInEx
    ///     <c>LogOutput.log</c> isn't file-accessible and the AI event ring misses a
    ///     villager that's destroyed before it ticks, so this records the two destroy
    ///     funnels into a queryable ring buffer:
    ///     <list type="bullet">
    ///       <item><b>ZDO destroyed</b> — Harmony prefix on <c>ZDOMan.DestroyZDO</c>
    ///         (the OWNER's destroy funnel), WITH the caller stack trace, so we can see
    ///         exactly which code path tore the villager down.</item>
    ///       <item><b>GameObject destroyed</b> — pushed from <c>VillagerAI.OnDestroy</c>
    ///         so we can tell a real ZDO destroy from a mere out-of-area instance removal
    ///         (instance gone but no DestroyZDO entry = the ZDO was flung out of this
    ///         peer's active area, not destroyed).</item>
    ///     </list>
    ///     Read it over MCP with <c>vv_despawns</c> on each peer. Pure diagnostic —
    ///     remove once the despawn cause is understood.
    /// </summary>
    public static class DespawnRecorder
    {
        private readonly struct Entry
        {
            public readonly float Time;
            public readonly string Summary;
            public readonly string Stack;

            public Entry(float time, string summary, string stack)
            {
                Time = time;
                Summary = summary;
                Stack = stack;
            }
        }

        private const int Capacity = 40;
        private static readonly List<Entry> s_entries = new();

        /// <summary>Record an event with the current call stack.</summary>
        public static void Record(string summary)
        {
            s_entries.Add(new Entry(Time.realtimeSinceStartup, summary, Environment.StackTrace));
            if (s_entries.Count > Capacity) s_entries.RemoveAt(0);
        }

        [HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.DestroyZDO))]
        private static class DestroyZdoCapture
        {
            [HarmonyPrefix]
            private static void Prefix(ZDO zdo)
            {
                if (zdo == null) return;
                // Cheap gate: only villager NPCs / record carriers carry vv_record_id.
                var recordId = zdo.GetString("vv_record_id");
                if (string.IsNullOrEmpty(recordId)) return;

                var pos = zdo.GetPosition();
                var sector = zdo.GetSector();
                var isServer = ZNet.instance != null && ZNet.instance.IsServer();
                Record(
                    $"ZDO DESTROY uid={zdo.m_uid} prefab={zdo.GetPrefab()} record={recordId} " +
                    $"pos=({pos.x:F1},{pos.y:F1},{pos.z:F1}) sector=({sector.x},{sector.y}) " +
                    $"owner={zdo.GetOwner()} session={ZDOMan.GetSessionID()} isServer={isServer} " +
                    $"isOwner={zdo.IsOwner()}");
            }
        }

        [DevCommand("Dump recent villager ZDO/GameObject destroy events with stack traces", Name = "vv_despawns")]
        public static void Dump()
        {
            var now = Time.realtimeSinceStartup;
            var sb = new StringBuilder();
            sb.AppendLine($"[vv_despawns] {s_entries.Count} captured event(s) (oldest first):");
            foreach (var e in s_entries)
            {
                sb.AppendLine($"  -{now - e.Time:F1}s  {e.Summary}");
                var shown = 0;
                foreach (var raw in e.Stack.Split('\n'))
                {
                    var t = raw.Trim();
                    if (t.Length == 0) continue;
                    // Skip the framework frames that lead into the capture itself.
                    if (t.Contains("DespawnRecorder") || t.Contains("Environment.get_StackTrace")) continue;
                    sb.AppendLine($"        {t}");
                    if (++shown >= 12) break;
                }
            }

            global::Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }
    }
}
