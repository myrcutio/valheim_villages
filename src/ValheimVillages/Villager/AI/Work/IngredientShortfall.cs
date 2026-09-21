using UnityEngine;

namespace ValheimVillages.Villager.AI.Work
{
    /// <summary>
    ///     One ingredient a work order is short of, and why.
    ///
    ///     <para>"Missing ingredients" is misleading often enough to be worth this type: the
    ///     village routinely HAS the item, in a chest no villager can walk to. Measured on a
    ///     live server — 53 Thistle sat on a shelf behind a fermenter, so the Sausages order
    ///     reported itself out of Thistle while the Thistle farm order simultaneously reported
    ///     "already have 53/50", because the quota counts the whole footprint and the fetch
    ///     only counts what is on the region graph. A shortfall the player fixes by moving one
    ///     chest should not read the same as one they have to go foraging for.</para>
    /// </summary>
    public struct IngredientShortfall
    {
        public string PrefabName;

        /// <summary>Localized name, for anything a player reads.</summary>
        public string DisplayName;

        public int Needed;

        /// <summary>How many are in chests the villager can actually walk to.</summary>
        public int FoundReachable;

        /// <summary>How many are in the village at all, reachable or not.</summary>
        public int FoundInVillage;

        /// <summary>
        ///     The chest holding the item that the villager cannot reach — non-null only when
        ///     the village holds enough and the ONLY problem is getting to it.
        /// </summary>
        public Container OutOfReachHolder;

        /// <summary>True when this is an access problem, not a supply problem.</summary>
        public bool IsOutOfReach => OutOfReachHolder != null;

        /// <summary>
        ///     The blocked-order reason. Every one of these ends in something the player can go
        ///     and do — a reason they cannot act on is just a villager complaining.
        /// </summary>
        public string Describe()
        {
            if (!IsOutOfReach)
                return $"Needs {Needed} {DisplayName}, and only {FoundReachable} are stocked. " +
                       "Put more in a village chest.";

            var pos = OutOfReachHolder.transform.position;
            return $"There are {FoundInVillage} {DisplayName} in the village, but the chest at " +
                   $"({pos.x:F0},{pos.z:F0}) is out of reach — nothing can stand next to it. " +
                   "Move the chest somewhere open, or clear the floor in front of it.";
        }
    }
}
