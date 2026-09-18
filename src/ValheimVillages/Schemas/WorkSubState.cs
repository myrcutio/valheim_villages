using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Villager.AI.Work;

namespace ValheimVillages.Schemas
{
    /// <summary>
    ///     Sub-states within the Working behavior state.
    /// </summary>
    public enum WorkSubState
    {
        Idle, // Not working, ready to scan
        GatheringFuel, // Walking to container with fuel
        FuelingStation, // Walking to fire/station to add fuel
        GatheringIngredients, // Walking to ingredient chest
        TravelingToStation, // Walking to the crafting station
        Crafting, // Waiting at station (craft timer)
        ReturningToChest, // Walking back to deposit result
        Depositing, // Placing crafted item in chest
    }

    /// <summary>
    ///     Tracks the current work order context for a villager's crafting session.
    /// </summary>
    public class WorkOrderContext
    {
        /// <summary>Input item prefab name for cooking (e.g. RawMeat); used to find our slot when polling.</summary>
        public string CookingInputItemName;

        /// <summary>True when we collected the cooked item from the station into the chest (skip deposit step on arrival).</summary>
        public bool CookingItemAlreadyInChest;

        /// <summary>
        ///     True after we've sent RPC_RemoveDoneItem for this craft; prevents re-entry from seeing Done again before slot
        ///     clears.
        /// </summary>
        public bool CookingRemovalRequested;

        /// <summary>
        ///     When using a physical CookingStation (roast meat/fish), the station instance so the NPC can add items and poll
        ///     for done.
        /// </summary>
        public CookingStation CookingStationRef;

        /// <summary>Cook time in seconds for the current item (from CookingStation GetItemConversion); 0 = use default.</summary>
        public float CraftCookTimeSeconds;

        /// <summary>Time when crafting animation started.</summary>
        public float CraftStartTime;

        /// <summary>Position of the crafting station to use.</summary>
        public Vector3 CraftStationPosition;

        /// <summary>Number of items crafted so far in this work session.</summary>
        public int CraftedCount;

        /// <summary>Index into IngredientSources for multi-chest gathering.</summary>
        public int CurrentIngredientIndex;

        /// <summary>Container holding the fuel item (set during scan when fueling is required).</summary>
        public Container FuelContainer;

        /// <summary>Fuel requirement diagnosed during scan; null when no fueling is needed.</summary>
        public FuelNeed? FuelRequirement;

        /// <summary>Where each ingredient comes from.</summary>
        public List<IngredientSource> IngredientSources;

        /// <summary>
        ///     True when this context is a cooking rescue (retrieving done items after load/reload), not a normal crafting
        ///     cycle.
        /// </summary>
        public bool IsRescue;

        /// <summary>The recipe to craft.</summary>
        public Recipe Recipe;

        /// <summary>Input item prefab name for smelting (e.g. CopperOre); used to identify the active conversion.</summary>
        public string SmelterInputItemName;

        /// <summary>True when we collected the smelted output into the chest (skip deposit step on arrival).</summary>
        public bool SmelterItemAlreadyInChest;

        /// <summary>Snapshot of Smelter.GetProcessedQueueSize() when the villager arrived; used to detect new output.</summary>
        public int SmelterProcessedAtStart;

        /// <summary>
        ///     When using a physical Smelter-family prefab (smelter, blastfurnace, charcoal_kiln, eitrrefinery,
        ///     piece_spinningwheel, windmill), the station instance so the NPC can add ore and poll for output.
        /// </summary>
        public Smelter SmelterRef;

        /// <summary>True after we've requested removal of the processed item; prevents re-entry from re-polling.</summary>
        public bool SmelterRemovalRequested;

        /// <summary>
        ///     The beehive this Honey order harvests from. Set during the scan; null for every
        ///     other order kind.
        /// </summary>
        public Beehive BeehiveRef;

        /// <summary>
        ///     True once RPC_Extract has been sent for this trip. The hive's honey drops appear
        ///     a frame or more later (the RPC goes through the network pump), so the workflow
        ///     extracts once and then polls the ground; without this it would re-extract every
        ///     poll tick.
        /// </summary>
        public bool BeehiveExtractRequested;

        /// <summary>
        ///     True when this order is fulfilled by picking a plant rather than by working a
        ///     station. Kept as its own flag instead of testing <see cref="PickableRef" />,
        ///     because that reference can legitimately evaporate mid-order: a plant with no
        ///     respawn timer destroys its own GameObject the moment it is picked, and a
        ///     destroyed Unity object reads as null. Testing the reference would then make a
        ///     forage order look like an ordinary craft and hand it to the fixed craft timer,
        ///     which produces the item out of nothing.
        /// </summary>
        public bool IsForageOrder;

        /// <summary>
        ///     The ripe pickable (berry bush, mushroom, thistle) this forage order harvests.
        ///     Set during the scan, re-aimed after every load; null for every other order kind,
        ///     and null again once a picked plant destroys itself.
        /// </summary>
        public Pickable PickableRef;

        /// <summary>
        ///     Where <see cref="PickableRef" />'s drops will land, captured at the scan BEFORE
        ///     the pick. A pickable with no respawn timer destroys its own GameObject the
        ///     instant it is picked, so reading the position off the component afterwards can
        ///     find nothing to read — and the ground sweep would then search around the origin.
        /// </summary>
        public Vector3 PickableOutputPoint;

        /// <summary>
        ///     True once the pick has been requested for this trip. The drops appear a frame or
        ///     more later (Interact dispatches RPC_Pick through the network pump), so the
        ///     workflow picks once and then polls the ground; without this it would re-pick
        ///     every poll tick.
        /// </summary>
        public bool PickableHarvestRequested;

        /// <summary>The container where the work order was found.</summary>
        public Container SourceContainer;

        /// <summary>The work order match data.</summary>
        public WorkOrderMatch WorkOrder;

        /// <summary>
        ///     Items pulled from source containers that haven't yet been
        ///     committed to a station/output. Each entry: (source container,
        ///     prefab name, amount). On successful commit at a station, the
        ///     corresponding entry is cleared. On AbandonWork or any other
        ///     workflow abort, the entries are restored to their source
        ///     containers (or dropped at the villager's feet if the source
        ///     is full/destroyed) so the items aren't permanently lost.
        ///     Without this list, a stall mid-walk between chest and station
        ///     drains the chest of items that effectively vanish — the
        ///     workflow restarts, pulls more from the chest, repeats.
        /// </summary>
        public List<HeldItem> HeldItems = new List<HeldItem>();

        /// <summary>
        ///     Set when the villager diverts to refuel a cooking station that went cold MID-CYCLE
        ///     — either on arrival (ingredients already in hand) or while the food was cooking
        ///     (ingredients already committed to the station). Either way the fuelling leg must
        ///     return straight to the station afterwards.
        ///
        ///     <para>The normal fuelling leg runs BEFORE gathering and correctly re-gathers when
        ///     it finishes; re-gathering in the divert case would withdraw a SECOND set of
        ///     ingredients from the chests.</para>
        /// </summary>
        public bool ResumeAtStationAfterFueling;
    }

    /// <summary>
    ///     One transient "in transit" item — pulled from <see cref="SourceContainer"/>
    ///     but not yet committed to a station. See <see cref="WorkOrderContext.HeldItems"/>.
    /// </summary>
    public class HeldItem
    {
        public Container SourceContainer;
        public string PrefabName;
        public int Amount;
    }

    /// <summary>
    ///     Represents a matching work order found in a container.
    /// </summary>
    public class WorkOrderMatch
    {
        public ItemDrop.ItemData ItemData;
        public string ItemPrefabName;
        public int MaxQuantity;
        public int MinQuantity;
        public Container SourceContainer;
        public string StationName;
    }

    /// <summary>
    ///     Represents a source of a required ingredient in a container.
    /// </summary>
    public class IngredientSource
    {
        public int Amount;
        public Container Container;
        public string PrefabName;
    }
}