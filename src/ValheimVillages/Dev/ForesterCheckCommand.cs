using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Forestry;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Reports whether a Forester's Post is actually usable, in the order the chain can
    ///     break. Each stage below was a real failure during development, and they fail
    ///     SILENTLY — a post can look placed, log its anchor, extend the bake, and still
    ///     leave the Lumberjack with nowhere to walk.
    /// </summary>
    public static class ForesterCheckCommand
    {
        [DevCommand("Check a Forester's Post is reachable: vv_forester_check", Name = "vv_forester_check")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var sb = new System.Text.StringBuilder();
            var any = false;

            foreach (var village in VillageRegistry.EnumerateAll())
            {
                if (!village.TryGetAnchor(ForesterPost.AnchorName, out var post)) continue;
                any = true;
                Report(sb, village, post);
            }

            if (!any)
                sb.AppendLine("[vv_forester_check] no village has a 'forester' anchor — " +
                              "place one with vv_forester_post (or build a Forester's Post).");

            var output = sb.ToString();
            global::Console.instance?.Print(output);
            Plugin.Log?.LogInfo(output);
        }

        private static void Report(System.Text.StringBuilder sb, Village village, Vector3 post)
        {
            var anchor = village.Anchor;
            sb.AppendLine($"[vv_forester_check] village {village.VillageId}");
            sb.AppendLine($"  post    = ({post.x:F1},{post.y:F1},{post.z:F1})  " +
                          $"{Vector3.Distance(post, anchor):F0}m from the village anchor");

            // 1. Is the post within range, and is the corridor of stepping stones that keeps
            //    the bake continuous actually on the record? A post further out than one bake
            //    diameter needs them; without them the grove bakes as an island.
            var distance = Vector3.Distance(post, anchor);
            var links = 0;
            foreach (var a in village.Anchors)
                if (a.Name != null && a.Name.StartsWith(ForesterPostAnchor.LinkAnchorPrefix))
                    links++;
            var inRange = distance <= 80f;
            sb.AppendLine($"  1. within 80m of the village  : {YesNo(inRange)}" +
                          $"  ({distance:F0}m, {links} corridor anchor(s))");

            // Where the lane out was drawn FROM. This decides whether the walk leaves through
            // an opening or runs at a wall, and it was invisible until a lane laid from the
            // registry crossed a stake_wall with no gate in it and left the grove an island.
            var gates = GateCount(village);
            var start = ForesterPost.CorridorStart(village, post, village.Graph?.GetGates());
            var fromGate = gates > 0 && start != anchor;
            sb.AppendLine($"     lane starts at            : ({start.x:F1},{start.z:F1}) " +
                          (fromGate
                              ? $"— nearest of {gates} gate(s)"
                              : "— the village ANCHOR, because the graph has no gates; the lane " +
                                "will cross the perimeter wherever it lands"));

            // 2. Did the bake leave walkable mesh out here? (The woodlot carve-out.)
            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };
            var onMesh = NavMesh.SamplePosition(post, out var hit, 8f, filter);
            sb.AppendLine($"  2. navmesh at the grove      : {YesNo(onMesh)}" +
                          (onMesh ? $" (snap {Vector3.Distance(hit.position, post):F1}m)" : ""));

            // 3. Is it in the region graph? HNA pathing uses the GRAPH, not the raw mesh, so
            //    mesh-without-region is still unreachable.
            var graph = village.Graph;
            var region = graph != null ? graph.PointToRegionId(onMesh ? hit.position : post) : null;
            sb.AppendLine($"  3. region graph covers it    : {YesNo(!string.IsNullOrEmpty(region))}" +
                          $" ({region ?? "unresolved"})");

            // 4. The one that actually decides it: can a villager walk there? A grove behind an
            //    unbroken palisade satisfies 1-3 and still fails here.
            var buf = new List<Vector3>();
            var walkable = VillagerMovement.TryFindCompletePath(anchor, onMesh ? hit.position : post, buf);
            sb.AppendLine($"  4. villager can path to it   : {YesNo(walkable)} ({buf.Count} corners)");

            if (walkable)
            {
                sb.AppendLine("  VERDICT: usable.");
                return;
            }

            sb.AppendLine(!onMesh
                ? "  VERDICT: no navmesh — the post is too far out, or a repartition has not run yet."
                : string.IsNullOrEmpty(region)
                    ? "  VERDICT: mesh but no region — the woodlot cells are still being pruned as " +
                      "unreachable; check the [ForesterPost] Woodlot line ran for BOTH the bake and the prune."
                    : "  VERDICT: the grove is a separate island — there is no opening in the village " +
                      "boundary between it and the post. Put the post outside a GATE, or make an " +
                      "opening; walls seal the hull and the Lumberjack cannot climb them.");
        }

        private static string YesNo(bool b) => b ? "yes" : "NO";

        private static int GateCount(Village village)
        {
            var gates = village.Graph?.GetGates();
            return gates?.Count ?? 0;
        }
    }
}
