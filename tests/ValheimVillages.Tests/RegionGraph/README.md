# Region-graph pipeline tests

Pure, engine-free unit tests for the region-graph build pipeline, **organized by
the pass they exercise**. Every test here runs without a live game, a NavMesh
bake, or a captured save — fixtures are hand-authored grids and synthetic
predicates so each reachability/pruning edge case is deterministic.

## What's covered (Tier 0 — pure)

| Pass / stage | Production code | Test file |
|---|---|---|
| Region discovery (BFS flood from beds) | `Algorithms/FloodFill.Run` | `FloodFillTests.cs` |
| ↳ barrier-crossing gate | `Algorithms/FloodFill.CrossesBarrier` | `BarrierCrossingTests.cs` |
| Pass 1 output representation (outside-cell key space, membership, rectangle decomposition) | `RubberBandPrune.{PackXzKey, IsOutsideCell, DecomposeToRectangles}` | `OutsideFillTests.cs` |
| Region linking (connected-component grouping) | `Algorithms/ConnectedIslands.FindIslands` | `RegionLinkingTests.cs` |
| Patrol boundary mapping (sort/smooth/simplify/prune) | `Algorithms/BoundaryPipeline` | `../Patrol/BoundaryPipelineTests.cs` |
| Patrol boundary scoring | `Algorithms/PathScoring` | `../Patrol/PathScoringTests.cs` |

## What's deferred (Tier 1 — needs a seam, or engine)

These passes are intentionally **not** covered here. They are welded to engine
statics (`Physics.OverlapBox`, `ZoneSystem.GetGroundHeight`,
`NavMesh.SamplePosition`) or are `private`, so unit-testing them requires the
delegate-injection seam described in the Tier-1 plan:

- **RubberBandPrune Pass 1 flood** — the perimeter outside-in flood itself
  (`WallBlocks` → `Physics.OverlapBox`). Only its *output* is tested above.
- **Pass 2 (bed inside-out flood)** — bed-snap + `WallBlocks` + `ZoneSystem` Y.
- **Pass 3 (bed-reachable piece climb)** — `ZoneSystem` Y + centroid climb gating.
- **Region-level cascade** — depends on the Pass 1–3 reachability sets.
- **Pass 5 (`ConsolidateLinearChains`)** — pure logic, but `private`; needs a
  visibility bump to test.
- **Pass 4 (`SnapBordersToAgentNavMesh`)** — `NavMesh.SamplePosition`.

The engine-bound *predicates* (`ProbeAtWaist` geometry, `IsBedCollider`, NavMesh
snapping) stay covered by the in-game `ModTest` / `IntegrationTests` harness.
The boundary is deliberate: **unit tests own the flood/cascade graph logic;
integration tests own "does the probe catch a real wall."**
