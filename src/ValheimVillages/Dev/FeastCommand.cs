using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Feasts;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI.Work;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Inspect and stage the feast swap.
    ///
    ///     <para>A feast is eaten one serving at a time by players, over hours, and only an
    ///     EMPTY one is a villager's business — so the interesting state cannot be reached by
    ///     waiting. <c>place</c>, <c>stock</c> and <c>empty</c> set it up directly;
    ///     <c>status</c> then shows exactly what <see cref="FeastBehavior" /> and its producer
    ///     see: which feasts are empty, and whether the village holds a replacement.</para>
    /// </summary>
    public static class FeastCommand
    {
        private const string DefaultFeast = "FeastMeadows";

        [DevCommand("Feast state, or stage one: vv_feast [status|place <prefab> [x z]|empty|stock <item> [n] [villageId]]",
            Name = "vv_feast")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var mode = args?.Args != null && args.Args.Length > 1
                ? args.Args[1].ToLowerInvariant()
                : "status";
            var sb = new StringBuilder();
            // Staging acts ONCE, while the report covers every village. It moves on to the
            // next village until one actually takes the action: acting on "the first village"
            // aimed `empty` at a village with no feasts in it and reported nothing done, and
            // acting on ALL of them put a feast in each — both at the coordinates meant for one.
            var staged = false;

            foreach (var village in new System.Collections.Generic.List<Village>(
                         VillageRegistry.EnumerateAll()))
            {
                var center = village.Anchor;
                var villageId = village.VillageId ?? "";
                sb.AppendLine($"[vv_feast] village {village.VillageId} anchored at " +
                              $"({center.x:F1},{center.z:F1})");

                if (!staged)
                    staged = mode switch
                    {
                        "place" => Place(sb, args, center),
                        "empty" => Empty(sb, center),
                        "stock" => Stock(sb, args, center, villageId),
                        _ => false,
                    };

                Report(sb, center);
            }

            var output = sb.Length > 0 ? sb.ToString() : "[vv_feast] no villages";
            ConsoleReport.Emit(output);
        }

        /// <summary>Every feast in range, and the one fact that decides who may touch it.</summary>
        private static void Report(StringBuilder sb, Vector3 center)
        {
            var containers = ContainerScanner.FindVillageContainers(
                center, WorkSettings.HaulScanRadius);

            var any = false;
            foreach (var feast in PhysicsHelper.GetAllInRadius<Feast>(center, WorkSettings.HaulScanRadius))
            {
                if (feast == null) continue;
                any = true;

                var hasStock = FeastBehavior.TryFindMaterial(feast, containers, out var material);
                sb.AppendLine(
                    $"  {FeastBehavior.PieceName(feast),-16} {feast.GetStack()}/{feast.m_eatStacks} " +
                    $"at ({feast.transform.position.x:F1},{feast.transform.position.z:F1}) " +
                    $"{(FeastBehavior.IsEmpty(feast) ? "EMPTY" : "in use — leave it alone")} " +
                    $"| replacement {(hasStock ? $"in stock ({material.PrefabName})" : "NOT in stock")}");
            }

            if (!any) sb.AppendLine("  no feasts in range");
        }

        private static bool Place(StringBuilder sb, Terminal.ConsoleEventArgs args, Vector3 center)
        {
            var name = args.Args.Length > 2 ? args.Args[2] : DefaultFeast;
            var pos = center;
            if (args.Args.Length > 4
                && float.TryParse(args.Args[3], out var x)
                && float.TryParse(args.Args[4], out var z))
                pos = new Vector3(x, 0f, z);

            if (ZoneSystem.instance != null)
                pos.y = ZoneSystem.instance.GetGroundHeight(pos);

            var prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(name) : null;
            if (prefab == null)
            {
                sb.AppendLine($"  place: no prefab named '{name}'");
                return false;
            }

            Object.Instantiate(prefab, pos, Quaternion.identity);
            sb.AppendLine($"  place: {name} at ({pos.x:F1},{pos.y:F1},{pos.z:F1})");
            return true;
        }

        /// <summary>
        ///     Eat a feast out in one go. Writes the same ZDO value the last serving would
        ///     (<c>s_value</c> = -1), so the piece reports empty exactly as it would in play.
        /// </summary>
        private static bool Empty(StringBuilder sb, Vector3 center)
        {
            Feast nearest = null;
            var bestSqr = float.MaxValue;
            foreach (var feast in PhysicsHelper.GetAllInRadius<Feast>(center, WorkSettings.HaulScanRadius))
            {
                if (feast == null || FeastBehavior.IsEmpty(feast)) continue;
                var sqr = (feast.transform.position - center).sqrMagnitude;
                if (sqr >= bestSqr) continue;
                nearest = feast;
                bestSqr = sqr;
            }

            if (nearest == null)
            {
                return false;
            }

            var nview = nearest.GetComponent<ZNetView>();
            nview.ClaimOwnership();
            nview.GetZDO().Set(ZDOVars.s_value, -1);
            nearest.UpdateVisual();
            sb.AppendLine($"  empty: ate out {FeastBehavior.PieceName(nearest)}");
            return true;
        }

        /// <summary>
        ///     Put items in a village chest. The optional third argument is a village-id
        ///     prefix: with more than one village in the world, "the first village that has a
        ///     chest with room" is whichever the registry happens to enumerate first, which is
        ///     rarely the one being tested.
        /// </summary>
        private static bool Stock(
            StringBuilder sb, Terminal.ConsoleEventArgs args, Vector3 center, string villageId)
        {
            var item = args.Args.Length > 2 ? args.Args[2] : DefaultFeast + "_Material";
            var count = args.Args.Length > 3 && int.TryParse(args.Args[3], out var n) ? n : 1;
            var wanted = args.Args.Length > 4 ? args.Args[4] : null;
            if (!string.IsNullOrEmpty(wanted) && !villageId.StartsWith(wanted)) return false;

            var containers = ContainerScanner.FindVillageContainers(
                center, WorkSettings.HaulScanRadius);
            foreach (var container in containers)
            {
                if (container == null) continue;
                if (!ContainerScanner.TryDepositItem(container, item, count)) continue;

                sb.AppendLine($"  stock: put {count}x {item} in the chest at " +
                              $"({container.transform.position.x:F1},{container.transform.position.z:F1})");
                return true;
            }

            sb.AppendLine($"  stock: no chest would take {item}");
            return false;
        }
    }
}
