# UC-10 CSP Itinerary Solver — Implementation Plan

## Status

Draft for developer approval, 2026-10-10, together with
[`specs/UC-10-csp-itinerary-solver-spec.md`](../specs/UC-10-csp-itinerary-solver-spec.md).

Worktree: `D:\FPTUCapstone\Capstone_BE_CSP`, branch `feature/phuctv-csp-scheduling` from
`develop` at `af505f4`. Do not commit, push, or open a PR without an explicit developer request.

## Verification commands

Focused:

```powershell
dotnet test tests/TripMate.Application.UnitTests -c Release --filter "FullyQualifiedName~Scheduling"
dotnet run -c Release --project tools/benchmarks/solver-comparison
```

Final (with `TRIPMATE_SQLSERVER_TEST_CONNECTION` set):

```powershell
dotnet format TripMate.slnx --verify-no-changes --no-restore
dotnet build TripMate.slnx -c Release --no-restore --nologo
dotnet test TripMate.slnx -c Release --no-build --nologo
git diff --check
```

## Phase 1 — PR 1 (opt-in CSP)

### P1.1 — Baseline
Record build and test results on the untouched worktree.

### P1.2 — Apply the developer patches
Apply 0001, 0002, and 0004 with `git am`, keeping their content unchanged. Then confirm:
- build has 0 warnings and format passes;
- Scheduling tests pass, including the 39 new CSP and routing tests.

No code is edited in this task.

### P1.3 — Configuration binding (TDD)
Tests first:
- `SchedulingGeneration:SolverMode` binds `Heuristic`, `Csp`, and `MiniRouting`.
- An unknown value fails options validation at startup.
- Non-positive `Csp:MaxNodes`, `Csp:TimeLimitMilliseconds`, or `Csp:MaxOptionalDomainSize`, or a
  non-positive `Csp:MaxStops` when set, fails options validation.
- `SchedulingGeneration:Csp:*` binds into `CspOptions`.
- The shipped `appsettings.json` resolves to `Heuristic`.

Then add the explicit `SolverMode` entry to `appsettings.json`, and validation to the existing
`SchedulingGenerationOptionsValidator` if it is not already enforced.

### P1.4 — Benchmark runner
Add `tools/benchmarks/solver-comparison`: a console project outside the solution, mirroring
`optional-route-optimization`.
- Product-rule comparison computed from the generated plans, using the canonical order from
  `OptionalRouteOptimizer.SortOptionalsCanonical`.
- Corpus: the named fixtures plus the UC-10 segments, candidates {10, 20, 40} × mandatory
  {0, 1, 3, 6} × seeds 1–25.
- Three timed modes per scenario: optimization disabled, Heuristic, and CSP. Each has a warmup
  and the same iteration count as the existing runner.
- Output is a Markdown table plus CSV with the disclosures required by the spec.
- Add a small unit test for the product-rule plan comparator used by the runner.
  - Option A: the comparator lives in the unit-test fixtures folder so the runner can reference it.
  - Option B: the comparator stays inside the runner.
  - Pick A, so the comparison logic itself is tested.

### P1.5 — Baseline benchmark report
Run the runner with the patch defaults and commit
`docs/benchmarks/UC-10-csp-solver-comparison-2026-10-10.md`. It must state that the result is
synthetic, generator-only evidence.

### P1.6 — Verification and PR 1 readiness
Final verification commands with SQL Server. Record the evidence in the spec status.

## Phase 2 — PR 2 (CSP follows the product rule)

Each task is TDD. Write a failing CSP test first, using the existing scenario fixtures, then the
minimal change in `CspItinerarySolver`.

### P2.1 — R4: respect `EnableOptionalRouteOptimization = false`
Expect the unoptimized heuristic result under `Csp` mode. Fixes the zigzag test and the
"disabled" assertions of three others.

### P2.2 — R1: lexicographic product objective
- Represent the optional-inclusion vector as a bitset over canonical rank. It must support any
  size: `MaxMatrixCandidates` has no upper bound, so `ulong` is not allowed.
- The incumbent and the final choice compare with `ScheduleGlobalComparator`.
- Branch and bound prunes on the optimistic mask (current mask OR remaining-domain mask). On an
  equal mask, it prunes on the existing travel lower bound.
- Tests:
  - the canonical tie-break scenarios pick the same POI as the heuristic;
  - a higher-ranked POI is never traded for travel time;
  - pruning never removes a strictly better completion (exhaustive check on small instances).

### P2.2a — R1a: heuristic result as the initial incumbent
Tests first:
- on every named fixture and a seeded synthetic sample, the CSP plan is never worse than the
  heuristic plan under `ScheduleGlobalComparator`, including with a tiny node budget;
- with no heuristic schedule, the search still runs and can find a feasible plan.

### P2.3 — R2/R3: remove the fixed stop cap and widen the domain
- `MaxStops` becomes nullable with default null.
- `MaxOptionalDomainSize` defaults to `MaxMatrixCandidates`.
- Test: a scenario where the heuristic schedules 11 stops yields at least 11 under CSP.

### P2.3a — R5: deterministic wall-clock safety
Test first: with a near-zero time limit, the result equals the heuristic result exactly and is
recorded as `CspFallback`. A partial CSP result is never returned.

### P2.3b — R8: solver diagnostics
Test first: the aggregate diagnostic carries the solver value (`Heuristic`, `Csp`, or
`CspFallback`) and the node and prune counts, with no user or POI identity.

### P2.3c — Affected flows and the TM-216 boundary under CSP
Run the `ItineraryVersionService` (Regenerate, Adjust Items) suites and the T-216 boundary tests
with `SolverMode = Csp`. Add a test where an optional with the maximum AI score that is
time-infeasible is still excluded under CSP.

### P2.4 — R6: pin heuristic-characterization tests
Pin only tests whose intent is legacy behavior, such as names containing `Characterize` or
`Legacy`, to `SolverMode = Heuristic`. Do not edit their expectations.

### P2.5 — Full suite under CSP
Run the whole solution once with `SolverMode = Csp` forced through configuration. Every test that
is not pinned must pass.

### P2.6 — Benchmark against the gate
- Re-run the runner and record the second report.
- Gate: 100% better-or-equal under the product rule; CSP adds at most 300 ms p95 over the
  disabled baseline in every segment; no run reaches the wall-clock limit.
- If the gate fails, tune within the spec (node budget) or report the failing scenarios. The
  product rule is never relaxed to pass the gate.

## Phase 3 — PR 3 (default switch)
Only after the gate passes:
- set `SchedulingGeneration:SolverMode = Csp` in `appsettings.json`;
- run the full suite with SQL Server;
- record the report reference in the PR.

## Review focus
1. The product rule is unchanged and reused from `ScheduleGlobalComparator`, not reimplemented.
2. The B&B bound is admissible under the lexicographic objective.
3. Determinism: the node budget is the stop condition, not wall-clock time.
4. No user-visible change before Phase 3.

## Definition of done (per phase)
- Every spec requirement of that phase has automated evidence.
- Build, format, and the full suite with SQL Server are green.
- The benchmark report is committed whenever solver behavior changes.
