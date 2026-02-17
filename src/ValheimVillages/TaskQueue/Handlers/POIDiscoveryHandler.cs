using System.Collections.Generic;
using System.IO;
using ValheimVillages.Core.Attributes;
using ValheimVillages.NPCs.AI;
using ValheimVillages.TaskQueue.ActivityLog;

namespace ValheimVillages.TaskQueue.Handlers
{
    // #region agent log
    internal static class DebugLog
    {
        private const string Path = "/home/benny/Projects/valheim_villages/.cursor/debug.log";
        public static void Write(string hypothesisId, string location, string message, string data)
        {
            try
            {
                long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string line = $"{{\"hypothesisId\":\"{hypothesisId}\",\"location\":\"{location}\",\"message\":\"{message}\",\"data\":{data},\"timestamp\":{ts}}}\n";
                File.AppendAllText(Path, line);
            }
            catch { }
        }
        public static string Str(string v) => $"\"{v?.Replace("\"", "\\\"")}\"";
        public static string Num(int v) => v.ToString();
    }
    // #endregion

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

            if (!VillagerAIManager.ActiveVillagers.TryGetValue(villagerId, out var ai))
                return TaskResult.Fail($"Villager {villagerId} not found");

            if (ai.Instance == null)
                return TaskResult.Fail("VillagerAI instance is null");

            bool isValidation = task.Name == ValidationTaskName;

            if (isValidation)
            {
                int beforeCount = ai.Memory.KnownLocations.Count;
                VillagerPOIDiscovery.ValidateKnownLocations(ai.Memory);
                int removed = beforeCount - ai.Memory.KnownLocations.Count;

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
                int beforeCount = ai.Memory.KnownLocations.Count;

                // Near-range discovery
                VillagerPOIDiscovery.DiscoverNearbyPOIs(ai.Instance, ai.Memory);

                // Extended LOS discovery while exploring
                bool isExploring = task.Attributes.TryGetValue("is_exploring", out var exp)
                    && exp == "true";
                if (isExploring)
                    VillagerPOIDiscovery.DiscoverVisiblePOIs(ai.Instance, ai.Memory);

                int discovered = ai.Memory.KnownLocations.Count - beforeCount;

                // #region agent log
                if (ai.NpcType == NPCs.NpcType.Farmer)
                {
                    var craftStationsAfter = new System.Collections.Generic.List<string>();
                    var cookingStationsAfter = new System.Collections.Generic.List<string>();
                    foreach (var loc in ai.Memory.KnownLocations)
                    {
                        if (loc.Type == LocationType.CraftStation)
                            craftStationsAfter.Add($"{loc.Position.X:F0},{loc.Position.Y:F0},{loc.Position.Z:F0}(q={loc.GetQualityScore():F0})");
                        else if (loc.Type == LocationType.CookingStation)
                            cookingStationsAfter.Add($"{loc.Position.X:F0},{loc.Position.Y:F0},{loc.Position.Z:F0}(q={loc.GetQualityScore():F0})");
                    }
                    DebugLog.Write("FH", "POIDiscoveryHandler:discovery", "AfterDiscover",
                        $"{{\"npc\":{DebugLog.Str(ai.NpcName)},\"discovered\":{DebugLog.Num(discovered)},\"totalLocs\":{DebugLog.Num(ai.Memory.KnownLocations.Count)},\"craftStationCount\":{DebugLog.Num(craftStationsAfter.Count)},\"craftStations\":{DebugLog.Str(string.Join("|", craftStationsAfter))},\"cookingStationCount\":{DebugLog.Num(cookingStationsAfter.Count)},\"cookingStations\":{DebugLog.Str(string.Join("|", cookingStationsAfter))}}}");
                }
                // #endregion

                if (discovered > 0)
                {
                    activityLog.Record(
                        villagerId,
                        task.Name,
                        "discover_pois",
                        $"discovered {discovered} new POI(s) near ({ai.Position.x:F0},{ai.Position.z:F0})");
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
