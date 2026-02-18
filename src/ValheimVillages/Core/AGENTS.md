# Core

Keywords: AttributeScanner, registration, ScanAndRegister, DevCommand, RegisterTaskHandler, RegisterAbility, RegisterPassive, RegisterTab, RegisterListPanel, RegisterContextMenu, RegisterBehavior, RegisterObjectDB, RegisterCleanup, RegisterModObject, hot reload, integration test, ModTestRunner, ModAssert, ModTest, SceneSnapshot, UIHelpers, console command, terminal command

## Purpose

Infrastructure for attribute-based registration of mod components and in-game integration testing. `AttributeScanner` is the central wiring mechanism that discovers annotated types at startup and connects them to the appropriate registries.

## Directory Structure

```
Core/
  Attributes/
    AttributeScanner.cs                -- Scans assembly for registration attributes; wires up all subsystems
  Helpers/
    UIHelpers.cs                       -- ShowHudMessage, ShowHudNotification, PrintConsole, CloseInventoryUI
  Testing/
    ModTestRunner.cs                   -- Discovers [ModTest] methods, runs them, writes JSON results
    ModAssert.cs                       -- Assertion helpers: True, Equal, NotNull, with soft-assert collection
    ModAssertException.cs              -- Exception type thrown on assertion failure
    SceneSnapshot.cs                   -- Captures villager positions, states, village areas for regression tests
```

## Key Types

| Type | Role |
|------|------|
| `AttributeScanner` | Scans assembly for all `[Register*]` and `[DevCommand]` attributes; calls registries |
| `ModTestRunner` | Discovers and runs `[ModTest]` methods after hot reload; writes JSON results |
| `ModAssert` | Test assertions (True, Equal, NotNull, etc.) with soft-assert mode |
| `SceneSnapshot` | Captures runtime state (villager positions, village areas) for regression testing |
| `UIHelpers` | Static helpers for HUD messages, console output, and UI management |

## Entry Points and Registration

- `Plugin.Awake` calls `AttributeScanner.ScanAndRegister(typeof(Plugin).Assembly)` which discovers:
  - `[DevCommand]` -> `Terminal.ConsoleCommand`
  - `[RegisterTaskHandler]` -> `TaskHandlerRegistry`
  - `[RegisterAbility]` / `[RegisterPassive]` -> internal ability/passive registries
  - `[RegisterTab]` -> `VillagerTabManager`
  - `[RegisterListPanel]` -> parent tab (e.g., InfoTab)
  - `[RegisterContextMenu]` -> context menu list
  - `[RegisterBehavior]` -> behavior factory
  - `[RegisterObjectDB]` -> ObjectDB registration callbacks
  - `[RegisterCleanup]` -> hot-reload cleanup callbacks
  - `[RegisterModObject]` -> mod object name collection
- `InvokeAllCleanup()` called during hot reload and world unload.
- `InvokeObjectDBRegistrations()` called from ObjectDB patches and hot reload.
- `ModTestRunner.RunAll()` runs after hot-reload cleanup when `AutoRunEnabled` is true.

## Integration

- Every other module depends on Core for registration attributes.
- **Plugin.cs** -- calls `ScanAndRegister`, `InvokeAllCleanup`, `InvokeObjectDBRegistrations`.
- **ValheimVillages.Core** (shared library) -- defines the attribute classes that `AttributeScanner` looks for.
