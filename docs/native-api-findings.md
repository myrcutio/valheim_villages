# Native Valheim API Findings (monodis)

Findings from inspecting `assembly_valheim.dll` for the duplicate/native API refactor. Use `monodis` (or equivalent) on the Valheim Managed assembly to re-verify after game updates.

## CookingStation

- **GetFreeSlot**, **GetSlot**, **GetItemConversion**, **RPC_RemoveDoneItem**: all `.method private hidebysig`. No public API for slot or conversion access.
- **m_slots**: field exists; visibility in assembly is per-class (CookingStation uses it internally).
- **m_conversion**: public list (already used by CookingRecipeDiscovery without reflection).
- **Conclusion**: Reflection in `CookingStationHelper` is required. Reflection is cached in static fields to avoid repeated `GetMethod`/`GetField` in hot paths.

## InventoryGui

- **m_selectedRecipe**: `.field private static` / instance field (private). No public property or method found that exposes the selected recipe.
- **Conclusion**: Reflection in `RecipeHelper` to read `m_selectedRecipe` remains appropriate.

## Shelter / position checks

- **Player.InShelter()**: instance method on `Player`; checks whether the player is in shelter (no `Vector3` overload).
- No public API found for “is this world position sheltered?” (e.g. `EnvMan` or `Location` with a position parameter that we could reuse).
- **Conclusion**: Custom `VillagerBehaviorLogic.CheckShelter(Vector3 position)` (raycast + Piece/static check) is kept; no native replacement for an arbitrary position.

## ContainerScanner.CountByPrefab

- Valheim’s **Inventory.CountItems** matches by `m_shared.m_name`; we need counting by `m_dropPrefab.name`.
- **Conclusion**: Keep custom `CountByPrefab`; no native API substitution.
