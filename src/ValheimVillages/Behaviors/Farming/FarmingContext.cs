using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Schemas;

namespace ValheimVillages.Behaviors
{
    /// <summary>
    ///     Sub-states within the farming workflow.
    /// </summary>
    public enum FarmSubState
    {
        Idle,
        GatheringSeeds,
        TravelingToFarm,
        WalkingToPlantSpot, // Walking to a specific planting position
        Planting, // Waiting for cooldown then placing plant
        TravelingToHarvest,
        Harvesting,
        CollectingDrops,
        ReturningToChest,
        Depositing,
    }

    /// <summary>
    ///     Tracks the current farming session for a villager.
    /// </summary>
    public class FarmingContext
    {
        /// <summary>Items currently carried to the chest (deposited on arrival at container).</summary>
        public int CarriedHarvestCount;

        /// <summary>Current harvest target (walking to this Pickable).</summary>
        public Pickable CurrentHarvestTarget;

        /// <summary>
        ///     The resolved standing spot beside <see cref="CurrentHarvestTarget" />, found when
        ///     the crop was chosen. A Pickable sits inside its own collider, so re-resolving an
        ///     approach at the crop's own position fails — the same trap the beehive path hit.
        ///     Resolve once at selection, then walk it with <c>snapToApproach: false</c>.
        /// </summary>
        public Vector3 HarvestApproach;

        /// <summary>Index into IngredientSources for multi-chest gathering.</summary>
        public int CurrentIngredientIndex;

        /// <summary>Position of the farm area for planting.</summary>
        public Vector3 FarmPosition;

        /// <summary>Number of items harvested so far toward the work order quota.</summary>
        public int HarvestedCount;

        /// <summary>Where each ingredient (seed) comes from.</summary>
        public List<IngredientSource> IngredientSources;

        /// <summary>Whether we're in a harvesting pass (true) or planting pass (false).</summary>
        public bool IsHarvestingPass;

        /// <summary>Next position where a seed will be planted.</summary>
        public Vector3? NextPlantPosition;

        /// <summary>
        ///     Plant spots this session has proven it cannot stand at — either NavTo found no
        ///     approach, or the villager arrived at the snapped approach still too far from the
        ///     spot to plant. <see cref="PlantingHelper.FindPlantingPosition" /> scans a
        ///     deterministic spiral, so without excluding these it hands back the same cell
        ///     forever and "skipping to find another" skips nothing.
        /// </summary>
        public readonly List<Vector3> UnreachablePlantSpots = new();

        /// <summary>Time until the next planting iteration</summary>
        public float PlantCooldown;

        /// <summary>Grow radius from the Plant component (for spacing).</summary>
        public float PlantGrowRadius;

        /// <summary>The piece prefab to plant (e.g., sapling_carrot).</summary>
        public GameObject PlantPiecePrefab;

        /// <summary>Number of items planted this session (not yet harvestable).</summary>
        public int PlantedThisSession;

        /// <summary>The recipe for this crop (e.g., CarrotSeeds -> Carrot).</summary>
        public Recipe Recipe;

        /// <summary>Number of seeds gathered this cycle.</summary>
        public int SeedsGathered;

        /// <summary>Container where the work order and seeds are.</summary>
        public Container SourceContainer;

        /// <summary>Work order that triggered this farming session.</summary>
        public WorkOrderMatch WorkOrder;
    }
}