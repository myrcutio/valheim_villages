using UnityEngine;

namespace ValheimVillages.Villager.AI.Work
{
    /// <summary>
    ///     The one correct way for a villager to take or remove an item lying on the ground.
    ///
    ///     <para>A ground drop is a networked object that usually belongs to someone else — the
    ///     client that picked the crop or dropped the stack. Two shortcuts both break it:</para>
    ///     <list type="bullet">
    ///       <item>A bare <c>Object.Destroy</c> removes only this peer's GameObject.
    ///         <c>ZNetView.OnDestroy</c> doesn't unregister, so ZNetScene keeps a dead entry;
    ///         the owner still has the item (the villager is credited AND it stays in the world).
    ///         When the owner later picks it up, <c>ZNetScene.OnZDODestroyed</c> throws on the
    ///         dead entry, the ZDO survives here, and ZNetScene re-creates it — a ghost only this
    ///         peer can see, parked underground by velocity extrapolation.</item>
    ///       <item><c>ZNetScene.Destroy</c> without ownership drops only the local instance
    ///         (it destroys the ZDO only for the owner), and the next CreateObjects pass
    ///         re-instantiates it.</item>
    ///     </list>
    ///     <para>So: claim ownership, then change the stack or destroy through ZNetScene, which
    ///     then destroys the ZDO on every peer.</para>
    /// </summary>
    public static class GroundDrops
    {
        /// <summary>
        ///     Take up to <paramref name="maxTake" /> from <paramref name="drop" />, removing it
        ///     entirely when nothing is left. Returns how many were taken.
        /// </summary>
        public static int Take(ItemDrop drop, int maxTake)
        {
            if (drop == null || drop.m_itemData == null || maxTake <= 0) return 0;

            var stack = drop.m_itemData.m_stack;
            if (stack <= 0) return 0;

            if (stack <= maxTake)
            {
                Destroy(drop);
                return stack;
            }

            // SetStack writes the ZDO only on the owner, so claim first or the change is local.
            var nview = drop.GetComponent<ZNetView>();
            if (nview != null && nview.IsValid()) nview.ClaimOwnership();
            drop.SetStack(stack - maxTake);
            return maxTake;
        }

        /// <summary>Remove <paramref name="drop" /> from the world on every peer.</summary>
        public static void Destroy(ItemDrop drop)
        {
            if (drop == null) return;

            var nview = drop.GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid())
            {
                // Not networked (no ZDO): there is nothing to replicate, only the local object.
                Object.Destroy(drop.gameObject);
                return;
            }

            nview.ClaimOwnership();
            ZNetScene.instance.Destroy(drop.gameObject);
        }
    }
}
