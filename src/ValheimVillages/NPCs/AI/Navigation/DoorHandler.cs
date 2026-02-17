using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.NPCs.AI
{
    /// <summary>
    /// Handles door interaction for NPC navigation.
    /// Detects blocking doors, opens them, and closes them after the NPC passes through.
    /// </summary>
    public class DoorHandler : MonoBehaviour
    {
        private Character m_character;
        private ZNetView m_znetView;

        /// <summary>
        /// Doors that were opened by this NPC and are pending closure.
        /// Key: Door instance, Value: Time when door should be closed.
        /// </summary>
        private readonly Dictionary<Door, float> m_pendingCloseDoors = new();

        /// <summary>
        /// Doors currently detected near the NPC.
        /// </summary>
        private readonly List<Door> m_nearbyDoors = new();

        private float m_lastDoorScanTime;

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

        /// <summary>
        /// Check for a closed door blocking the path to the target position.
        /// Returns the blocking door if found, null otherwise.
        /// </summary>
        public Door GetBlockingDoor(Vector3 targetPosition)
        {
            if (m_nearbyDoors.Count == 0)
                return null;

            Vector3 npcPosition = transform.position;
            Vector3 directionToTarget = (targetPosition - npcPosition).normalized;

            foreach (var door in m_nearbyDoors)
            {
                if (door == null) continue;

                // Check if door is closed
                if (!IsDoorClosed(door)) continue;

                // Check if door is player-built (has Piece component)
                if (!IsPlayerBuiltDoor(door)) continue;

                // Check if door is between NPC and target
                Vector3 doorPosition = door.transform.position;
                Vector3 npcToDoor = doorPosition - npcPosition;
                float distanceToDoor = npcToDoor.magnitude;

                // Only consider doors within detection radius
                if (distanceToDoor > DoorSettings.DoorDetectionRadius)
                    continue;

                // Check if door is roughly in the direction of the target
                float dotProduct = Vector3.Dot(directionToTarget, npcToDoor.normalized);
                if (dotProduct > 0.3f) // Door is somewhat in the direction we're trying to go
                {
                    return door;
                }
            }

            return null;
        }

        /// <summary>
        /// Open a door by setting its ZDO state.
        /// Opens the door AWAY from the NPC, matching Valheim's native door behavior:
        ///   userDir = (npcPos - doorPos).normalized
        ///   forward = Dot(doorForward, userDir) less than 0
        ///   state = forward ? 1 : -1
        /// </summary>
        public void OpenDoor(Door door)
        {
            if (door == null) return;

            var nview = GetDoorZNetView(door);
            if (nview == null || nview.GetZDO() == null) return;

            // Check if door is already open
            if (!IsDoorClosed(door)) return;

            // Compute open direction: door opens AWAY from the NPC
            // This matches Valheim's Door.Open(Vector3 userDir) logic exactly
            Vector3 userDir = (transform.position - door.transform.position).normalized;
            bool forward = Vector3.Dot(door.transform.forward, userDir) < 0f;
            int openState = forward ? 1 : -1;

            nview.GetZDO().Set(ZDOVars.s_state, openState);

            Plugin.Log?.LogDebug($"NPC opened door at {door.transform.position} (state={openState})");

            // Schedule the door to close after delay
            ScheduleClose(door);
        }

        /// <summary>
        /// Schedule a door to close after the configured delay.
        /// </summary>
        private void ScheduleClose(Door door)
        {
            float closeTime = Time.time + DoorSettings.DoorCloseDelay;

            // Update or add to pending closures
            if (m_pendingCloseDoors.ContainsKey(door))
            {
                m_pendingCloseDoors[door] = closeTime;
            }
            else
            {
                m_pendingCloseDoors.Add(door, closeTime);
            }
        }

        /// <summary>
        /// Process any doors that should be closed now.
        /// Only closes when NPC has moved away and is not in the door's swing path.
        /// </summary>
        private void ProcessPendingDoorClosures()
        {
            if (m_pendingCloseDoors.Count == 0) return;

            var doorsToRemove = new List<Door>();
            var doorsToReschedule = new List<Door>();

            // Iterate over a snapshot of the keys to avoid modification during enumeration
            var doorKeys = new List<Door>(m_pendingCloseDoors.Keys);

            foreach (var door in doorKeys)
            {
                if (!m_pendingCloseDoors.TryGetValue(door, out float closeTime))
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

        /// <summary>
        /// Check if the NPC has cleared the door and is not in its swing path.
        /// The door should only close when the NPC is either far enough away,
        /// or to the side of the door (not directly in front/behind where the door swings).
        /// </summary>
        private bool IsSafeToCLoseDoor(Door door)
        {
            float dist = Vector3.Distance(transform.position, door.transform.position);

            // Far enough away -- always safe
            if (dist > DoorSettings.DoorDetectionRadius + 1f)
                return true;

            // Still close -- only safe if NPC is to the side of the door, not in swing path
            Vector3 toDoor = (door.transform.position - transform.position).normalized;
            float dotForward = Mathf.Abs(Vector3.Dot(door.transform.forward, toDoor));

            // dotForward near 1.0 = NPC directly in front/behind (in swing path)
            // dotForward near 0.0 = NPC to the side (safe)
            return dotForward < 0.5f;
        }

        /// <summary>
        /// Close a door by setting its ZDO state.
        /// State 0 is always "closed" per Valheim's RPC_UseDoor logic.
        /// </summary>
        private void CloseDoor(Door door)
        {
            if (door == null) return;

            var nview = GetDoorZNetView(door);
            if (nview == null || nview.GetZDO() == null) return;

            // State 0 = closed (always, regardless of m_invertedOpenClosedText)
            nview.GetZDO().Set(ZDOVars.s_state, 0);

            Plugin.Log?.LogDebug($"NPC closed door at {door.transform.position}");
        }

        /// <summary>
        /// Scan for doors near the NPC.
        /// </summary>
        private void ScanForNearbyDoors()
        {
            m_nearbyDoors.Clear();

            var colliders = Physics.OverlapSphere(transform.position, DoorSettings.DoorDetectionRadius * 2f);
            foreach (var collider in colliders)
            {
                if (collider == null || collider.gameObject == null) continue;

                var door = collider.GetComponentInParent<Door>();
                if (door != null && !m_nearbyDoors.Contains(door))
                {
                    m_nearbyDoors.Add(door);
                }
            }
        }

        /// <summary>
        /// Check if a door is currently closed.
        /// Per Valheim's RPC_UseDoor: state 0 = closed, non-zero (1 or -1) = open.
        /// The m_invertedOpenClosedText flag only affects hover text, not actual state.
        /// </summary>
        private bool IsDoorClosed(Door door)
        {
            if (door == null) return false;

            var nview = GetDoorZNetView(door);
            if (nview == null || nview.GetZDO() == null)
                return false;

            return nview.GetZDO().GetInt(ZDOVars.s_state, 0) == 0;
        }

        /// <summary>
        /// Get the ZNetView component from a door.
        /// </summary>
        private ZNetView GetDoorZNetView(Door door)
        {
            if (door == null) return null;
            return door.GetComponent<ZNetView>();
        }

        /// <summary>
        /// Check if a door is player-built (has Piece component).
        /// </summary>
        private bool IsPlayerBuiltDoor(Door door)
        {
            if (door == null) return false;
            return door.GetComponent<Piece>() != null;
        }

        /// <summary>
        /// Called when component is destroyed.
        /// Close any doors we opened.
        /// </summary>
        private void OnDestroy()
        {
            // Close any doors we had opened
            foreach (var kvp in m_pendingCloseDoors)
            {
                if (kvp.Key != null)
                {
                    CloseDoor(kvp.Key);
                }
            }
            m_pendingCloseDoors.Clear();
        }
    }
}
