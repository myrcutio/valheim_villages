# Region-graph pipeline tests

Pure, engine-free unit tests for the region-graph build pipeline, **organized by
the pass they exercise**. Every test here runs without a live game, a NavMesh
bake, or a captured save — fixtures are hand-authored grids and synthetic
predicates so each reachability/pruning edge case is deterministic. The whole
suite runs in well under a second; `dotnet test` belongs in the edit loop
alongside `dotnet build`.

## Why these exist

The pipeline has repeatedly produced a *wrong walkable map* that nothing caught
until someone played the game and probed it by hand, at the cost of a server
rebuild, a restart and an afternoon. Each file below pins a failure that
actually happened, with the measurement in the test name or doc comment:

| Failure | Pinned by |
|---|---|
| Roofs baked as walkable floor (pitched mesh, **axis-aligned box collider**, flat top) | `RoofPrefabExclusionTests` |
| `MaxClimb` 1.0 → 0.2 deleted a building with its crafting stations (825 → 625 piece keys) | `Pass3PieceReachableTests` |
| A prune trusting the **link list** for reachability kept an unreachable roof | `Pass3PieceReachableTests` |
| A half-prune left ghost regions that `PointToRegionId` still answered with (`p1101`) | `GraphInvariantTests` |
| Narrow stairs stacked over a corridor are never claimed (measured at `-71,-373`) | `Pass3PieceReachableTests` (characterisation) |

## What's covered (Tier 0 — pure)

| Pass / stage | Production code | Test file |
|---|---|---|
| **RubberBand Pass 1** — perimeter outside-in flood | `RubberBandPrune.PerimeterOutsideFlood` | `Pass1OutsideFloodTests.cs` |
| **RubberBand Pass 2** — anchor inside-out flood | `RubberBandPrune.AnchorReachableFlood` | `Pass2AnchorReachableTests.cs` |
| **RubberBand Pass 3** — anchor-reachable piece climb | `RubberBandPrune.PieceReachableFlood` | `Pass3PieceReachableTests.cs` |
| Pass 1 output representation (key space, membership, rectangle decomposition) | `RubberBandPrune.{PackXzKey, IsOutsideCell, DecomposeToRectangles}` | `OutsideFillTests.cs` |
| Graph commit invariants (orphan cells/links/kinds, centroid-less and unresolvable regions, self-links) | `RegionGraph.ValidateInternalConsistency` | `GraphInvariantTests.cs` |
| Roof exclusion rule | `NavMeshBakeManager.IsRoofPrefabName` | `RoofPrefabExclusionTests.cs` |
| Patrol boundary mapping / scoring | `Algorithms/BoundaryPipeline`, `Algorithms/PathScoring` | `../Patrol/*` |

Fixtures: `GridEnv` models the flat cell grid Passes 1–2 walk. `PieceGridEnv`
models the layered world Pass 3 walks — piece regions stacked at arbitrary
heights over terrain cells, indexed exactly as `RubberBandPrune.Apply` indexes
them.

## Two rules for anything added here

1. **Drive production code through an injected seam. Never reimplement a pass.**
   An earlier headless harness (`tests/HnaPartitionTests`, 13 files) had its own
   copies of the flood and boundary algorithms. They drifted from production and
   the whole thing was deleted. Pass 3's only engine dependency was
   `ZoneSystem.GetGroundHeight`; it is now a `Func<float, float, float>`
   parameter, and that is the shape to follow.
2. **Pin trade-offs from both sides.** `MaxClimb` is not "a constant that must
   not change" — it is a value with a cost in each direction. There is a test
   asserting stairs survive at 1.0 *and* one asserting they are lost at 0.2 with
   the measured regression in the failure message, so whoever changes it next
   reads what it costs instead of discovering it in game three days later.

## What's deferred (Tier 1 — needs a seam, or engine)

- **Region-level cascade** — plain dictionaries, nearly pure; now testable since
  Pass 3's reachability sets can be supplied as fixtures. Next seam target.
- **Pass 5 (`ConsolidateLinearChains`)** — pure logic, but `private`; needs a
  visibility bump.
- **Pass 4 (`SnapBordersToAgentNavMesh`)** — `NavMesh.SamplePosition`; already
  self-guards to a no-op when the agent is unregistered, so it is free headless.
- **`RubberBandPrune.Apply` end-to-end** — its complete engine surface is ~7
  touch-points (solid mask, one `LayerMask.NameToLayer`, `GatherGateSeals`, two
  already-lambda oracles, `ZoneSystem.GetGroundHeight`, and a self-guarding
  border snap). An env struct with ~4 delegates would make whole-prune replay
  possible.

Engine-bound *predicates* (waist-probe geometry, NavMesh snapping) stay covered
by the in-game `ModTest` / `IntegrationTests` harness. The boundary is
deliberate: **unit tests own the flood/cascade graph logic; integration tests own
"does the probe catch a real wall."**

Note: headless Unity calls that touch native code (`Physics.*`, `NavMesh.*`,
`LayerMask.NameToLayer`, even `Bounds.Contains`) throw
`SecurityException: ECall methods must be packaged into a system module` — they
fail loudly rather than returning a plausible wrong answer, so there is no risk
of a test silently passing against a stub.
