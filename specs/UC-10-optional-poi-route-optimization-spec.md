# UC-10 Optional POI Route Optimization Specification

## Status

Implemented locally; functional verification is green and the approved performance gate passes the
recorded synthetic benchmark. Representative-corpus reporting remains follow-up evidence. See
`docs/benchmarks/UC-10-optional-route-optimization-2026-10-07.md`.

## Problem

`ItineraryGenerationService.GenerateAsync` currently sorts optional POIs once by the canonical
ranking/tie-break chain and evaluates them sequentially. Every feasible optional is appended after
the current route tail. A candidate rejected at that point is never reconsidered, and the completed
visit order receives no route-improvement pass.

This behavior preserves hard constraints, but it can produce avoidable backtracking/zigzag travel
or reject a useful optional POI that would fit at another position. The repository proves the
algorithmic limitation; the frequency and user impact in production remain benchmark questions.

## Goals

1. Preserve ranking as the authority for optional-POI admission priority.
2. For each optional candidate, evaluate every insertion position in the current visit sequence and
   choose the best feasible position by incremental travel cost.
3. Reconsider previously skipped candidates after route improvement can change feasibility.
4. Improve the selected route with deterministic, bounded 2-opt and relocate moves.
5. Rebuild and revalidate the complete timeline after every proposed order change, including
   opening hours, budget, rest behavior, horizon, and final endpoint.
6. Validate every returned plan through an independent invariant validator.
7. Record the comparable objective before and after optimization without exposing POI/user data.
8. Keep generation deterministic and bound CPU by a fixed evaluation budget.

## Non-goals

- No change to POI ranking weights, AI ranking, provider-pool membership, matrix-candidate cap, or
  route-duration provider calls.
- No replacement with a global optimizer, MILP/CP-SAT solver, genetic algorithm, or external route
  optimization service.
- No pure nearest-neighbor route construction.
- No change to `GenerateFixedOrderAsync` ordering; an explicitly adjusted order remains exact.
- No change to opening-hours representation, overnight-hours support, budget semantics, rest-policy
  semantics, transition/final buffers, or endpoint semantics.
- No removal of an already selected higher-priority optional merely to admit lower-priority POIs.
- No public request/response, HTTP status, database schema, or EF migration change.
- No claim of production UX improvement without corpus and load evidence.

## Required Existing Invariants

The optimized path must preserve all current generation rules:

1. Every requested mandatory POI appears exactly once as a visit and is marked mandatory.
2. A mandatory POI with unavailable hours, unknown cost under a budget, or other infeasibility
   continues to produce `planning.constraints_infeasible`.
3. Optional POIs with unknown cost are excluded when a budget is present.
4. Visit arrival/departure must fit the same-day opening interval after local-time conversion.
5. Estimated visit cost never exceeds the request budget.
6. Rest insertion follows the existing `None`, `Auto`, and `Frequent` rules and existing qualified
   rest-POI selection semantics.
7. Transition and final-return buffers remain unchanged.
8. The route reaches the requested end point within `AvailableMinutes`.
9. A plan contains at least one visit and contains no duplicate visit POI.
10. Matrix lookup remains directional; no symmetry or triangle inequality may be assumed.
11. `GenerateFixedOrderAsync` returns visits in exactly `orderedVisitPoiIds` order.
12. The route-duration matrix is requested exactly once per generation attempt.

## Canonical Optional Priority

Optional candidates retain the current frozen ordering:

1. `EffectiveDesirabilityScore` descending;
2. `ScenicScoreForRanking` descending, null last;
3. `PhotoRatingForRanking` descending, null last;
4. matrix minutes from the start ascending;
5. `EstimatedVisitCostForRanking` ascending, null last;
6. POI ID ascending.

This ordering controls **admission priority**, not final geographic visit order. A lower-ranked
candidate must not evict a feasible, already admitted higher-ranked candidate. Legacy
`PreferenceScore`, mutable raw scenic/photo fields, and input enumeration order must not replace
the canonical chain.

## Technical Design

### 1. Separate visit order from schedule evaluation

Refactor the current mixed construction loop into two concepts:

- a visit sequence containing only candidate IDs/candidates;
- a schedule evaluator that deterministically rebuilds arrival/departure times, buffers, cost,
  rest items, end arrival, duration, and travel objective for one complete visit sequence.

Every proposed insertion, 2-opt reversal, or relocation is accepted only if the schedule evaluator
can build a complete feasible plan. Existing feasibility helpers may be reused inside the evaluator,
but no move may edit timestamps/rest items in place or rely only on travel delta.

The evaluator must use precomputed candidate-to-matrix-index lookup rather than repeated linear
`Select(...).Single(...)` searches.

### 2. Legacy baseline and optimization seeds

The existing tail-append construction remains available as the deterministic baseline and feature-
flag fallback. It is evaluated for every mandatory permutation exactly as today.

To bound work when there can be up to six mandatory POIs (720 permutations), choose at most
`MaxRouteOptimizationSeeds` feasible baseline routes using the quality comparator below. The
default is 3. Ties use the visit-ID sequence, so seed selection never depends on hash/input order.

The final result is selected from all feasible legacy baselines and optimized seeds. The optimizer
therefore cannot make the returned result worse than its own legacy baseline under the declared
comparator, and budget exhaustion always leaves a valid fallback.

### 3. Cheapest feasible insertion

Starting from a seed's mandatory visit order, process optional candidates in canonical priority.
For one candidate:

1. Generate every insertion slot from before the first visit through after the last visit.
2. Compute directional added travel:

```text
travel(previous, candidate) + travel(candidate, next) - travel(previous, next)
```

   where `previous`/`next` may be the request start/end.
3. Fully evaluate every proposed sequence; discard infeasible proposals.
4. Select the feasible proposal by:
   - lowest added travel;
   - lowest resulting total travel;
   - earliest final end time;
   - lowest insertion position;
   - lexicographically lowest visit-ID sequence.
5. Admit the candidate at that position, or retain it in a skipped list when no slot is feasible.

No Euclidean/haversine proxy replaces the route matrix. A candidate is never admitted solely
because the local delta is good; the complete schedule must remain feasible.

### 4. Bounded local improvement

After the first insertion pass, improve only the order of the selected visit set:

- **2-opt:** reverse each eligible contiguous visit segment;
- **relocate:** remove one selected visit and insert it at every other position.

Moves may reorder mandatory and optional visits because `GenerateAsync` has no caller-specified
visit order. They may not add/remove a visit or change mandatory classification. Every proposal is
fully rescheduled and validated.

Use deterministic best improvement. For each pass, enumerate 2-opt then relocate proposals in
stable index order, select the best strict improvement under the local objective, apply it, and
repeat until no improvement or the evaluation budget is exhausted.

For a fixed selected set, the local objective is:

1. total directional matrix travel minutes including start and end, ascending;
2. final end time/total duration ascending;
3. visit-ID sequence lexicographically ascending.

### 5. Reconsider skipped candidates

After local improvement, retry skipped candidates once per admission round in their original
canonical priority, again across every insertion slot. If at least one is admitted, run local
improvement again and start another skipped-candidate round. Stop when a full round admits none or
the shared evaluation budget is exhausted.

Accepted candidates are never removed during reconsideration. The process therefore terminates
after at most the optional-candidate count in successful rounds even without the explicit budget.

### 6. Global quality comparator

Compare feasible baseline/optimized plans using this deterministic order:

1. optional-inclusion bit vector in canonical rank order, descending lexicographically (`included`
   beats `not included` at the first difference);
2. total directional matrix travel minutes, ascending;
3. total duration/final end time, ascending;
4. visit-ID sequence lexicographically ascending.

This makes ranking authoritative for membership while allowing geography to determine the order of
the same admitted set. A lower-ranked collection cannot displace a higher-ranked feasible optional
merely because it is shorter.

### 7. Deterministic work budget and cancellation

Add these validated `SchedulingGeneration` settings:

- `EnableOptionalRouteOptimization` (default `true`);
- `MaxRouteOptimizationSeeds` (default `3`, range `1..10`);
- `MaxRouteEvaluations` (default `5000`, range `100..50000`).

One evaluation is one complete proposed visit-sequence schedule evaluation. The budget is shared by
insertion, reconsideration, 2-opt, and relocate across the whole `GenerateAsync` call. Legacy
baseline construction is always allowed so budget exhaustion cannot turn a previously feasible
request into infeasible.

Do not use wall-clock expiration to select the returned route: identical input, matrix, timezone,
and options must produce identical output regardless of host speed. Check the cancellation token at
least once per mandatory permutation and proposed route evaluation; cancellation propagates.

When the feature is disabled, `GenerateAsync` returns the legacy tail-append result and performs no
optimization evaluations. This is a rollout/recovery switch, not a second public behavior contract.

### 8. Independent final invariant validator

Before either public generation method returns success, an independent validator recomputes the
hard invariants from `GenerationInput`, the matrix, candidates, options, and final plan. It must not
call the optimizer or treat the optimizer's `feasible` flag as proof.

The validator checks at minimum:

- contiguous sequence numbers and at least one visit;
- visit IDs exist, are unique, and include every mandatory ID exactly once;
- mandatory flags/reasons match request membership;
- directional travel, transition/final buffers, chronology, and reported end/duration agree;
- visit and qualified-rest opening hours;
- cost sum and budget;
- existing rest-policy limits/placement semantics;
- arrival at the requested endpoint within the horizon.

An internally generated plan that fails validation is an invariant violation, not user
infeasibility. The service must not return it as success. The implementation shall surface a clear
`InvalidOperationException` with no route/user data for tests and monitoring rather than silently
returning a different public `constraints_infeasible` result. `GenerateFixedOrderAsync` uses the
validator but never enters the optimizer.

## Diagnostics and Benchmark Contract

Record one aggregate `ActivitySource` diagnostic event per successful generation containing only:

- feature enabled/disabled;
- baseline and final optional counts;
- baseline and final total travel minutes;
- absolute/percentage travel change when comparable;
- evaluation count, seed count, reconsidered admissions, 2-opt/relocate move counts;
- budget-exhausted flag and elapsed optimization duration.

Do not log POI IDs/names, user ID, coordinates, preference payload, or the route sequence. Objective
diagnostics are not part of the public response.

A reproducible non-CI benchmark corpus must contain:

- synthetic zigzag matrices where insertion and local search have known improvements;
- asymmetric matrices;
- tight opening-hour, budget, rest, and end-horizon cases;
- 0, 1, 3, and 6 mandatory POIs;
- candidate populations 10, 20, and the configured maximum 40.

On the same host/configuration, enabled-mode generator-only p95 must add no more than 100 ms in
every corpus segment. The relative requirement (`enabled p95 <= 2x disabled p95`) additionally
applies when the disabled baseline p95 is at least 5 ms; ratios against smaller baselines are
reported for context but do not gate delivery because timer noise and fixed optimizer setup dominate
sub-millisecond measurements. This is a delivery benchmark, not a flaky wall-clock unit-test
assertion. End-to-end provider latency is reported separately.

## Acceptance Criteria

1. A golden zigzag matrix produces the same/higher-priority optional set and strictly lower total
   matrix travel than the legacy tail-append route.
2. A candidate infeasible at the tail but feasible between two existing visits is admitted at the
   minimum-delta feasible position.
3. A skipped candidate that becomes feasible after local improvement is reconsidered and admitted.
4. 2-opt and relocate each have a fixture where they strictly improve travel; no accepted move
   changes the selected visit set or violates a hard invariant.
5. A tempting shorter route that violates opening hours, budget, rest behavior, or end horizon is
   rejected after full rescheduling.
6. Asymmetric travel matrices use directional deltas and remain correct.
7. A lower-ranked candidate never displaces a feasible admitted higher-ranked candidate; canonical
   ranking tie-break tests remain authoritative.
8. Every success from `GenerateAsync` and `GenerateFixedOrderAsync` passes the independent invariant
   validator. Mutation tests/fixtures prove the validator rejects bad hours, cost, chronology,
   mandatory membership, duplicate visits, travel, and end time.
9. `GenerateFixedOrderAsync` preserves the exact supplied visit order with optimization enabled.
10. Repeated runs with identical input/matrix/timezone/options return byte-equivalent visit order,
    timestamps, rests, totals, and diagnostics except measured elapsed duration.
11. Evaluation count never exceeds `MaxRouteEvaluations`; exhaustion returns the best valid result
    found or the legacy baseline, never a partial/invalid plan.
12. Cancellation during insertion/local search exits promptly with `OperationCanceledException`.
13. The route-duration provider is called once and receives the same bounded candidate matrix.
14. Aggregate diagnostics contain before/after objective and work counts but no user/POI identity.
15. The representative benchmark satisfies the declared p95 gate and records travel improvement,
    optional inclusion, evaluation counts, and budget-exhaustion rate.
16. All existing scheduling, handler, itinerary-version, SQL integration, and OpenAPI tests remain
    green; no database migration or public API change is introduced.

## Approved Test Seams

1. `ItineraryGenerationService.GenerateAsync` for ordering, admission, constraints, determinism,
   cancellation, provider call count, and evaluation budget.
2. `ItineraryGenerationService.GenerateFixedOrderAsync` for exact-order regression and final
   validation.
3. The independent validator as an internal component visible to the Application test assembly,
   using deliberately corrupted plan fixtures. Optimizer private helpers are not direct test targets.
4. Handler/itinerary-version public seams only where needed to prove unchanged integration and
   option propagation.

## Alternatives Not Chosen

- **Pure nearest neighbor:** minimizes a local travel edge but can discard desirability priority and
  ignore future time windows.
- **Unvalidated 2-opt:** travel can improve while opening hours, rests, budget, or endpoint timing
  becomes invalid.
- **Wall-clock optimization timeout:** bounds latency but makes output depend on host speed and load.
- **Optimize all 720 mandatory permutations deeply:** increases worst-case work unnecessarily; a
  deterministic seed cap plus baseline fallback is safer for an incremental change.
- **Replace the engine with a global solver:** substantially changes ownership, objective, failure
  modes, and operational complexity beyond this confirmed issue.

## Delivery Constraints

- Follow red-green-refactor in the approved work-item order.
- Keep the optimization inside Application; do not introduce infrastructure/provider dependencies.
- Preserve unrelated working-tree changes.
- Do not alter ranking/provider-pool or fixed-order semantics.
- Do not add database migrations.
- Do not commit, push, merge, or open a pull request without explicit developer instruction.
