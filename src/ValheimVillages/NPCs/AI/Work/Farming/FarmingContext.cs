using System.Collections.Generic;
using UnityEngine;

namespace ValheimVillages.NPCs.AI.Work.Farming
{
    /// <summary>
    /// Sub-states within the farming workflow.
    /// </summary>
    public enum FarmSubState
    {
        Idle,                   // No farming in progress
        GatheringSeeds,         // Walking to chest to pick up seeds
        TravelingToFarm,        // Walking to farm area
        Planting,               // Placing plants on cultivated ground
        TravelingToHarvest,     // Walking to a mature crop
        Harvesting,             // Picking a mature crop
        CollectingDrops,        // Picking up item drops from harvest
        ReturningToChest,       // Walking back to deposit harvested items
        Depositing              // Placing harvested items in chest
    }

    /// <summary>
    /// Tracks the current farming session for a villager.
    /// </summary>
    public class FarmingContext
    {
        /// <summary>Work order that triggered this farming session.</summary>
        public WorkOrderMatch WorkOrder;

        /// <summary>The recipe for this crop (e.g., CarrotSeeds -> Carrot).</summary>
        public Recipe Recipe;

        /// <summary>Container where the work order and seeds are.</summary>
        public Container SourceContainer;

        /// <summary>Where each ingredient (seed) comes from.</summary>
        public List<IngredientSource> IngredientSources;

        /// <summary>Index into IngredientSources for multi-chest gathering.</summary>
        public int CurrentIngredientIndex;

        /// <summary>Position of the farm area for planting.</summary>
        public Vector3 FarmPosition;

        /// <summary>The piece prefab to plant (e.g., sapling_carrot).</summary>
        public GameObject PlantPiecePrefab;

        /// <summary>Grow radius from the Plant component (for spacing).</summary>
        public float PlantGrowRadius;

        /// <summary>Number of seeds gathered this cycle.</summary>
        public int SeedsGathered;

        /// <summary>Number of items harvested so far toward the work order quota.</summary>
        public int HarvestedCount;

        /// <summary>Items currently carried to the chest (deposited on arrival at container).</summary>
        public int CarriedHarvestCount;

        /// <summary>Number of items planted this session (not yet harvestable).</summary>
        public int PlantedThisSession;

        /// <summary>Current harvest target (walking to this Pickable).</summary>
        public Pickable CurrentHarvestTarget;

        /// <summary>Whether we're in a harvesting pass (true) or planting pass (false).</summary>
        public bool IsHarvestingPass;
    }
}
