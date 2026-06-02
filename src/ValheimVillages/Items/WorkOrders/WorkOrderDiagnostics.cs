using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Work;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Items.WorkOrders
{
    /// <summary>
    ///     Produces a human-readable status line for a work order (out of
    ///     materials, storage full, quota met, no recipe, in progress, ...).
    ///     Mirrors the checks the work_order_scan handler uses, re-derived live
    ///     so it reflects the current world state when the player opens the menu.
    /// </summary>
    public static class WorkOrderDiagnostics
    {
        public enum Severity
        {
            Info,
            Good,
            Warning,
        }

        /// <summary>
        ///     Describe the work order's current state. Returns an empty message
        ///     when the item isn't a work order or nothing noteworthy applies.
        /// </summary>
        public static Status Describe(ItemDrop.ItemData item, Vector3 scanPos)
        {
            if (item?.m_customData == null) return Empty();
            if (!item.m_customData.TryGetValue("wo_item", out var itemPrefab) ||
                string.IsNullOrEmpty(itemPrefab))
                return Empty();

            item.m_customData.TryGetValue("wo_station", out var station);

            var max = 10;
            if (item.m_customData.TryGetValue("wo_max", out var maxStr))
                int.TryParse(maxStr, out max);

            // One container scan feeds both the current count and the message.
            var containers = ContainerScanner.FindNearbyContainers(
                scanPos, WorkSettings.ChestScanRadius);
            var current = containers.Count > 0
                ? ContainerScanner.CountAcrossContainers(containers, itemPrefab)
                : 0;

            var (message, severity) =
                Evaluate(itemPrefab, station, max, containers, current);
            return new Status(message, severity, current);
        }

        private static (string message, Severity severity) Evaluate(
            string itemPrefab, string station, int max,
            List<Container> containers, int current)
        {
            if (IsBeingWorked(itemPrefab))
                return ("A villager is crafting this now.", Severity.Info);

            var recipe = ResolveRecipe(itemPrefab, station);
            if (recipe == null)
                return ("No known recipe — this can't be produced here.", Severity.Warning);

            if (containers.Count == 0)
                return ("No storage chests nearby.", Severity.Warning);

            if (current >= max)
                return ($"Quota met — {current} in storage.", Severity.Good);

            var outputAmount = recipe.m_amount > 0 ? recipe.m_amount : 1;
            if (!containers.Any(c =>
                    ContainerScanner.CanAcceptItem(c, itemPrefab, outputAmount)))
                return ("Storage full — no room for the output.", Severity.Warning);

            var missing = MissingIngredients(containers, recipe);
            if (missing != null)
                return ($"Out of materials — needs {missing}.", Severity.Warning);

            return ("Ready — a villager will craft it when free.", Severity.Info);
        }

        private static Status Empty()
        {
            return new Status("", Severity.Info, 0);
        }

        private static bool IsBeingWorked(string itemPrefab)
        {
            foreach (var kvp in VillagerAIManager.ActiveVillagers)
            {
                var cb = kvp.Value?.CraftingBehavior?.Crafting;
                if (cb == null || !cb.IsWorking) continue;
                if (cb.CurrentItemPrefab == itemPrefab) return true;
            }

            return false;
        }

        /// <summary>
        ///     Resolve the recipe for a work order. Physical stations
        ///     ($piece_forge, ...) match directly; virtual villager stations
        ///     ($vv_blacksmith, ...) resolve through the owning villager type's
        ///     work stations (which is how the scan handler finds them).
        /// </summary>
        private static Recipe ResolveRecipe(string itemPrefab, string station)
        {
            if (string.IsNullOrEmpty(station)) return null;

            var direct = StationMatcher.FindRecipe(itemPrefab, station);
            if (direct != null) return direct;

            var villagerType = VillagerTypeForStation(station);
            return villagerType != null
                ? StationMatcher.FindRecipeForNpc(itemPrefab, villagerType)
                : null;
        }

        private static string VillagerTypeForStation(string station)
        {
            foreach (var kv in VillagerRegistry.Definitions)
                if (kv.Value?.stationName == station)
                    return kv.Value.type;
            return null;
        }

        private static string MissingIngredients(
            List<Container> containers, Recipe recipe)
        {
            if (recipe.m_resources == null) return null;

            var parts = new List<string>();
            foreach (var req in recipe.m_resources)
            {
                if (req.m_resItem == null || req.m_amount <= 0) continue;

                var prefab = req.m_resItem.gameObject.name;
                var have = ContainerScanner.CountAcrossContainers(containers, prefab);
                if (have >= req.m_amount) continue;

                var token = req.m_resItem.m_itemData?.m_shared?.m_name;
                var name = string.IsNullOrEmpty(token)
                    ? prefab
                    : Localization.instance.Localize(token);
                parts.Add($"{req.m_amount - have} {name}");
            }

            return parts.Count > 0 ? string.Join(", ", parts) : null;
        }

        public readonly struct Status
        {
            public readonly string Message;
            public readonly Severity Severity;
            public readonly int CurrentCount;

            public Status(string message, Severity severity, int currentCount)
            {
                Message = message;
                Severity = severity;
                CurrentCount = currentCount;
            }
        }
    }
}
