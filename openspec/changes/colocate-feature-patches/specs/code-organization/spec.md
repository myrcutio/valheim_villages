# code-organization - Spec Delta

## ADDED Requirements

### Requirement: Patch Co-location

Harmony patch files SHALL reside in the same directory as the feature code they modify, not in a centralized `Patches/` folder.

Cross-cutting patches (those with no single feature owner) SHALL remain in the top-level `Patches/` directory.

#### Scenario: Developer locates all code for a feature

GIVEN a developer is working on the Fragments feature
WHEN they open the `Items/Fragments/` directory
THEN they find all fragment-related Harmony patches alongside the fragment logic files

#### Scenario: Cross-cutting patches remain centralized

GIVEN a patch hooks a lifecycle event used by all items (e.g., ObjectDB registration)
WHEN a developer checks the `Patches/` directory
THEN only cross-cutting patches remain: `ItemPatch.cs`, `PrefabProtectionPatch.cs`, `LocalizationPatch.cs`, `DiagnosticPatch.cs`
