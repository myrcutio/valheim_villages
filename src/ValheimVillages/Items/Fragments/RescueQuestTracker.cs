using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    ///     Tracks pending rescue quests and places a Lode Core reward when the player reaches
    ///     the quest location. Delivery depends on the location kind:
    ///     <list type="bullet">
    ///         <item>
    ///             <b>Interior dungeons</b> (crypts, caves, excavations) place the core inside,
    ///             triggered by EnvMan.SetForceEnvironment (the dungeon-entry hook) so the rooms
    ///             are loaded before we spawn — see <see cref="OnDungeonEntered" />.
    ///         </item>
    ///         <item>
    ///             <b>Surface sites</b> (farms, camps, towers, ruins) have no interior to enter
    ///             and so never raise that event; they deliver on arrival proximity instead — see
    ///             <see cref="TickArrival" />.
    ///         </item>
    ///     </list>
    /// </summary>
    public static class RescueQuestTracker
    {
        /// <summary>
        ///     How close the player must be to a quest's location position for a dungeon
        ///     entry event to be considered relevant to that quest.
        /// </summary>
        private const float QuestDungeonRadius = 150f;

        /// <summary>
        ///     How close the player must get to a surface quest's marked location before its
        ///     reward is placed. Generous so it fires as the player nears the structure rather
        ///     than only at its exact center.
        /// </summary>
        private const float SurfaceArrivalRadius = 30f;

        /// <summary>Minimum seconds between surface-arrival sweeps (pumped every frame).</summary>
        private const float ArrivalCheckInterval = 1f;

        private static float _lastArrivalCheck;

        private static readonly List<PendingQuest> _pendingQuests = new();

        /// <summary>
        ///     Whether there are any pending quests.
        /// </summary>
        public static int PendingCount => _pendingQuests.Count;

        /// <summary>
        ///     Registers a new pending rescue quest. The pawn will be spawned when
        ///     the player enters the dungeon at this position.
        /// </summary>
        public static void AddQuest(Vector3 position, string villagerType, string biome, bool isInterior)
        {
            _pendingQuests.Add(new PendingQuest
            {
                Position = position,
                VillagerType = villagerType,
                Biome = biome,
                IsInterior = isInterior,
            });

            Plugin.Log?.LogInfo(
                isInterior
                    ? $"Registered pending rescue quest: {villagerType} at {position} ({biome}, interior). " +
                      $"Reward spawns when player enters the dungeon within {QuestDungeonRadius}m."
                    : $"Registered pending rescue quest: {villagerType} at {position} ({biome}, surface). " +
                      $"Reward spawns when player arrives within {SurfaceArrivalRadius}m.");
        }

        /// <summary>
        ///     Called when the player enters a dungeon (EnvMan.SetForceEnvironment
        ///     called with a non-empty environment name). Checks if any pending quest
        ///     is near the player and spawns the pawn in a dungeon room.
        /// </summary>
        public static void OnDungeonEntered(Player player, string environmentName)
        {
            if (player == null || _pendingQuests.Count == 0)
                return;

            var playerPos = player.transform.position;

            Plugin.Log?.LogInfo(
                $"Dungeon entry detected (env: {environmentName}), " +
                $"player at {playerPos}, checking {_pendingQuests.Count} pending quest(s).");

            for (var i = _pendingQuests.Count - 1; i >= 0; i--)
            {
                var quest = _pendingQuests[i];

                // Surface sites deliver on arrival (TickArrival), not on dungeon entry. Skipping
                // them here also stops an interior dungeon's surface entrance from poaching a
                // nearby surface quest's reward.
                if (!quest.IsInterior)
                    continue;

                // Use horizontal (X,Z) distance only — dungeon interiors are placed
                // deep underground, so 3D distance would be huge
                var dx = playerPos.x - quest.Position.x;
                var dz = playerPos.z - quest.Position.z;
                var horizontalDistance = Mathf.Sqrt(dx * dx + dz * dz);

                Plugin.Log?.LogInfo(
                    $"  Quest {i}: {quest.VillagerType} at {quest.Position}, " +
                    $"horizontal distance: {horizontalDistance:F0}m");

                if (horizontalDistance <= QuestDungeonRadius)
                {
                    Plugin.Log?.LogInfo(
                        $"Player entered dungeon (env: {environmentName}) within " +
                        $"{horizontalDistance:F0}m (horizontal) of rescue quest for " +
                        $"{quest.VillagerType} — placing a Lode Core in a dungeon room.");

                    var spawnPos = FindDungeonRoomPosition(playerPos);
                    SpawnLodeCore(spawnPos);
                    _pendingQuests.RemoveAt(i);
                    return; // Only handle one quest per dungeon entry
                }
            }
        }

        /// <summary>
        ///     Arrival sweep for surface rescue sites, pumped every frame from <c>Plugin.Update</c>
        ///     and throttled to <see cref="ArrivalCheckInterval" />. Surface sites have no interior
        ///     to enter, so the dungeon-entry hook never fires for them; instead the reward is
        ///     placed once the player gets within <see cref="SurfaceArrivalRadius" /> of the marked
        ///     location. Interior quests are ignored here — they spawn in <see cref="OnDungeonEntered" />.
        /// </summary>
        public static void TickArrival(Player player)
        {
            if (player == null || _pendingQuests.Count == 0)
                return;

            if (Time.time - _lastArrivalCheck < ArrivalCheckInterval)
                return;
            _lastArrivalCheck = Time.time;

            var playerPos = player.transform.position;

            for (var i = _pendingQuests.Count - 1; i >= 0; i--)
            {
                var quest = _pendingQuests[i];
                if (quest.IsInterior)
                    continue; // delivered on dungeon entry, not on arrival

                // Horizontal distance only — surface Y can differ from the stored marker Y.
                var dx = playerPos.x - quest.Position.x;
                var dz = playerPos.z - quest.Position.z;
                var horizontalDistance = Mathf.Sqrt(dx * dx + dz * dz);
                if (horizontalDistance > SurfaceArrivalRadius)
                    continue;

                Plugin.Log?.LogInfo(
                    $"Player reached surface rescue site for {quest.VillagerType} " +
                    $"({horizontalDistance:F0}m from marker) — placing a Lode Core.");

                SpawnLodeCore(GroundSnap(quest.Position));
                _pendingQuests.RemoveAt(i);
                return; // one per tick
            }
        }

        /// <summary>
        ///     Snaps a position's Y to the terrain so a surface reward rests on the ground rather
        ///     than floating or burying — the stored location Y can be stale relative to terrain.
        /// </summary>
        private static Vector3 GroundSnap(Vector3 pos)
        {
            var zs = ZoneSystem.instance;
            if (zs != null && zs.GetGroundHeight(pos, out var height))
                pos.y = height;
            return pos;
        }

        /// <summary>
        ///     Clear all pending quests (e.g. on hot reload or world unload).
        /// </summary>
        [RegisterCleanup]
        public static void Clear()
        {
            _pendingQuests.Clear();
        }

        /// <summary>
        ///     Finds a room inside the dungeon the player just entered.
        ///     Uses the player's current position to find the nearest DungeonGenerator,
        ///     then picks a non-entrance room. Falls back to player position if no
        ///     rooms are found.
        /// </summary>
        private static Vector3 FindDungeonRoomPosition(Vector3 playerPos)
        {
            // Find the nearest DungeonGenerator — the player is now inside the dungeon,
            // so the generator and its rooms should be fully loaded
            var allDungeons = Object.FindObjectsByType<DungeonGenerator>(FindObjectsSortMode.None);
            DungeonGenerator nearest = null;
            var nearestDist = float.MaxValue;

            foreach (var dg in allDungeons)
            {
                var dist = Vector3.Distance(playerPos, dg.transform.position);
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
                if (!room.m_entrance)
                    interiorRooms.Add(room);

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
        ///     Places a generic Lode Core in the dungeon room. The core is the generic
        ///     recruitment currency; which villager type the quest was for is already baked
        ///     into the recipe the player unlocked on fragment-combine, so the reward is
        ///     generic. World-spawn lives in <see cref="LodeCore.DropAt" /> (shared with the
        ///     villager death drop).
        /// </summary>
        private static void SpawnLodeCore(Vector3 position)
        {
            // The reward sits on a Dvergr pedestal to pry the core from. If the pedestal prefab
            // isn't available, fail loudly but still drop the bare core so the reward is never lost.
            if (LodeCorePedestal.SpawnAt(position) != null)
            {
                Plugin.Log?.LogInfo($"Placed a Lode Core pedestal in the dungeon at {position}");
                return;
            }

            Plugin.Log?.LogError("[RescueQuest] Lode Core pedestal unavailable; dropping the bare core instead.");
            var spawnPos = new Vector3(position.x, position.y + 0.5f, position.z);
            LodeCore.DropAt(spawnPos);
        }

        /// <summary>
        ///     Represents a pending rescue quest that hasn't spawned its pawn yet.
        /// </summary>
        public class PendingQuest
        {
            public string Biome;
            public bool IsInterior;
            public Vector3 Position;
            public string VillagerType;
        }
    }
}