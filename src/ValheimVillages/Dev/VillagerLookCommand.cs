using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Inspect a LIVE villager's model: what is equipped, what props are grafted onto its
    ///     bones and with what local orientation, and which materials its body renders with.
    ///
    ///     <para>Separate from <c>vv_prefab_dump</c>, which reads ZNetScene PREFABS. Equipment is
    ///     attached at runtime by <see cref="VisEquipment" />, so a held item appears in neither
    ///     the prefab nor a prefab dump — the only place to see it is an instance.</para>
    /// </summary>
    internal static class VillagerLookCommand
    {
        [DevCommand("Inspect a live villager's model: held items, grafted props, body materials. " +
                    "Usage: vv_villager_look [villagerName]",
            Name = "vv_villager_look")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var wanted = args != null && args.Length > 1 ? args[1] : null;

            var bridges = Object.FindObjectsByType<UI.Interaction.VillagerBehaviorBridge>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            if (bridges.Length == 0)
            {
                Print("[vv_villager_look] no villagers loaded on this peer");
                return;
            }

            var sb = new StringBuilder();
            foreach (var bridge in bridges)
            {
                if (bridge == null) continue;

                var record = Villager.Records.VillagerRecordTable.FindById(bridge.UniqueId);
                var name = record != null ? record.Name : bridge.VillagerType;
                if (!string.IsNullOrEmpty(wanted) &&
                    name.IndexOf(wanted, System.StringComparison.OrdinalIgnoreCase) < 0 &&
                    (bridge.VillagerType == null ||
                     bridge.VillagerType.IndexOf(wanted, System.StringComparison.OrdinalIgnoreCase) < 0))
                    continue;

                var at = bridge.transform.position;
                sb.AppendLine(
                    $"  {name} ({bridge.VillagerType}) at " +
                    $"({at.x:F1},{at.z:F1},{at.y:F1}) [x,z,y]");

                // VisEquipment's own slots are private hashes; the public Humanoid properties
                // name the items, and the hierarchy walk below shows what actually got attached.
                var humanoid = bridge.GetComponent<Humanoid>();
                if (humanoid != null)
                    sb.AppendLine(
                        $"    right='{ItemName(humanoid.RightItem)}' " +
                        $"left='{ItemName(humanoid.LeftItem)}'");

                // Beard/hair are stored on the ZDO as name hashes. Resolving them against
                // ObjectDB is the only way to learn what a villager that HAS a beard is
                // actually wearing — the prefab ships none, and no item is named "beard".
                var view = bridge.GetComponent<ZNetView>();
                var zdo = view != null && view.IsValid() ? view.GetZDO() : null;
                if (zdo != null)
                {
                    var beard = zdo.GetInt(ZDOVars.s_beardItem);
                    var hair = zdo.GetInt(ZDOVars.s_hairItem);
                    sb.AppendLine(
                        $"    beard={beard} '{ResolveItem(beard)}' noBeard={zdo.GetBool(ZDOVars.s_noBeard)} " +
                        $"hair={hair} '{ResolveItem(hair)}'");
                }

                // Where the engine itself mounts things. A holstered prop belongs on one of
                // these, not on a raw bone: the item's attach_back offsets are authored against
                // them, which is why hanging one off the hip bone buried it in the body.
                var vis = bridge.GetComponent<VisEquipment>();
                if (vis != null)
                    sb.AppendLine(
                        $"    mounts: rightHand={Mount(vis.m_rightHand)} " +
                        $"leftHand={Mount(vis.m_leftHand)} helmet={Mount(vis.m_helmet)} " +
                        $"backShield={Mount(vis.m_backShield)} backMelee={Mount(vis.m_backMelee)} " +
                        $"backTwoHand={Mount(vis.m_backTwohandedMelee)} " +
                        $"backBow={Mount(vis.m_backBow)} backTool={Mount(vis.m_backTool)} " +
                        $"backAtgeir={Mount(vis.m_backAtgeir)}");

                // Anything that RENDERS is either the body or a grafted prop. Bones render
                // nothing, so this skips the ~70 finger joints a full dump would print.
                Walk(bridge.transform, bridge.transform, sb);
            }

            Print(sb.Length > 0
                ? "[vv_villager_look]\n" + sb.ToString().TrimEnd()
                : $"[vv_villager_look] no villager matched '{wanted}'");
        }

        private static void Walk(Transform t, Transform root, StringBuilder sb)
        {
            var skinned = t.GetComponent<SkinnedMeshRenderer>();
            var mesh = t.GetComponent<MeshRenderer>();

            if (skinned != null || mesh != null)
            {
                var renderer = skinned != null ? (Renderer)skinned : mesh;
                var parent = t.parent != null ? t.parent.name : "-";
                sb.AppendLine(
                    $"    {Path(t, root)} " +
                    // Whether a replaced prop is actually hidden is the whole point of the swap,
                    // and it is invisible in a transform dump without this.
                    $"[{(skinned != null ? "skinned" : "mesh")}:{(renderer.enabled ? "on" : "OFF")}] " +
                    $"parent={parent} " +
                    $"localPos=({t.localPosition.x:F3},{t.localPosition.y:F3},{t.localPosition.z:F3}) " +
                    $"localRot=({t.localEulerAngles.x:F1},{t.localEulerAngles.y:F1},{t.localEulerAngles.z:F1}) " +
                    $"localScale=({t.localScale.x:F3},{t.localScale.y:F3},{t.localScale.z:F3}) " +
                    // Bones sit under a 100x armature, so local scale says nothing about how big
                    // a grafted prop actually draws. World scale is the number that matters.
                    $"worldScale=({t.lossyScale.x:F3},{t.lossyScale.y:F3},{t.lossyScale.z:F3})");

                var mats = renderer.sharedMaterials;
                if (mats != null)
                    for (var i = 0; i < mats.Length; i++)
                        sb.AppendLine(
                            $"      mat[{i}] {(mats[i] != null ? mats[i].name : "null")} " +
                            $"shader={(mats[i] != null && mats[i].shader != null ? mats[i].shader.name : "-")}");
            }

            // Emissive extras carry no mesh, so the renderer walk above never shows them —
            // and an upgrade glow left running on a held tool is exactly the kind of thing
            // that is obvious on screen and invisible in a dump.
            if (t.GetComponent<ParticleSystem>() != null || t.GetComponent<Light>() != null)
                sb.AppendLine(
                    $"    {Path(t, root)} [fx:{(t.gameObject.activeInHierarchy ? "ON" : "off")}]");

            for (var i = 0; i < t.childCount; i++)
                Walk(t.GetChild(i), root, sb);
        }

        /// <summary>Name of the ObjectDB item with this stable hash, if any.</summary>
        private static string ResolveItem(int hash)
        {
            if (hash == 0) return "-";

            var db = ObjectDB.instance;
            if (db?.m_items == null) return "(ObjectDB not ready)";

            foreach (var item in db.m_items)
                if (item != null && item.name.GetStableHashCode() == hash)
                    return item.name;

            return "(unknown)";
        }

        private static string Mount(Transform t)
        {
            return t == null ? "-" : t.name;
        }

        private static string Path(Transform t, Transform root)
        {
            var parts = t.name;
            var cursor = t.parent;
            while (cursor != null && cursor != root)
            {
                parts = cursor.name + "/" + parts;
                cursor = cursor.parent;
            }

            return parts;
        }

        private static string ItemName(ItemDrop.ItemData item)
        {
            return item?.m_dropPrefab != null ? item.m_dropPrefab.name : "-";
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
