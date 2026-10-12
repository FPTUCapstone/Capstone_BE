# UC-10 CSP Itinerary Solver — Specification

## Status

Approved by the developer on 2026-10-10. Owner: UC-10 scheduling (phuctv).

- **Phase 1:** merged as PR #57 (merge commit `983a192`).
- **Phase 2:** merged as PR #62 (merge commit `31f9a5a`, final head `7f38cbc`).
- **Phase 3:** implemented on the same branch. The default switch merges only after the Report 3 V2
  fallback amendment is accepted. See [Phase 3 status](#phase-3-status). The review of head
  `d390462` requested changes; see
  [Phase 3 review remediation](#phase-3-review-remediation-review-of-d390462).

Phase 1 history: it was implemented and reviewed as PR #57 (branch `feature/phuctv-csp-solver`,
from `develop` at `af505f4`):

- Patches 0001, 0002, and 0004 applied as commits `63309cc`, `1ca8b57`, and `c864aa3`, with
  their content unchanged.
- Configuration binding and validation (`89d8fad`), the product-rule plan comparison fixture and
  the `solver-comparison` runner (`5173488`), implemented test-first.
- Review round 1 (reviewed head `d4a4992`, verdict: changes requested) is addressed by the commits
  that follow it. Review round 2 (reviewed head `7019457`, verdict: changes requested) is
  addressed by the benchmark-validation commit that follows it. Review round 3 (independent
  algorithm review of head `94995d9`, verdict: changes requested) is addressed by the rest-model,
  source-hash and diagnostics changes that follow it. Review round 4 (independent final re-review of
  head `ebd90a9`, verdict: changes requested) is addressed by the exact-corpus validation and
  node-count changes that follow it. The open-prefix fix found by the developer's brute-force check
  ships in the same change. See [Phase 1 review remediation](#phase-1-review-remediation).
- Baseline report: `docs/benchmarks/UC-10-csp-solver-comparison-2026-10-11.md`. It was regenerated
  after the review round 4 changes and the open-prefix fix. Against the 2026-10-10 report:
  - every plan, visit count, travel time and outcome is identical, because the synthetic matrices
    satisfy the triangle inequality;
  - only node-limited scenarios changed their node count, and every such scenario now reports
    exactly 200,000. That is 218 one-run scenarios, which drop by 1, and 78 reserved-plus-relaxed
    scenarios, which drop by 2 (round 4 LOW);
  - latency samples differ;
  - the 2026-10-10 report is kept as the round 3 evidence.
- It used patch defaults over 907 synthetic scenarios: 7 named fixtures plus
  300 per rest preference. Its `Source` line gives the hash of the benchmarked files. Under the
  product rule (CSP better / equal / worse):
  - every CSP plan stays within `MaxStops = 10`, and no run fell back to the heuristic or reached
    the wall-clock limit;
  - `None`: 88 / 27 / 192 of 307. With 10 candidates the CSP is never worse; with 20–40 candidates
    it is worse in 191 of 200;
  - `Auto` (the mobile default): 137 / 32 / 131 of 300;
  - `Frequent`: 2 / 0 / 298 of 300. The CSP schedules fewer optional POIs than the heuristic (for
    example 7.92 against 8.64 on average in c10-m0-frequent). Its travel-plus-penalty objective
    accepts dropping a POI to save travel. With frequent rests, the relaxed model's best solutions
    also often fail rest insertion, so a reserved-model solution with fewer stops is kept;
  - latency: two segments exceed the 300 ms CSP gate in this run (c40-m6, c40-m6-frequent). The
    2026-10-10 run had five, and the round 4 reviewer's run had six. Segments near the gate move
    with host noise;
  - this is the gap Phase 2 closes (R1, R1a, R2, R9). The CSP stays experimental and is not the
    default.
- Verification evidence for the current head is recorded in the PR. The previous evidence was
  recorded for `d4a4992` and is superseded.

## Purpose

Generate itineraries with a real constraint-satisfaction search (variables, domains, hard
constraints, backtracking, forward checking, branch and bound) instead of only greedy insertion
plus local search, while keeping the product's existing definition of the best itinerary. The
SEP490 report needs measured evidence that the CSP finds itineraries that are at least as good as
the current heuristic under that same definition.

## Sources

1. Developer design note "Áp dụng thuật toán CSP cho lập lịch TripMate" (2026-10-09).
2. Developer patches `0001-mini-routing-solver`, `0002-csp-solver`, `0004-csp-stability-fixes`
   (2026-10-09). Patch `0003-tripmate-ai-providers` is out of scope.
3. Current `develop` scheduling code: `OptionalRouteOptimizer`, `ScheduleGlobalComparator`,
   `SortOptionalsCanonical`, `ItineraryScheduleEvaluator`, `GeneratedItineraryInvariantValidator`.
4. Approved specs that constrain this work:
   - `UC-10-optional-poi-route-optimization-spec.md`: performance gate, no wall-clock
     optimization timeout, benchmark corpus segments;
   - `TM-216-ai-csp-responsibility-boundary-spec.md`: frozen AI/scheduler boundary;
   - `UC-10-candidate-pool-funnel-and-replay-spec.md`: benchmark report disclosure rules.
5. Report 3 SRS already describes itineraries as generated by the "CSP Engine".

## Affected flows

`ItineraryGenerationService` is the only plan producer (TM-216). It is called by:
- itinerary creation (`CreateSchedulingRequestCommandHandler`) through `GenerateAsync`;
- Regenerate (`ItineraryVersionService.CreateRegeneratedVersionAsync`) through `GenerateAsync`;
- Adjust Items (`ItineraryVersionService.CreateAdjustedVersionAsync`) through
  `GenerateFixedOrderAsync`.

`SolverMode` (Heuristic, CSP, or MiniRouting) applies only to creation and Regenerate, which
search for a visit order. Adjust Items keeps the user's fixed visit order, so no solver runs. It
uses the authoritative schedule evaluation (`ItineraryScheduleEvaluator`) and
`GeneratedItineraryInvariantValidator` in every mode, and it is intentionally independent of
`SolverMode`. The gates in this specification still run the Adjust Items suites under each mode,
as a regression check that the mode switch does not affect them.

## Invariants that must hold in every solver mode

- **TM-216 boundary.** AI influences scheduling only through `EffectiveDesirabilityScore`
  ordering. Every hard constraint (opening hours, travel time, budget, available duration,
  mandatory inclusion, rest and buffer) is enforced by the scheduler with current data. An
  impossible mandatory POI yields `planning.constraints_infeasible` and is never dropped.
- **Deterministic output.** The node budget, not the clock, stops the CSP. Identical input
  therefore produces an identical itinerary on any host, as long as the search finishes before the
  wall-clock safety limit. On the development host every benchmark scenario finishes well before
  it.
  - If the limit fires (R5), the partial CSP result is discarded. The heuristic itinerary for the
    same input is returned instead, and it is deterministic too. A partial result is never
    returned.
  - Which of the two plans a request gets can still depend on the host. A much slower or
    overloaded host can return the heuristic plan where a faster host returns the CSP plan.
  - Both plans pass the invariant validator, and the CSP plan is never worse than the heuristic
    plan.
  - Phase 3 measures how often the limit fires on production-like hardware (review of PR #62,
    Low 3).
- **Validation.** Every plan passes `GeneratedItineraryInvariantValidator`.

## Evaluation that motivates this specification

Measured on 2026-10-10 against `develop` with the three patches applied (scratch worktree):

- Patches apply cleanly; Release build has 0 warnings; 1604/1604 unit tests pass with the default
  `SolverMode = Heuristic`, including 39 new CSP and routing tests.
- With `Csp` forced as the default, 12 existing tests fail:
  - 8 because the CSP ignores the canonical ranking tie-break order;
  - 4 because the CSP runs even when `EnableOptionalRouteOptimization = false`.
- Over 307 scenarios (7 named fixtures plus 300 synthetic ones with 8–40 candidates and 0–4
  mandatory POIs), both solvers always succeed, but with the patch defaults the CSP schedules fewer
  optional POIs in about 31% of scenarios and more in none. Two causes:
  - **Different objective.** "Travel minutes + skip penalty" (penalty capped at 150 minutes) lets
    the CSP drop a POI to save travel. The product objective is `ScheduleGlobalComparator`.
  - **Fixed cap of 10 stops.** Every remaining shortfall happened exactly at this cap, while the
    heuristic scheduled 11.
- CSP latency: median ~6 ms and p95 ~184 ms, against ~2–7 ms for the heuristic.

## Product rule kept (developer decision, 2026-10-10)

The best itinerary is defined by the existing `ScheduleGlobalComparator`, unchanged:

1. All mandatory POIs present (hard constraint C6).
2. Optional-inclusion vector in canonical rank order (`SortOptionalsCanonical`), compared
   lexicographically. Including a higher-ranked optional POI beats any set of lower-ranked ones.
   The canonical order already embeds the ranking tie-break rules.
3. Fewer total matrix travel minutes.
4. Shorter total duration, then earlier end time.
5. Lexicographically smaller visit-ID sequence.

The CSP may not trade a higher-ranked POI for shorter travel. A "relaxed pace" itinerary, if ever
wanted, is a separate user-facing feature.

## Phase 1 — opt-in CSP (PR 1)

Behavior for users is unchanged: the default stays `SolverMode = Heuristic`.

1. Apply patches 0001, 0002, and 0004 with their content unchanged.
2. `SchedulingSolverMode` = `Heuristic` (default) | `Csp` | `MiniRouting`.
   - `MiniRouting` comes with patch 0001. It is the routing engine the CSP builds on and stays
     experimental: it is not part of the gate and is never made the default by this work.
3. Bind `SchedulingGeneration:SolverMode` and `SchedulingGeneration:Csp:*` from configuration.
   The value is set in `appsettings.json` explicitly as `Heuristic`, so switching needs no code
   change. The existing options validator fails startup on:
   - an invalid enum value;
   - a non-positive `Csp:MaxNodes`, `Csp:TimeLimitMilliseconds`, `Csp:MaxOptionalDomainSize`, or
     `Csp:FinalCandidatesToValidate`;
   - a non-positive `Csp:MaxStops` when it is set;
   - a non-positive `MiniRouting:MaxSkipPenaltyMinutes`, `MaxGlsIterations`,
     `TimeLimitMilliseconds`, `MaxOrOptSegmentLength`, or `FinalCandidatesToValidate`, or a
     negative or non-finite `MiniRouting:GlsLambdaFactor`. The CSP uses these for its skip penalty
     and its optional polish.
4. Add `tools/benchmarks/solver-comparison`, a console runner outside the solution, following
   `tools/benchmarks/optional-route-optimization`. The runner's output is audit evidence:
   - **Data files:** the per-scenario `<base>.csv`, the per-run `<base>.timings.csv` (every
     measured sample), and `<base>.meta.json` (source hash, environment, options, corpus,
     iterations). The Markdown report is a pure function of these three files.
   - **Consistency check:** `SolverComparisonReportTests` re-renders every committed report from
     its data files and fails on any difference. Reading, writing and rendering all go through
     `SolverComparisonReport.Validate`, which rejects malformed or surplus data, so no persisted
     sample can be silently left out of a statistic:
     - the metadata date matches the file name;
     - the source hash is a full lowercase SHA-256 over at least one file;
     - there is at least one warmup and one measured iteration;
     - the declared corpus is one the runner can produce:
       - candidate counts, mandatory counts and rest preferences are non-empty and free of
         duplicates;
       - every candidate count is positive, every mandatory count is non-negative, and every rest
         preference is a `RestPreference` value;
       - seeds are positive;
       - the named-fixture count is 0 or all of them;
     - the scenarios are exactly the declared corpus. `SolverComparisonCorpus` is the one corpus
       definition: the runner generates from it, and the validator rebuilds the expected descriptors
       from the metadata through it. Set equality is required: no missing, extra, duplicate or
       substituted scenario, and each scenario has the segment, rest preference and mandatory count
       that the corpus assigns it;
     - every scenario has a known verdict and an outcome that its mode can produce, and every CSP
       plan respects `MaxStops`;
     - every timing row has a known mode and an outcome allowed for that mode, a finite non-negative
       elapsed time, an existing scenario with the same segment, and an iteration in
       1–`MeasuredIterations`; each (scenario, mode, iteration) appears exactly once, so the row
       count is exactly scenarios × 3 × `MeasuredIterations`;
     - the latest report's source hash equals the hash of the benchmarked source files in the
       repository (see Reproducibility).

     Parameterized tests corrupt a valid data set in each of these ways and require the read to
     fail.
   - **Outcome:** recorded by the service itself on the same run, through
     `SchedulingSolverDiagnostics` (activity `SchedulingSolverOutcome`, tag `solver.outcome`), not
     inferred from solver statistics. A CSP run that finds candidates but fails final validation
     is counted as `heuristic_fallback_csp_no_valid_candidate`.
   - **Reproducibility:** the runner records a SHA-256 over the files that determine the results
     (`SolverComparisonReport.SourceInputs`: the scheduling `Common`, `Csp` and `Routing` folders,
     the corpus fixture, the product-rule comparison and the runner), with line endings
     normalized to LF. `SolverComparisonReportTests` recomputes the hash and fails, with the
     regeneration command, when the latest report no longer matches the source. The check needs no
     git history, so it holds after a squash or rebase merge and in a source archive. Any change to
     those files therefore needs a regenerated report in the same PR. Reports are named by date;
     `--label=<a-z0-9>` appends a suffix, so a second report of the same day does not overwrite a
     committed one. The ordinal order of the names is still chronological.
   - **Corpus:** every named `OptionalRouteOptimizationScenarios` fixture (rest preference
     `None`), plus synthetic scenarios using the approved UC-10 segments: candidates
     {10, 20, 40} × mandatory {0, 1, 3, 6} × rest preference {`None`, `Auto`, `Frequent`} ×
     seeds 1–25. `Auto` is the mobile app's default. The rest preference does not change the
     generated instance, and the report states the results per rest preference.
   - **Per scenario:** compare the CSP plan with the heuristic plan using the product rule above,
     computed from the generated plans, and classify the result as better, equal, or worse.
   - **Also reported, per segment:**
     - optional count, travel minutes, total duration;
     - latency p50/p95/max for three modes: optimization disabled, heuristic, and CSP;
     - how many CSP runs ended on the node budget and how many fell back to the heuristic.
   - **Output:** `docs/benchmarks/UC-10-csp-solver-comparison-<date>.md`, plus the three data
     files above.
   - **Disclosures** (required by the replay spec):
     - corpus type (synthetic only) and generator seeds;
     - sample size and excluded scenarios;
     - code commit, runtime, OS, CPU, and options;
     - the statement that conclusions generalize only to the tested synthetic distribution and
       are generator-only, not end-to-end latency.
5. Commit the first benchmark report (baseline with patch defaults) as report evidence.
6. Every plan the CSP returns respects its configured `MaxStops` and `MaxOptionalDomainSize`:
   - the initial incumbent is built only from the root search domain and within `MaxStops`;
   - every state is checked against the same limits before it enters the elite set or sets the
     branch-and-bound upper bound. This includes states from the optional local-search polish.
7. Rest stops. The CSP model has no rest stops; `ItineraryScheduleEvaluator` inserts them
   (`Auto`: one rest after 150 continuous visit minutes when at least 300 minutes are available;
   `Frequent`: a rest after every 120 continuous visit minutes).
   - When rest applies, the solver searches two models that share one `Csp:MaxNodes` budget:
     first a model whose horizon reserves rest time, then the relaxed model without the reserve.
     The relaxed run starts with the evaluated objective of the best schedule already found as its
     upper bound, so it only looks for better schedules.
   - The `Csp:FinalCandidatesToValidate` best solutions of each run go through the evaluator, and
     the schedule with the lowest evaluated objective wins: matrix travel minutes including legs
     to rest stops, plus the penalty of skipped optional POIs. Without rest, the two objectives are
     equal and the first valid solution is kept, as before.
   - Only the relaxed model may prove infeasibility.
   - Known limit, tracked as Phase 2 R9: the schedule that is optimal after rest insertion can
     still lie outside the evaluated solutions, so a completed search is optimal only without
     rest.

### Phase 1 review remediation

Review round 1 of PR #57 (head `d4a4992`) found two blocking issues and two non-blocking ones.

| Finding | Resolution |
| ------- | ---------- |
| MEDIUM 1: the CSP initial incumbent, built by `MiniRoutingSolver.Construct`, ignored `MaxStops` and `MaxOptionalDomainSize`, entered the elite set, and set the pruning bound. The benchmark had 95 plans with 11 visits under `MaxStops = 10`. | `Construct` takes the allowed optional nodes and a stop limit. MiniRouting still passes all optional nodes and no limit, so its behavior is unchanged. The CSP passes its root domain and `MaxStops`, and `SearchRun.WithinLimits` guards the incumbent and the polish. Regression tests cover restrictive limits (`MaxStops` = mandatory + 2, `MaxOptionalDomainSize = 3`) with the incumbent on and off and with polish. They also check that the incumbent never changes the optimum when the search completes. Phase 1 item 6. |
| MEDIUM 2: the benchmark was not reproducible. It cited an unreachable commit, persisted only per-scenario max timing, and inferred fallbacks from `SolutionsFound == 0`. | Phase 1 item 4: full SHA with a dirty-tree refusal, persisted timing samples, an outcome recorded by the service, a consistency test, and an environment disclosure. The report was regenerated after the MEDIUM 1 fix. |
| LOW 1: the spec said `SolverMode` applies to Adjust Items. | Corrected in [Affected flows](#affected-flows). |
| LOW 2: the plan had a stale approval status and branch name. | Updated in the plan and in this status. |

Review round 2 (head `7019457`) confirmed MEDIUM 1 and LOW 1 as fixed and found one remaining
blocking issue.

| Finding | Resolution |
| ------- | ---------- |
| MEDIUM 2 (partially fixed): the benchmark validation accepted unknown modes and outcomes, negative or non-finite elapsed times, surplus, duplicate or missing timing rows, mismatched segments and out-of-range iterations, so an extra row could be silently excluded from the latency statistics. | `SolverComparisonReport.Validate` enforces the rules in Phase 1 item 4 on read, write and render, and `EnsureProvenance` requires an ancestor commit and a clean tree. Parameterized tests cover each malformed case. The committed corpus passes unchanged, so the report was not regenerated. |
| LOW 2 (partially fixed): the plan's commit table stopped at `d4a4992`. | The table lists the remediation commits `96c0eb8` and `7019457`. |

Review round 3, an independent algorithm review of head `94995d9`, found one high, three medium
and four low issues. It confirmed that forward checking, `RemainingMandatoryFit` and `LowerBound`
are admissible against brute force without rest.

| Finding | Resolution |
| ------- | ---------- |
| HIGH: the rest reserve made the CSP non-optimal. The relaxed run started only when the reserved model found nothing, so a better schedule outside the reserved horizon was never considered, and the `SearchCompleted` comment claimed optimality. The benchmark used only `RestPreference.None`, while the mobile default is `Auto`. | Phase 1 item 7: both models run whenever rest applies, and the final choice uses the evaluated objective. `CspRestOptimalityTests` compares the CSP with an evaluator-based brute force on 80 random non-metric instances per preference. Schedules worse than the optimum went from 6/77 to 0/77 (`Auto`) and from 23/69 to 1/69 (`Frequent`), with no feasible instance left without a CSP schedule. The `SearchCompleted` comment states that optimality holds only without rest. The benchmark corpus adds `Auto` and `Frequent`, reported separately (Phase 1 item 4). The remaining gap is Phase 2 R9. |
| MEDIUM 1: the provenance test required the benchmarked commit to be an ancestor of `HEAD`, so a squash or rebase merge, or a source archive without `.git`, failed it permanently. | The report records a hash of the benchmarked source files instead of a commit (Phase 1 item 4, Reproducibility). The git-based provenance check is removed, and `ci.yml` no longer needs `fetch-depth: 0`. |
| MEDIUM 2: the wall-clock limit breaks determinism when it fires, and the relaxed re-run got a fresh node budget. | Both runs share one `Csp:MaxNodes` budget. The time limit stays a safety net; when it fires the result is host-dependent. The report states this, and Phase 2 R5 removes the dependency. |
| MEDIUM 3: a node-limited CSP plan is used without comparing it with the heuristic. | Kept while the CSP is opt-in. Phase 2 R1a (heuristic incumbent) makes the CSP never worse than the heuristic. |
| LOW: in CSP mode the `OptionalRouteOptimization` activity had a baseline equal to the plan, so its improvement counters were always 0, and `EvaluationsCount` held the node count. | A CSP plan no longer records that activity; its statistics are in `SchedulingSolverOutcome`, which also gains `csp.evaluator_calls` and `csp.chose_relaxed_rest_model`. `EvaluationsCount` holds evaluator calls. |
| LOW: the CSP ignores `EnableOptionalRouteOptimization = false`. | Already tracked as Phase 2 R4. |
| LOW: the options validator did not check `MiniRouting.*`, `Csp:FinalCandidatesToValidate` or `MaxSkipPenaltyMinutes`. | Validated (Phase 1 item 3), with tests. |
| LOW: per-node allocations in `LowerBound`, `RouteEliteSet.Offer` and `OrderValues`. | `LowerBound` reuses its buffers, `Offer` checks the objective before it builds the key, and `OrderValues` sorts an array with a comparer that keeps the same order. |

Review round 4, an independent final re-review of head `ebd90a9`, found one blocking issue and one
non-blocking issue. It confirmed every other Phase 1 finding as fixed or tracked for Phase 2.

| Finding | Resolution |
| ------- | ---------- |
| MEDIUM (Phase 1 blocker): validation compared only the scenario count with the metadata. A different corpus with the same cardinality passed. Example: metadata `rest_preferences: ["Auto"]` over a `None` row, or a substituted candidate count, mandatory count or seed. | `SolverComparisonCorpus` is the single corpus definition, used by the runner and by `Validate`. `Validate` rebuilds the expected descriptors from the metadata and requires set equality, including each scenario's segment, rest preference and mandatory count. It also rejects corpus declarations the runner cannot produce. Mutation tests cover same-cardinality substitutions (rest preference, candidate count, mandatory count, seed), per-row mismatches, missing and extra members, duplicate and invalid declared values, and empty lists. With the membership check removed, nine of them fail. The corpus file is part of `SourceInputs`. |
| LOW: `NodesExpanded` and the shared budget were incremented before the limit check, so diagnostics could report up to `MaxNodes + 2`. | `TryExpand` checks the node budget and the time limit before it counts a node. `NodesExpanded` equals `MaxNodes` exactly when the node limit stops the search, in one run or across the reserved and relaxed runs, and the tests assert equality. |

A developer brute-force check after round 3 (`CspBruteForceTests`) found one more defect. It was
missed by round 3's own brute-force comparison, and by `ExhaustiveBest`, for the same reason the
CSP missed it.

| Finding | Resolution |
| ------- | ---------- |
| The CSP pruned every partial route whose last stop could not return directly to the end in time. On a non-metric matrix, a later stop on a shortcut can still reach the end in time. The CSP missed such routes, returned a worse schedule, or wrongly reported `ProvedInfeasible`. The brute-force oracles pruned at the same point, so they agreed with the CSP. Example: on random seed 10 the CSP returned 391 against an optimum of 242. | The search extends open prefixes (`RouteState.BuildOpen`). A new value is pruned only by constraints that no later stop can repair: C1–C4, plus C5 against the admissible return lower bound `minExit` (direct return, or the shortest hop to another domain stop plus the shortest visit and return). A state counts as a solution only when it returns directly in time (`RouteState.Close`). Open prefixes reject `CanInsert` and `CanRemove`, so other solvers are unaffected. Covered by `Solve_FindsTheRoute_WhenOnlyAShortcutThroughAnotherStopReachesTheEndInTime` and by `CspBruteForceTests`, which compares against a non-pruning brute force on 1,000 random non-metric instances. `ExhaustiveBest` no longer prunes. The rest-aware comparison stays skipped (29/600 instances, Phase 2 R9). |

An independent review of head `29a2989` found no Phase 1 blocker. It confirmed the open-prefix
fix: with a non-pruning brute force, `94995d9` lost the optimum in 2 of 301 restricted-limit cases,
and `29a2989` in none. Two changes followed. The rest are tracked for Phase 2.

| Finding | Resolution |
| ------- | ---------- |
| Medium: the rest model is still not optimal after rest insertion. | Phase 2 R9, with the review's evidence and bound hypothesis recorded there. |
| Medium: the wall-clock limit still ends the search and makes the result host-dependent. Under heavy parallel load, `Solve_IsDeterministic_ForTheSameInput(40, 4, 62)` reached it and failed. | The algorithm change stays Phase 2 R5, alongside the Report 3 V2 timeout gap. The determinism test now sets a 600 s safety limit, because it checks node-budget determinism and must not depend on runner speed. |
| Low: `state.Close()` rebuilds the full state (O(n), one allocation) at every node that already has all mandatory POIs. | Phase 2. It is a cost, not a correctness issue, and changing the solver requires a new benchmark. Candidate fix: test `ReturnsInTime` on the open state first and build the closed state only for accepted solutions. |
| Low: node-limited results are not compared with the heuristic, and `EnableOptionalRouteOptimization = false` is ignored. | Phase 2 R1a and R4. The CSP stays opt-in until both are done. |
| Low: the Phase 3 latency gate is not met. The committed run misses c40-m6 and c40-m6-frequent; the reviewer's run missed six segments. | Phase 3 gate. |
| Test suggestion: a fixed, generator-free regression for the shortcut defect. | Added as `Solve_FindsTheRouteThatOnlyReachesTheEndThroughAnOptionalPoi`. It fails on `ebd90a9` with a null schedule and passes on `29a2989`. |

### Report 3 V2 compliance — timeout aligned; fallback amendment pending

Report 3 V2 (the canonical SRS) specifies two things for the CSP engine:
- a hard timeout of 2,500 ms;
- a nearest-neighbor fallback.

Developer decision, 2026-10-11 (R5, hybrid option):

- **Timeout — aligned.** `Csp:TimeLimitMilliseconds` defaults to 2,500 ms, the Report 3 V2
  value. It is the wall-clock safety limit.
  - The node budget stays the primary stop, so results are deterministic as long as the limit
    does not fire. The deterministic-output invariant describes what a slow host returns.
  - After the allocation-free search, a full budget takes a few hundred milliseconds, so the
    limit fires only on an overloaded host.
  - Since the Phase 3 review it is one hard deadline for the whole solve: the search checks it at
    every node, every evaluator call is checked before and after and receives a token cancelled
    at the deadline, and polish and the final evaluation stop at it too. Before, only the search
    checked the clock, every 256 nodes (see
    [Phase 3 review remediation](#phase-3-review-remediation-review-of-d390462)).
- **Fallback — differs, amendment pending.** When the limit fires, the partial CSP result is
  discarded. The heuristic result that R1a already computed is returned unchanged, and the
  outcome is recorded as `heuristic_fallback_csp_time_limit`.
  - The returned plan therefore does not depend on when the search stopped.
  - The fallback costs nothing extra.
  - The heuristic is the production algorithm, and it is at least as good as nearest-neighbor.
    A nearest-neighbor fallback would give users a worse plan than today's exactly when the host
    is slow.
- **Action needed outside the codebase:** amend the fallback sentence of Report 3 V2 through the
  report's own change process, from "Nearest-Neighbor fallback" to "fallback to the existing
  heuristic plan", with the rationale above. This specification does not override the SRS.
  Until the amendment is accepted, the fallback algorithm is a tracked gap that blocks the
  Phase 3 default switch.
- **Limitation:** when the limit fires and the heuristic found no schedule, the request is
  reported infeasible, even if the discarded partial CSP result held a schedule. Determinism is
  preferred here. On an overloaded host, a partial result is not reproducible.

Report 3 V2 also describes one-day itineraries in some sections, while the detailed UC-10
requirements use a date range. That inconsistency predates this work, is not introduced by it,
and is tracked separately.

## Phase 2 — CSP follows the product rule (PR 2)

Phase 2 is split into two PRs (developer decision, 2026-10-11), both from
`feature/phuctv-csp-solver` after PR #57 merged as `983a192`:
- **PR 2a:**
  - R4, R1, R1a, R2 and R3, so the CSP follows the product rule and is never worse than the
    heuristic;
  - the allocation-free search and the `Close()` cost (P2.6);
  - R5 (hybrid option) and the node-budget decision, which the developer asked to land together
    with PR 2a;
- **PR 2b:** R8, R9 sign-off, R6, P2.3c and the full suite under `SolverMode = Csp`.

### PR 2a status — merged in PR #62

- **R4:** `ItineraryGenerationService` uses an effective solver mode, which is `Heuristic`
  whenever `EnableOptionalRouteOptimization = false`, for both CSP and MiniRouting. The redundant
  mode check in `TrySolveWithMiniRouting` is removed.
- **R1:** `CspProductRule` applies the product rule to the model, with an inclusion bitset of any
  size (64 ranks per word, highest rank in the top bit, so unsigned word comparison is
  lexicographic). It ranks the elite set and the incumbent. The final choice evaluates the
  incumbent and the elite of each run, and picks with `ScheduleGlobalComparator` itself.
  - **Admissible bound:** the optimistic inclusion vector is the current inclusion plus a greedy,
    rank-ordered pick of the domain optionals that still fit the remaining time, budget and slots
    (each optional costs at least its shortest incoming leg plus its visit). Mandatory POIs still
    missing are reserved first. Under a lexicographic order, that greedy pick is the maximal
    vector of the relaxation.
  - **Pruning:** a branch is pruned when its optimistic vector is worse than the incumbent's. When
    the vectors are equal, it is pruned only when the travel lower bound strictly exceeds the
    incumbent's travel, because a travel tie can still win on duration or visit IDs. The lower
    bound is the travel so far, plus the shortest incoming leg of every mandatory POI and optional
    the tie requires, plus the shortest return.
  - **Domain order:** the domain is kept in canonical rank order, and forward checking preserves
    that order.
- **R1a:** in CSP mode the service runs the heuristic first and passes its schedule to
  `CspItinerarySolver.Solve`.
  - The schedule becomes the initial incumbent and a final candidate only when it is within the
    CSP limits (`MaxStops`, domain), as Phase 1 item 6 requires for any incumbent
    (`CspStatistics.InitialIncumbentAccepted`, `ReturnedInitialIncumbent`).
  - When the CSP returns no schedule, the service reuses that heuristic result instead of running
    the heuristic a second time.
  - The cheapest-insertion incumbent and `Csp:UseInitialIncumbent` are removed, so
    `MiniRoutingSolver.Construct` is back to its pre-Phase-1 form.
- **Incumbent with rest (R9 groundwork):** when rest applies, a solution tightens the incumbent
  only after it passes the evaluator. This is the review hypothesis for R9. It is capped at 256
  in-search evaluator calls per solve. Without rest, the model equals the evaluated schedule, and
  solutions tighten the incumbent directly.
- **R2, R3:** `Csp:MaxStops` and `Csp:MaxOptionalDomainSize` are nullable and null by default:
  there is no stop cap, and every optional the heuristic considers is in the domain. When set,
  each must be positive. Benchmark metadata stores a `SolverComparisonCspSettings` snapshot, so
  reports written before Phase 2 still read and re-render word for word.
- **Tests:**
  - The brute-force oracles now compare schedules with `ScheduleGlobalComparator`
    (`CspProductRuleOracle`, which never prunes prefixes and evaluates every model-feasible route).
    The CSP matches them on 600 random non-metric instances without rest, on 400 under restrictive
    limits with and without the heuristic incumbent, and on the fixed small instances.
  - New tests cover:
    - R4 for CSP and MiniRouting;
    - never worse than the heuristic, on named fixtures and synthetic scenarios, with `MaxNodes`
      1 and 200,000;
    - a higher-ranked optional never traded for travel;
    - the canonical ranking tie-break;
    - more than 10 stops when the heuristic schedules 11;
    - the heuristic incumbent not changing the optimum.
  - Rest optimality against an evaluator brute force (`CspRestOptimalityTests`): `Auto` 77/77 and
    `Frequent` 69/69 optimal, with thresholds pinned at 0.
  - The rest-aware brute force (`CspBruteForceTests.Solve_MatchesEvaluatorAwareOptimum_WithAutoAndFrequentRest`,
    600 instances) now passes and is no longer skipped.
- **Benchmark:** `docs/benchmarks/UC-10-csp-solver-comparison-2026-10-11-pr2a.md`. The runner's
  new `--label` keeps it from overwriting the Phase 1 report of the same day. Over 907 scenarios:
  - **Quality gate passed:** the CSP is better than or equal to the heuristic in all 907, against
    621 worse before PR 2a:

    | Rest | Better | Equal |
    | ---- | -----: | ----: |
    | `None` | 219 | 88 |
    | `Auto` | 223 | 77 |
    | `Frequent` | 32 | 268 |

  - **No regressions:** no fallback, and no run reached the wall-clock limit.
  - **Stops:** the largest CSP plan has 11 visits.
  - **Latency gate passed:** the largest added p95 in the committed run is 144 ms
    (c40-m6-auto). An earlier run of the same code peaked at 211 ms, because latency moves with
    host load. Before the search optimization below, c40-m0 failed at 306.7 ms.
  - **Node budget:** with the wider domain and no stop cap, 651 of 907 runs still end on the
    200,000-node budget, so latency is set by that budget and by the cost per node. The budget is
    unchanged.
- **R5 (hybrid option):** `Csp:TimeLimitMilliseconds` defaults to 2,500 ms. When the limit fires
  in either run, `CspItinerarySolver` returns no schedule. The service then returns the heuristic
  result unchanged, the one R1a already computed, with outcome
  `heuristic_fallback_csp_time_limit`.
  - Tests use a 0 ms limit, so the first clock check fires regardless of host speed. They check
    that the solver discards its result and that the service plan equals the heuristic-mode plan
    exactly.
  - See [Report 3 V2 compliance](#report-3-v2-compliance--timeout-aligned-fallback-amendment-pending).
- **Node budget kept at 200,000 (developer criteria, 2026-10-11):** a 300,000 budget was adopted
  only if the largest added p95 stayed at or below about 250 ms, and the better count rose
  clearly. It met neither:

  | Budget | Better | Equal | Worse | Largest added p95 |
  | -----: | -----: | ----: | ----: | ----------------: |
  | 200,000 | 474 | 433 | 0 | 146 ms |
  | 300,000 | 493 | 414 | 0 | 248 ms |

  - The gain was only 19 scenarios moving from equal to better.
  - At 248 ms, latency sat on the edge of the margin. Repeated runs of the same 200,000-node code
    measured 146 ms and 211 ms, so host noise alone could push a 300,000 budget past the 300 ms
    gate.
- **Search without per-node allocation (P2.6, part of PR 2a):** the search keeps one shared
  current route and pushes a stop on the way down and pops it on backtrack. The shared state is:
  - position arrays for the sequence and departure times;
  - depth arrays for travel and cost;
  - the visited flags;
  - an inclusion bitset that is updated incrementally.

  Each depth reuses its own domain and value-order buffers. A solution is built into a
  `RouteState` only when it can enter the elite set. The completion check is a single addition
  (departure plus return travel within the horizon), which replaces `RouteState.Close()`. The
  open-prefix API is no longer needed, so `RouteState` is back to its pre-Phase-1 form.
  - The exploration order and every check are unchanged. The regenerated report matches the
    unoptimized PR 2a run in every plan, outcome and node count across all 907 scenarios (0
    differing rows). Only timings changed.
  - On the 25 c40-m0 scenarios, a solve took about 128 ms instead of 380 ms (1.56 million instead
    of 0.52 million nodes per second).

PR 2a is committed locally as `65b09f7`.

### PR 2b status — merged in PR #62

- **R8 — implemented.** `SchedulingSolverOutcome` carries `solver.kind`, mapped from the detailed
  `solver.outcome` (`SchedulingSolverKinds.Of`); it is not a second classification.
  - The mapping:

    | `solver.outcome` | `solver.kind` |
    | ---------------- | ------------- |
    | `heuristic`, and `heuristic_fallback_mini_routing` | `Heuristic` |
    | `csp` | `Csp` |
    | every `heuristic_fallback_csp_*`, including the time limit | `CspFallback` (acceptance criterion 7) |
    | `mini_routing` | `MiniRouting`, an extension for the experimental mode so it is never counted as `Csp` or `Heuristic` |
    | `infeasible` | no tag |

  - It also carries `csp.pruned_by_bound`, `csp.pruned_by_constraints`,
    `csp.pruned_by_forward_checking` and `csp.in_search_evaluation_limit_reached`.
  - Phase 3 adds `csp.returned_heuristic_plan` and `csp.tie_evaluation_limit_reached` (see
    [Phase 3 status](#phase-3-status)).
  - A test pins the allowed tag keys and checks that no value contains a POI name, so a tag that
    carries identity fails the build.
- **R9 — three findings, two fixed, one measured:**
  1. **Rest stops at real places.** `ItineraryScheduleEvaluator` inserts a rest POI (for example a
     cafe) between visits, so the real schedule travels a → rest → b. On a non-metric matrix that
     detour can be shorter and earlier than a → b. The relaxed model was therefore not a true
     relaxation: it rejected schedules the evaluator accepts.
     - A new test, with two cafes on shortcuts over 80 random instances per preference, found
       5/77 `Auto` and 3/69 `Frequent` CSP schedules worse than the evaluator optimum.
     - Fix: the relaxed run uses `RoutingContext.RestShortcuts`. Each leg after a visit takes the
       shorter of the direct leg and a detour through a rest POI, in both travel and time (the
       detour time includes the buffer and the 30-minute rest). Rest-POI opening hours and the
       allowed number of rests are ignored, which keeps it a relaxation. Every evaluator-accepted
       schedule is feasible in it, and its travel never exceeds the real travel.
     - Result: 0/77 and 0/69. On a metric matrix no detour is shorter, so nothing changes; the
       benchmark corpus has no rest POIs.
  2. **In-search evaluator cap.** With `Frequent`, rests push later arrivals past opening hours,
     so many model solutions fail evaluation and use up the cap. Measured on the 300 `Frequent`
     scenarios of the benchmark corpus:

     | Cap | Scenarios exhausting it | CSP better than heuristic | Mean solve |
     | --: | ----------------------: | ------------------------: | ---------: |
     | 256 | 279 | 32 | 74 ms |
     | 4,096 | 104 | 67 | 80 ms |
     | 16,384 | 12 | 80 | 86 ms |

     `Auto` exhausted 256 in only 6 of 300 scenarios. The cap is now 16,384. Exhaustion is
     reported as `CspStatistics.InSearchEvaluationLimitReached`.
  3. **Large instances — measured, not fixed.** On 8-scenario samples, 200,000-node results were
     compared with a 20,000,000-node "best known" run:

     | Sample | Same as best known | Worse | Best known proven optimal |
     | ------ | -----------------: | ----: | ------------------------- |
     | c20 without rest | 8/8 | 0 | yes |
     | c40 without rest | 1/8 | 7 | yes |
     | c40 `Auto` | 4/8 | 4 | 2/8 |
     | c40 `Frequent` | 8/8 | 0 | unknown |

     - With 20 candidates the CSP is optimal.
     - With 40 candidates it often stops short. The gap is a few travel minutes, or one optional
       traded for the next-ranked one. It is never worse than the heuristic (R1a).
     - **Decision (developer, 2026-10-11): accept the gap and document it (option d).** First,
       option a was tried: try optionals in canonical rank order before the travel-minus-benefit
       score.

       | Ordering | Same as best known (c40 without rest / c40 `Auto`) | Better than heuristic, 16 scenarios | Mean solve |
       | -------- | -------------------------------------------------: | ----------------------------------: | ---------: |
       | Current | 1/8 and 4/8 | 8 | 128 / 102 ms |
       | Rank-first | 1/8 and 4/8 | 6 | 138 / 102 ms |

       Rank-first was reverted. Most gaps are the same optional set with a few more travel
       minutes, so they come from ordering the stops, not from choosing them.
     - A tighter travel bound (option b) or dominance pruning (option c) remain possible later
       improvements. The 300,000 budget measurement already showed that a larger budget alone
       buys little.
- **Benchmark:** `docs/benchmarks/UC-10-csp-solver-comparison-2026-10-11-pr2b.md`. All gates pass.
  - **`Frequent`:** 80 better and 220 equal, against 32 and 268 in `pr2a`. That is 48 scenarios
    moving from equal to better.
  - **`None` and `Auto`:** unchanged, except 4 `Auto` scenarios. Those keep the same plan and
    verdict but complete in about 82,000 nodes instead of exhausting 200,000, because evaluated
    solutions tighten the incumbent sooner.
  - **No regressions:** 0 worse, 0 fallbacks, no time limit reached.
  - **Latency:** the largest added p95 is 279 ms (c40-m6). That is host noise: c40-m6 has
    identical plans and node counts to `pr2a`, where it measured 126 ms. In the same run every
    mode slowed, including the unchanged heuristic (p95 25 ms to 83 ms). Margins near the gate
    should be judged across repeated runs.
- **P2.5, without SQL Server — done.** The default was temporarily switched to
  `SolverMode = Csp` and the full suite run, then the default was restored:
  - **Failures:** only 3, against 12 before Phase 2. None was a defect:
    - the benchmark source-hash test failed because the temporary default edit changed the hashed
      source;
    - `GenerateAsync_RecordsActivitySourceDiagnostics` checks the heuristic-only
      `OptionalRouteOptimization` activity, which a CSP plan intentionally does not record
      (round 3);
    - `GenerationService_RecordsHeuristicOutcome_InTheDefaultMode` asserted the default mode.
  - **After pinning (R6):** with the temporary CSP default, every test except the hash artifact
    passes. That covers creation, Regenerate, Adjust Items, the TM-216 unit tests and the
    non-SQL integration tests.
- **P2.5, with SQL Server and Redis — done.** The integration suite was run against a temporary
  SQL Server 2022 container and a Redis container, first with the shipped default, then with the
  temporary CSP default plus `SchedulingGeneration__SolverMode=Csp`:

  | Run | Passed | Failed | Skipped |
  | --- | ------ | ------ | ------- |
  | Heuristic (shipped default) | 881 | 0 | 1 |
  | CSP | 880 | 1 | 1 |

  - The one CSP failure is `ShippedConfiguration_SelectsTheHeuristicSolverExplicitly`, which
    asserts the shipped configuration and fails by design when the configuration is overridden.
    Phase 3 updates it together with the default switch.
  - The skipped test is the opt-in Cloudinary smoke test in both runs.
  - The default was restored to `Heuristic` afterwards, and the source is unchanged.
- **R6 — done.**
  - Pinned to `SolverMode = Heuristic`, with expectations unchanged:
    - the four `GenerateAsync_Characterize_*` tests;
    - `GenerateAsync_RecordsActivitySourceDiagnostics`.
  - The default-mode outcome test now pins `Heuristic` explicitly
    (`GenerationService_RecordsHeuristicOutcome_InTheHeuristicMode`).
  - `Generate_OrdersOptionalsByEffectiveBeforeLegacyPreferenceScore` is about the legacy
    preference score, not the legacy solver, and is not pinned.
- **P2.3c — done for unit scope.** Added
  `GenerationService_ExcludesTheTopAiScoredOptional_WhenItIsTimeInfeasible`: under CSP, an
  optional with the top AI score that opens only after the itinerary must end is excluded, and the
  plan passes the invariant validator.

| ID | Requirement |
| -- | ----------- |
| R1 | The CSP incumbent, the B&B pruning, and the final choice all use the product rule. The bound must be admissible: a branch is pruned only when even its optimistic completion — the current inclusion vector plus every optional still in the domain — cannot beat the incumbent on inclusion, or ties it on inclusion and cannot beat it on travel. The inclusion vector supports any `MaxMatrixCandidates` value (it has no upper bound today), so no 64-bit mask limit is allowed. |
| R1a | The initial B&B incumbent is the heuristic optimizer's result for the same input, replacing the patch's cheapest-insertion incumbent. This is the standard heuristic upper bound: the CSP can only return a plan that is better than or equal to the heuristic under the product rule, even when it stops on the node budget, and the stronger bound prunes earlier. If the heuristic finds no schedule, the search starts without an incumbent. |
| R2 | No fixed stop cap by default. Stops are limited only by the hard constraints. `Csp:MaxStops` stays as an optional administrative limit and is null by default. |
| R3 | The CSP domain contains every optional POI the heuristic may consider (up to `MaxMatrixCandidates`). `Csp:MaxOptionalDomainSize` defaults to that value. |
| R4 | When `EnableOptionalRouteOptimization = false`, `SolverMode` is ignored and the existing unoptimized path runs. |
| R5 | The node budget is the only stop that shapes the result. UC-10 rejected wall-clock optimization timeouts, so if the wall-clock safety limit fires, the partial CSP result is discarded and the deterministic heuristic result is returned. The heuristic still runs before infeasibility is reported, and infeasibility reports the conflicting mandatory POIs. |
| R8 | The existing aggregate optimization diagnostic records which solver produced the plan. Allowed values are `Heuristic`, `Csp`, and `CspFallback`, plus node and prune counts. It never records user or POI identity. Phase 1 already records the detailed outcome in `SchedulingSolverDiagnostics`, for benchmark auditability (see Phase 1 item 4). R8 maps those outcomes to these three values; it does not add a second, divergent classification. |
| R6 | Existing tests that characterize heuristic-only behavior pin `SolverMode = Heuristic` explicitly. Their expectations are not edited. |
| R7 | Every CSP result passes `GeneratedItineraryInvariantValidator`, exactly like the heuristic. |
| R9 | With `Auto` or `Frequent` rest, the CSP result is optimal after rest insertion, or the remaining gap is measured and accepted by the developer. Candidate approaches: model `ItineraryScheduleEvaluator.NeedsRest` inside `RouteState`, or evaluate solutions inside the search with an evaluator-aware bound. `CspRestOptimalityTests` is the regression test; its thresholds may only go down. Evidence from the review of `29a2989`: an evaluator brute force over 381 solvable `Auto`/`Frequent` instances found 24 worse schedules (mean gap ~47) and 4 null results. Raising `FinalCandidatesToValidate` to 50 or 1000 only reached 20 worse, so top-K selection is not the main cause. Hypothesis to test first: `_bestObjective` is tightened by model-objective solutions that have not passed the evaluator, so branches with a better post-rest schedule are pruned. When rest applies, tighten the bound only from evaluated solutions, or search on the evaluated objective. The skipped `CspBruteForceTests.Solve_MatchesEvaluatorAwareOptimum_WithAutoAndFrequentRest` (29/600 worse at `29a2989`) is unskipped when this is met. |

## Phase 3 — gate for making CSP the default (separate small PR)

The CSP becomes the default only when one benchmark run on the current branch shows all of:

1. **Quality:** in 100% of benchmark scenarios, under every rest preference (`None`, `Auto`,
   `Frequent`), the CSP plan is better than or equal to the heuristic plan under the product rule.
2. **Latency** (CSP-specific amendment of the UC-10 gate, developer decision 2026-10-10):
   - In every corpus segment, CSP-mode generator-only p95 adds at most **300 ms** over the
     optimization-disabled p95 on the same host.
   - The heuristic keeps the original 100 ms gate.
   - No benchmark run may reach the wall-clock safety limit.
   - Rationale: generation already waits on OpenRouteService for seconds, so 0.3 s of solver time
     is negligible end to end. The CSP measured about 184 ms p95 before Phase 2.
3. **Tests:** the full solution suite passes with `SolverMode = Csp`, including SQL Server suites
   without skips. This covers creation, Regenerate, Adjust Items, and the TM-216 boundary tests.

The switch is a configuration change (`SchedulingGeneration:SolverMode = Csp`) with the heuristic
kept as fallback.

### Phase 3 status

Phase 3 runs on `feature/phuctv-csp-solver`, after PR #62 merged as `31f9a5a`.

The review of PR #62 (head `7f38cbc`) approved it with four Low findings. The developer chose to
merge, and to resolve them here before the default switch.

**Low 1 — fixed.** With rest, a solution that tied the incumbent on inclusion and model travel was
never evaluated. The K = 5 elite is ordered by model finish, which ignores rest, so the plan could
lose a later tie-break.
- **Fix:**
  - `Tighten` now also sends ties to the evaluator, under their own cap (`MaxTieEvaluations =
    4,096`). The cap is separate from the 16,384 evaluations for strict improvements.
  - The final choice compares these schedules under the full product rule.
  - `csp.tie_evaluation_limit_reached` records when the cap is reached.
- **Reproduced first:** `Solve_BreaksTiesByTheFullProductRule_WhenRestApplies` uses
  reviewer-style instances: 0–2 real rest places, non-metric matrices, opening hours and budgets.
  Over 2,000 seeds per preference it found 4 mismatches in 3,727 feasible instances:
  - two differed only in the visit-ID order (Auto 423, Frequent 400);
  - two were 10 and 24 minutes longer, with the same POIs and travel (Frequent 199, 1691). The
    gap therefore also reached the duration level, not only the visit-ID level the reviewer
    observed.
- **After the fix:** 0 mismatches in 11,148 feasible instances, over 6,000 seeds per preference.
- **Regression tests:** the four seeds are pinned in
  `Solve_FindsTheOptimum_OnTheTieBreakRegressions`, which fails when the tie cap is set to 0. The
  sweep keeps 500 seeds per preference, so CI stays fast.

**Low 2 — fixed.** When the CSP returned the heuristic plan itself, the outcome stayed `csp` and no
tag said so.
- **Fix:** `csp.returned_heuristic_plan` carries `ReturnedInitialIncumbent`.
- **Why it is reliable:** the heuristic plan is the first candidate, and the final choice keeps
  the earlier candidate on a tie. An identical CSP solution therefore also sets the flag.
- **Monitoring:** count CSP improvements as outcome `csp` with this tag false.

**Low 3 — wording fixed; measurement pending.** The output can still depend on host speed near the
2,500 ms limit, which the invariant did not allow for.
- **Wording:** corrected in the deterministic-output invariant, the R5 decision and acceptance
  criterion 5. Also corrected in the comments of `CspOptions.MaxNodes`,
  `CspOptions.TimeLimitMilliseconds` and `DiscardTimedOutResult`.
- **Measurement:** the fallback rate on production-like hardware is still to be measured; see
  below.

**Low 4 — pending.** The latency margin is to be checked with repeated benchmark runs; see below.
Large instances stay a documented limitation.

**Benchmark — all gates pass.** The report is
`docs/benchmarks/UC-10-csp-solver-comparison-2026-10-12-phase3.md`, for the source above.

| Rest preference | Better | Equal | Worse | In `pr2b` |
| --------------- | ------ | ----- | ----- | --------- |
| `None` | 219 | 88 | 0 | unchanged |
| `Auto` | 223 | 77 | 0 | unchanged |
| `Frequent` | 85 | 215 | 0 | 80 / 220 / 0 |

- **Changed plans:** only five, all Frequent c10 scenarios (s4 in four segments, and
  c10-m3-frequent-s24). Each moved from equal to better. They keep the same optional count,
  travel and duration, and now win on the end time or the visit-ID order. That is the Low 1
  effect, and no plan got worse.
- **No fallback,** and the wall-clock limit was never reached.
- **Latency:** the largest added p95 is 189 ms (c40-m6). The noisy `pr2b` run measured 279 ms. The
  rest segments, where ties are now evaluated, did not slow down.

**Repeated runs — the margin holds.** The full benchmark ran three times on the development host,
for the same source. Only the main run is committed.

| Run | Largest added p95 | Wall-clock fallbacks |
| --- | ----------------- | -------------------- |
| 1 (`phase3`) | 189 ms (c40-m6) | 0 |
| 2 | 149 ms (c40-m6-frequent) | 0 |
| 3 | 233 ms (c40-m6-auto) | 0 |

- Every run passed every gate.
- The fallback count covers 907 scenarios × 3 measured iterations per run.
- The worst segment moves between runs, which is host noise. Even the worst run keeps 67 ms below
  the 300 ms gate.

**Production-like measurement — one fallback, on 1 CPU with 4 concurrent generations.**
- **Setup:**
  - The runner ran in a Linux container (`mcr.microsoft.com/dotnet/sdk:10.0`) with a CPU quota
    of 1, 2 or 3 CPUs.
  - 1 or 4 runners started at once. Each runner stands in for one concurrent generation.
  - Each runner covered every segment with 5 seeds and 1 measured iteration: 187 scenarios, or
    374 CSP runs per runner and 5,610 in all.
- **Results:** each segment has only 5 samples, so the latency figures are indicative. The
  fallback count is what this measures.

  | CPUs | Concurrent | Largest CSP time | Largest added p95 | Wall-clock fallbacks |
  | ---- | ---------- | ---------------- | ----------------- | -------------------- |
  | 1 | 1 | 471 ms | 471 ms | 0 of 374 |
  | 1 | 4 | 2,206 ms | 2,206 ms | 1 of 1,496 (c20-m0-s1) |
  | 2 | 1 | 151 ms | 134 ms | 0 of 374 |
  | 2 | 4 | 404 ms | 375 ms | 0 of 1,496 |
  | 3 | 1 | 159 ms | 146 ms | 0 of 374 |
  | 3 | 4 | 288 ms | 288 ms | 0 of 1,496 |

- **Two or more CPUs:** no generation reached the limit, even with 4 at once.
- **One CPU with 4 at once:** one generation reached the limit (0.07%) and returned the heuristic
  plan, as designed.
- **Latency:**
  - On 1 CPU the CSP adds up to 2.2 s under concurrency.
  - The 300 ms development-host gate is not met on 1 CPU, or on 2 CPUs with 4 concurrent
    generations.
  - The heuristic also slows under contention, up to 200 ms p95 on 1 CPU with 4 at once.
- **Caveats:** the quota is enforced per 100 ms period, and each runner measured right after a
  single warmup. These numbers therefore lean pessimistic for a long-running server.

**Decision (developer, 2026-10-12): switch without waiting for the production hardware.**
- The production hardware is not known yet.
- The switch cannot make a plan worse than today's. On any host, the worst case is the heuristic
  plan after at most 2.5 s.
- Rolling back is configuration only: `SchedulingGeneration:SolverMode` and
  `SchedulingGeneration:Csp:MaxNodes` can differ per environment without touching the
  benchmarked source.
- The default therefore switches now, under the monitoring and rollback rules below.

**Default switch — implemented; merge after the Report 3 V2 amendment.**
- `appsettings.json` sets `SchedulingGeneration:SolverMode = Csp`. The code default stays
  `Heuristic`, so a missing setting falls back to the heuristic.
- `ShippedConfiguration_SelectsTheCspSolverExplicitly` pins both values.
- `README.md` documents the setting and how to roll back.
- **Full suite with the shipped `Csp` default, and no configuration override:**
  - unit suites: Application 1,788, Infrastructure 714, CandidatePoolReplay 13, all passing;
  - integration suite against SQL Server 2022 and Redis containers: 885 passed, 0 failed,
    1 skipped. The total includes the four monitoring tests. The skipped test is the opt-in
    Cloudinary smoke test, as in every earlier run.
  - In that run, the scheduling tests went through the real API pipeline and wrote 17 solver
    outcome log lines, all `csp`. The monitoring signal therefore works end to end.
- **Merge condition:** the switch merges only after the Report 3 V2 fallback amendment is
  accepted (see
  [Report 3 V2 compliance](#report-3-v2-compliance--timeout-aligned-fallback-amendment-pending)).

**Monitoring signal — fixed.** Before this, nothing in the API host listened to the
`SchedulingSolverOutcome` activity source, so its tags were recorded only in tests.
- **Fix:** `SchedulingSolverOutcomeLogger`, a hosted service in `TripMate.Api`, now writes one
  Information line per generation:

  ```text
  Scheduling solver outcome {SolverOutcome} (kind {SolverKind}, mode {SolverMode}): CSP elapsed {CspElapsedMs} ms,
  time limit reached {CspTimeLimitReached}, returned heuristic plan {CspReturnedHeuristicPlan}, nodes {CspNodesExpanded},
  node limit reached {CspNodeLimitReached}, evaluator calls {CspEvaluatorCalls},
  in-search evaluation limit reached {CspInSearchEvaluationLimitReached},
  tie evaluation limit reached {CspTieEvaluationLimitReached}.
  ```

  The last four fields were added after the Phase 3 review.

- **Content:** the fields are the aggregate R8 tags only, with no user or POI identity. The API
  project is outside the benchmarked source, so the benchmark is unchanged.
- **Tests:** `SchedulingSolverOutcomeLoggerTests` pins the fields and checks that logging stops
  with the host. `SchedulingSolverOutcomeLoggerRegistrationTests` checks that the host registers
  the service.
- **End-to-end time:** the existing ASP.NET Core `Request finished` line already gives the total
  time of a generation, for `POST /api/v1/scheduling-requests` and
  `POST /api/v1/itineraries/{id}/regenerate`. Compare it with `CspElapsedMs` to see the CSP's
  share.

**Operations — monitoring and rollback.** These are starting thresholds, to be confirmed once
production data exists. Every signal comes from the solver outcome log line above.

| Signal | Expected | Action |
| ------ | -------- | ------ |
| Share of outcome lines with `SolverOutcome = heuristic_fallback_csp_time_limit` | About 0% | Above 1% over a day: set `SchedulingGeneration__Csp__MaxNodes=100000` |
| p95 of `CspElapsedMs` | Below 300 ms on 2 or more vCPUs | Above 1,000 ms over a day: the same action |
| Either signal, after `MaxNodes` was lowered | — | Still above its threshold: set `SchedulingGeneration__SolverMode=Heuristic` |
| Share of `csp` lines with `CspNodeLimitReached = True` | Large instances only (c40 in the benchmark) | Informational: the plan is still never worse than the heuristic. A large share on small inputs means production instances are larger than the corpus; re-run the benchmark before raising `MaxNodes`. |
| Share of `csp` lines with `CspInSearchEvaluationLimitReached` or `CspTieEvaluationLimitReached = True` | Rare (Frequent rest only) | Informational: rest plans may miss a better tie-break or rest placement. A rising share is a reason to revisit the evaluator caps with the benchmark. |

- **What the CSP adds:** count lines with `SolverOutcome = csp` and
  `CspReturnedHeuristicPlan = False`.
- **Hardware guidance:**
  - 2 or more vCPUs are recommended.
  - On 1 vCPU, expect up to about 2 s of extra solver time when generations run concurrently.
  - On Free or Shared plans, keep `Heuristic`.

### Phase 3 review remediation (review of `d390462`)

The review of head `d390462` requested changes: two blockers for the default switch and three
further concerns. It also acknowledged that the incumbent/final-comparator design and the
907-scenario benchmark show the CSP is not worse than the heuristic for the tested configuration.

**Blocker 1 — fallback algorithm: amendment route chosen (developer decision, 2026-10-12).**
Report 3 V2 requires a nearest-neighbor fallback; the code returns the existing heuristic plan.
- **Decision:** amend Report 3 V2 rather than implement nearest-neighbor. The rationale is the one
  in [Report 3 V2 compliance](#report-3-v2-compliance--timeout-aligned-fallback-amendment-pending):
  the heuristic plan is already computed, costs nothing extra, and is at least as good as
  nearest-neighbor, so a nearest-neighbor fallback would hand users a worse plan exactly when the
  host is slow.
- **Proposed wording for the amendment:** "On timeout, the CSP engine falls back to the existing
  heuristic plan (the production algorithm before the CSP), which is computed before the CSP
  search starts."
- This is a change to the SRS through its own process, outside the codebase. The merge condition
  of the default switch is unchanged: it merges only after the amendment is accepted.

**Blocker 2 — the 2,500 ms limit was not a hard deadline: fixed.** Only the search checked the
clock, and only every 256 nodes. So:
- small trees, or `MaxNodes` below 256, never checked it;
- evaluator calls during the search, the elite evaluation after it and polish ran unbounded;
- polish recorded its own time limit in statistics that were then discarded, so the CSP returned
  a polished result cut short by the clock with `TimeLimitReached = false`.

Fix (`CspItinerarySolver`): one deadline per solve (`SolveDeadline`), on the solve's own stopwatch,
with a token linked to the caller's token and cancelled at the deadline.
- The search checks the deadline and the caller's cancellation at every node.
- Every evaluator call checks the deadline before and after, and receives the deadline token, so
  a running evaluation stops at the deadline (`ItineraryScheduleEvaluator` checks its token per
  stop). Cancellation by the caller still throws `OperationCanceledException`; only the deadline
  token counts as reaching the limit.
- Polish reads the local search's time-limit flag.
- A final check covers work that does not read the clock itself (building the models, between
  stages).
- Every case goes through the R5 path: the CSP result is discarded, `TimeLimitReached = true`, and
  the service returns the heuristic plan unchanged.
- The overrun past the deadline is now bounded by one node or one stop of an evaluation, instead
  of up to 255 nodes plus any number of evaluations.

Tests, in `CspItinerarySolverTests` (each failed before the fix, except the two cancellation
guards):

| Test | Scenario |
| --- | --- |
| `Solve_HonorsTheTimeLimit_WhenTheSearchStopsBeforeTheOldClockCheckInterval` | `MaxNodes = 100`, and a 3-candidate tree, with a 0 ms limit |
| `Solve_StopsRightAfterTheEvaluationThatCrossesTheTimeLimit_DuringTheSearch` | Frequent rest, 60 ms per evaluation, 100 ms limit: at most 2 evaluations (48 before the fix) |
| `Solve_DiscardsTheResult_WhenEvaluationAfterTheSearchCrossesTheTimeLimit` | No rest, one 300 ms elite evaluation, 200 ms limit |
| `Solve_InterruptsARunningEvaluation_AtTheTimeLimit` | An evaluation that waits on its token for up to 3 s returns at the 200 ms limit |
| `Solve_DiscardsThePolishedResult_WhenLocalSearchReachesTheTimeLimit` | Polish on, unbounded GLS iterations, 150 ms limit |
| `Solve_PropagatesCallerCancellation_DuringAnEvaluation` | Caller cancels during an evaluation: `OperationCanceledException`, not a time-limit result |
| `Solve_Throws_WhenTheCallerIsAlreadyCancelled_EvenForASmallTree` | Pre-cancelled caller, `MaxNodes = 100` |

The slow evaluator is simulated through an internal test hook, `BeforeEvaluation`, which runs
before each evaluator call with the token the evaluator receives. Production never sets it.

Benchmark after the fix: `docs/benchmarks/UC-10-csp-solver-comparison-2026-10-12-phase3deadline.md`,
regenerated after `dotnet format` for the new source hash.
- Every plan, node count and outcome is identical to the `phase3` report (the CSV columns up to
  `csp_time_limit` match on all 907 scenarios). Quality stays 219/88/0, 223/77/0 and 85/215/0.
- Checking the clock at every node costs nothing measurable: the largest added p95 is 159 ms
  (c40-m3), against 189 ms in `phase3` and 149–233 ms across its repeated runs.
- Every gate passes, and no run reached the wall-clock limit.

**Concern — the benchmark does not establish the SRS API latency or 200-CCU requirements:
accepted as a stated limitation.** The benchmark is synthetic and measures the generator only.
The production-like measurement above shows the 300 ms gate failing on 1 CPU, and on 2 CPUs with 4
concurrent generations, and the production hardware is unknown. The SRS API latency and
200-concurrent-user requirements need a load test of the API on the target hardware. That is
outside this PR and stays open; nothing in this specification claims those requirements are met.

**Concern — tie, in-search and node-limit signals were not logged: fixed.** The outcome log line
now carries `CspNodeLimitReached`, `CspEvaluatorCalls`, `CspInSearchEvaluationLimitReached` and
`CspTieEvaluationLimitReached` (template above), with matching rows in the monitoring table.
`SchedulingSolverOutcomeLoggerTests.LogsTheSearchLimitSignals` pins them.

**Concern — keep `Heuristic` as the shipped default until both blockers are resolved.** Blocker 2
is fixed in this PR. The default switch commit stays in the PR, and re-review is requested once
the Report 3 V2 amendment is accepted. If the PR must merge before that, the switch commit is
reverted and the switch ships later as its own change (developer decision, 2026-10-12).

## Non-goals

- Patch 0003 (TripMate AI providers) and the `Capstone_AI` service.
- Admin-editable CSP parameters through the algorithm-parameter configuration feature.
- A user-selectable itinerary pace.
- Any change to the request, response, persistence, or itinerary contracts.

## Acceptance criteria

1. Phase 1 merges with user-visible behavior unchanged; build, format, and the full test suite
   pass, with SQL Server enabled.
2. The benchmark runner reproduces the comparison table and classifies each scenario using the
   product rule.
3. After Phase 2, the 12 previously failing tests pass under `SolverMode = Csp` without editing
   their expectations, except tests that explicitly characterize legacy heuristic behavior (for
   example `..._Characterize_...InLegacy`). Those are pinned to `Heuristic` (R6).
4. After Phase 2, the benchmark meets the Phase 3 quality and latency gate, or the report states
   exactly which scenarios fail it.
5. CSP and heuristic results both pass the existing invariant validator. CSP results are
   deterministic for identical input whenever the wall-clock safety limit is not reached (see the
   deterministic-output invariant).
6. Under `SolverMode = Csp`, the creation, Regenerate, and Adjust Items suites and the TM-216
   boundary tests pass.
7. When the wall-clock safety limit fires, the returned itinerary equals the heuristic result
   exactly and is diagnosed as `CspFallback`.
8. The aggregate diagnostic names the solver used and never contains user or POI identity.
