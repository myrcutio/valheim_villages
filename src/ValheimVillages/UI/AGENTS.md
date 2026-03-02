# UI

Keywords: UI, user interface, tab, info tab, debug tab, InfoTab, DebugTab, VillagerTabManager, VillagerTabRenderer, VillagerUIFactory, IVillagerTab, IListPanel, IContextMenu, RegisterTab, RegisterListPanel, RegisterContextMenu, VillagerInteract, VillagerBehaviorBridge, interaction, hover text, crafting UI, InventoryGui, work order menu, WorkOrderMenu, WorkOrderMenuBuilder, context menu, list panel, patrol status, patrol map, PatrolStatusPanel, PatrolMapRenderer, VillageMapPanel, DialogPatches, VillagerCraftingPatch, TMPro, crafting panel, tab switching

## Purpose

Villager interaction UI built on top of Valheim's `InventoryGui`. Provides a tabbed interface (Info, Debug, and station-specific Orders/Upgrade tabs) with list panels, context menus, and map renderers. Reuses native Valheim UI elements via cloning and modification.

## Directory Structure

```
UI/
  Core/
    IContextMenu.cs                    -- IContextMenuUI interface (CanShow, Show with VillagerBehaviorBridge)
    IListPanel.cs                      -- IListPanelUI interface (GetListItems, GetDetail)
    IVillagerTab.cs                    -- IVillagerTabUI interface (OnSelected, OnUpdate, GetListItems, GetDetail)
    VillagerTabManager.cs              -- Manages tab system: clones native buttons, registers tabs, populates lists
    VillagerTabRenderer.cs             -- Partial: renders list items, description, map texture, action button
    VillagerUIFactory.cs               -- Builds Valheim-style UI elements (labels, buttons, sliders, popups)
    VillagerUIFactory.Controls.cs      -- Partial: slider and input field controls
  Interaction/
    VillagerInteract.cs                -- Implements Hoverable + Interactable; opens crafting UI on E key
    VillagerBehaviorBridge.cs          -- MonoBehaviour bridge from NPC GameObject to VillagerAI
  Tabs/
    InfoTab.cs                         -- [RegisterTab("info", Order=0)] shows locations, abilities, patrol status
    DebugTab.cs                        -- [RegisterTab("debug", Order=1)] shows state, tasks, movement tests
  Panels/
    PatrolStatusPanel.cs               -- [RegisterListPanel("patrolstatus", "info")] patrol status, breach alert
    PatrolMapRenderer.cs               -- Renders 256x256 top-down patrol map texture
    VillageMapPanel.cs                 -- [RegisterListPanel("villagemap", "debug")] patrol map and "Remap" action
  Patches/
    DialogPatches.cs                   -- Patches Character.GetHoverText, Tameable.Interact, InventoryGui.Hide, NpcTalk
    VillagerCraftingPatch.cs           -- Hides crafting panel for non-crafters via CanvasGroup.alpha
  ContextMenus/
    WorkOrderMenu.cs                   -- [RegisterModObject] popup for work order min/max quota configuration
    WorkOrderMenuBuilder.cs            -- Builds WorkOrderMenu hierarchy via VillagerUIFactory
```

## Key Types

| Type | Role |
|------|------|
| `VillagerTabManager` | Central tab controller: registers tabs, switches content, manages lifecycle |
| `VillagerTabRenderer` | Renders list items, detail panels, map textures, and action buttons |
| `VillagerUIFactory` | Factory for Valheim-style UI elements (uses TMPro via reflection) |
| `VillagerInteract` | Hoverable/Interactable on NPC; pauses AI during interaction |
| `VillagerBehaviorBridge` | MonoBehaviour exposing `VillagerAI` state to UI components |
| `InfoTab` | Shows favorite places (bed, fire, chair) and registered list panels |
| `DebugTab` | Shows AI state, recent activity log, movement test commands |
| `WorkOrderMenu` | Popup for configuring work order quotas (min/max sliders) |

## Entry Points and Registration

- `[RegisterTab]` discovered by `AttributeScanner` -> `VillagerTabManager.RegisterTab()`.
- `[RegisterListPanel]` discovered by `AttributeScanner` -> wired to parent tab (e.g., `InfoTab`).
- `VillagerInteract.Interact()` (E key) pauses NPC, sets crafting station, calls `VillagerTabManager.Activate()`.
- `DialogPatches` applied via `Harmony.PatchAll()`: patches `Character.GetHoverText`, `Tameable.Interact`, `InventoryGui.Hide`, `NpcTalk.RandomTalk`.
- `VillagerCraftingPatch` applied via `Harmony.PatchAll()`: hides crafting panel for non-crafters.

## Integration

- **Villager/** -- `VillagerBehaviorBridge` resolves AI via `VillagerAIManager.ActiveVillagers`; `VillagerStation.HasCraftingRecipes` checks the `tab:workorder` tag to determine if Orders tab appears.
- **Behaviors/** -- `PatrolStatusPanel` reads `PerimeterPatrolBehavior` state for patrol/breach info.
- **TaskQueue/** -- `DebugTab` displays `VillagerActivityLog` entries.
- **Items/** -- `WorkOrderMenu` reads/writes `ItemDrop.ItemData.m_customData`.
