# UC-10 Optional POI Route Optimization Implementation Plan

## Status

Implemented locally with functional regression verification complete. The approved benchmark gate
passes: absolute p95 overhead is bounded for every row, and the relative 2x check passes for every
row whose disabled baseline p95 is at least 5 ms.

## Goal

Replace tail-only optional insertion with deterministic cheapest-feasible insertion, bounded
2-opt/relocate improvement, skipped-candidate reconsideration, and independent final validation,
while preserving ranking priority, all hard constraints, fixed-order behavior, and one matrix call.

## Source Specification

`specs/UC-10-optional-poi-route-optimization-spec.md`

## Baseline and Workspace

- Current branch: `feature/phuctv-uc10-poi-recommendations` (non-main).
- The working tree contains extensive pre-existing UC-10 changes. Record them and preserve them.
- Before implementation, run and record:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter FullyQualifiedName~ItineraryGenerationServiceTests
dotnet test TripMate.slnx --no-restore --nologo
```

- Capture current golden output and generator-only timing for the benchmark corpus before changing
  the algorithm. Do not present one local timing sample as a production SLO.

## Frozen Technical Decisions

- Optimization applies only to `GenerateAsync`; `GenerateFixedOrderAsync` is validation-only.
- Canonical ranking controls optional admission; the matrix controls selected-visit ordering.
- Every proposed sequence is fully rescheduled; timestamps/rests are never patched in place.
- Distributed/directional matrix values are authoritative; no geographic-distance proxy.
- Search is bounded by deterministic evaluation count, not elapsed wall time.
- Defaults: enabled, 3 seeds, 5,000 evaluations.
- Legacy construction remains a candidate/fallback, so exhaustion cannot regress feasibility.
- Final success must pass an independent invariant validator.
- No public API, database, ranking, matrix-cap, or provider-call change.

## Approved TDD Seams

1. `ItineraryGenerationService.GenerateAsync` for all optimizer behavior.
2. `ItineraryGenerationService.GenerateFixedOrderAsync` for exact-order compatibility.
3. Internal final validator through deliberate invalid-plan fixtures.
4. Existing handler and itinerary-version tests for integration regressions.

Private insertion, 2-opt, and relocate helpers are implementation details. Tests should assert
observable route quality, feasibility, determinism, and bounded work.

## Work Items

### W01 — Characterize legacy behavior and establish benchmark corpus

Files:

- Modify
  `tests/TripMate.Application.UnitTests/Features/Scheduling/ItineraryGenerationServiceTests.cs`.
- Add `tests/TripMate.Application.UnitTests/Features/Scheduling/Fixtures/OptionalRouteOptimizationScenarios.cs`.
- Add a reproducible benchmark runner under `tools/benchmarks/optional-route-optimization/` using
  the lightest repository-compatible approach; do not add BenchmarkDotNet unless approved after
  checking package/dependency impact.

Red/characterization work:

- freeze current canonical optional ranking and tie-break order;
- capture a tail-only zigzag scenario and a tail-infeasible/middle-feasible candidate;
- cover asymmetric matrix, tight hours, budget, rest, and final endpoint;
- capture exact fixed-order behavior and one route-provider call;
- define corpus sizes 10/20/40 and mandatory counts 0/1/3/6;
- run disabled/legacy timing and record host/runtime configuration.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter FullyQualifiedName~ItineraryGenerationServiceTests
```

Definition of done: quality gaps reproduce deterministically, existing behavior unrelated to route
quality is frozen, and a repeatable before-measurement exists.

### W02 — Options, candidate index, and pure schedule evaluator

Files:

- Modify
  `src/TripMate.Application/Features/Scheduling/Common/SchedulingGenerationOptions.cs`.
- Add
  `src/TripMate.Application/Features/Scheduling/Common/SchedulingGenerationOptionsValidator.cs`.
- Add `src/TripMate.Application/Features/Scheduling/Common/ItineraryScheduleEvaluator.cs`.
- Modify `src/TripMate.Application/Features/Scheduling/Common/ItineraryGenerationService.cs`.
- Add focused option tests and evaluator coverage under
  `tests/TripMate.Application.UnitTests/Features/Scheduling/`.
- Modify `src/TripMate.Infrastructure/DependencyInjection.cs` only as needed to activate validated
  options; preserve the developer's other changes.
- Modify `src/TripMate.Api/appsettings.json` with the three non-secret defaults.

Red tests first:

- valid defaults/ranges pass and invalid seed/evaluation settings fail startup validation;
- one visit sequence rebuilds the same visits, timestamps, costs, rests, end, and travel totals as
  the characterized service;
- directional matrix lookup and start/end arcs are correct;
- invalid hours, budget, rest continuation, or horizon yields no schedule;
- precomputed ID-to-matrix index eliminates repeated linear lookup without changing results;
- cancellation during evaluation propagates.

Implementation:

- represent proposed routes as visit candidates/IDs only;
- move full timeline/rest construction into one deterministic evaluator;
- return a schedule plus total directional matrix travel/objective data;
- keep the old public methods green while delegating construction to the evaluator.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ItineraryGenerationServiceTests|FullyQualifiedName~ItineraryScheduleEvaluator|FullyQualifiedName~SchedulingGenerationOptions"
```

Definition of done: one reusable full-feasibility evaluator is the sole constructor for proposed
visit orders, with unchanged public results before optimization is enabled.

### W03 — Cheapest feasible insertion and baseline fallback

Files:

- Add `src/TripMate.Application/Features/Scheduling/Common/OptionalRouteOptimizer.cs`.
- Modify `src/TripMate.Application/Features/Scheduling/Common/ItineraryGenerationService.cs`.
- Modify
  `tests/TripMate.Application.UnitTests/Features/Scheduling/ItineraryGenerationServiceTests.cs`.

Red tests first, one vertical case at a time:

- every insertion slot is considered and the lowest directional delta wins;
- a tail-infeasible candidate is admitted in a feasible middle slot;
- full evaluation rejects a low-delta proposal that violates hours/budget/rest/end;
- asymmetric deltas choose the correct slot;
- canonical rank controls admission and lower-ranked candidates cannot evict admitted higher ones;
- at most three deterministic seeds are selected from mandatory-permutation baselines;
- disabled mode is byte-compatible with the characterized legacy route;
- zero/exhausted optimization budget returns a valid baseline.

Implementation:

- retain legacy baselines for every mandatory permutation;
- select stable seeds using the declared global comparator;
- process optionals in canonical rank and evaluate all slots;
- count every full proposed-route evaluation against one call-wide budget;
- compare baselines and optimized results without returning a worse plan.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter FullyQualifiedName~ItineraryGenerationServiceTests
```

Definition of done: optional membership remains ranking-led, insertion is geographically aware and
fully feasible, and the feature has a valid legacy fallback.

### W04 — Deterministic 2-opt, relocate, and reconsideration

Files:

- Modify `src/TripMate.Application/Features/Scheduling/Common/OptionalRouteOptimizer.cs`.
- Modify
  `tests/TripMate.Application.UnitTests/Features/Scheduling/ItineraryGenerationServiceTests.cs`.

Red tests first:

- a 2-opt fixture strictly reduces total directional travel;
- a relocate fixture strictly reduces travel where 2-opt alone does not;
- moves never add/remove visits or alter mandatory classification;
- an attractive but infeasible move is rejected after complete rescheduling;
- skipped candidates are retried in canonical order after improvement and a newly feasible one is
  admitted;
- a no-improvement round terminates;
- evaluations never exceed the configured shared maximum;
- cancellation within the search exits promptly;
- repeated runs produce identical plan and work counters.

Implementation:

- enumerate 2-opt then relocate moves in stable index order;
- apply deterministic best strict improvement under travel/duration/ID objective;
- repeat until local optimum or budget exhaustion;
- retry skipped candidates and rerun local search only after an admission;
- never remove an admitted candidate.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter FullyQualifiedName~ItineraryGenerationServiceTests
```

Definition of done: both local-search operators and reconsideration improve the golden cases without
violating selection or hard constraints, and work is deterministic/bounded.

### W05 — Independent invariant validator

Files:

- Add `src/TripMate.Application/Features/Scheduling/Common/GeneratedItineraryInvariantValidator.cs`.
- Modify `src/TripMate.Application/Features/Scheduling/Common/ItineraryGenerationService.cs`.
- Add
  `tests/TripMate.Application.UnitTests/Features/Scheduling/GeneratedItineraryInvariantValidatorTests.cs`.
- Modify existing generator tests only where needed to assert both public methods use validation.

Red tests first:

- reject missing/duplicate/unavailable visits and wrong mandatory metadata;
- reject non-contiguous sequence numbers;
- reject chronology, directional travel, transition/final buffer, and reported-end mismatches;
- reject visit/rest opening-hour violations;
- reject wrong cost totals and budget overflow;
- reject rest-policy violations and end beyond horizon;
- accept representative valid optimized, legacy, and fixed-order plans;
- corrupting the optimizer output prevents a successful return and surfaces an invariant failure.

Implementation:

- independently replay the final plan from input/matrix/options;
- do not call optimizer helpers or trust cached feasibility/objective flags;
- validate success from both public generation methods;
- keep internal invariant failure distinct from user infeasibility.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter "FullyQualifiedName~GeneratedItineraryInvariantValidatorTests|FullyQualifiedName~ItineraryGenerationServiceTests"
```

Definition of done: every returned success is independently proven to satisfy the declared hard
invariants, and deliberate corruptions are detected.

### W06 — Aggregate objective diagnostics

Files:

- Add `src/TripMate.Application/Features/Scheduling/Common/RouteOptimizationDiagnostics.cs`.
- Modify `src/TripMate.Application/Features/Scheduling/Common/ItineraryGenerationService.cs`.
- Modify construction sites in `CreateSchedulingRequestCommandHandler` and
  `ItineraryVersionService` only if an observer/logger must be passed.
- Add diagnostics assertions to Application unit tests.

Red tests first:

- one successful optimized call records baseline/final optional count and travel objective;
- evaluation/seed/reconsideration/2-opt/relocate/budget-exhausted values are correct;
- disabled mode is identifiable without fake improvement values;
- diagnostics contain no user ID, POI ID/name, coordinates, preferences, or route sequence;
- measured elapsed duration is excluded from deterministic equality assertions.

Implementation:

- expose one repository-named `System.Diagnostics.ActivitySource` and emit one aggregate activity
  per successful generation, with `RouteOptimizationDiagnostics` mapped to fixed low-cardinality
  tag names;
- keep diagnostics out of public response DTOs and persistence;
- do not log per proposal, candidate, permutation, or move.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ItineraryGenerationServiceTests|FullyQualifiedName~RouteOptimizationDiagnostics"
```

Definition of done: before/after objective and bounded-work evidence are observable without sensitive
or high-cardinality route data.

### W07 — Integration regression and representative benchmark

Files:

- Modify only affected handler/version tests if their expected visit order legitimately changes.
- Update the W01 benchmark runner/results.
- Do not rewrite unrelated audit reports.

Tests and measurements:

- create-generation handler preserves ranking/provider pool, one matrix call, API contract, and
  deterministic persistence;
- regenerated itinerary uses optimization; adjusted/fixed-order itinerary preserves exact order;
- snapshot retry with unchanged inputs produces the same optimized plan;
- run 10/20/40 candidate and 0/1/3/6 mandatory corpus cases in enabled/disabled modes;
- record optional inclusion vector, total travel, duration, evaluations, exhausted rate, elapsed
  p50/p95/p99, allocations, runtime/OS/CPU, and options;
- verify enabled p95 adds no more than 100 ms in every corpus segment;
- when disabled p95 is at least 5 ms, also verify enabled p95 is at most 2x baseline;
- report ratios below the 5 ms baseline floor as non-gating context.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~Scheduling|FullyQualifiedName~Itinerary"
```

Definition of done: integration semantics remain intact, golden routes improve, and the performance
gate has reproducible evidence rather than an unmeasured assertion.

### W08 — Full regression, review, and delivery evidence

Files:

- Update status/evidence in the source spec and this plan only after every criterion is proven.
- Add the final benchmark artifact path; preserve raw evidence.

Actions:

1. Run formatting/analyzers and the full solution.
2. Review first for specification compliance, then for repository standards/code quality.
3. Fix every critical finding and rerun affected/full verification.
4. Inspect `git diff`/`git status` and confirm unrelated working-tree changes are untouched.
5. Do not commit, push, merge, or open a PR without explicit developer instruction.

Verification:

```powershell
dotnet test TripMate.slnx --no-restore --nologo
dotnet format TripMate.slnx --verify-no-changes
```

Definition of done: all acceptance criteria have evidence, tests/formatting pass, benchmark gate
passes, critical findings are resolved, and no unrelated changes were introduced.

## Dependency Order

```text
W01 → W02 → W03 → W04 → W05 → W06 → W07 → W08
```

Do not begin the next work item while targeted verification is red or a critical review finding is
unresolved. W05 intentionally follows a working optimizer so its mutation fixtures validate real
output, but the final validator must be complete before integration/benchmark acceptance.

## Risk Controls

| Risk | Control | Evidence |
|---|---|---|
| Better travel breaks hours/budget/end | full schedule evaluation for every proposal + independent final validator | W02/W05 |
| Ranking loses ownership | canonical admission order, no eviction, inclusion-vector comparator | W03/W04 |
| Six mandatory POIs explode CPU | legacy baseline, three deterministic seeds, shared 5,000-evaluation cap | W03/W07 |
| Time budget makes output nondeterministic | fixed evaluation count; wall time is measurement only | W04 determinism tests |
| Asymmetric matrix is treated as symmetric | directional delta and full matrix replay | W02/W03 fixtures |
| Local search corrupts rests/timestamps | rebuild whole schedule; never move generated items in place | W02/W04 |
| Fixed-order adjustment is reordered | optimizer excluded from `GenerateFixedOrderAsync` | W05/W07 |
| Budget exhaustion returns partial route | always retain best valid baseline/result | W03/W04 |
| Cancellation wastes CPU | checks per permutation and route evaluation | W02/W04 |
| Diagnostics leak route/user data | one aggregate event with fixed fields and privacy tests | W06 |
| Claimed improvement is anecdotal | golden matrices + representative reproducible corpus | W01/W07 |
| Existing user changes are overwritten | path-level diff audit; no reset/checkout of unrelated files | W08 |

## Definition of Done

- [ ] Spec and plan explicitly approved before implementation.
- [ ] Canonical ranking remains the optional admission authority.
- [ ] Every optional is tested at every insertion slot within the deterministic work budget.
- [ ] Skipped candidates are reconsidered after improvement.
- [ ] 2-opt and relocate improve their golden routes without changing selected membership.
- [ ] Every proposal is fully rescheduled for hours, cost, rests, buffers, horizon, and endpoint.
- [ ] Independent validator gates all successful generated/fixed-order plans.
- [ ] Fixed-order adjustment and one matrix call remain unchanged.
- [ ] Evaluation cap, cancellation, feature-off fallback, and determinism are proven.
- [ ] Before/after objective diagnostics are recorded without user/POI identity.
- [ ] Representative benchmark meets the declared p95 gate and records quality/work evidence.
- [ ] No API/schema/ranking/provider-pool semantics or database migration changed.
- [ ] Targeted/full tests and formatting pass; review has no unresolved critical finding.
- [ ] No commit, push, merge, or PR occurs without explicit instruction.

## Verification Evidence

- Solution build: succeeded with 0 warnings and 0 errors.
- Application unit tests: 1,208 passed, 0 failed.
- Infrastructure unit tests: 286 passed, 0 failed, 1 external-provider test skipped.
- API integration tests: 348 passed, 0 failed, 172 external-service tests skipped locally.
- Targeted optimizer/evaluator/options/validator tests: 84 passed, 0 failed.
- Benchmark runner and project build successfully; raw local evidence is recorded in
  `docs/benchmarks/UC-10-optional-route-optimization-2026-10-07.md`.
- Benchmark travel improved in every corpus row and absolute p95 overhead stayed below 12 ms in
  this run. All three rows with disabled p95 at least 5 ms also remained within 2x; ratios for the
  sub-5 ms rows are recorded as non-gating context under the approved threshold floor.
