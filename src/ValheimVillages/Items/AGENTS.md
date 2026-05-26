# Items

Keywords: item, pawn, fragment, work order, ItemFactory, ItemDefinition, prefab, ObjectDB, ZNetScene, virtual recipe,
VirtualRecipeLoader, VirtualRecipeParser, crafting station, CraftingStationPatch, WorkOrderUsePatch, FragmentCombiner,
RescueQuestTracker, rescue quest, biome fragment, cultivator, cooking recipe, CultivatorRecipeDiscovery,
CookingRecipeDiscovery, work order icon, WorkOrderIconCompositor, WorkOrderStatusOverlay, PlantPieceRegistry, embedded
resource, JSON definition

## Purpose

Item registration and management for three item types: pawns (summon villagers), biome fragments (combine into rescue
quests), and work orders (assign crafting tasks to villagers). Also handles virtual recipe discovery for stations with a
`stationName`.

## Directory Structure

```
Items/
  ItemFactory.cs                       -- Loads JSON defs + generates pawn/work-order items from VillagerRegistry
  ItemDefinition.cs                    -- JSON-serializable item data (name, itemType, biome, stationType)
  Definitions/
    Pawns/pawn.json                    -- Generic (random-type) pawn; per-type pawns generated from VillagerRegistry
    Fragments/*.json                   -- Biome fragment definitions (fragment_meadows, fragment_swamp, etc.)
    WorkOrders/*.json                  -- Physical station work orders; virtual station work orders generated from VillagerRegistry
    VirtualRecipes/*.json              -- Recipe definitions (farmer_recipes, tavernkeeper_recipes)
  VirtualRecipes/
    VirtualRecipeLoader.cs             -- Registers virtual recipes in ObjectDB; cultivator/cooking discovery
    VirtualRecipeParser.cs             -- Parses JSON recipe definitions; creates station templates
    VirtualRecipeDefinition.cs         -- Data class for virtual recipe JSON
    CultivatorRecipeDiscovery.cs       -- Discovers plantable pieces from cultivator for Farmer recipes
    CookingRecipeDiscovery.cs          -- Discovers cooking recipes from ZNetScene for Farmer recipes
    PlantPieceRegistry.cs              -- Registry of plantable pieces
  WorkOrders/
    CraftingStationPatch.cs            -- Harmony patch on InventoryGui.UpdateCraftingPanel; adds "Order" button
    WorkOrderUsePatch.cs               -- Harmony patch on Humanoid.UseItem; opens WorkOrderMenu
    RecipeHelper.cs                    -- Recipe lookup utilities
    WorkOrderTutorial.cs               -- Tutorial popup for first work order use
  Fragments/
    FragmentCombiner.cs                -- Combines 3 same-biome fragments into a rescue quest
    RescueQuestTracker.cs              -- Tracks rescue quests; spawns captive pawn at quest location
    FragmentUsePatch.cs                -- Harmony patch for fragment item use
    FragmentLootPatch.cs               -- Adds fragment drops to dungeon loot tables
    RescueQuestProximityPatch.cs       -- Triggers quest events on player proximity
  Icons/
    WorkOrderIconLoader.cs             -- Loads embedded PNG icons for work order items
    WorkOrderIconPatch.cs              -- Harmony patch to apply composited icons
    WorkOrderIconCompositor.cs         -- Composites work order icons with status overlays
    WorkOrderStatusResolver.cs         -- Resolves current work order status for overlay
    WorkOrderStatusOverlay.cs          -- Renders status overlay on work order icons
    WorkOrderStatus.cs                 -- Enum for work order statuses
```

## Key Types

| Type                      | Role                                                                                                           |
|---------------------------|----------------------------------------------------------------------------------------------------------------|
| `ItemFactory`             | Loads JSON items + generates per-type pawns/work-orders from VillagerRegistry; registers in ObjectDB/ZNetScene |
| `VirtualRecipeLoader`     | Registers virtual recipes; runs cultivator/cooking recipe discovery                                            |
| `CraftingStationPatch`    | Adds "Order" button to crafting UI for stations that have work orders                                          |
| `FragmentCombiner`        | Combines 3 same-biome fragments into a rescue quest                                                            |
| `RescueQuestTracker`      | Manages active rescue quests; spawns captive NPC on arrival                                                    |
| `WorkOrderIconCompositor` | Composites work order icons with real-time status overlays                                                     |

## Entry Points and Registration

- `ItemFactory.RegisterAll(ObjectDB)` called from `Patches/ItemPatch.cs` (ObjectDB.Awake/CopyOtherDB) and hot reload.
- `ItemFactory.GenerateRegistryItems()` creates pawn items (`vv_{type}_pawn`) and virtual station work orders
  (`vv_workorder_{type}`) from `VillagerRegistry.Definitions`.
- `ItemFactory.RegisterAllInZNetScene(ZNetScene)` called from `Patches/ItemPatch.cs` (ZNetScene.Awake).
- `VirtualRecipeLoader.RegisterAll(ObjectDB)` called from ObjectDB patches.
- Harmony patches (`CraftingStationPatch`, `WorkOrderUsePatch`, `FragmentUsePatch`, `FragmentLootPatch`) applied via
  `Harmony.PatchAll()`.
- JSON definitions are embedded resources under `ValheimVillages.Items.Definitions.*`; per-type items are generated at
  runtime.

## Integration

- **Villager/** -- Pawns spawn villagers via `Villager/SpawnPatch` (VillagerPawnPatch); work orders drive **Behaviors/**
  `CraftingBehavior`/`FarmingBehavior`.
- **TaskQueue/** -- `RecipeDiscoveryRefreshHandler` calls `VirtualRecipeLoader.RecheckDiscoveredRecipes()`.
- **UI/** -- `WorkOrderMenu` configures work order min/max quotas.
- **Patches/** -- `ItemPatch` triggers `ItemFactory.RegisterAll` and `VirtualRecipeLoader.RegisterAll`.
