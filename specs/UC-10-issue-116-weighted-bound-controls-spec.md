# UC-10 Issue #116 — Isolate Weighted Bound Test Controls

## Context

`WeightedUtilityKernel.CanImprove` runs in the CSP search hot path. It currently builds an unused
debug string after calculating the upper bound. `CspOptions` also exposes `DisableBound` and
`BoundVariant`, allowing application configuration to select controls created for oracle and trap
tests.

## Scope

- Remove heap allocation from `WeightedUtilityKernel.CanImprove` after warm-up in Release builds.
- Prevent application configuration from disabling the weighted upper bound or selecting a faulty
  test variant.
- Keep exhaustive oracle and faulty-bound trap coverage through an internal test seam.
- Remove unused test accessors from the production kernel.
- Regenerate the required solver comparison report after scheduling source changes.

## Acceptance Criteria

1. `CanImprove` allocates zero bytes across repeated calls after warm-up.
2. `CspOptions` has no public `DisableBound` or `BoundVariant` property.
3. Production kernel creation always uses the correct enabled upper bound.
4. Trap, oracle, weighted golden, and configuration binding tests pass.
5. The Release timing and solver comparison benchmark gates pass.
6. Application and Infrastructure unit test suites pass.

## Design Decisions

- Delete the unused debug string instead of retaining a debug-only branch.
- Keep bound variants internal because they document and reproduce invalid-bound traps.
- Inject variant kernels through an internal solver factory used only by the test assembly.
- Preserve the weighted objective, pruning formula, scoring, and HTTP API contracts.

## Out of Scope

- Changing weighted scoring or itinerary ranking.
- Replacing the branch-and-bound implementation.
- Changing Lexicographic scheduling behavior.
- Addressing existing package vulnerability warnings.
