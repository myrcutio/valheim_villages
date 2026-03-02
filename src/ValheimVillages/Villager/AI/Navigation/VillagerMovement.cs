using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using ValheimVillages.Settings;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    /// Movement and navigation helpers for VillagerAI.
    /// Calls FindPath once per destination change, then follows the cached
    /// path each frame via MoveTowards. With NavMeshLinks bridging floor
    /// islands, FindPath returns complete cross-floor paths so no special
    /// cross-floor bypass is needed.
    /// </summary>
    public static class VillagerMovement
    {
        /// <summary>Last destination sent to FindPath, per NPC instance ID.</summary>
        private static readonly Dictionary<int, Vector3> s_lastFindPathDest =
            new Dictionary<int, Vector3>();

        /// <summary>Timestamp of last FindPath call, per NPC. Used to respect
        /// BaseAI's 1-second internal cooldown and retry failed paths.</summary>
        private static readonly Dictionary<int, float> s_lastFindPathTime =
            new Dictionary<int, float>();

        /// <summary>Whether the last FindPath produced a usable path.</summary>
        private static readonly Dictionary<int, bool> s_lastFindPathHadResult =
            new Dictionary<int, bool>();

        /// <summary>Minimum seconds between FindPath calls. Must exceed BaseAI's
        /// internal 1-second cooldown so retries get fresh results.</summary>
        private const float FindPathRetryInterval = 5f;

        private static readonly FieldInfo s_pathField = typeof(BaseAI).GetField(
            "m_path", BindingFlags.NonPublic | BindingFlags.Instance);

        private static object Invoke<T>(
            object instance, string methodName, params object[] args)
        {
            return typeof(T)
                .GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(instance, args);
        }

        /// <summary>Clear tracked movement state for a specific NPC.</summary>
        public static void ClearMovementState(int npcId)
        {
            s_lastFindPathDest.Remove(npcId);
            s_lastFindPathTime.Remove(npcId);
            s_lastFindPathHadResult.Remove(npcId);
        }

        /// <summary>
        /// Snap a world position to the walkable surface below it, if close.
        /// Avoids snapping to roofs/ceilings in multi-story buildings.
        /// </summary>
        public static Vector3 GetWalkableDestination(Vector3 worldPosition)
        {
            if (ZoneSystem.instance == null) return worldPosition;
            if (!ZoneSystem.instance.GetSolidHeight(worldPosition, out float h, 1000))
                return worldPosition;
            if (Mathf.Abs(h - worldPosition.y) < 2f)
                return new Vector3(worldPosition.x, h, worldPosition.z);
            return worldPosition;
        }

        /// <summary>
        /// One tick of movement toward a destination.
        /// Issues FindPath once when the destination changes, then follows
        /// the resulting path each frame via MoveTowards.
        /// Returns true if within arrival distance (3D).
        /// </summary>
        public static bool ExecutePathingTick(
            MonsterAI instance, Vector3 destination, float dt,
            float arrivalDistance, bool ignoreFire)
        {
            float remaining = Vector3.Distance(instance.transform.position, destination);
            if (remaining < arrivalDistance) return true;

            if (!ignoreFire)
            {
                var burningArea = EffectArea.IsPointInsideArea(
                    instance.transform.position, EffectArea.Type.Burning, 2f);
                if (burningArea)
                {
                    Invoke<BaseAI>(instance, "RandomMovementArroundPoint", dt,
                        burningArea.transform.position,
                        burningArea.GetRadius() + 3f, true);
                    return false;
                }
            }

            int npcId = instance.GetInstanceID();
            float remainingXZ = Utils.DistanceXZ(instance.transform.position, destination);
            bool running = remainingXZ > 5f;

            // Issue FindPath when destination changes, or retry after cooldown if
            // previous attempt returned an empty path. BaseAI.FindPath has a 1-second
            // internal cooldown — calling it faster returns stale cached results.
            bool destChanged = !s_lastFindPathDest.TryGetValue(npcId, out Vector3 lastDest) ||
                               Vector3.Distance(lastDest, destination) > 1f;
            s_lastFindPathTime.TryGetValue(npcId, out float lastCallTime);
            s_lastFindPathHadResult.TryGetValue(npcId, out bool hadResult);
            float timeSinceLastCall = Time.time - lastCallTime;
            bool shouldRetry = !hadResult && timeSinceLastCall >= FindPathRetryInterval;

            if (destChanged || shouldRetry)
            {
                if (timeSinceLastCall >= FindPathRetryInterval || destChanged)
                {
                    s_lastFindPathDest[npcId] = destination;
                    s_lastFindPathTime[npcId] = Time.time;
                    Invoke<BaseAI>(instance, "FindPath", destination);
                }
            }

            // Follow the cached path
            var path = s_pathField?.GetValue(instance) as List<Vector3>;
            bool hasPath = path != null && path.Count > 0;
            s_lastFindPathHadResult[npcId] = hasPath;

            if (!hasPath)
            {
                // No path available — do NOT report arrived. The NPC isn't at the
                // destination; it just can't find a route. Return false so the work
                // state machine doesn't advance. Retry will fire after cooldown.
                return false;
            }

            // Advance past reached waypoints
            float closeEnough = running ? 1f : 0.5f;
            while (path.Count > 1 &&
                   Utils.DistanceXZ(path[0], instance.transform.position) < closeEnough)
            {
                path.RemoveAt(0);
            }

            if (path.Count == 0)
            {
                Invoke<BaseAI>(instance, "StopMoving");
                return true;
            }

            // Last-waypoint arrival: when only 1 wp remains and NPC is close to it,
            // check if we've also reached the destination (within the caller's distance).
            // If yes -> arrived. If no -> end of a partial path, stop and report not-arrived
            // so the work state machine can time out gracefully instead of oscillating.
            if (path.Count == 1 &&
                Utils.DistanceXZ(path[0], instance.transform.position) < closeEnough)
            {
                if (Vector3.Distance(instance.transform.position, destination) < arrivalDistance)
                {
                    path.Clear();
                    Invoke<BaseAI>(instance, "StopMoving");
                    return true;
                }

                Invoke<BaseAI>(instance, "StopMoving");
                return false;
            }

            // Drive movement every frame toward the current waypoint.
            // Valheim's Character.UpdateMovement consumes m_moveDir each FixedUpdate,
            // so MoveTowards must be called continuously to keep the NPC moving.
            var dir = (path[0] - instance.transform.position).normalized;
            Invoke<BaseAI>(instance, "MoveTowards", dir, running);

            return false;
        }

        /// <summary>Delegates to ExecutePathingTick for API compatibility.</summary>
        public static bool MoveToDestination(
            MonsterAI instance, Vector3 destination, float dt,
            float distance = 0.5f, bool ignoreFire = false)
        {
            return ExecutePathingTick(instance, destination, dt, distance, ignoreFire);
        }

        /// <summary>
        /// Run essential BaseAI updates that are normally done by BaseAI.UpdateAI.
        /// </summary>
        public static void RunBaseAIUpdates(MonsterAI instance, float dt)
        {
            typeof(BaseAI)
                .GetMethod("UpdateRegeneration",
                    BindingFlags.NonPublic | BindingFlags.Instance)
                ?.Invoke(instance, new object[] { dt });

            var field = typeof(BaseAI).GetField("m_timeSinceHurt",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (field != null)
            {
                float current = (float)field.GetValue(instance);
                field.SetValue(instance, current + dt);
            }
        }

        /// <summary>
        /// Check if NPC is stalled and try to open blocking doors.
        /// </summary>
        public static void CheckMovementStall(
            MonsterAI instance, Vector3? target,
            ref Vector3 lastCheckPosition, ref float stallStartTime)
        {
            if (!target.HasValue) return;

            if (instance.gameObject.GetComponent<DoorHandler>() == null)
                instance.gameObject.AddComponent<DoorHandler>();

            var currentPos = instance.transform.position;
            float moved = Vector3.Distance(currentPos, lastCheckPosition);

            if (moved > DoorSettings.MovementProgressThreshold)
            {
                lastCheckPosition = currentPos;
                stallStartTime = 0f;
                return;
            }

            if (stallStartTime == 0f)
                stallStartTime = Time.time;

            float stallDuration = Time.time - stallStartTime;
            if (stallDuration >= DoorSettings.MovementStallThreshold)
            {
                var doorHandler = instance.GetComponent<DoorHandler>();
                var blockingDoor = doorHandler?.GetBlockingDoor(target.Value);
                if (blockingDoor != null)
                {
                    Plugin.Log?.LogDebug("[AI] Stalled, opening blocking door");
                    doorHandler.OpenDoor(blockingDoor);
                    stallStartTime = 0f;
                    lastCheckPosition = currentPos;
                }
            }
        }
    }
}
