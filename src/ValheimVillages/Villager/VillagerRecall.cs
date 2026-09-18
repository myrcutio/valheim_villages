using UnityEngine;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.Villager
{
    /// <summary>
    ///     Recall for a villager with no live instance on this peer.
    ///     <para>
    ///     A villager that has wandered — or been flung — far enough from the village that
    ///     its zone unloaded has no GameObject to move, so the roster's Recall button used to
    ///     give up with "try again near the village". That is useless advice for the case it
    ///     fires on: the villager is unreachable precisely BECAUSE it is far away, and
    ///     walking there is what the player was trying to avoid. Its stored position is still
    ///     writable though, so recall it by moving that instead; it arrives already standing
    ///     at the station the next time its zone loads.
    ///     </para>
    /// </summary>
    internal static class VillagerRecall
    {
        /// <summary>
        ///     Move an unloaded villager's stored position to <paramref name="stationPos" />.
        ///     Host-only — villager ZDOs are host-authoritative, and a client cannot make the
        ///     write stick. Returns false when there is nothing to move.
        /// </summary>
        internal static bool RecallUnloaded(VillagerRecord record, Vector3 stationPos)
        {
            if (record == null) return false;

            if (ZNet.instance == null || !ZNet.instance.IsServer())
            {
                Plugin.Log?.LogWarning(
                    "[VillagerRecall] Recall of an unloaded villager must run on the host; " +
                    "the villager ZDO is host-authoritative.");
                return false;
            }

            var npc = record.NpcZdoId;
            if (npc == ZDOID.None) return false;

            var zdoMan = ZDOMan.instance;
            var zdo = zdoMan?.GetZDO(npc);
            if (zdo == null) return false;

            // Claim before writing: ZDO edits only persist for the owner.
            zdo.SetOwner(ZDOMan.GetSessionID());

            var from = zdo.GetPosition();
            zdo.SetPosition(stationPos);
            zdo.Persistent = true;

            Plugin.Log?.LogInfo(
                $"[VillagerRecall] Recalled unloaded '{record.Name}' ({record.RecordId}) from " +
                $"({from.x:F1},{from.y:F1},{from.z:F1}) to the station at " +
                $"({stationPos.x:F1},{stationPos.y:F1},{stationPos.z:F1}); it will be there " +
                "when its zone next loads.");
            return true;
        }
    }
}
