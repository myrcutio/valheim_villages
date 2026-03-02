# Abilities

Keywords: ability, passive effect, Mountain Stride, spawn block, spawn protection, status effect, SE_MountainStride, VillagerAbilityManager, IAbility, IPlayerAbility, IPassiveEffect, RegisterAbility, RegisterPassive, sliding immunity, cooldown, village safety

## Purpose

Player abilities and passive village effects. Abilities are teachable skills (e.g., Mountain Stride) activated by the player. Passive effects (e.g., spawn block) apply automatically based on village area.

## Directory Structure

```
Abilities/
  IAbility.cs                          -- IPlayerAbility interface (extends IAbility with Learn/Activate)
  IPassiveEffect.cs                    -- PassiveEffectExtensions (Unity Vector3 adapter for IPassiveEffect)
  VillagerAbilityManager.cs            -- Manages Mountain Stride keybind (R), cooldown, persistence via Player.HaveUniqueKey
  SE_MountainStride.cs                 -- StatusEffect: 5 min duration, 20 min cooldown, sliding immunity
  MountainStride/
    MountainStrideAbility.cs           -- [RegisterAbility("mountainstride")] implementation
    MountaineerPatches.cs              -- Harmony patches: ObjectDB.Awake, Player.Update, Character.ApplySlide
  SpawnBlock/
    SpawnBlockPassiveEffect.cs         -- [RegisterPassive("spawnblock")] checks VillageAreaManager.IsInsideAnyVillage
    SpawnBlockPassive.cs               -- Harmony patch on SpawnSystem.UpdateSpawnList to block spawns in villages
```

## Key Types

| Type | Role |
|------|------|
| `VillagerAbilityManager` | Update loop for Mountain Stride keybind, cooldown tracking, status effect application |
| `SE_MountainStride` | Unity StatusEffect subclass with sliding immunity |
| `MountainStrideAbility` | Registered ability that teaches Mountain Stride to players |
| `SpawnBlockPassiveEffect` | Passive that suppresses enemy spawns inside village polygons |
| `SpawnBlockPassive` | Harmony patch on `SpawnSystem.UpdateSpawnList` |

## Entry Points and Registration

- `[RegisterAbility("mountainstride")]` and `[RegisterPassive("spawnblock")]` discovered by `AttributeScanner.ScanAndRegister()` in `Plugin.Awake`.
- `MountaineerPatches` applied via `Harmony.PatchAll()` -- patches `ObjectDB.Awake`, `ObjectDB.CopyOtherDB`, `Player.Update`, `Character.ApplySlide`.
- `SpawnBlockPassive` applied via `Harmony.PatchAll()` -- patches `SpawnSystem.UpdateSpawnList`.

## Integration

- **Villages/** -- `SpawnBlockPassiveEffect.IsActive` calls `VillageAreaManager.IsInsideAnyVillage`.
- **UI/** -- InfoTab panels display abilities/passives via `AttributeScanner.GetAbility()` / `GetPassive()`.
- **Villager/** -- Villager type definitions (Villager/Registry/Definitions/*.json) list passive/ability tags in `benefits` arrays.
