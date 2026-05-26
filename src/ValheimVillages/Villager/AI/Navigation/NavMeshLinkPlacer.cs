using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Algorithms;
using ValheimVillages.Settings;
using ValheimVillages.Villager.AI.Pathfinding;
using ValheimVillages.Villages;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Places NavMeshLinks between disconnected NavMesh islands caused by
    ///     Valheim's tile-based NavMesh system failing to stitch across tile
    ///     boundaries at elevated geometry (staircases, ramps).
    /// </summary>
    public static class NavMeshLinkPlacer
    {
        private const float AttemptCooldown = 5f;

        /// <summary>Probe grid step size in meters for island detection.</summary>
        private const float ProbeStep = 3f;

        /// <summary>Padding added around village bounds for probing (meters).</summary>
        private const float BoundsPadding = 5f;

        /// <summary>Fallback radius when no village bounds are available.</summary>
        private const float FallbackProbeRadius = 20f;

        /// <summary>Y probe range below lowest bed (meters).</summary>
        private const float ProbeYBelow = 3f;

        /// <summary>Y probe range above highest bed (meters).</summary>
        private const float ProbeYAbove = 20f;

        /// <summary>Y probe step size.</summary>
        private const float ProbeYStep = 1.5f;

        /// <summary>Maximum 3D distance between two probe positions to test connectivity.</summary>
        private const float MaxNeighborDist = 6f;

        /// <summary>Maximum 3D distance between two island points for a link to be placed.</summary>
        private const float MaxLinkDistance = 10f;

        /// <summary>Offset from door center along its forward axis to sample each side (m).</summary>
        private const float DoorProbeOffset = 0.5f;

        private static bool s_scanned;
        private static float s_lastAttemptTime;
        private static readonly List<(Vector3, Vector3)> s_emptyEndpoints = new();

        /// <summary>
        ///     Midpoint positions of placed door links, each paired with its Door reference.
        ///     Used by DoorHandler for proactive door opening along a path.
        /// </summary>
        private static readonly List<(Vector3 midpoint, Door door)> s_doorLinks = new();

        private static NavMeshLinkHolder s_holder;

        /// <summary>True if links have been placed at least once.</summary>
        public static bool HasLinks => Holder != null && Holder.LinkCount > 0;

        /// <summary>Start/end positions of all placed NavMeshLinks. Used by debug visualization.</summary>
        public static IReadOnlyList<(Vector3 start, Vector3 end)> LinkEndpoints =>
            Holder != null ? Holder.Endpoints : s_emptyEndpoints;

        public static IReadOnlyList<(Vector3 midpoint, Door door)> DoorLinks => s_doorLinks;

        private static NavMeshLinkHolder Holder
        {
            get
            {
                if (s_holder == null)
                {
                    var go = new GameObject("VV_NavMeshLinkHolder");
                    Object.DontDestroyOnLoad(go);
                    go.hideFlags = HideFlags.HideInHierarchy;
                    s_holder = go.AddComponent<NavMeshLinkHolder>();
                }

                return s_holder;
            }
        }

        /// <summary>
        ///     Scans the villager NavMesh for disconnected islands and bridges them.
        ///     Safe to call repeatedly; defers if NavMesh tiles aren't built yet.
        ///     Only scans once per tile generation until <see cref="RemoveAllLinks" /> resets.
        /// </summary>
        /// <returns>True if links were placed in this call.</returns>
        public static bool PlaceLinks()
        {
            if (s_scanned) return false;
            if (Time.time - s_lastAttemptTime < AttemptCooldown) return false;
            s_lastAttemptTime = Time.time;
            if (!VillagerAgentType.IsRegistered) return false;

            var agentTypeID = VillagerAgentType.UnityAgentTypeID;
            if (agentTypeID == 0) return false;

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = agentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            var placed = BridgeDisconnectedIslands(agentTypeID, filter);
            var doorLinks = PlaceDoorLinks(agentTypeID, filter);
            placed += doorLinks;

            if (placed >= 0)
            {
                s_scanned = true;
                Plugin.Log?.LogInfo(
                    $"[NavMeshLink] PlaceLinks: bridged {placed} gaps ({placed - doorLinks} island, {doorLinks} door) (agentTypeID={agentTypeID})");
            }

            return placed > 0;
        }

        /// <summary>
        ///     Probe the baked NavMesh at a grid of positions, discover disconnected
        ///     islands via CalculatePath connectivity, and bridge them.
        /// </summary>
        private static int BridgeDisconnectedIslands(int agentTypeID, NavMeshQueryFilter filter)
        {
            var beds = VillagerAIManager.GetAllBedPositions();
            if (beds == null || beds.Count == 0) return 0;

            float minBedY = float.MaxValue, maxBedY = float.MinValue;
            foreach (var bed in beds)
            {
                if (bed.y < minBedY) minBedY = bed.y;
                if (bed.y > maxBedY) maxBedY = bed.y;
            }

            float probeMinX, probeMaxX, probeMinZ, probeMaxZ;

            if (VillageAreaManager.TryGetCombinedBounds(out var vMinX, out var vMinZ, out var vMaxX, out var vMaxZ))
            {
                probeMinX = vMinX - BoundsPadding;
                probeMaxX = vMaxX + BoundsPadding;
                probeMinZ = vMinZ - BoundsPadding;
                probeMaxZ = vMaxZ + BoundsPadding;

                foreach (var bed in beds)
                {
                    if (bed.x - BoundsPadding < probeMinX) probeMinX = bed.x - BoundsPadding;
                    if (bed.x + BoundsPadding > probeMaxX) probeMaxX = bed.x + BoundsPadding;
                    if (bed.z - BoundsPadding < probeMinZ) probeMinZ = bed.z - BoundsPadding;
                    if (bed.z + BoundsPadding > probeMaxZ) probeMaxZ = bed.z + BoundsPadding;
                }
            }
            else
            {
                float cx = 0f, cz = 0f;
                foreach (var bed in beds)
                {
                    cx += bed.x;
                    cz += bed.z;
                }

                cx /= beds.Count;
                cz /= beds.Count;
                probeMinX = cx - FallbackProbeRadius;
                probeMaxX = cx + FallbackProbeRadius;
                probeMinZ = cz - FallbackProbeRadius;
                probeMaxZ = cz + FallbackProbeRadius;
            }

            var probeYMin = minBedY - ProbeYBelow;
            var probeYMax = maxBedY + ProbeYAbove;

            if (LogSettings.VerboseNavMesh)
                DebugLog.Throttled("navmesh_probe", "NavMeshLink", "probe_area",
                    ("x0", probeMinX), ("x1", probeMaxX),
                    ("z0", probeMinZ), ("z1", probeMaxZ),
                    ("y0", probeYMin), ("y1", probeYMax));
            else
                Plugin.Log?.LogDebug(
                    $"[NavMeshLink] probe_area x0={probeMinX:F0} x1={probeMaxX:F0} " +
                    $"z0={probeMinZ:F0} z1={probeMaxZ:F0} y0={probeYMin:F0} y1={probeYMax:F0}");

            const float sampleRadius = 4f;
            var allHits = new List<Vector3>();
            var seen = new HashSet<string>();

            for (var px = probeMinX; px <= probeMaxX; px += ProbeStep)
            for (var pz = probeMinZ; pz <= probeMaxZ; pz += ProbeStep)
            for (var py = probeYMin; py <= probeYMax; py += ProbeYStep)
            {
                var probe = new Vector3(px, py, pz);
                if (NavMesh.SamplePosition(probe, out var hit, sampleRadius, filter))
                {
                    var kx = Mathf.RoundToInt(hit.position.x * 2);
                    var ky = Mathf.RoundToInt(hit.position.y * 2);
                    var kz = Mathf.RoundToInt(hit.position.z * 2);
                    var key = $"{kx}_{ky}_{kz}";
                    if (seen.Add(key))
                        allHits.Add(hit.position);
                }
            }

            if (allHits.Count < 2) return -1;

            var maxNeighborDist2 = MaxNeighborDist * MaxNeighborDist;
            var cachedFilter = filter;

            if (!VillagerAgentType.TryGetSlope(out var maxSlope) ||
                !VillagerAgentType.TryGetClimb(out var maxClimb))
            {
                Plugin.Log?.LogError(
                    "[NavMeshLink] BridgeDisconnectedIslands: cannot evaluate path walkability " +
                    "because VillagerAgentType slope/climb are not yet available " +
                    "(Pathfinding.instance not alive or agent slot 31 not registered). " +
                    "Skipping island bridging this cycle.");
                return 0;
            }

            var islands = ConnectedIslands.FindIslands(
                allHits.Count,
                (a, b) =>
                {
                    var path = new NavMeshPath();
                    NavMesh.CalculatePath(allHits[a], allHits[b], cachedFilter, path);
                    return IsPathPhysicallyWalkableCore(path, maxSlope, maxClimb);
                },
                (a, b) =>
                {
                    var dx = allHits[a].x - allHits[b].x;
                    var dy = allHits[a].y - allHits[b].y;
                    var dz = allHits[a].z - allHits[b].z;
                    return dx * dx + dy * dy + dz * dz <= maxNeighborDist2;
                });

            Plugin.Log?.LogInfo(
                $"[NavMeshLink] Island detection: {allHits.Count} probe points, " +
                $"{islands.Count} islands found");

            if (islands.Count < 2) return 0;

            var placed = 0;
            for (var i = 0; i < islands.Count; i++)
            for (var j = i + 1; j < islands.Count; j++)
            {
                var bestDist = float.MaxValue;
                Vector3 bestA = default, bestB = default;

                foreach (var ai in islands[i])
                foreach (var bi in islands[j])
                {
                    var d = Vector3.Distance(allHits[ai], allHits[bi]);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestA = allHits[ai];
                        bestB = allHits[bi];
                    }
                }

                if (bestDist > MaxLinkDistance)
                {
                    Plugin.Log?.LogDebug(
                        $"[NavMeshLink] Skipping island pair {i}-{j}: " +
                        $"closest distance {bestDist:F1}m exceeds MaxLinkDistance {MaxLinkDistance}m");
                    continue;
                }

                var testPath = new NavMeshPath();
                NavMesh.CalculatePath(bestA, bestB, filter, testPath);
                if (IsPathPhysicallyWalkableCore(testPath, maxSlope, maxClimb))
                    continue;

                Plugin.Log?.LogInfo(
                    $"[NavMeshLink] Bridging islands {i} ({islands[i].Count} pts) " +
                    $"and {j} ({islands[j].Count} pts) at distance {bestDist:F1}m");

                // Snap to NavMesh edges facing the gap so the link
                // bridges the actual gap, not interior probe points.
                Vector3 linkA = bestA, linkB = bestB;
                if (NavMesh.FindClosestEdge(bestA, out var eA, filter))
                    linkA = eA.position;
                if (NavMesh.FindClosestEdge(bestB, out var eB, filter))
                    linkB = eB.position;

                var edgeDist = Vector3.Distance(linkA, linkB);
                if (edgeDist > 1.0f)
                {
                    Plugin.Log?.LogDebug(
                        $"[NavMeshLink] Skipping island pair {i}-{j}: " +
                        $"edge-to-edge distance {edgeDist:F2}m exceeds 0.5m");
                    continue;
                }

                if (TryAddLink(linkA, linkB, agentTypeID))
                    placed++;
            }

            return placed;
        }

        /// <summary>
        ///     Find all doors with a Piece parent and place a NavMeshLink across each
        ///     one that has disconnected NavMesh on opposite sides.
        /// </summary>
        private static int PlaceDoorLinks(int agentTypeID, NavMeshQueryFilter filter)
        {
            var doors = Object.FindObjectsByType<Door>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (doors == null || doors.Length == 0) return 0;

            var placed = 0;
            foreach (var door in doors)
            {
                if (door == null || door.GetComponentInParent<Piece>() == null) continue;

                var pos = door.transform.position;
                var fwd = door.transform.forward;
                fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.01f) continue;
                fwd.Normalize();

                var sideA = pos - fwd * DoorProbeOffset;
                var sideB = pos + fwd * DoorProbeOffset;

                if (!NavMesh.SamplePosition(sideA, out var hitA, 2f, filter)) continue;
                if (!NavMesh.SamplePosition(sideB, out var hitB, 2f, filter)) continue;

                var path = new NavMeshPath();
                NavMesh.CalculatePath(hitA.position, hitB.position, filter, path);
                if (!TryIsPathPhysicallyWalkable(path, out var walkable))
                {
                    Plugin.Log?.LogError(
                        "[NavMeshLink] Skipping door at " + pos + ": cannot evaluate path walkability " +
                        "because VillagerAgentType slope/climb are not yet available " +
                        "(Pathfinding.instance not alive or agent slot 31 not registered). " +
                        "Caller should defer door-link placement until registration completes.");
                    continue;
                }

                if (walkable) continue;

                if (TryAddLink(hitA.position, hitB.position, agentTypeID))
                {
                    s_doorLinks.Add((pos, door));
                    placed++;
                }
            }

            if (placed > 0)
                Plugin.Log?.LogInfo($"[NavMeshLink] Door links: {placed} placed across {doors.Length} doors");

            return placed;
        }

        /// <summary>
        ///     Tries to determine whether a NavMesh path is physically walkable for the villager
        ///     agent by checking per-segment slope/climb against the live agent settings.
        ///     Returns false when slope/climb cannot be read (villager agent slot not registered
        ///     yet); the caller must not substitute a default in that case.
        /// </summary>
        private static bool TryIsPathPhysicallyWalkable(NavMeshPath path, out bool walkable)
        {
            walkable = false;
            if (!VillagerAgentType.TryGetSlope(out var maxSlope) ||
                !VillagerAgentType.TryGetClimb(out var maxClimb))
                return false;
            walkable = IsPathPhysicallyWalkableCore(path, maxSlope, maxClimb);
            return true;
        }

        /// <summary>
        ///     Pure walkability math with caller-supplied slope/climb. Callers must obtain those
        ///     from <see cref="VillagerAgentType.TryGetSlope" />/<see cref="VillagerAgentType.TryGetClimb" />
        ///     — there are no synthesised defaults inside this helper, by design.
        /// </summary>
        private static bool IsPathPhysicallyWalkableCore(NavMeshPath path, float maxSlope, float maxClimb)
        {
            if (path.status != NavMeshPathStatus.PathComplete) return false;
            if (path.corners == null || path.corners.Length < 2) return true;
            for (var k = 1; k < path.corners.Length; k++)
            {
                var dy = Mathf.Abs(path.corners[k].y - path.corners[k - 1].y);
                var dxh = path.corners[k].x - path.corners[k - 1].x;
                var dzh = path.corners[k].z - path.corners[k - 1].z;
                var horiz = Mathf.Sqrt(dxh * dxh + dzh * dzh);
                if (horiz > 0.01f)
                {
                    var slope = Mathf.Atan2(dy, horiz) * Mathf.Rad2Deg;
                    if (slope > maxSlope) return false;
                }
                else if (dy > maxClimb)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryAddLink(Vector3 start, Vector3 end, int agentTypeID)
        {
            var linkData = new NavMeshLinkData
            {
                startPosition = start,
                endPosition = end,
                width = 1f,
                bidirectional = true,
                area = 0,
                agentTypeID = agentTypeID,
            };

            var instance = NavMesh.AddLink(linkData);
            if (NavMesh.IsLinkValid(instance))
            {
                Holder.AddLink(instance, start, end);
                return true;
            }

            return false;
        }

        /// <summary>
        ///     Removes all previously placed links and resets the scan flag,
        ///     allowing <see cref="PlaceLinks" /> to re-scan on next call.
        /// </summary>
        public static void RemoveAllLinks()
        {
            if (s_holder != null)
                s_holder.RemoveAll();
            s_doorLinks.Clear();
            s_scanned = false;
        }

        #region HNA Candidate Visualization

        /// <summary>
        ///     Classification of an HNA link candidate for debug rendering.
        /// </summary>
        public enum HnaCandidateStatus
        {
            /// <summary>NavMesh already connects these points — no link needed.</summary>
            AlreadyConnected,

            /// <summary>NavMesh gap exists — link would be placed here.</summary>
            NeedsLink,

            /// <summary>Wall blocks the straight path between endpoints.</summary>
            WallBlocked,

            /// <summary>Insufficient headroom at one or both endpoints.</summary>
            NoClearance,
        }

        /// <summary>
        ///     A candidate link derived from HNA graph data, with its validation status.
        /// </summary>
        public struct HnaCandidate
        {
            public Vector3 Start;
            public Vector3 End;
            public RegionLinkType LinkType;
            public HnaCandidateStatus Status;
        }

        private static readonly List<HnaCandidate> s_hnaCandidates = new();

        /// <summary>Computed HNA link candidates for debug rendering.</summary>
        public static IReadOnlyList<HnaCandidate> HnaCandidates => s_hnaCandidates;

        /// <summary>True if HNA candidates have been computed at least once.</summary>
        public static bool LinkCandidatesReady { get; private set; }

        private const float WallCheckHeight = 1.0f;
        private const float WallSphereRadius = 0.35f;
        private const float PlayerClearanceHeight = 2.0f;

        private static readonly int WallCheckMask =
            LayerMask.GetMask("Default", "static_solid", "terrain", "piece");

        private static readonly int ClearanceMask =
            LayerMask.GetMask("Default", "static_solid", "terrain", "piece");

        /// <summary>
        ///     Evaluate all HNA Stair/Slope links and classify each as a candidate
        ///     for NavMesh link placement. Does NOT place any links — purely diagnostic.
        ///     Call once after HNA graph is available.
        /// </summary>
        public static void ComputeLinkCandidates()
        {
            s_hnaCandidates.Clear();
            LinkCandidatesReady = false;


            if (!RegionGraph.IsAnyAvailable) return;
            if (!VillagerAgentType.IsRegistered) return;

            var agentTypeID = VillagerAgentType.UnityAgentTypeID;
            if (agentTypeID == 0) return;

            if (!VillagerAgentType.TryGetSlope(out var maxSlope) ||
                !VillagerAgentType.TryGetClimb(out var maxClimb))
            {
                Plugin.Log?.LogError(
                    "[NavMeshLink] ComputeLinkCandidates: IsRegistered=true but slope/climb " +
                    "could not be resolved. Aborting candidate computation this cycle.");
                return;
            }

            var filter = new NavMeshQueryFilter
            {
                agentTypeID = agentTypeID,
                areaMask = NavMesh.AllAreas,
            };

            var links = new List<RegionLink>();
            foreach (var graph in RegionGraph.GetAll())
            {
                var graphLinks = graph.GetAllLinks();
                if (graphLinks != null)
                    links.AddRange(graphLinks);
            }

            if (links.Count == 0) return;

            var wallMask = WallCheckMask != 0 ? WallCheckMask : ~0;
            var clearMask = ClearanceMask != 0 ? ClearanceMask : ~0;

            var dbgProcessed = 0;
            foreach (var link in links)
            {
                if (link.LinkType != RegionLinkType.Stair && link.LinkType != RegionLinkType.Slope)
                    continue;

                dbgProcessed++;

                // Snap to nearest NavMesh edges so rendering shows
                // exactly where the NavMesh surface ends on each side.
                var edgeA = link.PositionStart;
                var edgeB = link.PositionEnd;
                if (NavMesh.FindClosestEdge(link.PositionStart, out var edgeHitA, filter))
                    edgeA = edgeHitA.position;
                if (NavMesh.FindClosestEdge(link.PositionEnd, out var edgeHitB, filter))
                    edgeB = edgeHitB.position;

                var candidate = new HnaCandidate
                {
                    Start = edgeA,
                    End = edgeB,
                    LinkType = link.LinkType,
                };

                // Check if NavMesh already connects these points
                var path = new NavMeshPath();
                var sampledA = NavMesh.SamplePosition(link.PositionStart, out var hitA, 4f, filter);
                var sampledB = NavMesh.SamplePosition(link.PositionEnd, out var hitB, 4f, filter);

                if (sampledA && sampledB)
                {
                    NavMesh.CalculatePath(hitA.position, hitB.position, filter, path);
                    if (IsPathPhysicallyWalkableCore(path, maxSlope, maxClimb))
                    {
                        candidate.Status = HnaCandidateStatus.AlreadyConnected;
                        s_hnaCandidates.Add(candidate);
                        continue;
                    }
                }

                // Check clearance at both endpoints
                if (!HasClearanceAbove(link.PositionStart, clearMask) ||
                    !HasClearanceAbove(link.PositionEnd, clearMask))
                {
                    candidate.Status = HnaCandidateStatus.NoClearance;
                    s_hnaCandidates.Add(candidate);
                    continue;
                }

                // Check wall obstruction
                if (IsWallBlocking(link.PositionStart, link.PositionEnd, wallMask))
                {
                    candidate.Status = HnaCandidateStatus.WallBlocked;
                    s_hnaCandidates.Add(candidate);
                    continue;
                }

                candidate.Status = HnaCandidateStatus.NeedsLink;
                s_hnaCandidates.Add(candidate);
            }

            LinkCandidatesReady = true;
            int needs = 0, connected = 0, walled = 0, noClr = 0;
            foreach (var c in s_hnaCandidates)
                switch (c.Status)
                {
                    case HnaCandidateStatus.NeedsLink: needs++; break;
                    case HnaCandidateStatus.AlreadyConnected: connected++; break;
                    case HnaCandidateStatus.WallBlocked: walled++; break;
                    case HnaCandidateStatus.NoClearance: noClr++; break;
                }

            DebugLog.Event("NavMeshLink", "hna_candidates",
                ("total", s_hnaCandidates.Count), ("need_link", needs), ("already_connected", connected),
                ("wall_blocked", walled), ("no_clearance", noClr));
        }

        private static bool HasClearanceAbove(Vector3 pos, int mask)
        {
            var origin = new Vector3(pos.x, pos.y + 0.15f, pos.z);
            return !Physics.Raycast(origin, Vector3.up, PlayerClearanceHeight, mask,
                QueryTriggerInteraction.Ignore);
        }

        private static bool IsWallBlocking(Vector3 from, Vector3 to, int mask)
        {
            var wallY = Mathf.Max(from.y, to.y) + WallCheckHeight;
            var wFrom = new Vector3(from.x, wallY, from.z);
            var wTo = new Vector3(to.x, wallY, to.z);
            var wDir = wTo - wFrom;
            var wDist = wDir.magnitude;
            if (wDist < 0.1f) return false;
            return Physics.SphereCast(wFrom, WallSphereRadius, wDir / wDist,
                out _, wDist, mask, QueryTriggerInteraction.Ignore);
        }

        #endregion
    }

    /// <summary>
    ///     DontDestroyOnLoad MonoBehaviour that holds NavMeshLinkInstance handles.
    ///     Survives scene transitions; during hot reload, the stale component sweep
    ///     destroys the old assembly's instance, triggering OnDestroy which removes
    ///     the old NavMesh links from Unity before the new assembly rescans.
    /// </summary>
    internal class NavMeshLinkHolder : MonoBehaviour
    {
        private readonly List<(Vector3 start, Vector3 end)> m_endpoints = new();
        private readonly List<NavMeshLinkInstance> m_links = new();

        internal int LinkCount => m_links.Count;
        internal IReadOnlyList<(Vector3 start, Vector3 end)> Endpoints => m_endpoints;

        private void OnDestroy()
        {
            var count = m_links.Count;
            foreach (var link in m_links)
                NavMesh.RemoveLink(link);
            m_links.Clear();
            m_endpoints.Clear();

            if (count > 0)
                Plugin.Log?.LogInfo($"[NavMeshLinkHolder] OnDestroy: removed {count} orphaned NavMesh links");
        }

        internal void AddLink(NavMeshLinkInstance instance, Vector3 start, Vector3 end)
        {
            m_links.Add(instance);
            m_endpoints.Add((start, end));
        }

        internal void RemoveAll()
        {
            foreach (var link in m_links)
                NavMesh.RemoveLink(link);
            m_links.Clear();
            m_endpoints.Clear();
        }
    }
}