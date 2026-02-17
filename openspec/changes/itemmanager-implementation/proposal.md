# Proposal: Implement ItemManager-Style Registration

## Summary

Implement item and prefab registration using Harmony patches modeled after Azumatt's [ItemManagerModTemplate](https://github.com/AzumattDev/ItemManagerModTemplate). This approach provides reliable integration with Valheim's ObjectDB and ZNetScene systems while maintaining compatibility with other mods.

## Motivation

The current codebase has stripped-down item registration (only `ItemFactory.GetDefinitions()` remains). Azumatt's ItemManager is a well-tested implementation used by many Valheim mods that:

1. Uses `[HarmonyPriority(Priority.VeryLow)]` to ensure patches run after other mods
2. Patches the correct lifecycle methods (`ObjectDB.Awake`, `ObjectDB.CopyOtherDB`, `ZNetScene.Awake`)
3. Properly registers prefabs with both ObjectDB's item list and hash map
4. Properly registers prefabs with ZNetScene's prefab list and named prefabs dictionary

## Approach

Retain the existing JSON-based item definition pattern (`ItemFactory.GetDefinitions()`) while implementing Azumatt's registration patterns:

1. **ObjectDB Registration**: Clone base prefabs, configure item properties from JSON, register with ObjectDB
2. **ZNetScene Registration**: Ensure prefabs are registered with ZNetScene for network spawning
3. **Harmony Priority**: Use `Priority.VeryLow` on all patches for mod compatibility

## Scope

- Modify `ObjectDBPatch.cs` to implement registration patches
- Modify `ItemFactory.cs` to add registration methods
- No new files or classes required

## Out of Scope

- Asset bundle loading (items use clone-based approach)
- Item caching/recreation (fail-fast approach per user preference)
- Crafting recipe configuration (future change)

## Reference

- [Azumatt's ItemManagerModTemplate](https://github.com/AzumattDev/ItemManagerModTemplate)
- [Jötunn modding framework tutorials](https://valheim-modding.github.io/Jotunn/tutorials/overview.html)

