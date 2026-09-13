using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Diagnostic: dump the real state of every cooking station near a point — is the fire
    ///     lit, does it hold fuel, what is in each slot and how far along is it.
    ///
    ///     <para>Exists because "the meat is cooking on a cold fire" was otherwise unfalsifiable
    ///     from outside the game window: the mod logs what it *decides*, not what the station
    ///     *is*. This reads the station's own ZDO (slot / slotstatus), which is the same data
    ///     <c>TryPollCookingStation</c> polls, so the command and the behaviour cannot disagree.</para>
    /// </summary>
    public static class CookStationProbeCommand
    {
        private const float DefaultRadius = 20f;

        [DevCommand("Dump cooking-station fire/fuel/slot state near a point: vv_cookprobe [x z [radius]]",
            Name = "vv_cookprobe")]
        public static void Probe(Terminal.ConsoleEventArgs args)
        {
            var origin = Player.m_localPlayer != null
                ? Player.m_localPlayer.transform.position
                : Vector3.zero;
            var radius = DefaultRadius;

            if (args != null && args.Length >= 3 &&
                float.TryParse(args[1], out var x) && float.TryParse(args[2], out var z))
            {
                // Resolve Y from the ground, not from the (absent) local player. On a dedicated
                // server m_localPlayer is null so origin.y stayed 0, and the distance below is
                // 3D — every station then read ~31m further away than it is (a station 5.6m
                // away printed d=33.5m), which silently excluded them from small radii.
                var y = origin.y;
                if (Player.m_localPlayer == null && ZoneSystem.instance != null)
                    y = ZoneSystem.instance.GetGroundHeight(new Vector3(x, 0f, z));
                origin = new Vector3(x, y, z);
                if (args.Length > 3 && float.TryParse(args[3], out var r)) radius = r;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[vv_cookprobe] within {radius:F0}m of ({origin.x:F1},{origin.z:F1})");

            var found = 0;
            foreach (var station in Object.FindObjectsByType<CookingStation>(FindObjectsSortMode.None))
            {
                if (station == null) continue;
                var d = Vector3.Distance(origin, station.transform.position);
                if (d > radius) continue;
                found++;

                var lit = StationFinder.IsCookingStationFireLit(station);
                var fuel = StationFinder.GetCookingStationFuel(station);
                var ready = StationFinder.IsCookingStationReady(station);
                var accept = StationFinder.CanAcceptItem(station);

                var p = station.transform.position;
                sb.AppendLine(
                    $"  {station.gameObject.name} @ ({p.x:F1},{p.y:F1},{p.z:F1}) d={d:F1}m");
                sb.AppendLine(
                    $"    requireFire={station.m_requireFire} fireLit={lit} " +
                    $"useFuel={station.m_useFuel} fuel={fuel:F1}");
                // Two different engine gates, deliberately shown side by side: ACCEPT mirrors
                // OnUseItem (can food go in at all), READY mirrors UpdateCooking (will it
                // progress). They diverge on a fire-requiring station that holds fuel.
                sb.AppendLine(
                    $"    ACCEPT={accept} (OnUseItem gate)   READY={ready} (UpdateCooking gate)");

                var nview = station.GetComponent<ZNetView>();
                var zdo = nview != null ? nview.GetZDO() : null;
                if (zdo == null)
                {
                    sb.AppendLine("    (no ZDO — cannot read slots)");
                    continue;
                }

                var slots = station.m_slots != null ? station.m_slots.Length : 0;
                var occupied = 0;
                for (var i = 0; i < slots; i++)
                {
                    var item = zdo.GetString("slot" + i);
                    if (string.IsNullOrEmpty(item)) continue;
                    occupied++;

                    // 0=NotDone 1=Done 2=Burnt — the exact values TryPollCookingStation acts on.
                    var status = zdo.GetInt("slotstatus" + i);
                    var label = status switch
                    {
                        0 => "cooking",
                        1 => "DONE",
                        2 => "BURNT",
                        _ => "status" + status,
                    };
                    sb.AppendLine($"    slot{i}: {item} -> {label}");
                }

                if (occupied == 0) sb.AppendLine("    slots: (empty)");

                // Cross-check against the fireplace's OWN truth. CookingStation.IsFireLit()
                // looks for a Burning EffectArea at its fire-check points; Fireplace.IsBurning()
                // reads state/fuel off the fireplace ZDO. They can disagree — an EffectArea that
                // outlives the flame reads as "lit" to the station while the fire is visibly out.
                ReportFireplacesNear(sb, station.transform.position);
                if (occupied > 0 && !ready)
                    sb.AppendLine("    ** food on a station that is NOT ready — it will never finish **");
            }

            if (found == 0) sb.AppendLine("  (no cooking stations in range)");
            Print(sb.ToString().TrimEnd());
        }

        /// <summary>
        ///     Dump every Fireplace near the station with the values <c>Fireplace.IsBurning()</c>
        ///     actually consults, so a station claiming "lit" can be checked against the fire.
        /// </summary>
        private static void ReportFireplacesNear(StringBuilder sb, Vector3 stationPos)
        {
            const float r = 3f;
            var any = false;

            foreach (var fp in Object.FindObjectsByType<Fireplace>(FindObjectsSortMode.None))
            {
                if (fp == null) continue;
                var d = Vector3.Distance(stationPos, fp.transform.position);
                if (d > r) continue;
                any = true;

                var nview = fp.GetComponent<ZNetView>();
                var zdo = nview != null ? nview.GetZDO() : null;
                if (zdo == null)
                {
                    sb.AppendLine($"    fireplace {fp.gameObject.name} d={d:F1}m (no ZDO)");
                    continue;
                }

                var fuel = zdo.GetFloat(ZDOVars.s_fuel);
                var state = zdo.GetInt(ZDOVars.s_state, 1);
                sb.AppendLine(
                    $"    fireplace {fp.gameObject.name} d={d:F1}m " +
                    $"IsBurning={fp.IsBurning()} fuel={fuel:F2} state={state} " +
                    $"infinite={fp.m_infiniteFuel}");
            }

            if (!any) sb.AppendLine($"    (no Fireplace within {r:F0}m of the station)");
        }

        private static void Print(string s)
        {
            global::Console.instance?.Print(s);
            Plugin.Log?.LogInfo(s);
        }
    }
}
