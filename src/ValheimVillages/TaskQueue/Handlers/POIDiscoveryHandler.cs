using System.Collections.Generic;
using System.IO;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Interfaces;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.AI;
using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.TaskQueue.Handlers
{
    /// <summary>
    /// Handles "poi_discovery" and "poi_validation" tasks.
    /// Wraps VillagerPOIDiscovery.DiscoverNearbyPOIs, DiscoverVisiblePOIs,
    /// and ValidateKnownLocations.
    /// Priority: Medium (2) for discovery, Low (1) for validation.
    /// Note: The priority is set on the VillagerTask at enqueue time, not here.
    /// This handler processes both task names.
    /// </summary>
    [RegisterTaskHandler]
    public class POIDiscoveryHandler : ITaskHandlerWithLog
    {
        public const string DiscoveryTaskName = "poi_discovery";
        public const string ValidationTaskName = "poi_validation";

        public string TaskName => DiscoveryTaskName;

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            if (!task.Attributes.TryGetValue("villager_id", out var villagerId))
                return TaskResult.Fail("Missing villager_id");

            if (!ValheimVillages.Villager.AI.VillagerAIManager.ActiveVillagers.TryGetValue(villagerId, out var ai))
                return TaskResult.Fail($"Villager {villagerId} not found");

            var memory = ai.GetMemory();
            if (memory == null)
                return TaskResult.Fail("VillagerAI memory is null");

            bool isValidation = task.Name == ValidationTaskName;

            if (isValidation)
            {
                int beforeCount = memory.KnownLocations.Count;
                VillagerPOIDiscovery.ValidateKnownLocations(memory);
                int removed = beforeCount - memory.KnownLocations.Count;

                if (removed > 0)
                {
                    activityLog.Record(
                        villagerId,
                        task.Name,
                        "validate_locations",
                        $"removed {removed} invalid location(s) from memory");
                }

                return TaskResult.Ok(new Dictionary<string, string>
                {
                    { "removed_count", removed.ToString() }
                });
            }
            else
            {
                int beforeCount = memory.KnownLocations.Count;

                // Near-range discovery (Villager.AI path: transform + IVillagerMemory)
                var transform = ai.Villager != null ? ai.Villager.transform : null;
                if (transform != null)
                    VillagerPOIDiscovery.DiscoverNearbyPOIs(transform, memory);

                // Extended LOS discovery while exploring
                bool isExploring = task.Attributes.TryGetValue("is_exploring", out var exp)
                    && exp == "true";
                if (isExploring && transform != null)
                    VillagerPOIDiscovery.DiscoverVisiblePOIs(transform, memory);

                int discovered = memory.KnownLocations.Count - beforeCount;

                if (discovered > 0)
                {
                    var pos = ai.Position;
                    activityLog.Record(
                        villagerId,
                        task.Name,
                        "discover_pois",
                        $"discovered {discovered} new POI(s) near ({pos.x:F0},{pos.z:F0})");
                }

                return TaskResult.Ok(new Dictionary<string, string>
                {
                    { "discovered_count", discovered.ToString() }
                });
            }
        }
    }

    /// <summary>
    /// Separate handler registration for "poi_validation" tasks.
    /// Delegates to POIDiscoveryHandler which handles both task names.
    /// </summary>
    [RegisterTaskHandler]
    public class POIValidationHandler : ITaskHandlerWithLog
    {
        private readonly POIDiscoveryHandler m_inner = new POIDiscoveryHandler();

        public string TaskName => POIDiscoveryHandler.ValidationTaskName;

        public TaskResult Handle(VillagerTask task, VillagerActivityLog activityLog)
        {
            return m_inner.Handle(task, activityLog);
        }
    }
}
