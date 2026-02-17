using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Core.Attributes;
using ValheimVillages.NPCs;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    /// Tracks pending rescue quests and spawns captive pawn items when the player
    /// enters the dungeon at the quest location. Pawn spawning is triggered by
    /// EnvMan.SetForceEnvironment (the dungeon entry hook) rather than proximity,
    /// ensuring the dungeon rooms are fully loaded before we try to place the item.
    /// </summary>
    public static class RescueQuestTracker
    {
        /// <summary>
        /// How close the player must be to a quest's location position for a dungeon
        /// entry event to be considered relevant to that quest.
        /// </summary>
        private const float QuestDungeonRadius = 150f;

        /// <summary>
        /// Represents a pending rescue quest that hasn't spawned its pawn yet.
        /// </summary>
        public class PendingQuest
        {
            public Vector3 Position;
            public NpcType NpcType;
            public string Biome;
        }

        private static readonly List<PendingQuest> _pendingQuests = new();

        /// <summary>
        /// Registers a new pending rescue quest. The pawn will be spawned when
        /// the player enters the dungeon at this position.
        /// </summary>
        public static void AddQuest(Vector3 position, NpcType npcType, string biome)
        {
            _pendingQuests.Add(new PendingQuest
            {
                Position = position,
                NpcType = npcType,
                Biome = biome
            });

            Plugin.Log?.LogInfo(
                $"Registered pending rescue quest: {npcType} at {position} ({biome}). " +
                $"Pawn will spawn when player enters dungeon within {QuestDungeonRadius}m.");
        }

        /// <summary>
        /// Called when the player enters a dungeon (EnvMan.SetForceEnvironment
        /// called with a non-empty environment name). Checks if any pending quest
        /// is near the player and spawns the pawn in a dungeon room.
        /// </summary>
        public static void OnDungeonEntered(Player player, string environmentName)
        {
            if (player == null || _pendingQuests.Count == 0)
                return;

            var playerPos = player.transform.position;

            Plugin.Log?.LogInfo(
                $"Dungeon entry detected (env: {environmentName}), " +
                $"player at {playerPos}, checking {_pendingQuests.Count} pending quest(s).");

            for (int i = _pendingQuests.Count - 1; i >= 0; i--)
            {
                var quest = _pendingQuests[i];

                // Use horizontal (X,Z) distance only — dungeon interiors are placed
                // deep underground, so 3D distance would be huge
                float dx = playerPos.x - quest.Position.x;
                float dz = playerPos.z - quest.Position.z;
                float horizontalDistance = Mathf.Sqrt(dx * dx + dz * dz);

                Plugin.Log?.LogInfo(
                    $"  Quest {i}: {quest.NpcType} at {quest.Position}, " +
                    $"horizontal distance: {horizontalDistance:F0}m");

                if (horizontalDistance <= QuestDungeonRadius)
                {
                    Plugin.Log?.LogInfo(
                        $"Player entered dungeon (env: {environmentName}) within " +
                        $"{horizontalDistance:F0}m (horizontal) of rescue quest for " +
                        $"{quest.NpcType} — spawning captive pawn in dungeon room.");

                    var spawnPos = FindDungeonRoomPosition(playerPos);
                    SpawnCaptivePawn(spawnPos, quest.NpcType);
                    _pendingQuests.RemoveAt(i);
                    return; // Only handle one quest per dungeon entry
                }
            }
        }

        /// <summary>
        /// Whether there are any pending quests.
        /// </summary>
        public static int PendingCount => _pendingQuests.Count;

        /// <summary>
        /// Clear all pending quests (e.g. on hot reload or world unload).
        /// </summary>
        [RegisterCleanup]
        public static void Clear() => _pendingQuests.Clear();

        /// <summary>
        /// Finds a room inside the dungeon the player just entered.
        /// Uses the player's current position to find the nearest DungeonGenerator,
        /// then picks a non-entrance room. Falls back to player position if no
        /// rooms are found.
        /// </summary>
        private static Vector3 FindDungeonRoomPosition(Vector3 playerPos)
        {
            // Find the nearest DungeonGenerator — the player is now inside the dungeon,
            // so the generator and its rooms should be fully loaded
            var allDungeons = Object.FindObjectsByType<DungeonGenerator>(FindObjectsSortMode.None);
            DungeonGenerator nearest = null;
            float nearestDist = float.MaxValue;

            foreach (var dg in allDungeons)
            {
                float dist = Vector3.Distance(playerPos, dg.transform.position);
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = dg;
                }
            }

            if (nearest == null)
            {
                Plugin.Log?.LogWarning("No DungeonGenerator found, spawning near player.");
                return playerPos;
            }

            Plugin.Log?.LogInfo(
                $"Found DungeonGenerator at {nearest.transform.position} " +
                $"(distance: {nearestDist:F1}m from player)");

            // Get all Room components that are children of this dungeon
            var rooms = nearest.GetComponentsInChildren<Room>();
            if (rooms == null || rooms.Length == 0)
            {
                Plugin.Log?.LogWarning("DungeonGenerator has no rooms, spawning near player.");
                return playerPos;
            }

            // Collect non-entrance rooms so the player has to explore
            var interiorRooms = new List<Room>();
            foreach (var room in rooms)
            {
                if (!room.m_entrance)
                    interiorRooms.Add(room);
            }

            // If all rooms are entrances (e.g. single-room troll cave), use all rooms
            if (interiorRooms.Count == 0)
            {
                Plugin.Log?.LogInfo(
                    $"Dungeon has {rooms.Length} room(s), all marked as entrance — " +
                    "spawning in available room.");
                interiorRooms.AddRange(rooms);
            }

            // Pick a random interior room
            var chosenRoom = interiorRooms[Random.Range(0, interiorRooms.Count)];
            var roomPos = chosenRoom.transform.position;

            Plugin.Log?.LogInfo(
                $"Picked dungeon room: {chosenRoom.name} " +
                $"(entrance: {chosenRoom.m_entrance}, " +
                $"total rooms: {rooms.Length}, interior: {interiorRooms.Count}) " +
                $"at {roomPos}");

            return roomPos;
        }

        /// <summary>
        /// Maps NPC types to their corresponding pawn item prefab names.
        /// </summary>
        private static readonly Dictionary<NpcType, string> NpcPawnMap = new()
        {
            { NpcType.Farmer, "vv_farmer_pawn" },
            { NpcType.Miner, "vv_miner_pawn" },
            { NpcType.Blacksmith, "vv_blacksmith_pawn" },
            { NpcType.Carpenter, "vv_carpenter_pawn" },
            { NpcType.Scout, "vv_scout_pawn" },
            { NpcType.Trader, "vv_trader_pawn" },
            { NpcType.Guard, "vv_guard_pawn" },
            { NpcType.Mountaineer, "vv_mountaineer_pawn" },
            { NpcType.Shipwright, "vv_shipwright_pawn" },
            { NpcType.TavernKeeper, "vv_tavernkeeper_pawn" }
        };

        /// <summary>
        /// Spawns the type-specific pawn item as a proper world-dropped pickable
        /// item using ItemDrop.DropItem.
        /// </summary>
        private static void SpawnCaptivePawn(Vector3 position, NpcType npcType)
        {
            if (!NpcPawnMap.TryGetValue(npcType, out var pawnName))
            {
                Plugin.Log?.LogWarning($"No pawn item mapped for NPC type: {npcType}");
                return;
            }

            if (ZNetScene.instance == null)
            {
                Plugin.Log?.LogError("ZNetScene not available, cannot spawn captive pawn");
                return;
            }

            var prefab = ZNetScene.instance.GetPrefab(pawnName);
            if (prefab == null)
            {
                Plugin.Log?.LogError($"Pawn prefab not found: {pawnName}");
                return;
            }

            var itemDrop = prefab.GetComponent<ItemDrop>();
            if (itemDrop == null)
            {
                Plugin.Log?.LogError($"Pawn prefab {pawnName} has no ItemDrop component");
                return;
            }

            // Inside a dungeon, use the room position directly with a small Y offset
            var spawnPos = new Vector3(position.x, position.y + 0.5f, position.z);

            var dropped = ItemDrop.DropItem(itemDrop.m_itemData, 1, spawnPos, Quaternion.identity);

            if (dropped != null)
                Plugin.Log?.LogInfo($"Spawned captive pawn {pawnName} at {spawnPos}");
            else
                Plugin.Log?.LogError($"Failed to spawn captive pawn {pawnName} at {spawnPos}");
        }
    }
}
