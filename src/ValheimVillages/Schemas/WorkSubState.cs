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

        /// <summary>The container where the work order was found.</summary>
        public Container SourceContainer;

        /// <summary>The work order match data.</summary>
        public WorkOrderMatch WorkOrder;
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