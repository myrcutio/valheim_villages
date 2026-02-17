using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

namespace ValheimVillages.NPCs.AI
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

        // #region agent log
        /// <summary>Throttle for MoveTowards position logging (every 2s).</summary>
        private static readonly Dictionary<int, float> s_lastMoveLogTime =
            new Dictionary<int, float>();
        // #endregion

        /// <summary>Minimum seconds between FindPath calls. Must exceed BaseAI's
        /// internal 1-second cooldown so retries get fresh results.</summary>
        private const float FindPathRetryInterval = 1.5f;

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
            s_lastMoveLogTime.Remove(npcId);
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
            // (pathRecomputed tracked for telemetry only; MoveTowards runs every frame now)
            bool destChanged = !s_lastFindPathDest.TryGetValue(npcId, out Vector3 lastDest) ||
                               Vector3.Distance(lastDest, destination) > 1f;
            s_lastFindPathTime.TryGetValue(npcId, out float lastCallTime);
            s_lastFindPathHadResult.TryGetValue(npcId, out bool hadResult);
            float timeSinceLastCall = Time.time - lastCallTime;
            bool shouldRetry = !hadResult && timeSinceLastCall >= FindPathRetryInterval;

            if (destChanged || shouldRetry)
            {
                // Don't call FindPath if within the cooldown (results would be stale)
                if (timeSinceLastCall >= FindPathRetryInterval || destChanged)
                {
                    s_lastFindPathDest[npcId] = destination;
                    s_lastFindPathTime[npcId] = Time.time;
                    Invoke<BaseAI>(instance, "FindPath", destination);
                }

                // #region agent log
                try
                {
                    var pos = instance.transform.position;
                    string npcName = instance.GetComponent<Character>()?.GetHoverName() ?? "unknown";

                    // H1/H2: Check FindPath result and path quality
                    var fpPath = s_pathField?.GetValue(instance) as List<Vector3>;
                    int pathCount = fpPath?.Count ?? 0;
                    float lastWpDistToDest = pathCount > 0
                        ? Vector3.Distance(fpPath[pathCount - 1], destination) : -1f;
                    bool pathLooksComplete = pathCount > 0 && lastWpDistToDest < 2f;

                    // Serialize first/last 3 waypoints
                    var wpSample = new System.Text.StringBuilder("[");
                    if (fpPath != null)
                    {
                        int show = System.Math.Min(pathCount, 3);
                        for (int i = 0; i < show; i++)
                        {
                            if (i > 0) wpSample.Append(",");
                            wpSample.Append($"\"{fpPath[i].x:F1},{fpPath[i].y:F1},{fpPath[i].z:F1}\"");
                        }
                        if (pathCount > 6) wpSample.Append(",\"...\"");
                        for (int i = System.Math.Max(show, pathCount - 3); i < pathCount; i++)
                        {
                            wpSample.Append($",\"{fpPath[i].x:F1},{fpPath[i].y:F1},{fpPath[i].z:F1}\"");
                        }
                    }
                    wpSample.Append("]");

                    // H4: Check if NavMeshLinks are placed
                    bool linksPlaced = NavMeshLinkPlacer.HasLinks;
                    bool navBaked = VillageNavMeshBake.HasBakedInstance;

                    // H5: Compare with Unity NavMesh.CalculatePath
                    int humanoidAgent = VillageNavMeshBake.ResolveValheimHumanoidAgentTypeID();
                    var filter = new NavMeshQueryFilter { agentTypeID = humanoidAgent, areaMask = NavMesh.AllAreas };
                    bool srcSample = NavMesh.SamplePosition(pos, out NavMeshHit srcHit, 5f, filter);
                    bool dstSample = NavMesh.SamplePosition(destination, out NavMeshHit dstHit, 5f, filter);
                    string calcStatus = "skip";
                    int calcCorners = 0;
                    if (srcSample && dstSample)
                    {
                        var calcPath = new NavMeshPath();
                        NavMesh.CalculatePath(srcHit.position, dstHit.position, filter, calcPath);
                        calcStatus = calcPath.status.ToString();
                        calcCorners = calcPath.corners?.Length ?? 0;
                    }

                    // H2: Deviation from recorded path
                    float avgDeviation = ComputeDeviationFromRecordedPath(fpPath);

                    float yDiff = Mathf.Abs(pos.y - destination.y);
                    string logData = "{" +
                        $"\"npc\":\"{npcName}\"" +
                        $",\"npcPos\":\"{pos.x:F1},{pos.y:F1},{pos.z:F1}\"" +
                        $",\"dest\":\"{destination.x:F1},{destination.y:F1},{destination.z:F1}\"" +
                        $",\"yDiff\":{yDiff:F1}" +
                        $",\"findPathWaypoints\":{pathCount}" +
                        $",\"lastWpDistToDest\":{lastWpDistToDest:F1}" +
                        $",\"pathLooksComplete\":{pathLooksComplete.ToString().ToLower()}" +
                        $",\"wpSample\":{wpSample}" +
                        $",\"navBaked\":{navBaked.ToString().ToLower()}" +
                        $",\"linksPlaced\":{linksPlaced.ToString().ToLower()}" +
                        $",\"srcOnNavMesh\":{srcSample.ToString().ToLower()}" +
                        $",\"dstOnNavMesh\":{dstSample.ToString().ToLower()}" +
                        $",\"calcPathStatus\":\"{calcStatus}\"" +
                        $",\"calcPathCorners\":{calcCorners}" +
                        $",\"avgDeviationFromRecorded\":{avgDeviation:F1}" +
                        $",\"trigger\":\"{(destChanged ? "destChanged" : "retry")}\"" +
                        $",\"timeSinceLastCall\":{timeSinceLastCall:F1}" +
                        "}";
                    long ts = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    System.IO.File.AppendAllText(
                        "/home/benny/Projects/valheim_villages/.cursor/debug.log",
                        $"{{\"hypothesisId\":\"H1_H2_H4_H5\",\"location\":\"VillagerMovement.cs:FindPath\",\"message\":\"FindPath telemetry on destination change\",\"data\":{logData},\"timestamp\":{ts}}}\n");
                }
                catch (System.Exception ex)
                {
                    try
                    {
                        long tsErr = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        System.IO.File.AppendAllText(
                            "/home/benny/Projects/valheim_villages/.cursor/debug.log",
                            $"{{\"hypothesisId\":\"ERR\",\"location\":\"VillagerMovement.cs:FindPath\",\"message\":\"Telemetry error\",\"data\":{{\"error\":\"{ex.Message.Replace("\"", "'")}\"}},\"timestamp\":{tsErr}}}\n");
                    }
                    catch { }
                }
                // #endregion
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
            // If yes → arrived. If no → end of a partial path, stop and report not-arrived
            // so the work state machine can time out gracefully instead of oscillating.
            if (path.Count == 1 &&
                Utils.DistanceXZ(path[0], instance.transform.position) < closeEnough)
            {
                if (Utils.DistanceXZ(instance.transform.position, destination) < arrivalDistance)
                {
                    path.Clear();
                    Invoke<BaseAI>(instance, "StopMoving");
                    return true; // Arrived at destination
                }

                // End of partial path — can't reach destination from here.
                Invoke<BaseAI>(instance, "StopMoving");
                return false;
            }

            // Drive movement every frame toward the current waypoint.
            // Valheim's Character.UpdateMovement consumes m_moveDir each FixedUpdate,
            // so MoveTowards must be called continuously to keep the NPC moving.
            var dir = (path[0] - instance.transform.position).normalized;
            Invoke<BaseAI>(instance, "MoveTowards", dir, running);

            // #region agent log
            // Log position every ~2 seconds to verify movement is happening
            if (!s_lastMoveLogTime.TryGetValue(npcId, out float lastMoveLog)) lastMoveLog = 0f;
            if (Time.time - lastMoveLog >= 2f)
            {
                s_lastMoveLogTime[npcId] = Time.time;
                try
                {
                    var p = instance.transform.position;
                    long tsMove = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    System.IO.File.AppendAllText(
                        "/home/benny/Projects/valheim_villages/.cursor/debug.log",
                        $"{{\"hypothesisId\":\"H_MOVE\",\"location\":\"VillagerMovement.cs:MoveTowards\",\"message\":\"MoveTowards frame\",\"data\":{{\"npc\":\"{instance.GetComponent<Character>()?.GetHoverName() ?? "?"}\",\"pos\":\"{p.x:F2},{p.y:F2},{p.z:F2}\",\"wp\":\"{path[0].x:F2},{path[0].y:F2},{path[0].z:F2}\",\"dir\":\"{dir.x:F2},{dir.y:F2},{dir.z:F2}\",\"wpCount\":{path.Count},\"running\":{running.ToString().ToLower()}}},\"timestamp\":{tsMove}}}\n");
                }
                catch { }
            }
            // #endregion

            return false;
        }

        /// <summary>Delegates to ExecutePathingTick for API compatibility.</summary>
        public static bool MoveToDestination(
            MonsterAI instance, Vector3 destination, float dt,
            float distance = 0.5f, bool ignoreFire = false)
        {
            return ExecutePathingTick(instance, destination, dt, distance, ignoreFire);
        }

        // #region agent log
        /// <summary>Recorded player walkable path, loaded once from disk.</summary>
        private static List<Vector3> s_recordedPath;
        private static bool s_recordedPathLoaded;

        /// <summary>
        /// Compute the average closest-point distance between computed path and recorded path.
        /// Returns -1 if recorded path is unavailable.
        /// </summary>
        private static float ComputeDeviationFromRecordedPath(List<Vector3> computedPath)
        {
            if (computedPath == null || computedPath.Count == 0) return -1f;

            if (!s_recordedPathLoaded)
            {
                s_recordedPathLoaded = true;
                try
                {
                    string json = System.IO.File.ReadAllText(
                        "/home/benny/Projects/valheim_villages/.cursor/hna_walkable_path.json");
                    s_recordedPath = new List<Vector3>();
                    // Minimal JSON parse: find "positions" array, extract [x,y,z] triples
                    int idx = json.IndexOf("\"positions\"");
                    if (idx >= 0)
                    {
                        int arrStart = json.IndexOf('[', idx);
                        // Parse nested arrays: [[x,y,z],[x,y,z],...]
                        int pos = arrStart + 1;
                        while (pos < json.Length)
                        {
                            int innerStart = json.IndexOf('[', pos);
                            if (innerStart < 0) break;
                            int innerEnd = json.IndexOf(']', innerStart);
                            if (innerEnd < 0) break;
                            string inner = json.Substring(innerStart + 1, innerEnd - innerStart - 1);
                            var parts = inner.Split(',');
                            if (parts.Length >= 3 &&
                                float.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                                float.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out float y) &&
                                float.TryParse(parts[2].Trim(), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out float z))
                            {
                                s_recordedPath.Add(new Vector3(x, y, z));
                            }
                            pos = innerEnd + 1;
                        }
                    }
                    Plugin.Log?.LogInfo($"[Movement] Loaded recorded path: {s_recordedPath.Count} points");
                }
                catch
                {
                    s_recordedPath = null;
                }
            }

            if (s_recordedPath == null || s_recordedPath.Count == 0) return -1f;

            float totalDev = 0f;
            foreach (var wp in computedPath)
            {
                float minDist = float.MaxValue;
                foreach (var rp in s_recordedPath)
                {
                    float d = Vector3.Distance(wp, rp);
                    if (d < minDist) minDist = d;
                }
                totalDev += minDist;
            }
            return totalDev / computedPath.Count;
        }
        // #endregion

        /// <summary>
        /// Run essential BaseAI updates that are normally done by BaseAI.UpdateAI.
        /// </summary>
        public static void RunBaseAIUpdates(MonsterAI instance, float dt)
        {
            try
            {
                typeof(BaseAI)
                    .GetMethod("UpdateRegeneration",
                        BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.Invoke(instance, new object[] { dt });
            }
            catch { /* ignore */ }

            try
            {
                var field = typeof(BaseAI).GetField("m_timeSinceHurt",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null)
                {
                    float current = (float)field.GetValue(instance);
                    field.SetValue(instance, current + dt);
                }
            }
            catch { /* ignore */ }
        }

        /// <summary>
        /// Check if NPC is stalled and try to open blocking doors.
        /// </summary>
        public static void CheckMovementStall(
            MonsterAI instance, Vector3? target,
            ref Vector3 lastCheckPosition, ref float stallStartTime)
        {
            if (!target.HasValue) return;

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
