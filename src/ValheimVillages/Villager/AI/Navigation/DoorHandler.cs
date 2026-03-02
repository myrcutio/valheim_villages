using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Settings;

namespace ValheimVillages.Villager.AI.Navigation
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
        /// Opens the door AWAY from the NPC, matching Valheim's native door behavior.
        /// </summary>
        public void OpenDoor(Door door)
        {
            if (door == null) return;

            var nview = GetDoorZNetView(door);
            if (nview == null || nview.GetZDO() == null) return;

            if (!IsDoorClosed(door)) return;

            Vector3 userDir = (transform.position - door.transform.position).normalized;
            bool forward = Vector3.Dot(door.transform.forward, userDir) < 0f;
            int openState = forward ? 1 : -1;

            nview.GetZDO().Set(ZDOVars.s_state, openState);

            Plugin.Log?.LogDebug($"NPC opened door at {door.transform.position} (state={openState})");

            ScheduleClose(door);
        }

        private void ScheduleClose(Door door)
        {
            float closeTime = Time.time + DoorSettings.DoorCloseDelay;

            if (m_pendingCloseDoors.ContainsKey(door))
            {
                m_pendingCloseDoors[door] = closeTime;
            }
            else
            {
                m_pendingCloseDoors.Add(door, closeTime);
            }
        }

        private void ProcessPendingDoorClosures()
        {
            if (m_pendingCloseDoors.Count == 0) return;

            var doorsToRemove = new List<Door>();
            var doorsToReschedule = new List<Door>();

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

        private bool IsSafeToCLoseDoor(Door door)
        {
            float dist = Vector3.Distance(transform.position, door.transform.position);

            if (dist > DoorSettings.DoorDetectionRadius + 1f)
                return true;

            Vector3 toDoor = (door.transform.position - transform.position).normalized;
            float dotForward = Mathf.Abs(Vector3.Dot(door.transform.forward, toDoor));

            return dotForward < 0.5f;
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
                if (door != null && !m_nearbyDoors.Contains(door))
                {
                    m_nearbyDoors.Add(door);
                }
            }
        }

        private bool IsDoorClosed(Door door)
        {
            if (door == null) return false;

            var nview = GetDoorZNetView(door);
            if (nview == null || nview.GetZDO() == null)
                return false;

            return nview.GetZDO().GetInt(ZDOVars.s_state, 0) == 0;
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

        private void OnDestroy()
        {
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
