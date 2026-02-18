# testability - Spec Delta

## ADDED Requirements

### Requirement: Core Assembly Independence

All interfaces, attributes, enums, data types, tag system utilities, and pure computation algorithms SHALL reside in `ValheimVillages.Core` (netstandard2.0) with zero Unity or Valheim dependencies.

#### Scenario: Unit tests reference Core without Unity

GIVEN `ValheimVillages.Tests` (xUnit, net8.0) references `ValheimVillages.Core`
WHEN `dotnet test` is run on a machine without Valheim or Unity installed
THEN all unit tests compile and execute successfully

### Requirement: JSON Test Results for Agent Parsing

The in-game test runner SHALL write results to `BepInEx/config/vv_test_results.json` using JSON schema v1, including source locations (file + line), stack traces with file:line info, expected/actual values on assertions, and BepInEx log line ranges.

#### Scenario: Agent reads test results after hot-reload

GIVEN `ModTestRunner.RunAll()` executes after hot-reload
WHEN the agent reads `vv_test_results.json`
THEN it finds structured JSON with `result` ("PASS"/"FAIL"), `summary`, `failures[]` with `location.file`, `location.line`, `assertions[].expected`, `assertions[].actual`, `stackTrace`, and `logLines`

### Requirement: Vec3 Bridge

Core data types SHALL use a lightweight `Vec3` struct instead of `UnityEngine.Vector3`. The mod assembly SHALL provide `Vec3Extensions` for `ToVec3()` / `ToVector3()` conversions at Unity boundaries.

#### Scenario: KnownLocation uses Vec3

GIVEN `KnownLocation.Position` is typed as `Vec3`
WHEN mod code creates a `KnownLocation` from a Unity transform
THEN it converts via `transform.position.ToVec3()`
AND unit tests can create `KnownLocation` instances without Unity references
