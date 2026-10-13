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
        [DevCommand("Dump a prefab's transform hierarchy + meshes. " +
                    "Usage: vv_prefab_dump [name] [mat] — 'mat' prints only nodes that render, " +
                    "which is what you want when comparing how several prefabs look.",
            Name = "vv_prefab_dump")]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            var name = args.Args.Length >= 2 ? args.Args[1] : "piece_cartographytable";

            // A full creature dump is ~80 lines of finger bones. Comparing the look of seven
            // prefabs means 7 of those, so renderers-only is its own mode.
            var materialsOnly = args.Args.Length >= 3 &&
                                string.Equals(args.Args[2], "mat", System.StringComparison.OrdinalIgnoreCase);

            var scene = ZNetScene.instance;
            if (scene == null)
            {
                Print("[vv_prefab_dump] ZNetScene not ready");
                return;
            }

            // A trailing '*' dumps every match instead of one prefab. Comparing a family —
            // which of eleven banners is yellow, which pole is core wood — otherwise costs one
            // round trip per candidate.
            if (name.EndsWith("*"))
            {
                DumpMatching(scene, name.TrimEnd('*'), materialsOnly);
                return;
            }

            var prefab = scene.GetPrefab(name);
            if (prefab == null)
            {
                // Exact names are rarely guessable (feasts, saplings and station pieces all
                // use different conventions), so a miss lists what DOES match instead of
                // sending the caller back to guess again.
                // Items live in ObjectDB, not ZNetScene — beards, hair and other equipment are
                // invisible to a scene search, so a miss has to look in both.
                Print($"[vv_prefab_dump] prefab '{name}' not found. " +
                      $"Prefabs containing it: {Matching(scene, name)}. " +
                      $"ObjectDB items containing it: {MatchingItems(name)}");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[vv_prefab_dump] {name}");
            if (!materialsOnly) sb.AppendLine($"  root components: {RootComponents(prefab)}");
            Walk(prefab.transform, 0, sb, materialsOnly);
            Print(sb.ToString());
        }

        /// <summary>
        ///     What the prefab IS, at the root. Whether a thing is a build piece, a pickup, a
        ///     feast or several at once decides which villager behaviours are allowed to touch
        ///     it — a placed feast and a dropped apple are both "an ItemDrop in the world" to a
        ///     naive scan, and telling them apart needs this.
        /// </summary>
        private static string MatchingItems(string fragment)
        {
            var db = ObjectDB.instance;
            if (db?.m_items == null) return "(ObjectDB not ready)";

            var sb = new StringBuilder();
            var shown = 0;
            foreach (var item in db.m_items)
            {
                if (item == null) continue;
                if (item.name.IndexOf(fragment, System.StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                if (++shown > 40)
                {
                    sb.Append(", …");
                    break;
                }

                if (sb.Length > 0) sb.Append(", ");
                sb.Append(item.name);
            }

            return sb.Length > 0 ? sb.ToString() : "none";
        }

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

        private static void Walk(Transform t, int depth, StringBuilder sb, bool materialsOnly)
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

            var renders = false;

            var smr = t.GetComponent<SkinnedMeshRenderer>();
            if (smr != null && smr.sharedMesh != null)
            {
                renders = true;
                line += $"  [skinned:{smr.sharedMesh.name} verts:{smr.sharedMesh.vertexCount}]";
                // Creature bodies are SKINNED, so without this the one renderer that decides
                // how a villager looks was the only one whose material never printed.
                line += DescribeMaterials(smr.sharedMaterials);
            }

            var mr = t.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterials != null)
            {
                renders = true;
                line += DescribeMaterials(mr.sharedMaterials);
                var b = mr.bounds.size;
                line += $"  size=({b.x:F2},{b.y:F2},{b.z:F2})";
            }

            // In materials-only mode a node earns a line solely by rendering something;
            // everything else is skipped so the surviving lines line up across prefabs.
            if (!materialsOnly || renders) sb.AppendLine(line);

            for (var i = 0; i < t.childCount; i++)
                Walk(t.GetChild(i), depth + 1, sb, materialsOnly);
        }

        /// <summary>
        ///     Tint properties worth knowing about on a creature body, in the order we would
        ///     prefer to drive. Reported per material because which one exists decides how a
        ///     villager can be recoloured at all — <c>VisEquipment.SetSkinColor</c> is not an
        ///     option on a non-player, so the property has to be written directly.
        /// </summary>
        private static readonly string[] TintProperties =
            { "_SkinColor", "_Color", "_Hue", "_Saturation", "_Value", "_EmissionColor" };

        /// <summary>
        ///     Material name, shader and available tint properties. Names lie about colour
        ///     (Valheim's Guck banner material is called "OrangeGrey"), so the texture and the
        ///     shader are the better clues and all three get printed.
        /// </summary>
        private static string DescribeMaterials(Material[] materials)
        {
            if (materials == null || materials.Length == 0) return "";

            var mats = new StringBuilder();
            foreach (var m in materials)
            {
                if (mats.Length > 0) mats.Append(" | ");
                if (m == null)
                {
                    mats.Append("null");
                    continue;
                }

                mats.Append(m.name);
                if (m.shader != null) mats.Append($" shader={m.shader.name}");

                var tex = m.HasProperty("_MainTex") ? m.mainTexture : null;
                if (tex != null) mats.Append($" tex={tex.name}");

                var tints = new StringBuilder();
                foreach (var prop in TintProperties)
                {
                    if (!m.HasProperty(prop)) continue;
                    if (tints.Length > 0) tints.Append(',');
                    tints.Append(prop);
                }

                mats.Append(tints.Length > 0 ? $" tint=[{tints}]" : " tint=(none)");

                // "tint=(none)" above only means none of OUR candidate names matched, which is
                // a guess about the shader, not a fact about it. Enumerate what the shader
                // actually exposes so the conclusion rests on the shader itself.
                mats.Append(ColorInputs(m));
            }

            return $"  [mat: {mats}]";
        }

        /// <summary>
        ///     Every Color- or Vector-typed input the shader declares, with its current value.
        ///     These are the only things that could recolour a mesh without swapping its
        ///     texture, so this is the definitive answer to "can this material be tinted".
        /// </summary>
        private static string ColorInputs(Material m)
        {
            if (m.shader == null) return "";

            var found = new StringBuilder();
            var count = m.shader.GetPropertyCount();
            for (var i = 0; i < count; i++)
            {
                var type = m.shader.GetPropertyType(i);
                if (type != UnityEngine.Rendering.ShaderPropertyType.Color &&
                    type != UnityEngine.Rendering.ShaderPropertyType.Vector)
                    continue;

                var prop = m.shader.GetPropertyName(i);
                if (found.Length > 0) found.Append(' ');
                found.Append($"{prop}={m.GetColor(prop)}");
            }

            // Float/range inputs too: gloss and metallic live there, and guessing their names
            // ("_Glossiness"? "_Smoothness"? "_Metallic"?) is how a shiny blanket survives three
            // attempts to matte it.
            var floats = new StringBuilder();
            for (var i = 0; i < count; i++)
            {
                var type = m.shader.GetPropertyType(i);
                if (type != UnityEngine.Rendering.ShaderPropertyType.Float &&
                    type != UnityEngine.Rendering.ShaderPropertyType.Range)
                    continue;

                var prop = m.shader.GetPropertyName(i);
                if (floats.Length > 0) floats.Append(' ');
                floats.Append($"{prop}={m.GetFloat(prop):F2}");
            }

            var colorPart = found.Length > 0 ? $" colorInputs=[{found}]" : " colorInputs=(none)";
            var floatPart = floats.Length > 0 ? $" floatInputs=[{floats}]" : "";
            return colorPart + floatPart;
        }

        /// <summary>Dump every prefab whose name contains <paramref name="needle" />.</summary>
        private static void DumpMatching(ZNetScene scene, string needle, bool materialsOnly)
        {
            var sb = new StringBuilder();
            var shown = 0;
            foreach (var prefab in scene.m_prefabs)
            {
                if (prefab == null) continue;
                if (prefab.name.IndexOf(needle, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (shown >= MaxWildcardMatches) break;

                sb.AppendLine($"- {prefab.name}");
                Walk(prefab.transform, 1, sb, materialsOnly);
                shown++;
            }

            if (shown == 0) sb.AppendLine($"[vv_prefab_dump] nothing matches '{needle}'");
            Print(sb.ToString().TrimEnd());
        }

        /// <summary>Cap on a wildcard dump, so a broad needle cannot flood the console.</summary>
        private const int MaxWildcardMatches = 16;

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
