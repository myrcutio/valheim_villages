using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Dev console command to dump a prefab's transform hierarchy with the
    ///     MeshFilter / MeshRenderer on each node. Used to locate a specific child
    ///     mesh inside a vanilla prefab (e.g. the parchment map on
    ///     <c>piece_cartographytable</c>) so it can be extracted/reused.
    ///     Usage: <c>vv_prefab_dump [prefabName=piece_cartographytable]</c>
    /// </summary>
    internal static class PrefabDumpCommand
    {
        [DevCommand("Dump a prefab's transform hierarchy + meshes. Usage: vv_prefab_dump [name]",
            Name = "vv_prefab_dump")]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            var name = args.Args.Length >= 2 ? args.Args[1] : "piece_cartographytable";

            var scene = ZNetScene.instance;
            if (scene == null)
            {
                Print("[vv_prefab_dump] ZNetScene not ready");
                return;
            }

            var prefab = scene.GetPrefab(name);
            if (prefab == null)
            {
                Print($"[vv_prefab_dump] prefab '{name}' not found");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[vv_prefab_dump] {name}");
            Walk(prefab.transform, 0, sb);
            Print(sb.ToString());
        }

        private static void Walk(Transform t, int depth, StringBuilder sb)
        {
            var indent = new string(' ', depth * 2);
            var line = $"{indent}- {t.name}";

            var mf = t.GetComponent<MeshFilter>();
            if (mf != null && mf.sharedMesh != null)
                line += $"  [mesh:{mf.sharedMesh.name} verts:{mf.sharedMesh.vertexCount}]";

            var smr = t.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
                line += $"  [skinned:{smr.sharedMesh.name} verts:{smr.sharedMesh.vertexCount}]";

            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterials != null)
            {
                var mats = new StringBuilder();
                foreach (var m in mr.sharedMaterials)
                {
                    if (mats.Length > 0) mats.Append(',');
                    mats.Append(m != null ? m.name : "null");
                }
                var b = mr.bounds.size;
                line += $"  [mat:{mats}]  size=({b.x:F2},{b.y:F2},{b.z:F2})";
            }

            sb.AppendLine(line);

            for (var i = 0; i < t.childCount; i++)
                Walk(t.GetChild(i), depth + 1, sb);
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
