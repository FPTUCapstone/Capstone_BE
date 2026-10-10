# UC-10 CSP Itinerary Solver — Implementation Plan

## Status

Approved by the developer on 2026-10-10, together with
[`specs/UC-10-csp-itinerary-solver-spec.md`](../specs/UC-10-csp-itinerary-solver-spec.md).

- **Phase 1:** implemented and in review as PR #57, on branch `feature/phuctv-csp-solver` from
  `develop` at `af505f4`. Worktree: `D:\FPTUCapstone\Capstone_BE_CSP`.
- **Phase 1 commits:**

  | Task | Commit |
  | ---- | ------ |
  | P1.2 (patches 0001, 0002, 0004) | `63309cc`, `1ca8b57`, `c864aa3` |
  | P1.3 | `89d8fad` |
  | P1.4 | `5173488` |
  | P1.5 | `928d353` |
  | Spec and plan | `4ca96ed`, `d4a4992` |
  | P1.7 (review round 1 remediation) | `96c0eb8` |
  | P1.7 (regenerated baseline report) | `7019457` |
  | P1.8 (review round 2 remediation) | `8988247`, `637ebbd` |
  | Merge of `develop` (reviewed in round 3) | `94995d9` |

  P1.7 follows the reviewed head `d4a4992`; P1.8 follows the reviewed head `7019457`; P1.9 follows
  the reviewed head `94995d9`.
- **Phases 2 and 3:** not started. Each gets its own PR.

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
- Output: the data files (`.csv`, `.timings.csv`, `.meta.json`) and a Markdown report rendered
  from them, with the disclosures required by the spec (P1.7 item 2).
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

### P1.7 — Review round 1 remediation (PR #57, reviewed head `d4a4992`)
1. **MEDIUM 1, CSP limits (TDD).**
   - Write the tests first, and see them fail on `d4a4992`. With restrictive `MaxStops` and
     `MaxOptionalDomainSize`, the CSP result:
     - stays within `MaxStops`;
     - uses only top-N optional POIs;
     - is the same with the incumbent on or off once the search completes.
   - Then:
     - `MiniRoutingSolver.Construct` takes the allowed optional nodes and a stop limit;
     - the CSP builds its incumbent from the root domain;
     - `SearchRun.WithinLimits` guards the incumbent and the polish.
2. **MEDIUM 2, auditable benchmark.**
   - The service records the solver outcome through `SchedulingSolverDiagnostics`.
   - Tests cover the `csp`, `heuristic`, search-limit fallback and infeasible outcomes.
   - The runner:
     - persists `.csv`, `.timings.csv` and `.meta.json`, and renders the Markdown from them;
     - records the full SHA;
     - refuses a dirty source tree.
   - `SolverComparisonReportTests` checks the committed report against its data.
3. **Regenerate the baseline.**
   - Commit the source changes.
   - Run `dotnet run -c Release --project tools/benchmarks/solver-comparison` on the clean tree.
   - Commit the four report files in a separate commit.
   - Do not rewrite history.
4. **LOW 1 and LOW 2.** Correct the spec's affected flows (Adjust Items bypasses `SolverMode`), and
   update this status and the spec's branch and commit references.
5. **Re-review gate.**
   - Full suite, plus SQL Server and Redis integration tests with no required-test skips.
   - Format.
   - Exact-head CI.
   - Request re-review on the new head SHA.

### P1.8 — Review round 2 remediation (PR #57, reviewed head `7019457`)
1. **MEDIUM 2, fail-closed benchmark validation (TDD).**
   - Write parameterized tests that corrupt a valid data set and require `Read` or `Validate` to
     fail: unknown mode or outcome, an outcome of another mode, negative, NaN or infinite elapsed
     time, a surplus row for an unknown scenario, a duplicate key, a mismatched segment, a missing
     iteration, iterations 0 and above `MeasuredIterations`, an unknown verdict, a CSP plan above
     `MaxStops`, a duplicate scenario, a corpus-size mismatch, a short commit, no measured
     iterations, and a metadata date that does not match the file name.
   - Add `SolverComparisonReport.Validate`, called by `Read`, `Write` and `Markdown`.
   - Add `FindProvenance` and `EnsureProvenance`: the committed report's commit must be an ancestor
     of `HEAD` and the run must use a clean tree. Tests cover the not-ancestor, unknown and dirty
     cases. `ci.yml` checks out with `fetch-depth: 0`.
   - The committed corpus passes unchanged, so the report is not regenerated.
2. **LOW 2.** Add `96c0eb8` and `7019457` to the commit table.
3. **Re-review gate.** As in P1.7 step 5, on the new head SHA.

### P1.9 — Review round 3 remediation (independent algorithm review of head `94995d9`)
1. **HIGH, rest reserve (test first).**
   - Add `CspRestOptimalityTests`: random non-metric instances (4–6 candidates, tight opening hours,
     optional budget), 80 per rest preference. An evaluator-based brute force gives the optimum,
     and the test counts CSP schedules that are worse or missing.
   - Measured on `94995d9`: `Auto` 6/77 worse, `Frequent` 23/69 worse, none missing.
   - Then, in `CspItinerarySolver`:
     - always run the relaxed model when rest applies, after the reserved one, with the reserved
       run's best evaluated objective as its initial bound;
     - evaluate every elite solution of both runs and keep the lowest evaluated objective.
   - Result: `Auto` 0/77 worse, `Frequent` 1/69 worse. The test thresholds pin these numbers.
   - Correct the `SearchCompleted` comment.
2. **MEDIUM 2.** One `NodeBudget` shared by both runs, with a test that the total stays within
   `MaxNodes`. Document the time-limit determinism exception (spec invariants, report
   limitations).
3. **MEDIUM 1.** Replace the commit provenance with `SolverComparisonReport.ComputeSourceHash`
   over `SourceInputs`, with tests for line endings, content changes and missing inputs. The latest
   report must match the current source. Remove the git calls from the runner and the test, and
   revert `ci.yml` to the `develop` checkout.
4. **Benchmark corpus.** Add `RestPreference` to `CreateSyntheticCorpusScenario` and the runner
   (`None`, `Auto`, `Frequent`; `-auto` and `-frequent` segment suffixes), a `rest_preference`
   column, and a per-preference quality table.
5. **LOWs.**
   - A CSP plan no longer records `OptionalRouteOptimization`.
   - New `SchedulingSolverOutcome` tags: `csp.evaluator_calls` and `csp.chose_relaxed_rest_model`.
   - Validate `MiniRouting.*` and `Csp:FinalCandidatesToValidate`.
   - Remove per-node allocations in `LowerBound`, `RouteEliteSet.Offer` and `OrderValues`.
6. **MEDIUM 3 and R4.** No code change. They stay Phase 2 R1a and R4.
7. **Regenerate the report** with the source changes. The order of commits no longer matters,
   because the report is tied to a source hash, not a commit. Commit the source and the four report
   files together, or the report right after the source.
8. **Re-review gate.** As in P1.7 step 5, on the new head SHA.

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
