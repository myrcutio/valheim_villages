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
| **RubberBand Pass 1** — perimeter outside-in flood | `RubberBandPrune.PerimeterOutsideFlood` | `Pass1OutsideFloodTests.cs` |
| **RubberBand Pass 2** — bed inside-out flood | `RubberBandPrune.BedReachableFlood` | `Pass2BedReachableTests.cs` |
| Pass 1 output representation (outside-cell key space, membership, rectangle decomposition) | `RubberBandPrune.{PackXzKey, IsOutsideCell, DecomposeToRectangles}` | `OutsideFillTests.cs` |
| Region linking (connected-component grouping) | `Algorithms/ConnectedIslands.FindIslands` | `RegionLinkingTests.cs` |
| Patrol boundary mapping (sort/smooth/simplify/prune) | `Algorithms/BoundaryPipeline` | `../Patrol/BoundaryPipelineTests.cs` |
| Patrol boundary scoring | `Algorithms/PathScoring` | `../Patrol/PathScoringTests.cs` |

## What's deferred (Tier 1 — needs a seam, or engine)

Passes 1 and 2 were the first Tier-1 seam carve: their BFS logic is now pure
(`PerimeterOutsideFlood`, `BedReachableFlood`) with the engine touch-points
(`Physics.OverlapBox` wall gate, `ZoneSystem` heights) injected as delegates.
Live code wires the real providers; tests pass synthetic grids (`GridEnv`).

Still **not** covered here — the remaining seam work:

- **Pass 3 (bed-reachable piece climb)** — `ZoneSystem` Y + centroid climb
  gating + edge recording; the most entangled pass. Next seam target.
- **Region-level cascade** — operates on plain dicts (nearly pure); testable
  once Pass 3's reachability sets can be supplied as fixtures.
- **Pass 5 (`ConsolidateLinearChains`)** — pure logic, but `private`; needs a
  visibility bump to test.
- **Pass 4 (`SnapBordersToAgentNavMesh`)** — `NavMesh.SamplePosition`.

The engine-bound *predicates* (`ProbeAtWaist` geometry, `IsBedCollider`, NavMesh
snapping) stay covered by the in-game `ModTest` / `IntegrationTests` harness.
The boundary is deliberate: **unit tests own the flood/cascade graph logic;
integration tests own "does the probe catch a real wall."**
