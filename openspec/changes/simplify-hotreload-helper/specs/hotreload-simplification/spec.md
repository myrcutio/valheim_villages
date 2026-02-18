# hotreload-simplification - Spec Delta

## MODIFIED Requirements

### Requirement: Hot-Reload Cleanup Mechanism

The hot-reload cleanup system SHALL use `AttributeScanner.InvokeAllCleanup()` to discover and invoke all `[RegisterCleanup]`-annotated methods, instead of maintaining a hardcoded list of cleanup calls.

The mod object cleanup system SHALL use `AttributeScanner.GetModObjectNames()` to discover registered mod objects, instead of maintaining a hardcoded `ModGameObjectNames` array.

#### Scenario: New module cleanup is automatic

GIVEN a new module adds a static `Clear()` method annotated with `[RegisterCleanup]`
WHEN hot-reload occurs
THEN `HotReloadHelper.ResetAllStaticState()` invokes the new cleanup method without any modification to HotReloadHelper
