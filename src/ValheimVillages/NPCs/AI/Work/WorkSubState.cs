using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.NPCs.AI.Work
{
    /// <summary>
    /// Sub-states within the Working behavior state.
    /// </summary>
    public enum WorkSubState
    {
        Idle,                   // Not working, ready to scan
        GatheringIngredients,   // Walking to ingredient chest
        TravelingToStation,     // Walking to the crafting station
        Crafting,               // Waiting at station (craft timer)
        ReturningToChest,       // Walking back to deposit result
        Depositing              // Placing crafted item in chest
    }

    /// <summary>
    /// Tracks the current work order context for a villager's crafting session.
    /// </summary>
    public class WorkOrderContext
    {
        /// <summary>The container where the work order was found.</summary>
        public Container SourceContainer;

        /// <summary>The work order match data.</summary>
        public WorkOrderMatch WorkOrder;

        /// <summary>The recipe to craft.</summary>
        public Recipe Recipe;

        /// <summary>Where each ingredient comes from.</summary>
        public List<IngredientSource> IngredientSources;

        /// <summary>Position of the crafting station to use.</summary>
        public Vector3 CraftStationPosition;

        /// <summary>When using a physical CookingStation (roast meat/fish), the station instance so the NPC can add items and poll for done.</summary>
        public CookingStation CookingStationRef;

        /// <summary>Number of items crafted so far in this work session.</summary>
        public int CraftedCount;

        /// <summary>Time when crafting animation started.</summary>
        public float CraftStartTime;

        /// <summary>Cook time in seconds for the current item (from CookingStation GetItemConversion); 0 = use default.</summary>
        public float CraftCookTimeSeconds;

        /// <summary>Input item prefab name for cooking (e.g. RawMeat); used to find our slot when polling.</summary>
        public string CookingInputItemName;

        /// <summary>True when we collected the cooked item from the station into the chest (skip deposit step on arrival).</summary>
        public bool CookingItemAlreadyInChest;

        /// <summary>True after we've sent RPC_RemoveDoneItem for this craft; prevents re-entry from seeing Done again before slot clears.</summary>
        public bool CookingRemovalRequested;

        /// <summary>Index into IngredientSources for multi-chest gathering.</summary>
        public int CurrentIngredientIndex;

        /// <summary>True when this context is a cooking rescue (retrieving done items after load/reload), not a normal crafting cycle.</summary>
        public bool IsRescue;
    }

    /// <summary>
    /// Represents a matching work order found in a container.
    /// </summary>
    public class WorkOrderMatch
    {
        public Container SourceContainer;
        public ItemDrop.ItemData ItemData;
        public string ItemPrefabName;
        public string StationName;
        public int MinQuantity;
        public int MaxQuantity;
    }

    /// <summary>
    /// Represents a source of a required ingredient in a container.
    /// </summary>
    public class IngredientSource
    {
        public string PrefabName;
        public int Amount;
        public Container Container;
    }
}
