using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Diagnostic: for every chest near a point, say whether a work order reserves it and
    ///     what that reservation lets villagers store there.
    ///
    ///     <para>Exists because the reservation is invisible in-world — a reserved chest looks
    ///     like any other, and a villager declining to haul into it is indistinguishable from a
    ///     villager that found nothing to haul. This prints the allow-list
    ///     <see cref="WorkOrderChestPolicy" /> actually resolved, so "why did he leave that wood
    ///     on the ground?" has an answer that isn't guesswork.</para>
    /// </summary>
    public static class ChestPolicyProbeCommand
    {
        [DevCommand("Show which nearby chests are reserved by a work order and what they accept: " +
                    "vv_chestpolicy [item] [x z [radius]]", Name = "vv_chestpolicy")]
        public static void Probe(Terminal.ConsoleEventArgs args)
        {
            var origin = Player.m_localPlayer != null
                ? Player.m_localPlayer.transform.position
                : Vector3.zero;
            var radius = WorkSettings.ChestScanRadius;

            // Optional first arg is an item prefab to test against each chest; the rest is the
            // same [x z [radius]] shape as vv_cookprobe.
            string testItem = null;
            var rest = 1;
            if (args != null && args.Length > 1 && !float.TryParse(args[1], out _))
            {
                testItem = args[1];
                rest = 2;
            }

            if (args != null && args.Length >= rest + 2
                && float.TryParse(args[rest], out var x)
                && float.TryParse(args[rest + 1], out var z))
            {
                // Resolve Y from the ground, not from the (absent) local player. On a dedicated
                // server m_localPlayer is null so origin.y stayed 0, and FindNearbyContainers
                // measures in 3D — every chest then read ~31m further away than it is, and a
                // radius query near the village returned nothing at all.
                var y = origin.y;
                if (Player.m_localPlayer == null && ZoneSystem.instance != null)
                    y = ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z));
                origin = new Vector3(x, y, z);
                if (args.Length > rest + 2 && float.TryParse(args[rest + 2], out var r)) radius = r;
            }

            // Scope the way PRODUCTION scopes: by the village footprint, falling back to a
            // radius only where there is no village. A radius probe disagreed with what
            // villagers actually see — the footprint routinely reaches further than the scan
            // radius (measured: a Farmer's ingredient chest 23.5m out against a 20m radius),
            // so this command would report a chest as out of scope that the villager was
            // using, and vice versa.
            var village = Villages.Entity.VillageRegistry.GetVillageAt(origin);
            var containers = ContainerScanner.FindVillageContainers(origin, radius);

            var scope = village != null && village.TryGetFootprint(
                out var fpMinX, out var fpMinZ, out var fpMaxX, out var fpMaxZ)
                ? $"village {village.VillageId.Substring(0, 8)} footprint " +
                  $"x[{fpMinX:F0}..{fpMaxX:F0}] z[{fpMinZ:F0}..{fpMaxZ:F0}]"
                : $"{radius:F0}m radius (no village footprint here)";

            var sb = new StringBuilder();
            sb.AppendLine($"[vv_chestpolicy] {containers.Count} chest(s) in {scope} from " +
                          $"({origin.x:F1},{origin.z:F1})" +
                          (testItem != null ? $"; testing '{testItem}'" : ""));

            foreach (var c in containers)
            {
                var p = c.transform.position;
                var inv = c.GetInventory();
                var free = inv != null ? inv.GetEmptySlots() : 0;
                var total = inv != null ? inv.GetWidth() * inv.GetHeight() : 0;

                sb.AppendLine($"  {c.m_name} @ ({p.x:F1},{p.y:F1},{p.z:F1}) " +
                              $"d={Vector3.Distance(origin, p):F1}m free={free}/{total}");

                var allowed = WorkOrderChestPolicy.Describe(c);
                if (allowed == null)
                {
                    sb.AppendLine("    unreserved — accepts anything");
                }
                else
                {
                    sb.AppendLine($"    RESERVED — accepts: {allowed}");
                    sb.AppendLine("    (plus work-order tokens)");
                }

                if (testItem != null)
                    sb.AppendLine($"    Allows('{testItem}') = " +
                                  WorkOrderChestPolicy.Allows(c, testItem) +
                                  (WorkOrderChestPolicy.HoldsOrder(c, testItem, null)
                                      ? "  <-- holds this order"
                                      : ""));
            }

            // The allow-list is a veto; it does not say WHERE the item actually lands. Print the
            // resolver's pick too, because "every chest allows it" was exactly the state in which
            // every order's output still piled into the one box nearest the anchor.
            if (testItem != null)
            {
                var target = WorkOrderChestPolicy.ResolveDepositChest(
                    containers, testItem, null, 1, origin);
                sb.AppendLine(target == null
                    ? $"  deposit target for '{testItem}': none (no chest has room)"
                    : $"  deposit target for '{testItem}': {target.m_name} @ " +
                      $"({target.transform.position.x:F1},{target.transform.position.y:F1}," +
                      $"{target.transform.position.z:F1})" +
                      (WorkOrderChestPolicy.HoldsOrder(target, testItem, null)
                          ? " [its own work-order chest]"
                          : " [spill — no order chest with room]"));
            }

            if (containers.Count == 0) sb.AppendLine("  (no chests in range)");
            Print(sb.ToString().TrimEnd());
        }

        private static void Print(string s)
        {
            global::Console.instance?.Print(s);
            Plugin.Log?.LogInfo(s);
        }
    }
}
