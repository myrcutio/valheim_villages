using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Pose a live villager's held tool:
    ///     <c>vv_tool &lt;villager&gt; [rx ry rz] [px py pz]</c>, or with no numbers just report
    ///     the current pose alongside the prop it replaced.
    ///
    ///     <para>Each item model is authored along its own axis, so the rotation that makes a
    ///     grafted tool sit in the hand like the original prop cannot be derived — it has to be
    ///     seen. Finding it by rebuilding costs a hot reload per guess; this costs a round trip,
    ///     and the number it lands on is what gets baked into
    ///     <c>VillagerAppearance.HeldToolRotationOffset</c>.</para>
    /// </summary>
    internal static class ToolPoseCommand
    {
        private const string GraftName = "VV_HeldTool";

        [DevCommand("Pose a live villager's held tool: vv_tool <villager> [rx ry rz] [px py pz] " +
                    "| vv_tool <villager> nudge <dx> <dy> <dz> (body-relative metres)",
            Name = "vv_tool")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            if (args == null || args.Length < 2)
            {
                Print("[vv_tool] usage: vv_tool <villager> [rx ry rz] [px py pz]");
                return;
            }

            var wanted = args[1];

            // Body-relative nudge: "down and to the left" is meaningful against the villager's
            // own axes, and meaningless against a hand bone's, which is rolled at an arbitrary
            // angle by the animation. Converting here is what keeps the tuning loop in the same
            // terms the eye uses.
            var nudge = args.Length >= 5 &&
                        string.Equals(args[2], "nudge", System.StringComparison.OrdinalIgnoreCase);

            float rx = 0f, ry = 0f, rz = 0f, px = 0f, py = 0f, pz = 0f;

            var hasRotation = args.Length >= 5 &&
                              float.TryParse(args[2], out rx) &&
                              float.TryParse(args[3], out ry) &&
                              float.TryParse(args[4], out rz);

            var hasPosition = args.Length >= 8 &&
                              float.TryParse(args[5], out px) &&
                              float.TryParse(args[6], out py) &&
                              float.TryParse(args[7], out pz);

            var bridges = Object.FindObjectsByType<UI.Interaction.VillagerBehaviorBridge>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);

            var hits = 0;
            foreach (var bridge in bridges)
            {
                if (bridge == null) continue;

                var record = Villager.Records.VillagerRecordTable.FindById(bridge.UniqueId);
                var name = record != null ? record.Name : bridge.VillagerType;
                if (!string.Equals(name, wanted, System.StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(bridge.VillagerType, wanted,
                        System.StringComparison.OrdinalIgnoreCase))
                    continue;

                hits++;

                var graft = FindDescendant(bridge.transform, GraftName);
                if (graft == null)
                {
                    Print($"[vv_tool] {name}: no {GraftName} grafted");
                    continue;
                }

                var mesh = Villager.VillagerAppearance.FindRenderedChild(graft);
                if (mesh == null)
                {
                    Print($"[vv_tool] {name}: {GraftName} draws nothing");
                    continue;
                }

                if (nudge)
                {
                    float nx = 0f, ny = 0f, nz = 0f;
                    float.TryParse(args[3], out nx);
                    float.TryParse(args[4], out ny);
                    if (args.Length >= 6) float.TryParse(args[5], out nz);

                    // right / up / forward of the villager, so negative x is its left and
                    // negative y is down, whatever the arm is doing.
                    var body = bridge.transform;
                    mesh.position += body.right * nx + body.up * ny + body.forward * nz;
                }
                else if (hasRotation)
                {
                    mesh.localRotation = Quaternion.Euler(rx, ry, rz);
                    if (hasPosition) mesh.localPosition = new Vector3(px, py, pz);
                }

                // The wrapper's rotation is half the answer: a mesh rotation only means the
                // same thing in both props if their wrappers share a frame.
                Print($"[vv_tool] {name}: wrapper {Rot(mesh.parent)} world {World(mesh)}");
                Print($"[vv_tool] {name}: {GraftName}/{mesh.name} " +
                      $"localRot=({mesh.localEulerAngles.x:F1},{mesh.localEulerAngles.y:F1}," +
                      $"{mesh.localEulerAngles.z:F1}) " +
                      $"localPos=({mesh.localPosition.x:F3},{mesh.localPosition.y:F3}," +
                      $"{mesh.localPosition.z:F3})");

                // The hidden original is the reference the pose is trying to match.
                var hand = mesh.parent != null ? mesh.parent.parent : null;
                if (hand == null) continue;
                for (var i = 0; i < hand.childCount; i++)
                {
                    var sibling = hand.GetChild(i);
                    if (!sibling.name.StartsWith("attach")) continue;

                    var original = Villager.VillagerAppearance.FindRenderedChild(sibling);
                    if (original == null) continue;

                    Print($"[vv_tool] {name}: replaced wrapper {Rot(original.parent)} " +
                          $"world {World(original)}");
                    Print($"[vv_tool] {name}: replaced {sibling.name}/{original.name} " +
                          $"localRot=({original.localEulerAngles.x:F1}," +
                          $"{original.localEulerAngles.y:F1},{original.localEulerAngles.z:F1}) " +
                          $"localPos=({original.localPosition.x:F3}," +
                          $"{original.localPosition.y:F3},{original.localPosition.z:F3})");
                }
            }

            if (hits == 0) Print($"[vv_tool] no villager matched '{wanted}'");
        }

        private static string Rot(Transform t)
        {
            return t == null
                ? "-"
                : $"{t.name}=({t.localEulerAngles.x:F1},{t.localEulerAngles.y:F1},{t.localEulerAngles.z:F1})";
        }

        private static string World(Transform t)
        {
            return $"({t.eulerAngles.x:F1},{t.eulerAngles.y:F1},{t.eulerAngles.z:F1})";
        }

        private static Transform FindDescendant(Transform root, string name)
        {
            if (root.name == name) return root;

            for (var i = 0; i < root.childCount; i++)
            {
                var found = FindDescendant(root.GetChild(i), name);
                if (found != null) return found;
            }

            return null;
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
