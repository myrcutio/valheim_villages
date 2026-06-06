using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     Prevents villager ZDOs from being released to no-owner when a player
    ///     leaves the area. Instead, transfers ownership to the server so the
    ///     server can continue simulating the villager AI.
    /// </summary>
    [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
    public static class VillagerZDOOwnershipPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Vector3 refPosition)
        {
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var zone = ZoneSystem.GetZone(refPosition);
            var nearObjects = new List<ZDO>();
            ZDOMan.instance.FindSectorObjects(
                zone,
                ZoneSystem.instance.m_activeArea,
                0,
                nearObjects);

            var serverUID = ZNet.GetUID();
            foreach (var zdo in nearObjects)
            {
                // Villager NPC ZDOs (back-reference) and record carrier ZDOs both carry
                // vv_record_id; keep them server-owned so the server simulates/persists them.
                var vid = zdo.GetString("vv_record_id");
                if (!string.IsNullOrEmpty(vid) && zdo.GetOwner() != serverUID)
                    zdo.SetOwner(serverUID);
            }
        }
    }
}