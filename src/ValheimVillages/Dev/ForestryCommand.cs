using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Forestry;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI.Work;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Inspect and hurry along a woodlot.
    ///
    ///     <para><c>grow</c> exists because a tree sapling's <c>m_growTime</c> is measured in
    ///     game-hours — without it the plant→fell→haul cycle cannot be observed end to end in
    ///     one sitting, which makes the feature effectively untestable.</para>
    /// </summary>
    public static class ForestryCommand
    {
        [DevCommand("Woodlot status, force saplings to mature, plant one now, or pull the duds: " +
                    "vv_forestry [status|grow|plant|clear]",
            Name = "vv_forestry")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var mode = args?.Args != null && args.Args.Length > 1
                ? args.Args[1].ToLowerInvariant()
                : "status";

            var sb = new StringBuilder();
            var found = false;

            // Materialised first: the work below (resolving a village's chests, its region
            // graph) can populate the registry's own caches, and mutating those mid-enumeration
            // threw "Collection was modified" out of `plant`.
            var villages = new List<Village>(VillageRegistry.EnumerateAll());
            foreach (var village in villages)
            {
                if (!village.TryGetAnchor(ForesterPost.AnchorName, out var post)) continue;
                found = true;

                var r = ForesterPost.WorkRadius;
                sb.AppendLine($"[vv_forestry] village {village.VillageId}");
                sb.AppendLine($"  woodlot at ({post.x:F1},{post.y:F1},{post.z:F1}) radius {r:F0}m");

                int saplings = 0, grown = 0, logs = 0, timber = 0;
                var saplingStatus = new Dictionary<string, int>();
                foreach (var plant in PhysicsHelper.GetAllInRadius<Plant>(post, r))
                {
                    if (plant == null) continue;
                    saplings++;
                    // Broken out because a sapling that reports anything but Healthy is a dud:
                    // a spent seed holding a planting spot that will never produce a tree.
                    var key = plant.GetStatus().ToString();
                    saplingStatus.TryGetValue(key, out var had);
                    saplingStatus[key] = had + 1;
                }
                foreach (var tree in PhysicsHelper.GetAllInRadius<TreeBase>(post, r))
                    if (TreeFelling.IsFellable(tree))
                        grown++;
                foreach (var log in PhysicsHelper.GetAllInRadius<TreeLog>(post, r))
                    if (TreeFelling.IsBreakable(log))
                        logs++;
                var onGround = new Dictionary<string, int>();
                foreach (var drop in PhysicsHelper.GetAllInRadius<ItemDrop>(post, r))
                {
                    var n = drop?.m_itemData?.m_shared?.m_name ?? "";
                    if (n.IndexOf("wood", System.StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("log", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        timber += drop.m_itemData.m_stack;

                    // Everything else on the forest floor, by prefab. Seeds are the whole
                    // question for replanting and they are NOT named like the tree they come
                    // from ("Acorn", "PineCone"), so a wood/log name filter hides them.
                    if (drop == null) continue;
                    var prefab = drop.gameObject.name.Replace("(Clone)", "");
                    onGround.TryGetValue(prefab, out var had);
                    onGround[prefab] = had + drop.m_itemData.m_stack;
                }

                sb.AppendLine($"  saplings={saplings} grownTrees={grown} logs={logs} loose timber={timber}");
                sb.AppendLine($"  saplings by status: {Histogram(saplingStatus)}");
                sb.AppendLine($"  on the ground: {Histogram(onGround)}");
                // Chests are scanned from the village's own anchor, not the post: the post sits
                // deliberately OUTSIDE the walls, which is not where the stores are.
                AppendSeedStock(sb, post, village.Anchor);

                if (mode == "plant")
                {
                    PlantOne(sb, post, village.Anchor);
                    continue;
                }

                if (mode == "clear")
                {
                    ClearDuds(sb, post, r);
                    continue;
                }

                if (mode != "grow") continue;

                var matured = 0;
                foreach (var plant in PhysicsHelper.GetAllInRadius<Plant>(post, r))
                {
                    if (plant == null) continue;
                    var status = plant.GetStatus();
                    if (status != Plant.Status.Healthy)
                    {
                        // Grow() on an unhealthy plant produces nothing useful; say why
                        // rather than silently skipping it.
                        sb.AppendLine($"    skipped a sapling: status={status}");
                        continue;
                    }

                    var nview = plant.GetComponent<ZNetView>();
                    if (nview != null && nview.IsValid()) nview.ClaimOwnership();
                    // Report WHAT failed. The dev-command dispatcher invokes by reflection,
                    // so an exception here surfaces only as "Exception has been thrown by the
                    // target of an invocation" with the real cause buried in InnerException —
                    // useless for working out why a sapling would not mature.
                    try
                    {
                        plant.Grow();
                        matured++;
                    }
                    catch (System.Exception ex)
                    {
                        var inner = ex.InnerException;
                        sb.AppendLine(
                            $"    Grow() threw {ex.GetType().Name}: {ex.Message}" +
                            (inner != null ? $" | inner {inner.GetType().Name}: {inner.Message}" : ""));
                    }
                }

                sb.AppendLine($"  grow: matured {matured} sapling(s)");
            }

            if (!found)
                sb.AppendLine("[vv_forestry] no village has a Forester's Post.");

            var output = sb.ToString();
            ConsoleReport.Emit(output);
        }

        /// <summary>
        ///     What the villager's species choice is actually working from: the seeds in the
        ///     village's chests, per species, with the two facts that veto a species anyway —
        ///     whether it matures in this biome, and whether any order wants its wood.
        /// </summary>
        private static void AppendSeedStock(StringBuilder sb, Vector3 post, Vector3 home)
        {
            var containers = ContainerScanner.FindVillageContainers(home, WorkSettings.HaulScanRadius);
            var biome = Heightmap.FindBiome(post);

            sb.AppendLine($"  biome at the post: {biome}, chests in village: {containers.Count}");
            foreach (var species in TreeSpecies.All)
            {
                var stock = 0;
                var byName = 0;
                var shared = SharedName(species.Seed);
                foreach (var container in containers)
                {
                    var inv = container?.GetInventory();
                    if (inv == null) continue;
                    stock += ContainerScanner.CountByPrefab(inv, species.Seed);

                    // Counted a second way on purpose. Everything that decides whether work can
                    // proceed — this, work-order quotas, ingredient checks — counts by
                    // m_dropPrefab.name, which is a LIVE object reference: if a chest's items
                    // come back from a ZDO without it, the stock reads zero while the seeds are
                    // plainly sitting in the chest. A mismatch here says which of the two is
                    // lying.
                    if (shared == null) continue;
                    foreach (var item in inv.GetAllItems())
                        if (item?.m_shared != null && item.m_shared.m_name == shared)
                            byName += item.m_stack;
                }

                sb.AppendLine(
                    $"    {species.Name,-6} {species.Seed,-11} x{stock,-4} -> {species.Wood,-9} " +
                    $"{(species.GrowsIn(biome) ? "grows here" : "wrong biome")}" +
                    $"{(byName != stock ? $"  [by name x{byName} — m_dropPrefab lost]" : "")}");
            }
        }

        /// <summary>The display name an item prefab's drop carries, or null if unknown here.</summary>
        private static string SharedName(string prefabName)
        {
            var go = ObjectDB.instance != null ? ObjectDB.instance.GetItemPrefab(prefabName) : null;
            var drop = go != null ? go.GetComponent<ItemDrop>() : null;
            return drop?.m_itemData?.m_shared?.m_name;
        }

        /// <summary>
        ///     Plant one sapling right now, paid for out of the chests, deliberately IGNORING
        ///     the stocking target the villager works to.
        ///
        ///     <para>Same reason <c>grow</c> exists: a woodlot already at its target will not
        ///     plant again for hours of game time, so the seed→sapling half of the round is
        ///     otherwise unobservable. Everything past the target check is the villager's own
        ///     code path, so what this proves is what he does.</para>
        /// </summary>
        private static void PlantOne(StringBuilder sb, Vector3 post, Vector3 home)
        {
            if (!ForestryBehavior.TryFindPlantSpot(
                    post, home, new HashSet<string>(), out var spot, out var species, out var seed))
            {
                sb.AppendLine("  plant: no spot with room, on the region graph, and a seed to pay for it");
                return;
            }

            if (!species.TryPlantAt(spot, seed, out var failure))
            {
                sb.AppendLine($"  plant: chose {species.Name} but did not plant it: {failure}");
                return;
            }

            sb.AppendLine(
                $"  plant: {species.Name} (from {seed.PrefabName}, for {species.Wood}) at " +
                $"({spot.x:F1},{spot.y:F1},{spot.z:F1})");
        }

        /// <summary>
        ///     Pull up every sapling in the woodlot that has judged itself unable to grow.
        ///
        ///     <para>The Lumberjack does this himself, one at a time and behind every other
        ///     errand, so a woodlot that accumulated duds before the grow check existed would
        ///     take him a long while to tidy. This clears them in one go.</para>
        /// </summary>
        private static void ClearDuds(StringBuilder sb, Vector3 post, float radius)
        {
            var pulled = 0;
            foreach (var plant in PhysicsHelper.GetAllInRadius<Plant>(post, radius))
            {
                if (plant == null || plant.GetStatus() == Plant.Status.Healthy) continue;

                var nview = plant.GetComponent<ZNetView>();
                if (nview == null || !nview.IsValid()) continue;

                sb.AppendLine(
                    $"    pulled {plant.gameObject.name.Replace("(Clone)", "")} " +
                    $"({plant.GetStatus()}) at ({plant.transform.position.x:F1}," +
                    $"{plant.transform.position.z:F1})");
                nview.ClaimOwnership();
                nview.Destroy();
                pulled++;
            }

            sb.AppendLine($"  clear: pulled {pulled} sapling(s) that could not grow");
        }

        private static string Histogram(Dictionary<string, int> counts)
        {
            if (counts.Count == 0) return "nothing";

            var sb = new StringBuilder();
            foreach (var kv in counts)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append($"{kv.Key} x{kv.Value}");
            }

            return sb.ToString();
        }
    }
}
