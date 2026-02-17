# AI Agent Instructions for Valheim Mod Development

## Core Principles

When working on this Valheim total conversion mod, you MUST adhere to these principles:

### 1. Modularity and Atomic Features

- **Each feature must be atomic**: A feature should represent a single, discrete capability that can be independently evaluated, tested, and understood.
- **No broad scope changes**: Break down any request into smaller, focused tasks. If a change affects multiple systems, split it into separate atomic features.
- **Self-contained capabilities**: Features should be composable but not interdependent. Each spec should describe one complete capability.

### 2. Code Modification Over Addition

- **Find and fix existing code**: Before adding new code, search for existing functions, classes, or systems that can be modified to meet the requirement.
- **Modify existing functions**: When implementing changes, modify existing functions rather than creating new ones that duplicate or wrap existing functionality.
- **Avoid code bloat**: Do not add layers of abstraction, wrappers, or helper functions unless absolutely necessary. Prefer direct modification of existing code.
- **Refactor, don't extend**: If existing code is close to what's needed, refactor it to be correct rather than adding parallel code paths.
- **Reuse existing assets**: For UI elements and GameObjects, inherit from, extend, or clone existing game assets rather than creating new ones. This maintains visual consistency and reduces design work.

### 3. Discrete Scope

- **Narrow focus**: Each change proposal should address a single, well-defined problem or feature.
- **Clear boundaries**: The scope of a change should be obvious from its proposal. If it's unclear, the scope is too broad.
- **Incremental progress**: Build features incrementally. Each change should be reviewable and implementable independently.

### 4. Correctness Loop

- **Fix existing code**: When bugs or issues are found, fix the root cause in existing code rather than adding workarounds or patches.
- **Make code correct**: The goal is to make existing functions work correctly, not to add new functions that work around incorrect ones.
- **Iterative refinement**: Modify code iteratively to improve correctness. Each iteration should make the code more correct, not add more code.

## Valheim Modding Context

### Technology Stack

- **Language**: C# (.NET Framework)
- **Framework**: BepInEx (BepInEx 5.x)
- **Patching**: Harmony (for runtime patching of game code)
- **Game Engine**: Unity (Valheim is built on Unity)

### Development Environment

- **Platforms**: Development occurs on macOS or Linux environments
- **Mono Runtime**: Mono is required to build and run assemblies on non-Windows platforms
- **Cross-Platform Compatibility**: Ensure code and build processes are compatible with Mono runtime

### Common Patterns

- **Harmony Patches**: Use `[HarmonyPatch]` attributes to patch existing game methods
- **Plugin Structure**: BepInEx plugins are classes that inherit from `BaseUnityPlugin`
- **Configuration**: Use `ConfigEntry` for mod configuration
- **Game References**: Access game objects through Unity's component system

### Modding Best Practices

- **Minimal patches**: Patch only what's necessary. Avoid broad patches that affect unrelated systems.
- **Preserve game behavior**: When modifying game systems, ensure changes are compatible with existing game mechanics.
- **Performance conscious**: Valheim mods run in-game. Keep patches efficient and avoid unnecessary allocations.
- **Leverage existing assets**: Inherit, extend, or clone existing in-game UI elements and GameObjects rather than recreating them from scratch. This ensures mod elements appear native to the game, maintains visual consistency, and saves UI design effort. Prefer modifying existing Unity prefabs and components over creating new ones.

## Working with Specs

### Reading Specs

- **Always read relevant specs first**: Before implementing a change, read all related specs in `openspec/specs/` to understand current system state.
- **Understand requirements**: Specs use "SHALL" statements to define requirements. These are mandatory, not suggestions.
- **Check scenarios**: Scenarios define expected behavior. Your implementation must satisfy all scenarios.

### Creating Change Proposals

When proposing a change:

1. **Identify existing code**: Before proposing new code, identify existing functions, classes, or systems that should be modified.
2. **Create spec deltas**: Show how requirements will change using the spec delta format.
3. **Break into tasks**: Each task should be atomic and focused on modifying specific existing code.
4. **Avoid new code**: Tasks should primarily involve modifying existing code, not adding new files or classes.

### Implementation Guidelines

- **Start with existing code**: When implementing, first locate and read the existing code that needs modification.
- **Modify in place**: Make changes directly to existing functions rather than creating new ones.
- **Test incrementally**: After each atomic change, verify the modification works correctly.
- **Update specs**: After implementation, update specs to reflect the new system state.

## Anti-Patterns to Avoid

1. **Adding wrapper functions**: Don't create new functions that call existing ones with slight modifications. Modify the existing function instead.
2. **Creating parallel systems**: Don't build new systems alongside existing ones. Modify existing systems to meet requirements.
3. **Broad patches**: Don't create Harmony patches that affect large swaths of code. Patch specific, targeted methods.
4. **Feature creep**: Don't expand scope during implementation. Stick to the atomic feature defined in the spec.
5. **Code accumulation**: Don't leave old code paths when refactoring. Remove or update them.
6. **Recreating UI/GameObjects**: Don't create new UI elements or GameObjects from scratch when similar ones exist in-game. Inherit, extend, or clone existing assets instead.

## Review Criteria

Before considering a change complete:

- [ ] All existing code that should be modified has been identified
- [ ] Changes modify existing functions rather than adding new ones
- [ ] The feature is atomic and independently evaluable
- [ ] Specs have been updated to reflect the new system state
- [ ] No unnecessary code has been added
- [ ] The change scope is narrow and well-defined
- [ ] UI elements and GameObjects leverage existing game assets (inherit/extend/clone) rather than being recreated from scratch
