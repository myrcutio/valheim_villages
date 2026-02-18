# Tasks: add-attribute-registration

## Implementation Checklist

*Create attribute infrastructure first, then annotate existing code incrementally. Each sub-step is independently buildable. Verify with `dotnet build` after each sub-step.*

### Sub-step 4a: Attribute Scanner Infrastructure

- [x] Create `Core/Attributes/AttributeScanner.cs` (~100 lines)
  - `ScanAndRegister(Assembly)` -- calls all Register* methods
  - `InvokeObjectDBRegistrations(Assembly, ObjectDB)` -- called from ObjectDB.Awake patch
  - `InvokeAllCleanup(Assembly)` -- called during hot-reload
  - `GetModObjectNames(Assembly)` -- returns registered mod object names
  - `GetRegisteredTabs()` -- returns discovered `IVillagerTab` instances
  - `GetListPanels(string parentTab)` -- returns discovered `IListPanel` instances
  - `GetContextMenus()` -- returns discovered `IContextMenu` instances
  - `GetAbility(string id)` -- returns `IAbility` by id
  - `CreateBehavior(string tag, VillagerAI owner)` -- creates `IBehavior` by tag
- [x] Wire `AttributeScanner.ScanAndRegister()` into `Plugin.Awake()` after Harmony patches
- [x] Run `dotnet build`

### Sub-step 4b: [DevCommand] -- Console Commands

- [x] Create `Core/Attributes/DevCommandAttribute.cs` (~15 lines)
  - `Description` property, `Name` property (null = auto-derive from `Class.Method`)
  - Applied to static methods: `static void Method()` or `static void Method(Terminal.ConsoleEventArgs)`
- [x] Implement `RegisterDevCommands(Assembly)` in AttributeScanner
  - Auto-derive name: `DeclaringType.Name` + `MethodName` lowercase with space separator
- [x] Annotate `HnaDebugVisualization.ToggleMarkers()` with `[DevCommand("Toggle HNA debug markers in-game")]`
- [x] Annotate `HnaBoundaryDump.Dump()` with `[DevCommand("Dump HNA boundary cells to JSON")]`
- [x] Annotate all console commands in `VillagerAbilityManager.cs` with `[DevCommand]`
- [x] Remove 11 manual `new Terminal.ConsoleCommand()` calls
- [x] Run `dotnet build`

### Sub-step 4c: [DebugRow] -- Debug Panel Rows

*Deferred: DebugTab works well with its current command-based pattern. No other sub-step depends on this. Can be revisited if debug row composition becomes needed.*

### Sub-step 4d: [InfoRow] -- Info Panel Rows

*Deferred: InfoTab works well with its current panel-driven pattern via IListPanel. No other sub-step depends on this. Can be revisited if info row composition becomes needed.*

### Sub-step 4e: [RegisterObjectDB] -- ObjectDB Registration

- [x] Create `Core/Attributes/RegisterObjectDBAttribute.cs` (~5 lines)
  - Applied to static methods: `static void Method(ObjectDB db)`
- [x] Implement `InvokeObjectDBRegistrations(Assembly, ObjectDB)` in AttributeScanner
- [x] Annotate `SE_MountainStride.Register(ObjectDB)` with `[RegisterObjectDB]`
- [x] Replace manual ObjectDB registration in `Plugin.cs` (line 84) with scanner invocation
- [x] Run `dotnet build`

### Sub-step 4f: [RegisterTaskHandler] -- Task Handlers

- [x] Create `Core/Attributes/RegisterTaskHandlerAttribute.cs` (~5 lines)
  - Applied to `ITaskHandler` classes
- [x] Implement `RegisterTaskHandlers(Assembly)` in AttributeScanner (instantiate + register)
- [x] Annotate all `ITaskHandler` implementations in `TaskQueue/Handlers/` with `[RegisterTaskHandler]`
- [x] Remove 8 manual `Register(new XHandler())` calls from `Plugin.Awake()`
- [x] Run `dotnet build`

### Sub-step 4g: [RegisterCleanup] -- Hot-Reload Cleanup

- [x] Create `Core/Attributes/RegisterCleanupAttribute.cs` (~5 lines)
  - Applied to static void methods
- [x] Implement `InvokeAllCleanup(Assembly)` in AttributeScanner
- [x] Annotate `VillagerAIManager.Clear()`, `GlobalTaskQueue.Clear()`, and other static state cleanup methods with `[RegisterCleanup]`
- [x] Run `dotnet build` (Phase 5 will wire into HotReloadHelper)

### Sub-step 4h: [RegisterModObject] -- Mod Singleton GameObjects

- [x] Create `Core/Attributes/RegisterModObjectAttribute.cs` (~8 lines) -- `GameObjectName`
  - Applied to MonoBehaviour classes
- [x] Implement `GetModObjectNames(Assembly)` in AttributeScanner
- [x] Annotate `WorkOrderMenu` and `VillagerTabManager` with `[RegisterModObject("...")]`
- [x] Run `dotnet build` (Phase 5 will wire into HotReloadHelper)

### Sub-step 4i: [RegisterAbility] -- Teachable Abilities

- [x] Create `Abilities/IAbility.cs` (~15 lines) -- `Id`, `DisplayName`, `Description`, `HasLearned(Player)`, `Learn(Player)`, `Activate(Player)`
- [x] Create `Core/Attributes/RegisterAbilityAttribute.cs` (~10 lines) -- `Id`
- [x] Implement ability registry in AttributeScanner: `Dictionary<string, IAbility>`
- [x] Create `Abilities/MountainStride/MountainStrideAbility.cs` (~40 lines) implementing `IAbility`, annotated with `[RegisterAbility("mountainstride")]`
- [x] Remove manual `RegisterStatusEffect` from `Plugin.cs` line 84 (now handled by `[RegisterObjectDB]` on `SE_MountainStride`)
- [x] Run `dotnet build`

### Sub-step 4j: [RegisterPassive] -- Passive Village Effects

- [x] Create `Abilities/IPassiveEffect.cs` (~10 lines) -- `Id`, `DisplayName`, `IsActive(Vector3)`
- [x] Create `Core/Attributes/RegisterPassiveAttribute.cs` (~10 lines) -- `Id`
- [x] Implement passive registry in AttributeScanner: `Dictionary<string, IPassiveEffect>`
- [x] Create `Abilities/SpawnBlock/SpawnBlockPassiveEffect.cs` implementing `IPassiveEffect`, annotated with `[RegisterPassive("spawnblock")]`
- [x] Run `dotnet build`

### Sub-step 4k: UI Registration Attributes

- [x] Create `Core/Attributes/RegisterTabAttribute.cs` (~10 lines) -- `Id`, `Order`
- [x] Create `Core/Attributes/RegisterListPanelAttribute.cs` (~10 lines) -- `Id`, `ParentTab`
- [x] Create `Core/Attributes/RegisterContextMenuAttribute.cs` (~8 lines) -- `Id`
- [x] Create `Core/Attributes/RegisterBehaviorAttribute.cs` (~8 lines) -- `Tag`
- [x] Implement tab/panel/menu/behavior discovery in AttributeScanner
- [x] Annotate `InfoTab` with `[RegisterTab("info", Order = 0)]`
- [x] Annotate `DebugTab` with `[RegisterTab("debug", Order = 1)]`
- [x] Annotate `GuardStatusPanel` with `[RegisterListPanel("guardstatus", "info")]`
- [x] Annotate all 5 IBehavior implementations with `[RegisterBehavior]`
- [x] Remove manual tab/panel registration from `Plugin.Awake()`
- [x] Run `dotnet build`

### Sub-step 4l: Shared Helpers

- [x] Create `Core/Helpers/UIHelpers.cs` (~40 lines)
  - `ShowHudMessage(string text)` -- wraps `Player.m_localPlayer?.Message(...)`
  - `ShowHudNotification(string text)` -- wraps top-left messages
  - `PrintConsole(string text)` -- wraps `Console.instance.Print(...)`
  - `CloseInventoryUI()` -- wraps `InventoryGui.instance?.Hide()`
- [x] Run `dotnet build`

### Sub-step 4m: [ModTest] Attribute (for Phase 6 integration tests)

- [x] Create `Core/Attributes/ModTestAttribute.cs` (~10 lines) -- `Name`, `Order`
  - Applied to static methods: `static void Method()` or `static void Method(TestContext ctx)`
- [x] Run `dotnet build`

## Dependencies

- Phase 3 must be complete first (IBehavior, IListPanel, IContextMenu interfaces exist)
- Sub-step 4a (scanner) should be first
- Sub-steps 4b-4m can be done in any order after 4a
- Phase 5 depends on 4g ([RegisterCleanup]) and 4h ([RegisterModObject])

## Notes

- All paths are relative to `src/ValheimVillages/`
- Total new infrastructure: ~450 lines across ~18 files in `Core/Attributes/` and `Core/Helpers/`
- Replaces ~500+ lines of manual registration scattered across `Plugin.cs`, `HotReloadHelper.cs`, `DebugTab.cs`, `InfoTab.cs`, `VillagerAI.cs`, and `VillagerAbilityManager.cs`
- Can be implemented one attribute type at a time -- each sub-step is independently compilable
- Sub-steps 4c and 4d deferred as they would refactor working UI without clear benefit at this stage
