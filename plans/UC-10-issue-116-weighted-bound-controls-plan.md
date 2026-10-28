# UC-10 Issue #116 — Isolate Weighted Bound Test Controls

## Task 1 — Pin both regressions

- Add a configuration binding test proving test-only bound keys cannot change `CspOptions`.
- Add a warmed Release allocation test around `WeightedUtilityKernel.CanImprove`.
- Red command:
  `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --filter "FullyQualifiedName~Bind_TestOnlyBoundKeys_CannotChangeCspBehavior|FullyQualifiedName~Kernel_CanImprove_DoesNotAllocateAfterWarmup"`

## Task 2 — Remove the hot-path allocation

- Delete `LastCanImproveDebug` and its interpolated string assignment.
- Replace interface-based mandatory-node enumeration when the allocation test identifies it as a
  remaining per-call allocation.
- Verify the allocation regression reports zero bytes.

## Task 3 — Isolate test-only bound controls

- Remove `DisableBound` and `BoundVariant` from public `CspOptions`.
- Make `BoundVariant` internal.
- Make `ObjectiveKernelFactory` construct only the production weighted kernel.
- Add an internal weighted-kernel factory seam on `CspItinerarySolver` for trap and oracle tests.
- Remove unused `*ForTest` members and migrate existing tests to the internal seam.

## Task 4 — Verify correctness and performance

- Run trap, oracle, weighted kernel, weighted golden, and configuration tests.
- Run both weighted timing report tests.
- Regenerate `docs/benchmarks/UC-10-csp-solver-comparison-2026-10-25-issue116.*`.
- Run `SolverComparisonReportTests`.
- Run the full Application and Infrastructure unit test suites.
- Run `dotnet build TripMate.slnx --configuration Release --no-restore` and `git diff --check`.

## Definition of Done

- Both new regression tests pass.
- Production configuration cannot alter weighted bound behavior.
- Existing correctness tests and benchmark gates pass.
- The branch contains only issue #116 code, tests, specification, plan, and benchmark evidence.
