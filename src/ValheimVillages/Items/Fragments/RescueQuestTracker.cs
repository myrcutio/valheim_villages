using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Items.Fragments
{
    /// <summary>
    ///     Watches for the local player reaching a pending rescue quest and places the Lode
    ///     Core reward. The quests themselves are world state in <see cref="RescueQuestStore" />,
    ///     so any player can complete any quest. Delivery depends on the location kind:
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
    ///     Either way the host must grant the claim first (<see cref="FragmentQuestRpc.RequestClaim" />);
    ///     the core is placed in <see cref="OnClaimResult" />.
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

        // Claims sent to the host and not yet answered, so the once-a-second arrival sweep
        // doesn't re-request the same quest while the reply is in flight.
        private static readonly HashSet<ZDOID> _claimsInFlight = new();

        /// <summary>
        ///     Records a new pending rescue quest in the world save. Its reward is placed when
        ///     any player enters the dungeon (interior) or reaches the site (surface).
        /// </summary>
        public static void AddQuest(Vector3 position, string villagerType, string biome, bool isInterior,
            string locationName)
        {
            var quest = RescueQuestStore.Create(position, villagerType, biome, isInterior, locationName);

            Plugin.Log?.LogInfo(
                isInterior
                    ? $"Registered pending rescue quest {quest.Id}: {villagerType} at {position} ({biome}, interior). " +
                      $"Reward spawns when a player enters the dungeon within {QuestDungeonRadius}m."
                    : $"Registered pending rescue quest {quest.Id}: {villagerType} at {position} ({biome}, surface). " +
                      $"Reward spawns when a player arrives within {SurfaceArrivalRadius}m.");
        }

        /// <summary>
        ///     Called when the player enters a dungeon (EnvMan.SetForceEnvironment
        ///     called with a non-empty environment name). Claims the nearest interior quest
        ///     within <see cref="QuestDungeonRadius" />, if any.
        /// </summary>
        public static void OnDungeonEntered(Player player, string environmentName)
        {
            if (player == null)
                return;

            var playerPos = player.transform.position;
            var quests = RescueQuestStore.Near(playerPos);
            if (quests.Count == 0)
                return;

            Plugin.Log?.LogInfo(
                $"Dungeon entry detected (env: {environmentName}), " +
                $"player at {playerPos}, checking {quests.Count} nearby pending quest(s).");

            foreach (var quest in quests)
            {
                // Surface sites deliver on arrival (TickArrival), not on dungeon entry. Skipping
                // them here also stops an interior dungeon's surface entrance from poaching a
                // nearby surface quest's reward.
                if (!quest.IsInterior)
                    continue;

                // Horizontal (X,Z) distance only — dungeon interiors are placed thousands of
                // metres above the surface, so 3D distance would be huge
                var horizontalDistance = HorizontalDistance(playerPos, quest.Position);

                Plugin.Log?.LogInfo(
                    $"  Quest {quest.Id}: {quest.VillagerType} at {quest.Position}, " +
                    $"horizontal distance: {horizontalDistance:F0}m");

                if (horizontalDistance > QuestDungeonRadius)
                    continue;

                Plugin.Log?.LogInfo(
                    $"Player entered dungeon (env: {environmentName}) within " +
                    $"{horizontalDistance:F0}m (horizontal) of rescue quest for " +
                    $"{quest.VillagerType} — claiming it from the host.");
                RequestClaim(quest);
                return; // Only handle one quest per dungeon entry
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
            if (player == null)
                return;

            if (Time.time - _lastArrivalCheck < ArrivalCheckInterval)
                return;
            _lastArrivalCheck = Time.time;

            var playerPos = player.transform.position;
            foreach (var quest in RescueQuestStore.Near(playerPos))
            {
                if (quest.IsInterior)
                    continue; // delivered on dungeon entry, not on arrival

                // Horizontal distance only — surface Y can differ from the stored marker Y.
                var horizontalDistance = HorizontalDistance(playerPos, quest.Position);
                if (horizontalDistance > SurfaceArrivalRadius)
                    continue;

                Plugin.Log?.LogInfo(
                    $"Player reached surface rescue site for {quest.VillagerType} " +
                    $"({horizontalDistance:F0}m from marker) — claiming it from the host.");
                RequestClaim(quest);
                return; // one per tick
            }
        }

        private static void RequestClaim(RescueQuest quest)
        {
            if (!_claimsInFlight.Add(quest.Id))
                return;
            FragmentQuestRpc.RequestClaim(quest.Id);
        }

        /// <summary>
        ///     The host's answer to <see cref="RequestClaim" />. On a grant the quest is already
        ///     gone from the world, so the core is placed now: in a dungeon room for an interior
        ///     quest (the player is inside it), on the ground at the marker for a surface one.
        /// </summary>
        public static void OnClaimResult(bool granted, ZDOID questId, Vector3 questPos, bool isInterior)
        {
            _claimsInFlight.Remove(questId);
            if (!granted)
            {
                Plugin.Log?.LogInfo($"[RescueQuest] claim of {questId} refused: another player completed it first");
                return;
            }

            var player = Player.m_localPlayer;
            if (player == null)
            {
                Plugin.Log?.LogError(
                    $"[RescueQuest] host granted quest {questId} but there is no local player to place the reward for");
                return;
            }

            var spawnPos = isInterior ? FindDungeonRoomPosition(player.transform.position) : GroundSnap(questPos);
            SpawnLodeCore(spawnPos);
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            var dx = a.x - b.x;
            var dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
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

        /// <summary>Forget unanswered claims (e.g. on hot reload or world unload).</summary>
        [RegisterCleanup]
        public static void Clear()
        {
            _claimsInFlight.Clear();
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
    }
}