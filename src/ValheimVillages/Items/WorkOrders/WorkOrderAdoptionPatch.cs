using HarmonyLib;
using ValheimVillages.Villages;

namespace ValheimVillages.Items.WorkOrders
{
    /// <summary>
    ///     Claim a work-order token the moment the player finishes putting it in a chest,
    ///     rather than waiting for that village's next map rebuild.
    ///     <para>
    ///     Adoption originally ran only inside the partition, because that is where the
    ///     village footprint it scopes by is published. But dropping an item into a chest
    ///     changes no pieces and no terrain, so it triggers no partition — measured: a token
    ///     carried between two villages sat unclaimed in the destination's chest
    ///     (<c>vv_chestpolicy</c> showed the chest RESERVED for it) until a manual
    ///     <c>vv_repartition</c>, which is not something a player would do.
    ///     </para>
    ///     <para>
    ///     Hooked on the container UI closing rather than on every inventory change: that is
    ///     the moment the player has finished arranging the chest, it fires once instead of
    ///     per item moved, and the work is one village's chest scan.
    ///     </para>
    /// </summary>
    [HarmonyPatch(typeof(InventoryGui), nameof(InventoryGui.Hide))]
    public static class WorkOrderAdoptionOnChestClosePatch
    {
        [HarmonyPostfix]
        public static void Postfix()
        {
            // Host-only writer; on a dedicated server the client's close is not the
            // authority and AdoptOrphans no-ops there anyway.
            if (ZNet.instance == null || !ZNet.instance.IsServer()) return;

            var player = Player.m_localPlayer;
            if (player == null) return;

            // Every village, not just the one underfoot: taking a token OUT of a chest has to
            // release it from that village too, and the player may have walked away by then.
            var changed = WorkOrderAdoption.ReconcileAll();
            if (changed > 0)
                player.Message(MessageHud.MessageType.Center,
                    changed == 1 ? "Work order updated" : $"{changed} work orders updated");
        }
    }
}
