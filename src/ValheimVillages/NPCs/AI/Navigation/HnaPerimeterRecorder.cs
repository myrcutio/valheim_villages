using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using ValheimVillages.Core.Attributes;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Records the player's perimeter walk as a reference path for boundary pipeline testing.
    /// The player walks the exact path they want the guard to follow.
    /// Run via console: hna_perimeter_start / hna_perimeter_stop
    /// </summary>
    public class HnaPerimeterRecorder : MonoBehaviour
    {
        private const float SampleInterval = 0.2f;
        private const float MinMoveDist = 0.3f;
        private const string OutputPath =
            "/home/benny/Projects/valheim_villages/.cursor/hna_perimeter_path.json";

        private static HnaPerimeterRecorder s_instance;
        private static readonly List<Vector3> s_positions = new();
        private float m_timer;
        private Vector3 m_lastPos;

        [DevCommand("Start recording player's perimeter walk as reference path for pipeline testing", Name = "hna_perimeter_start")]
        public static void StartRecording()
        {
            if (s_instance != null)
            {
                Console.instance?.Print("Already recording perimeter. Use hna_perimeter_stop to stop.");
                return;
            }
            s_positions.Clear();
            var go = new GameObject("HnaPerimeterRecorder");
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<HnaPerimeterRecorder>();
            Console.instance?.Print(
                "Recording perimeter path. Walk the exact patrol route, then run: hna_perimeter_stop");
        }

        [DevCommand("Stop recording and save perimeter path to .cursor/hna_perimeter_path.json", Name = "hna_perimeter_stop")]
        public static void StopRecording()
        {
            if (s_instance == null)
            {
                Console.instance?.Print("Not recording. Use hna_perimeter_start first.");
                return;
            }
            Destroy(s_instance.gameObject);
            s_instance = null;
            SavePath();
        }

        private void Update()
        {
            m_timer -= Time.deltaTime;
            if (m_timer > 0f) return;
            m_timer = SampleInterval;

            var player = Player.m_localPlayer;
            if (player == null || player.transform == null) return;
            var pos = player.transform.position;
            if (s_positions.Count > 0 && Vector3.Distance(pos, m_lastPos) < MinMoveDist)
                return;
            s_positions.Add(pos);
            m_lastPos = pos;
        }

        private static void SavePath()
        {
            if (s_positions.Count == 0)
            {
                Console.instance?.Print("No positions recorded.");
                return;
            }

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"count\": {s_positions.Count},\n");
            sb.Append("  \"positions\": [\n");
            for (int i = 0; i < s_positions.Count; i++)
            {
                var p = s_positions[i];
                sb.Append($"    [{p.x:F2}, {p.y:F2}, {p.z:F2}]");
                sb.Append(i < s_positions.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ]\n");
            sb.Append("}\n");

            File.WriteAllText(OutputPath, sb.ToString());
            Console.instance?.Print($"Saved {s_positions.Count} perimeter positions to {OutputPath}");
            Plugin.Log?.LogInfo($"[HNA] Perimeter recording: {s_positions.Count} positions -> {OutputPath}");
        }
    }
}
