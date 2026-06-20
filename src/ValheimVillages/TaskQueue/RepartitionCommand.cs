using System.Collections.Generic;
using System.Globalization;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.TaskQueue
{
    /// <summary>
    ///     Dev command to force an immediate hna_partition rebuild. Useful for
    ///     verifying partition-time behavior (e.g., polygon clip activation,
    ///     door blocker counts) without waiting for the bake sweep / patrol
    ///     behavior to request one. Enqueues one village-scoped rebuild per
    ///     loaded village (multi-village worlds: a dedicated server keeps every
    ///     village loaded, so this forces a fresh bake of them all).
    /// </summary>
    internal static class RepartitionCommand
    {
        [DevCommand("Force enqueue an immediate hna_partition rebuild", Name = "vv_repartition")]
        public static void Repartition()
        {
            var enqueued = 0;
            if (ZoneSystem.instance != null)
            {
                foreach (var village in VillageRegistry.EnumerateAll())
                {
                    var id = village.VillageId;
                    if (string.IsNullOrEmpty(id)) continue;
                    var anchor = village.Anchor;
                    if (!ZoneSystem.instance.IsZoneLoaded(ZoneSystem.GetZone(anchor))) continue;

                    GlobalTaskQueue.Enqueue(new VillagerTask
                    {
                        Name = "hna_partition",
                        SourceId = "user",
                        Priority = TaskPriority.High,
                        TimeoutSeconds = 60f,
                        Attributes = new Dictionary<string, string>
                        {
                            { "village_id", id },
                            { "anchor_x", anchor.x.ToString("F2", CultureInfo.InvariantCulture) },
                            { "anchor_z", anchor.z.ToString("F2", CultureInfo.InvariantCulture) },
                        },
                    });
                    enqueued++;
                }
            }

            // Fallback: no loaded village resolved (e.g. run before any village exists or
            // its zone is streaming) — enqueue a single unscoped rebuild so the command
            // still does something rather than silently no-op.
            if (enqueued == 0)
            {
                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = "hna_partition",
                    SourceId = "user",
                    Priority = TaskPriority.High,
                    TimeoutSeconds = 60f,
                    Attributes = new Dictionary<string, string>(),
                });
            }

            var msg = enqueued > 0
                ? $"[vv_repartition] Enqueued hna_partition rebuild for {enqueued} loaded village(s)"
                : "[vv_repartition] No loaded village resolved; enqueued one unscoped rebuild";
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
