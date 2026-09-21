using System.Globalization;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Forestry;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Places a <see cref="ForesterPost" /> at a point. A dedicated server has no player
    ///     to swing a hammer, so without this the anchor-and-repartition chain — the whole
    ///     reason the post exists — cannot be exercised headlessly.
    /// </summary>
    public static class ForesterPostCommand
    {
        [DevCommand("Place a Forester's Post: vv_forester_post <x> <z> [y]   (X,Z,[Y] order)",
            Name = "vv_forester_post", Destructive = true)]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var inv = CultureInfo.InvariantCulture;
            if (args?.Args == null || args.Args.Length < 3
                || !float.TryParse(args.Args[1], NumberStyles.Float, inv, out var x)
                || !float.TryParse(args.Args[2], NumberStyles.Float, inv, out var z))
            {
                Print("Usage: vv_forester_post <x> <z> [y]   (X,Z,[Y] order)");
                return;
            }

            var pos = new Vector3(x, MeshProbe.ResolveY(x, z, args, 3, inv), z);

            var scene = ZNetScene.instance;
            var prefab = scene != null ? scene.GetPrefab(ForesterPost.PrefabName) : null;
            if (prefab == null)
            {
                Print($"[vv_forester_post] prefab '{ForesterPost.PrefabName}' not registered");
                return;
            }

            // ZNetView.Awake mints the ZDO, and ForesterPostAnchor.Awake does the anchor work
            // off the back of it — so plain Instantiate is the whole placement.
            var go = Object.Instantiate(prefab, pos, Quaternion.identity);
            Print($"[vv_forester_post] placed at ({pos.x:F1}, {pos.y:F1}, {pos.z:F1}) — " +
                  $"instance '{go.name}'. Watch for [ForesterPost] anchor + partition lines.");
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
