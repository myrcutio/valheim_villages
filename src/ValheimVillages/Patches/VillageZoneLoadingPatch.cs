using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Villager.AI;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     Keeps village zones loaded on the server even when no player is nearby.
    ///     Injects village anchor positions as phantom reference points for zone
    ///     creation and object spawning.
    /// </summary>
    public static class VillageZoneLoadingPatch
    {
        private const int MaxPhantomZones = 10;
        private const float ZoneSize = 64f;

        /// <summary>
        ///     Deduplicate anchor positions into one per zone grid cell so multiple
        ///     villagers in the same village produce only one phantom zone center.
        /// </summary>
        private static List<Vector3> GetPhantomPositions()
        {
            var anchors = VillagerAIManager.GetAllAnchorPositions();
            if (anchors.Count == 0) return anchors;

            var seen = new HashSet<Vector2Int>();
            var result = new List<Vector3>();

            foreach (var pos in anchors)
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
        ///     Collapse a ZDO list to one entry per ZDO uid in place, preserving order and
        ///     dropping nulls. Undoes the duplicate appends from overlapping sector sweeps
        ///     before the list reaches CreateObjects, which would otherwise instantiate a ZDO
        ///     once per duplicate entry and leave untracked orphan GameObjects.
        /// </summary>
        private static void DedupeByUid(List<ZDO> zdos)
        {
            var seen = new HashSet<ZDOID>();
            var write = 0;
            for (var read = 0; read < zdos.Count; read++)
            {
                var zdo = zdos[read];
                if (zdo == null || !seen.Add(zdo.m_uid)) continue;
                zdos[write++] = zdo;
            }

            if (write < zdos.Count)
                zdos.RemoveRange(write, zdos.Count - write);
        }

        /// <summary>
        ///     After vanilla creates zones for player peers, also create zones
        ///     around village anchor positions so terrain and vegetation load.
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

                // The reference-position sweep and each phantom sweep cover overlapping zone
                // blocks, so FindSectorObjects appends the SAME ZDO to nearObjects more than
                // once whenever blocks overlap (e.g. the connected player stands in a phantom
                // village). Vanilla never hands CreateObjects a list with duplicate ZDOs, and
                // CreateObjectsSorted only guards on ZDO.Created at COLLECTION time — so two
                // copies of one not-yet-created ZDO both pass the guard and CreateObject (which
                // has no re-create guard) runs twice, instantiating two GameObjects for one ZDO.
                // AddInstance overwrites m_instances with the second; the first is an untracked
                // orphan that RemoveObjects (it only walks m_instances) can never reap. Its
                // collider lingers and doubles the slot-31 navmesh bake input near the village.
                // Restore vanilla's invariant by collapsing nearObjects to one entry per ZDO.
                DedupeByUid(nearObjects);

                tr.Method("CreateObjects", nearObjects, distantObjects).GetValue();
                tr.Method("RemoveObjects", nearObjects, distantObjects).GetValue();

                return false;
            }
        }
    }
}