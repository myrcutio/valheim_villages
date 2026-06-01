using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Settings;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Handles door interaction for NPC navigation.
    ///     Detects blocking doors, opens them, and closes them after the NPC passes through.
    /// </summary>
    public class DoorHandler : MonoBehaviour
    {
        private const float DoorCloseBlockRadius = 3f;

        /// <summary>
        ///     Doors currently detected near the NPC.
        /// </summary>
        private readonly List<Door> m_nearbyDoors = new();

        /// <summary>
        ///     Doors that were opened by this NPC and are pending closure.
        ///     Key: Door instance, Value: Time when door should be closed.
        /// </summary>
        private readonly Dictionary<Door, float> m_pendingCloseDoors = new();

        private Character m_character;

        private float m_lastDoorScanTime;
        private ZNetView m_znetView;

        private void Awake()
        {
            m_character = GetComponent<Character>();
            m_znetView = GetComponent<ZNetView>();
        }

        private void Update()
        {
            // Process pending door closures
            ProcessPendingDoorClosures();

            // Periodically scan for nearby doors
            if (Time.time - m_lastDoorScanTime > 0.5f)
            {
                m_lastDoorScanTime = Time.time;
                ScanForNearbyDoors();
            }
        }

        private void OnDestroy()
        {
            foreach (var kvp in m_pendingCloseDoors)
                if (kvp.Key != null)
                    CloseDoor(kvp.Key);

            m_pendingCloseDoors.Clear();
        }

        /// <summary>
        ///     Check for a closed door blocking the path to the target position.
        ///     Returns the blocking door if found, null otherwise.
        /// </summary>
        public Door GetBlockingDoor(Vector3 targetPosition)
        {
            DebugLog.Append("DoorHandler.cs:entry", "GetBlockingDoor_called", new Dictionary<string, object>
            {
                { "nearbyCount", m_nearbyDoors.Count },
                { "npcPos", transform.position.ToString("F2") },
                { "targetPos", targetPosition.ToString("F2") },
            }, "H3H4", "run1");
            if (m_nearbyDoors.Count == 0)
                return null;

            var npcPosition = transform.position;
            var directionToTarget = (targetPosition - npcPosition).normalized;

            foreach (var door in m_nearbyDoors)
            {
                if (door == null) continue;

                var closed = IsDoorClosed(door);
                var playerBuilt = IsPlayerBuiltDoor(door);
                var doorPosition = door.transform.position;
                var npcToDoor = doorPosition - npcPosition;
                var distanceToDoor = npcToDoor.magnitude;
                var dotProduct = Vector3.Dot(directionToTarget, npcToDoor.normalized);

                DebugLog.Append("DoorHandler.cs:eval", "door_eval", new Dictionary<string, object>
                {
                    { "doorPos", doorPosition.ToString("F2") },
                    { "closed", closed },
                    { "playerBuilt", playerBuilt },
                    { "distance", distanceToDoor },
                    { "dotProduct", dotProduct },
                    { "detectionRadius", DoorSettings.DoorDetectionRadius },
                }, "H3H4", "run1");

                if (!closed) continue;
                if (!playerBuilt) continue;
                if (distanceToDoor > DoorSettings.DoorDetectionRadius) continue;

                if (dotProduct > 0.3f) return door;
            }

            return null;
        }


        /// <summary>
        ///     True iff the line segment from <paramref name="a" /> to
        ///     <paramref name="b" /> crosses the door's perpendicular plane
        ///     (passes from one side of <paramref name="doorForward" /> to
        ///     the other) AND the crossing point lies within
        ///     <paramref name="doorwayHalfWidth" /> of the door midpoint
        ///     horizontally.
        /// </summary>
        private static bool SegmentCrossesDoor(
            Vector3 a, Vector3 b, Vector3 doorMidpoint, Vector3 doorForward, float doorwayHalfWidth)
        {
            var aDot = Vector3.Dot(a - doorMidpoint, doorForward);
            var bDot = Vector3.Dot(b - doorMidpoint, doorForward);

            // Same-sign means both endpoints are on the same side of the
            // door plane; no crossing.
            if ((aDot >= 0f) == (bDot >= 0f)) return false;

            // Parameterize the crossing point along a -> b. aDot / (aDot - bDot)
            // is the t-value where the forward-axis projection equals zero
            // (i.e. the segment crosses the door plane).
            var denom = aDot - bDot;
            if (Mathf.Abs(denom) < 1e-5f) return false;
            var t = aDot / denom;
            var cross = Vector3.Lerp(a, b, t);

            // Distance from the crossing point to the door midpoint,
            // ignoring vertical so a door at Y=1 isn't gated by floor-level
            // crossings being a bit below.
            var dx = cross.x - doorMidpoint.x;
            var dz = cross.z - doorMidpoint.z;
            return dx * dx + dz * dz <= doorwayHalfWidth * doorwayHalfWidth;
        }

        /// <summary>
        ///     Open a door by setting its ZDO state.
        ///     Opens the door AWAY from the NPC, matching Valheim's native door behavior.
        /// </summary>
        public void OpenDoor(Door door)
        {
            if (door == null) return;

            var nview = GetDoorZNetView(door);
            if (nview == null || nview.GetZDO() == null) return;

            if (!IsDoorClosed(door)) return;

            var userDir = (transform.position - door.transform.position).normalized;
            var forward = Vector3.Dot(door.transform.forward, userDir) < 0f;
            var openState = forward ? 1 : -1;

            DebugLog.Append("DoorHandler.cs:open", "open_door", new Dictionary<string, object>
            {
                { "npcPos", transform.position.ToString("F2") },
                { "doorPos", door.transform.position.ToString("F2") },
                { "doorFwd", door.transform.forward.ToString("F2") },
                { "userDir", userDir.ToString("F2") },
                { "dot", Vector3.Dot(door.transform.forward, userDir) },
                { "forward", forward },
                { "openState", openState },
            }, "direction", "run1");

            nview.GetZDO().Set(ZDOVars.s_state, openState);

            Plugin.Log?.LogDebug($"NPC opened door at {door.transform.position} (state={openState})");

            ScheduleClose(door);
        }

        private void ScheduleClose(Door door)
        {
            var closeTime = Time.time + DoorSettings.DoorCloseDelay;

            if (m_pendingCloseDoors.ContainsKey(door))
                m_pendingCloseDoors[door] = closeTime;
            else
                m_pendingCloseDoors.Add(door, closeTime);
        }

        private void ProcessPendingDoorClosures()
        {
            if (m_pendingCloseDoors.Count == 0) return;

            var doorsToRemove = new List<Door>();
            var doorsToReschedule = new List<Door>();

            var doorKeys = new List<Door>(m_pendingCloseDoors.Keys);

            foreach (var door in doorKeys)
            {
                if (!m_pendingCloseDoors.TryGetValue(door, out var closeTime))
                    continue;

                if (Time.time < closeTime) continue;

                if (door == null)
                {
                    doorsToRemove.Add(door);
                    continue;
                }

                if (IsSafeToCLoseDoor(door))
                {
                    CloseDoor(door);
                    doorsToRemove.Add(door);
                }
                else
                {
                    doorsToReschedule.Add(door);
                }
            }

            foreach (var door in doorsToReschedule)
                m_pendingCloseDoors[door] = Time.time + 0.5f;

            foreach (var door in doorsToRemove)
                m_pendingCloseDoors.Remove(door);
        }

        private bool IsSafeToCLoseDoor(Door door)
        {
            var doorPos = door.transform.position;
            var nearby = Physics.OverlapSphere(doorPos, DoorCloseBlockRadius);
            foreach (var col in nearby)
            {
                if (col == null) continue;
                var character = col.GetComponentInParent<Character>();
                if (character != null)
                    return false;
            }

            return true;
        }

        private void CloseDoor(Door door)
        {
            if (door == null) return;

            var nview = GetDoorZNetView(door);
            if (nview == null || nview.GetZDO() == null) return;

            nview.GetZDO().Set(ZDOVars.s_state, 0);

            Plugin.Log?.LogDebug($"NPC closed door at {door.transform.position}");
        }

        private void ScanForNearbyDoors()
        {
            m_nearbyDoors.Clear();

            var colliders = Physics.OverlapSphere(transform.position, DoorSettings.DoorDetectionRadius * 2f);
            foreach (var collider in colliders)
            {
                if (collider == null || collider.gameObject == null) continue;

                var door = collider.GetComponentInParent<Door>();
                if (door != null && !m_nearbyDoors.Contains(door)) m_nearbyDoors.Add(door);
            }

            if (m_nearbyDoors.Count > 0)
                DebugLog.Append("DoorHandler.cs:scan", "scan_result", new Dictionary<string, object>
                {
                    { "npcPos", transform.position.ToString("F2") },
                    { "doorsFound", m_nearbyDoors.Count },
                    { "scanRadius", DoorSettings.DoorDetectionRadius * 2f },
                }, "H4", "run1");
        }

        private bool IsDoorClosed(Door door)
        {
            if (door == null) return false;

            var nview = GetDoorZNetView(door);
            if (nview == null || nview.GetZDO() == null)
                return false;

            return nview.GetZDO().GetInt(ZDOVars.s_state) == 0;
        }

        private ZNetView GetDoorZNetView(Door door)
        {
            if (door == null) return null;
            return door.GetComponent<ZNetView>();
        }

        private bool IsPlayerBuiltDoor(Door door)
        {
            if (door == null) return false;
            return door.GetComponent<Piece>() != null;
        }
    }
}