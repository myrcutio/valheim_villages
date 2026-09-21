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
                // Exact names are rarely guessable (feasts, saplings and station pieces all
                // use different conventions), so a miss lists what DOES match instead of
                // sending the caller back to guess again.
                Print($"[vv_prefab_dump] prefab '{name}' not found. " +
                      $"Prefabs containing it: {Matching(scene, name)}");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[vv_prefab_dump] {name}");
            sb.AppendLine($"  root components: {RootComponents(prefab)}");
            Walk(prefab.transform, 0, sb);
            Print(sb.ToString());
        }

        /// <summary>
        ///     What the prefab IS, at the root. Whether a thing is a build piece, a pickup, a
        ///     feast or several at once decides which villager behaviours are allowed to touch
        ///     it — a placed feast and a dropped apple are both "an ItemDrop in the world" to a
        ///     naive scan, and telling them apart needs this.
        /// </summary>
        private static string Matching(ZNetScene scene, string fragment)
        {
            var sb = new StringBuilder();
            var shown = 0;
            foreach (var candidate in scene.m_prefabs)
            {
                if (candidate == null) continue;
                if (candidate.name.IndexOf(fragment, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (++shown > 40)
                {
                    sb.Append(", …");
                    break;
                }

                if (sb.Length > 0) sb.Append(", ");
                sb.Append(candidate.name);
            }

            return sb.Length > 0 ? sb.ToString() : "none";
        }

        private static string RootComponents(GameObject prefab)
        {
            var sb = new StringBuilder();
            foreach (var component in prefab.GetComponents<Component>())
            {
                if (component == null) continue;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(component.GetType().Name);
            }

            var drop = prefab.GetComponent<ItemDrop>();
            if (drop != null)
                sb.Append($"  [item='{drop.m_itemData?.m_shared?.m_name}' " +
                          $"type={drop.m_itemData?.m_shared?.m_itemType}]");

            var feast = prefab.GetComponent<Feast>();
            if (feast != null)
                sb.Append($"  [feast eatStacks={feast.m_eatStacks} " +
                          $"food='{(feast.m_foodItem != null ? feast.m_foodItem.name : "self")}']");

            var piece = prefab.GetComponent<Piece>();
            if (piece != null && piece.m_resources != null)
            {
                sb.Append("  [piece costs:");
                foreach (var req in piece.m_resources)
                    sb.Append($" {(req?.m_resItem != null ? req.m_resItem.name : "?")}x{req?.m_amount}" +
                              $"{(req != null && req.m_recover ? "(recover)" : "")}");
                sb.Append(']');
            }

            return sb.ToString();
        }

        private static void Walk(Transform t, int depth, StringBuilder sb)
        {
            var indent = new string(' ', depth * 2);
            var line = $"{indent}- {t.name}";

            // Local TRS, because rebuilding a look out of another prefab's parts means
            // reproducing how those parts sit relative to their parent — guessing a rotation
            // is how a banner's pole ends up lying on its side like a floating plank.
            if (depth > 0)
            {
                var p = t.localPosition;
                var e = t.localEulerAngles;
                var s = t.localScale;
                line += $"  [pos:({p.x:F2},{p.y:F2},{p.z:F2})";
                if (e != Vector3.zero) line += $" rot:({e.x:F0},{e.y:F0},{e.z:F0})";
                if (s != Vector3.one) line += $" scale:({s.x:F2},{s.y:F2},{s.z:F2})";
                line += "]";
            }

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
                    // Material NAMES lie about colour (Valheim's Guck banner is called
                    // "OrangeGrey"); the texture it samples is the better clue, so print both.
                    mats.Append(m != null ? m.name : "null");
                    var tex = m != null && m.HasProperty("_MainTex") ? m.mainTexture : null;
                    if (tex != null) mats.Append($"<tex:{tex.name}>");
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
