using UnityEngine;
using ValheimVillages.Attributes;

namespace ValheimVillages.Villager.AI.Navigation
{
    /// <summary>
    ///     Cleanup command for legacy torch-based debug markers. Torch visualization
    ///     has been replaced by the GL-based <see cref="Pathfinding.PathDebugRenderer" />;
    ///     this class only retains the cleanup command so persisted torch ZDOs from
    ///     old sessions can still be removed.
    /// </summary>
    public static class RegionDebugVisualization
    {
        private const string LinkTorchPrefabName = "piece_groundtorch_mist";
        private const string RegionTorchPrefabName = "piece_groundtorch_wood";

        /// <summary>
        ///     Emergency cleanup: find and destroy ALL persisted torch markers from
        ///     old sessions that used torch-based visualization.
        /// </summary>
        [DevCommand("Remove ALL persisted torch markers from the world", Name = "vv_hna_cleanup")]
        public static void CleanupPersistedMarkers()
        {
            if (ZNetScene.instance == null)
            {
                Console.instance?.Print("ZNetScene not available. Load into a world first.");
                return;
            }

            var linkClone = LinkTorchPrefabName + "(Clone)";
            var regionClone = RegionTorchPrefabName + "(Clone)";
            var allZnvs = Object.FindObjectsByType<ZNetView>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            var destroyed = 0;
            foreach (var znv in allZnvs)
            {
                if (znv == null || znv.gameObject == null) continue;
                var goName = znv.gameObject.name;
                if (goName != linkClone && goName != regionClone &&
                    goName != LinkTorchPrefabName && goName != RegionTorchPrefabName)
                    continue;

                if (znv.IsValid())
                {
                    if (!znv.IsOwner())
                        znv.GetZDO()?.SetOwner(ZDOMan.GetSessionID());
                    ZNetScene.instance.Destroy(znv.gameObject);
                }
                else
                {
                    Object.Destroy(znv.gameObject);
                }

                destroyed++;
            }

            Console.instance?.Print($"Cleaned up {destroyed} torch marker(s). Save to persist.");
            Plugin.Log?.LogInfo($"[Region] Torch cleanup: {destroyed} removed");
        }
    }
}