# Patches

Keywords: Harmony patch, ObjectDB, ZNetScene, ItemPatch, LocalizationPatch, PrefabProtectionPatch, DiagnosticPatch, item
registration, localization, token, template prefab, ZNetView, ItemDrop, prefab protection, vv_ prefix

## Purpose

Global Harmony patches that wire mod systems into Valheim's initialization lifecycle. Handles item/recipe registration
in ObjectDB and ZNetScene, localization token injection, and protection of template prefabs from unintended
initialization.

## Directory Structure

```
Patches/
  ItemPatch.cs                         -- Postfix on ObjectDB.Awake/CopyOtherDB and ZNetScene.Awake
  LocalizationPatch.cs                 -- Postfix on Localization.SetupLanguage; registers vv_* tokens
  PrefabProtectionPatch.cs             -- Prefix on ItemDrop.Awake/OnDestroy and ZNetView.Awake for templates
  DiagnosticPatch.cs                   -- Postfix on ItemDrop.Awake for debug logging
```

## Key Types

| Type                                       | Role                                                                                             |
|--------------------------------------------|--------------------------------------------------------------------------------------------------|
| `ObjectDBAwakePatch` / `ObjectDBCopyPatch` | Calls `ItemFactory.RegisterAll`, `VirtualRecipeLoader.RegisterAll`                               |
| `ZNetSceneAwakePatch`                      | Calls `ItemFactory.RegisterAllInZNetScene`, `VirtualRecipeLoader.RegisterCookingRecipesIfNeeded` |
| `LocalizationPatch`                        | Adds localization tokens: `vv_farmer`, `vv_tavernkeeper`, `vv_villager`, etc.                    |
| `ItemDropAwakeProtectionPatch`             | Skips `ItemDrop.Awake` for template prefabs (`vv_*` without `(Clone)`)                           |
| `ZNetViewAwakeProtectionPatch`             | Skips `ZNetView.Awake` for template prefabs                                                      |

## Entry Points and Registration

- All patches applied via `Harmony.PatchAll()` in `Plugin.Awake`.
- `LocalizationPatch.RegisterTokens()` also called explicitly in `Plugin.Awake`.

## Integration

- **Items/** -- `ItemPatch` triggers `ItemFactory.RegisterAll` and `VirtualRecipeLoader.RegisterAll`.
- **Villager/** -- `VillagerPawnPatch.LogAvailableDvergrPrefabs` (in `Villager/SpawnPatch.cs`) called from
  `ZNetSceneAwakePatch`.
- **Attributes/** -- `AttributeScanner.InvokeObjectDBRegistrations()` called during hot reload alongside these patches.
