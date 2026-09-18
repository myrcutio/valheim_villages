using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
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
    ///     <para>
    ///     Bare, it is a FULL rebuild: every triangle re-probed, cached verdicts
    ///     discarded. That is deliberate — it is the correctness backstop the
    ///     incremental path is checked against. <c>dirty=x,z,r</c> forces the
    ///     incremental path instead, which is how the two are compared without
    ///     having to go and swing a hammer.
    ///     </para>
    /// </summary>
    internal static class RepartitionCommand
    {
        [DevCommand("Force an immediate hna_partition rebuild. " +
                    "vv_repartition [dirty=<x>,<z>,<radius>] — with dirty=, runs the " +
                    "INCREMENTAL path scoped to that circle instead of a full rebuild",
            Name = "vv_repartition")]
        public static void Repartition(Terminal.ConsoleEventArgs args)
        {
            var dirty = ParseDirty(args);

            if (ZoneSystem.instance == null)
            {
                Print("[vv_repartition] ZoneSystem is not up; nothing to partition.");
                return;
            }

            var enqueued = 0;
            foreach (var village in VillageRegistry.EnumerateAll())
            {
                var id = village.VillageId;
                if (string.IsNullOrEmpty(id)) continue;
                var anchor = village.Anchor;
                if (!ZoneSystem.instance.IsZoneLoaded(ZoneSystem.GetZone(anchor))) continue;

                var attributes = new Dictionary<string, string>
                {
                    { "village_id", id },
                    { "anchor_x", anchor.x.ToString("F2", CultureInfo.InvariantCulture) },
                    { "anchor_z", anchor.z.ToString("F2", CultureInfo.InvariantCulture) },
                };

                if (dirty.HasValue)
                {
                    var d = dirty.Value;
                    attributes["dirty_min_x"] = (d.x - d.z).ToString("R", CultureInfo.InvariantCulture);
                    attributes["dirty_min_z"] = (d.y - d.z).ToString("R", CultureInfo.InvariantCulture);
                    attributes["dirty_max_x"] = (d.x + d.z).ToString("R", CultureInfo.InvariantCulture);
                    attributes["dirty_max_z"] = (d.y + d.z).ToString("R", CultureInfo.InvariantCulture);
                }

                GlobalTaskQueue.Enqueue(new VillagerTask
                {
                    Name = "hna_partition",
                    SourceId = "user",
                    Priority = TaskPriority.High,
                    TimeoutSeconds = 60f,
                    Attributes = attributes,
                });
                enqueued++;
            }

            // No unscoped rebuild here on purpose. A partition without a village is not a
            // degraded partition, it is an incoherent one: the graph, footprint, triangle
            // cache and verdict cache are all keyed by village. Say so instead.
            if (enqueued == 0)
            {
                Print("[vv_repartition] No loaded village resolved — nothing enqueued. " +
                      "A village's zone must be loaded before its graph can be rebuilt.");
                return;
            }

            var scope = dirty.HasValue
                ? $"INCREMENTAL around ({dirty.Value.x:F0},{dirty.Value.y:F0}) r={dirty.Value.z:F0}m"
                : "FULL";
            Print($"[vv_repartition] Enqueued {scope} hna_partition for {enqueued} loaded village(s)");
        }

        /// <summary>
        ///     Parse <c>dirty=x,z,radius</c> (or <c>dirty=player,radius</c>) into
        ///     (x, z, radius) packed as a Vector3. Malformed input throws — a dev command
        ///     that quietly ignored the argument it was given would report a full rebuild's
        ///     timings as though they were the incremental path's.
        /// </summary>
        private static Vector3? ParseDirty(Terminal.ConsoleEventArgs args)
        {
            if (args?.Args == null) return null;
            for (var i = 1; i < args.Args.Length; i++)
            {
                var a = args.Args[i];
                if (string.IsNullOrEmpty(a)
                    || !a.StartsWith("dirty=", StringComparison.OrdinalIgnoreCase))
                    continue;

                var parts = a.Substring("dirty=".Length).Split(',');
                if (parts.Length == 2
                    && parts[0].Equals("player", StringComparison.OrdinalIgnoreCase))
                {
                    var p = Player.m_localPlayer;
                    if (p == null)
                        throw new InvalidOperationException(
                            "dirty=player requires a local player; there is none.");
                    return new Vector3(p.transform.position.x, p.transform.position.z,
                        ParseFloat(parts[1], a));
                }

                if (parts.Length != 3)
                    throw new ArgumentException(
                        $"'{a}' is malformed; expected dirty=<x>,<z>,<radius> or dirty=player,<radius>.");

                return new Vector3(
                    ParseFloat(parts[0], a), ParseFloat(parts[1], a), ParseFloat(parts[2], a));
            }

            return null;
        }

        private static float ParseFloat(string raw, string arg)
        {
            if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                throw new ArgumentException($"'{raw}' in '{arg}' is not a number.");
            return v;
        }

        private static void Print(string msg)
        {
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
