using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Bridges every doorway with a NavMesh link, so a villager can walk THROUGH a door
    ///     instead of only standing next to one.
    ///
    ///     <para><b>Why a link and not mesh.</b> <c>NavMeshBakeManager.AddDoorBlockers</c>
    ///     deliberately punches a NotWalkable box through every <see cref="Door" />, and that is
    ///     correct: a doorway left open must not let the outside-flood pour in and reclassify a
    ///     whole village as outside its own walls. The intended other half was a link across each
    ///     doorway — the bake's own comment still refers to a <c>PlaceDoorLinks</c> — but the
    ///     placer was removed and <c>RegionBuilder.CollectDoorLinks</c> was left commented out
    ///     behind a "re-enable once the region graph is validated" TODO. What remained was a
    ///     village whose doors are walls.</para>
    ///
    ///     <para>Measured on a live server: a walled village with a sound gate and clean stairs
    ///     either side. The Lumberjack could path to the top of the stairs at
    ///     (-98.2,36.0,-397.4) and not one step further; the cell past the gate probed
    ///     <c>WallBlocks=TRUE hits=[door;door]</c> with the nearest navmesh 1.7m back inside. His
    ///     woodlot was 60m beyond it. A villager could not leave his own village.</para>
    ///
    ///     <para>Uses the runtime <see cref="NavMesh.AddLink(NavMeshLinkData)" /> rather than a
    ///     <c>NavMeshLink</c> component, for the same reason the bake uses
    ///     <c>NavMesh.AddNavMeshData</c>: it needs no package the game does not ship, and the
    ///     handles are ours to remove on the next rebuild. The agent crosses these manually —
    ///     <c>VillagerAI</c> sets <c>autoTraverseOffMeshLink = false</c> and walks the link
    ///     itself — while the door-opening behaviour swings the door as it goes.</para>
    /// </summary>
    public static class DoorLinkPlacer
    {
        /// <summary>
        ///     How far either side of the door the endpoints sit. Past the 0.3m-deep NotWalkable
        ///     box the bake puts through the doorway, but close enough that both ends land on
        ///     the floor either side rather than out in the room.
        /// </summary>
        private const float SideOffset = 1.1f;

        /// <summary>How far an endpoint may snap to find the mesh on its side.</summary>
        private const float SnapRadius = 1.5f;

        /// <summary>Doorway width the link spans.</summary>
        private const float LinkWidth = 1f;

        /// <summary>
        ///     Bounds on how far apart a door link's ends may be. Shorter than
        ///     <see cref="MinLinkSpan" /> and the two ends are effectively the same place;
        ///     longer than <see cref="MaxLinkSpan" /> and it is not a doorway but a leap across
        ///     something, which the agent would attempt in a straight line.
        /// </summary>
        private const float MinLinkSpan = 0.8f;

        private const float MaxLinkSpan = 3.5f;

        /// <summary>
        ///     How far out from a door the doorstep reaches, along the door's own axis. A gate
        ///     in the perimeter has the outside-flood pressed right up against its far face, and
        ///     the bake stamps every outside cell NotWalkable — so without this the link has
        ///     nothing to land on and the door is still a wall. Measured at a real gate: the
        ///     nearest navmesh on the outer side was 1.7m back INSIDE, with the next walkable
        ///     ground 3m further out where the woodlot lane had claimed it.
        /// </summary>
        private const float ApronReach = 3f;

        /// <summary>
        ///     Half-width of that doorstep. Deliberately narrow — this claims the ground you
        ///     step onto leaving a door, not a bite out of the perimeter. The hull everywhere
        ///     else stays sealed, and the bake still respects colliders, so no mesh appears
        ///     where a wall actually stands.
        /// </summary>
        private const float ApronHalfWidth = 1.2f;

        private static readonly List<NavMeshLinkInstance> s_links = new();

        /// <summary>
        ///     Reclaim the ground immediately either side of each door from the outside-flood,
        ///     so a doorway has somewhere to stand at both ends.
        ///
        ///     <para>Runs BEFORE the outside-cell blockers are built, exactly like
        ///     <c>ForesterPost.UnblockWoodlotCells</c>, and must be called from BOTH the bake and
        ///     the Pass-1 prune for the same reason that one is: the bake decides where navmesh
        ///     exists and the flood decides what counts as anchor-reachable, and a disagreement
        ///     between them is walkable ground the graph refuses to admit exists.</para>
        /// </summary>
        public static int UnblockDoorAprons(
            HashSet<long> outsideCells, Bounds bounds, HashSet<long> extramural = null)
        {
            if (outsideCells == null || outsideCells.Count == 0) return 0;

            var freed = 0;
            var doors = Object.FindObjectsByType<Door>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var door in doors)
            {
                if (door == null || door.transform == null) continue;
                if (!bounds.Contains(door.transform.position)) continue;
                if (door.GetComponentInParent<Piece>() == null) continue;

                var pos = door.transform.position;
                var fwd = door.transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) continue;
                fwd.Normalize();

                freed += FreeSegment(outsideCells,
                    pos - fwd * ApronReach, pos + fwd * ApronReach, ApronHalfWidth, extramural);
            }

            if (freed > 0)
                Plugin.Log?.LogInfo(
                    $"[DoorLinks] Doorsteps: freed {freed} outside cell(s) across " +
                    $"{doors.Length} door(s), so gates have walkable ground on both sides");
            return freed;
        }

        /// <summary>Free every outside cell within <paramref name="radius" /> of the segment a→b.</summary>
        private static int FreeSegment(
            HashSet<long> outsideCells, Vector3 a, Vector3 b, float radius,
            HashSet<long> extramural = null)
        {
            var cellSize = RegionGraph.LookupCellSize;
            var minX = Mathf.FloorToInt((Mathf.Min(a.x, b.x) - radius) / cellSize);
            var maxX = Mathf.FloorToInt((Mathf.Max(a.x, b.x) + radius) / cellSize);
            var minZ = Mathf.FloorToInt((Mathf.Min(a.z, b.z) - radius) / cellSize);
            var maxZ = Mathf.FloorToInt((Mathf.Max(a.z, b.z) + radius) / cellSize);
            var r2 = radius * radius;

            var abx = b.x - a.x;
            var abz = b.z - a.z;
            var abLen2 = abx * abx + abz * abz;

            var freed = 0;
            for (var cx = minX; cx <= maxX; cx++)
            for (var cz = minZ; cz <= maxZ; cz++)
            {
                var wx = (cx + 0.5f) * cellSize;
                var wz = (cz + 0.5f) * cellSize;

                var t = abLen2 > 0.0001f
                    ? Mathf.Clamp01(((wx - a.x) * abx + (wz - a.z) * abz) / abLen2)
                    : 0f;
                var dx = wx - (a.x + abx * t);
                var dz = wz - (a.z + abz * t);
                if (dx * dx + dz * dz > r2) continue;

                var key = RegionGraph.PackXz(cx, cz);
                if (!outsideCells.Remove(key)) continue;
                extramural?.Add(key);
                freed++;
            }

            return freed;
        }

        /// <summary>
        ///     Drop every link we placed. Also the hot-reload cleanup: a link outlives the
        ///     assembly that made it, and a second copy of this class would otherwise add a
        ///     duplicate at every door it could no longer see to remove.
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            foreach (var link in s_links)
                if (NavMesh.IsLinkValid(link))
                    NavMesh.RemoveLink(link);
            s_links.Clear();
        }

        /// <summary>
        ///     Replace every door link inside <paramref name="bounds" />; returns how many were
        ///     placed. A door whose two sides do not BOTH have navmesh is skipped: a link with
        ///     an endpoint off the mesh is worse than no link, because the agent commits to
        ///     crossing it and then has nowhere to land.
        /// </summary>
        public static int Rebuild(Bounds bounds)
        {
            Clear();
            if (!VillagerAgentType.IsRegistered) return 0;

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = VillagerAgentType.UnityAgentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            var skipped = 0;
            var doors = Object.FindObjectsByType<Door>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var door in doors)
            {
                if (door == null || door.transform == null) continue;
                if (!bounds.Contains(door.transform.position)) continue;
                if (door.GetComponentInParent<Piece>() == null) continue;

                var pos = door.transform.position;
                var fwd = door.transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) continue;
                fwd.Normalize();

                if (!NavMesh.SamplePosition(pos - fwd * SideOffset, out var a, SnapRadius, filter) ||
                    !NavMesh.SamplePosition(pos + fwd * SideOffset, out var b, SnapRadius, filter))
                {
                    skipped++;
                    continue;
                }

                // Both ends snapped — but to WHERE? A snap radius of 1.5m around points 1.1m
                // apart can pull both endpoints onto the same side of the door, or sideways
                // along the wall. That link is not a doorway: it is a shortcut to nowhere, and
                // the agent drives a link in a straight line with no route planning, so a bad
                // one walks the villager into whatever is in the way. Require the two ends to
                // sit on OPPOSITE sides of the door plane and the link to be roughly the length
                // a doorway should be.
                var acrossA = Vector3.Dot(a.position - pos, fwd);
                var acrossB = Vector3.Dot(b.position - pos, fwd);
                if (acrossA >= 0f || acrossB <= 0f)
                {
                    skipped++;
                    continue;
                }

                var span = Vector3.Distance(a.position, b.position);
                if (span < MinLinkSpan || span > MaxLinkSpan)
                {
                    skipped++;
                    continue;
                }

                var instance = NavMesh.AddLink(new NavMeshLinkData
                {
                    startPosition = a.position,
                    endPosition = b.position,
                    width = LinkWidth,
                    costModifier = -1f,
                    bidirectional = true,
                    area = 0, // Walkable
                    agentTypeID = VillagerAgentType.UnityAgentTypeID,
                });

                if (NavMesh.IsLinkValid(instance)) s_links.Add(instance);
                else skipped++;
            }

            Plugin.Log?.LogInfo(
                $"[DoorLinks] {s_links.Count} doorway(s) bridged" +
                (skipped > 0 ? $", {skipped} skipped (no navmesh on both sides)" : ""));
            return s_links.Count;
        }
    }
}
