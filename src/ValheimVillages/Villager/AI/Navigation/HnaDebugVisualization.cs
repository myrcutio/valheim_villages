using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    /// Spawns torches at HNA link positions (door/stair traverse points) so link locations are visible in-game.
    /// Clears previous markers when the graph is rebuilt.
    /// </summary>
    public static class HnaDebugVisualization
    {
        private const string LinkTorchPrefabName = "piece_groundtorch_mist";
        private const string RegionTorchPrefabName = "piece_groundtorch_wood";
        /// <summary>Raise marker so it sits on the surface instead of embedded (meters).</summary>
        private const float MarkerHeightOffset = 0.25f;
        /// <summary>Positions within this distance are treated as duplicate (meters).</summary>
        private const float DuplicateTolerance = 0.2f;
        private static readonly List<GameObject> s_spawnedLinkMarkers = new List<GameObject>();
        private static readonly List<GameObject> s_spawnedRegionMarkers = new List<GameObject>();

        /// <summary>Whether markers are currently displayed.</summary>
        public static bool MarkersEnabled { get; private set; }

        /// <summary>Toggle marker visibility. Spawns if off → on, clears if on → off.</summary>
        [DevCommand("Toggle HNA debug markers (region/link torches) on or off", Name = "hna_markers")]
        public static void ToggleMarkers()
        {
            if (MarkersEnabled)
            {
                ClearMarkers();
                MarkersEnabled = false;
                Plugin.Log?.LogInfo("[HNA] Debug markers disabled");
                Console.instance?.Print("HNA debug markers OFF");
            }
            else
            {
                SpawnTorchesAtRegions();
                MarkersEnabled = true;
                Plugin.Log?.LogInfo("[HNA] Debug markers enabled");
                Console.instance?.Print($"HNA debug markers ON ({s_spawnedRegionMarkers.Count} regions, {s_spawnedLinkMarkers.Count} links)");
            }
        }

        private static (int, int, int) PosKey(Vector3 p)
        {
            float g = 1f / DuplicateTolerance;
            return (Mathf.RoundToInt(p.x * g), Mathf.RoundToInt(p.y * g), Mathf.RoundToInt(p.z * g));
        }


        /// <summary>
        /// Properly destroy a marker GameObject, removing its ZDO so it doesn't persist to the save file.
        /// </summary>
        private static void DestroyMarker(GameObject go)
        {
            if (go == null) return;
            var zNetView = go.GetComponent<ZNetView>();
            if (zNetView != null && zNetView.IsValid() && ZNetScene.instance != null)
                ZNetScene.instance.Destroy(go);
            else
                Object.Destroy(go);
        }

        /// <summary>
        /// Remove all marker prefabs from the scene (including any not in our list, e.g. after hot reload).
        /// Uses ZNetScene.Destroy to properly remove ZDOs so markers don't persist to the save file.
        /// </summary>
        [RegisterCleanup]
        public static void ClearMarkers()
        {
            foreach (var go in s_spawnedLinkMarkers)
                DestroyMarker(go);
            foreach (var go in s_spawnedRegionMarkers)
                DestroyMarker(go);
            s_spawnedLinkMarkers.Clear();
            s_spawnedRegionMarkers.Clear();

            // Also clean up orphaned clones (e.g. from previous hot-reloads where we lost our list references)
            string linkCloneName = LinkTorchPrefabName + "(Clone)";
            string regionCloneName = RegionTorchPrefabName + "(Clone)";
            var allTransforms = ZNetView.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int removed = 0;
            foreach (var t in allTransforms)
            {
                if (t == null || t.gameObject == null) continue;
                if (t.gameObject.name == linkCloneName || t.gameObject.name == regionCloneName)
                {
                    DestroyMarker(t.gameObject);
                    removed++;
                }
            }
            if (removed > 0)
                Plugin.Log?.LogInfo($"[HNA] Cleared {removed} orphaned marker prefab(s) from scene");
        }

        /// <summary>
        /// Spawn a torch at each HNA link position (start and end of each door/stair link).
        /// Markers show where villagers would traverse between regions.
        /// </summary>
        public static void SpawnTorchesAtRegions()
        {
            ClearMarkers();
            if (!HnaRegionGraph.IsAvailable)
                return;
            if (ZNetScene.instance == null)
            {
                Plugin.Log?.LogWarning("[HNA] ZNetScene not available, skipping torch visualization");
                return;
            }
            var linkPrefab = ZNetScene.instance.GetPrefab(LinkTorchPrefabName);
            var regionPrefab = ZNetScene.instance.GetPrefab(RegionTorchPrefabName);
            if (linkPrefab == null || regionPrefab == null)
            {
                Plugin.Log?.LogWarning($"[HNA] Prefab '{LinkTorchPrefabName}' or '{RegionTorchPrefabName}' not found, skipping torch visualization");
                return;
            }
            var linkPositions = HnaRegionGraph.GetAllLinkPositions();
            var regionPositions = HnaRegionGraph.GetAllRegionCenters();
            Vector3 surfaceOffset = Vector3.up * MarkerHeightOffset;
            foreach (var pos in linkPositions)
            {
                var go = UnityEngine.Object.Instantiate(linkPrefab, pos + surfaceOffset, Quaternion.identity);
                if (go != null)
                    s_spawnedLinkMarkers.Add(go);
            }
            foreach (var pos in regionPositions)
            {
                var go = UnityEngine.Object.Instantiate(regionPrefab, pos + surfaceOffset, Quaternion.identity);
                if (go != null)
                    s_spawnedRegionMarkers.Add(go);
            }
        }

        /// <summary>
        /// Emergency cleanup: find and destroy ALL instances of the marker prefabs in the world.
        /// This removes markers persisted from previous sessions where Object.Destroy was used
        /// instead of ZNetScene.Destroy, leaving ZDOs in the save file.
        /// </summary>
        [DevCommand("Remove ALL persisted torch markers from the world (fixes leftover markers from previous sessions)", Name = "hna_cleanup")]
        public static void CleanupPersistedMarkers()
        {
            if (ZNetScene.instance == null)
            {
                Console.instance?.Print("ZNetScene not available. Load into a world first.");
                return;
            }

            // IMPORTANT: Do NOT call ClearMarkers() first — it would invalidate the ZNetViews
            // before we can use ZNetScene.Destroy() to properly remove the ZDOs.
            // Instead, scan ZNetViews directly and destroy via ZNetScene.

            string linkClone = LinkTorchPrefabName + "(Clone)";
            string regionClone = RegionTorchPrefabName + "(Clone)";
            var allZnvs = Object.FindObjectsByType<ZNetView>(FindObjectsInactive.Include, FindObjectsSortMode.None);

            int destroyedWithZdo = 0;
            int destroyedWithoutZdo = 0;
            foreach (var znv in allZnvs)
            {
                if (znv == null || znv.gameObject == null) continue;
                var goName = znv.gameObject.name;
                if (goName != linkClone && goName != regionClone &&
                    goName != LinkTorchPrefabName && goName != RegionTorchPrefabName)
                    continue;

                if (znv.IsValid())
                {
                    // Claim ownership so we can destroy the ZDO
                    if (!znv.IsOwner())
                    {
                        var zdo = znv.GetZDO();
                        zdo?.SetOwner(ZDOMan.GetSessionID());
                    }
                    ZNetScene.instance.Destroy(znv.gameObject);
                    destroyedWithZdo++;
                }
                else
                {
                    Object.Destroy(znv.gameObject);
                    destroyedWithoutZdo++;
                }
            }

            // Also clear our internal tracking lists (they may reference now-destroyed objects)
            s_spawnedLinkMarkers.Clear();
            s_spawnedRegionMarkers.Clear();

            Console.instance?.Print($"Cleaned up {destroyedWithZdo + destroyedWithoutZdo} marker(s) ({destroyedWithZdo} with ZDO, {destroyedWithoutZdo} local-only). Save to persist.");
            Plugin.Log?.LogInfo($"[HNA] Cleanup: {destroyedWithZdo} ZDO-destroyed, {destroyedWithoutZdo} local-only");
        }

        /// <summary>
        /// Dump positions of all surviving marker prefabs to a JSON file.
        /// Run after manually deleting invalid markers to capture the "validated" set.
        /// </summary>
        [DevCommand("Save positions of all surviving markers to .cursor/hna_validated_markers.json", Name = "hna_markers_dump")]
        public static void DumpSurvivingMarkers()
        {
            string linkClone = LinkTorchPrefabName + "(Clone)";
            string regionClone = RegionTorchPrefabName + "(Clone)";
            var regionPositions = new List<Vector3>();
            var linkPositions = new List<Vector3>();

            var allTransforms = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var t in allTransforms)
            {
                if (t == null || t.gameObject == null) continue;
                var n = t.gameObject.name;
                if (n == regionClone || n == RegionTorchPrefabName)
                    regionPositions.Add(t.position);
                else if (n == linkClone || n == LinkTorchPrefabName)
                    linkPositions.Add(t.position);
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"regionCount\": {regionPositions.Count},\n");
            sb.Append($"  \"linkCount\": {linkPositions.Count},\n");

            sb.Append("  \"regions\": [\n");
            for (int i = 0; i < regionPositions.Count; i++)
            {
                var p = regionPositions[i];
                sb.Append($"    [{p.x:F2}, {p.y:F2}, {p.z:F2}]");
                sb.Append(i < regionPositions.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ],\n");

            sb.Append("  \"links\": [\n");
            for (int i = 0; i < linkPositions.Count; i++)
            {
                var p = linkPositions[i];
                sb.Append($"    [{p.x:F2}, {p.y:F2}, {p.z:F2}]");
                sb.Append(i < linkPositions.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ]\n");
            sb.Append("}\n");

            string path = System.IO.Path.Combine(
                "/home/benny/Projects/valheim_villages",
                ".cursor", "hna_validated_markers.json");
            System.IO.File.WriteAllText(path, sb.ToString());
            Console.instance?.Print($"Saved {regionPositions.Count} regions + {linkPositions.Count} links to {path}");
            Plugin.Log?.LogInfo($"[HNA] Marker dump: {regionPositions.Count} regions, {linkPositions.Count} links → {path}");
        }
    }
}
