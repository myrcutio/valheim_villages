using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Villager.AI;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     Keeps village zones loaded on the server even when no player is nearby.
    ///     Injects village bed positions as phantom reference points for zone
    ///     creation and object spawning.
    /// </summary>
    public static class VillageZoneLoadingPatch
    {
        private const int MaxPhantomZones = 10;
        private const float ZoneSize = 64f;

        /// <summary>
        ///     Deduplicate bed positions into one per zone grid cell so multiple
        ///     villagers in the same village produce only one phantom zone center.
        /// </summary>
        private static List<Vector3> GetPhantomPositions()
        {
            var beds = VillagerAIManager.GetAllBedPositions();
            if (beds.Count == 0) return beds;

            var seen = new HashSet<Vector2Int>();
            var result = new List<Vector3>();

            foreach (var pos in beds)
            {
                var cell = new Vector2Int(
                    Mathf.FloorToInt(pos.x / ZoneSize),
                    Mathf.FloorToInt(pos.z / ZoneSize));

                if (seen.Add(cell))
                {
                    result.Add(pos);
                    if (result.Count >= MaxPhantomZones) break;
                }
            }

            return result;
        }

        /// <summary>
        ///     After vanilla creates zones for player peers, also create zones
        ///     around village bed positions so terrain and vegetation load.
        /// </summary>
        [HarmonyPatch(typeof(ZoneSystem), "CreateLocalZones")]
        [HarmonyPatch(new[] { typeof(Vector3) })]
        private static class ZoneSystemCreateZonesPatch
        {
            // We can't postfix on Update easily (CreateLocalZones is called
            // from a loop over peers). Instead, piggyback on each
            // CreateLocalZones call: after the last peer's call, also invoke
            // for phantom positions. To avoid calling per-peer, we use a
            // separate postfix on the ZoneSystem.Update method.
        }

        [HarmonyPatch(typeof(ZoneSystem), "Update")]
        private static class ZoneSystemUpdatePatch
        {
            [HarmonyPostfix]
            private static void Postfix(ZoneSystem __instance)
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

                var phantoms = GetPhantomPositions();
                if (phantoms.Count == 0) return;

                var traverse = Traverse.Create(__instance);
                foreach (var pos in phantoms)
                    traverse.Method("CreateLocalZones", pos).GetValue<bool>();
            }
        }

        /// <summary>
        ///     Prefix-replace CreateDestroyObjects on the server so village zone
        ///     ZDOs are included in the active set. Without this, vanilla's
        ///     RemoveObjects would destroy village zone GameObjects every frame.
        ///     On clients, vanilla runs unmodified.
        /// </summary>
        [HarmonyPatch(typeof(ZNetScene), "CreateDestroyObjects")]
        private static class ZNetSceneCreateDestroyPatch
        {
            [HarmonyPrefix]
            private static bool Prefix(ZNetScene __instance)
            {
                if (ZNet.instance == null || !ZNet.instance.IsServer())
                    return true;

                var phantoms = GetPhantomPositions();
                if (phantoms.Count == 0)
                    return true;

                var tr = Traverse.Create(__instance);
                var nearObjects = tr.Field<List<ZDO>>("m_tempCurrentObjects").Value;
                var distantObjects = tr.Field<List<ZDO>>("m_tempCurrentDistantObjects").Value;

                nearObjects.Clear();
                distantObjects.Clear();

                var refPos = ZNet.instance.GetReferencePosition();
                var zone = ZoneSystem.GetZone(refPos);
                var activeArea = ZoneSystem.instance.m_activeArea;
                var activeDistant = ZoneSystem.instance.m_activeDistantArea;

                ZDOMan.instance.FindSectorObjects(zone, activeArea, activeDistant, nearObjects, distantObjects);

                foreach (var pos in phantoms)
                {
                    var phantomZone = ZoneSystem.GetZone(pos);
                    if (phantomZone.x == zone.x && phantomZone.y == zone.y)
                        continue;
                    ZDOMan.instance.FindSectorObjects(phantomZone, activeArea, 0, nearObjects);
                }

                tr.Method("CreateObjects", nearObjects, distantObjects).GetValue();
                tr.Method("RemoveObjects", nearObjects, distantObjects).GetValue();

                return false;
            }
        }
    }
}