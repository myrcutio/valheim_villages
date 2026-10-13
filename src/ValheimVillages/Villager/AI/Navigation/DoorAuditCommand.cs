using System.Text;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Report every door near the player: its open state, the orientation the bake derives
    ///     its blocker and link from, and — the thing that actually matters — whether the
    ///     villager navmesh is WALKABLE at the door's own position.
    ///
    ///     <para>Walkable at a door means the doorway was never carved, so a villager will path
    ///     straight into solid wood and wedge. That is the symptom this exists to localise:
    ///     one door or all of them.</para>
    /// </summary>
    internal static class DoorAuditCommand
    {
        [DevCommand("Audit doors: open state, orientation, and whether navmesh is walkable " +
                    "through them. Usage: vv_doors [radius=40] [x] [z]", Name = "vv_doors")]
        public static void Audit(Terminal.ConsoleEventArgs args)
        {
            var radius = args != null && args.Length > 1 && float.TryParse(args[1], out var r) ? r : 40f;

            // Centre on the player when there is one, else on a village anchor — on a dedicated
            // server there is no local player and the default would otherwise be world origin,
            // which finds nothing and reads as "no doors" rather than "wrong centre".
            var player = Player.m_localPlayer;
            var origin = player != null ? player.transform.position : Vector3.zero;
            if (player == null)
                foreach (var village in Villages.Entity.VillageRegistry.EnumerateAll())
                {
                    origin = village.Anchor;
                    break;
                }

            if (args != null && args.Length > 3 &&
                float.TryParse(args[2], out var ax) && float.TryParse(args[3], out var az))
                origin = new Vector3(ax, origin.y, az);

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            var sb = new StringBuilder($"[vv_doors] within {radius:F0}m of ({origin.x:F0},{origin.z:F0})");
            var walkable = 0;
            var total = 0;

            foreach (var door in Object.FindObjectsByType<Door>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (door == null || door.transform == null) continue;
                var pos = door.transform.position;
                if (Vector3.Distance(pos, origin) > radius) continue;
                total++;

                // Valheim keeps the open/closed flag on the ZDO; the animator only plays it.
                var zdo = door.GetComponent<ZNetView>()?.GetZDO();
                var state = zdo != null ? zdo.GetInt(ZDOVars.s_state) : int.MinValue;

                // Does the ROOT turn when the door opens? If it does, every orientation the
                // bake reads is state-dependent. Reported next to the state so the two can be
                // compared across an open/close.
                var euler = door.transform.rotation.eulerAngles;
                var fwd = door.transform.forward;

                // The panel child is what physically swings. Found by name rather than by
                // Animator: that type lives in a Unity module this assembly does not reference.
                var panel = door.transform.Find("door");

                var hit = NavMesh.SamplePosition(pos, out var nav, 0.6f, filter);
                if (hit) walkable++;

                sb.Append($"\n  ({pos.x:F1},{pos.y:F1},{pos.z:F1}) state={state,2} " +
                          $"rootYaw={euler.y:F0} fwd=({fwd.x:F2},{fwd.z:F2})");
                if (panel != null)
                    sb.Append($" panelYaw={panel.rotation.eulerAngles.y:F0}");
                sb.Append($"  navmesh={(hit ? $"WALKABLE at d={Vector3.Distance(pos, nav.position):F2}m" : "carved")}");
            }

            if (total == 0) sb.Append("\n  (no doors in range)");
            else sb.Append($"\n  {walkable} of {total} door(s) have WALKABLE navmesh at their centre " +
                           "(each is a wedge waiting to happen)");

            global::Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogInfo(sb.ToString());
        }
    }
}
