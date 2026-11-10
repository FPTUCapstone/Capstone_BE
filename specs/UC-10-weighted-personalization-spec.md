# UC-10 Weighted Personalization Rollout: Specification

## Status

The developer confirmed the decisions on 2026-11-10. The architecture decision is
[ADR-002](../docs/adr/ADR-002-weighted-utility-production-objective.md). This spec covers phases G0–G6 of that ADR. Each
phase ships as its own PR.

| Phase | State |
|---|---|
| G0 | Committed (`67e5bb1f`) |
| G1 | Implemented on `feature/phuctv-uc10-weighted-personalization` |
| G2–G6 | Planned |

## Problem

The UC-10 audit of 2026-11-10 (`origin/develop` `f6cd480e`) found three problems.

1. **The shipped solver personalizes along one axis only.**
   - It runs `SchedulingGeneration:Csp:Mode=Lexicographic`. Saved interests and like/dislike/skip behavior change the plan.
   - Saved pace and risk tolerance do not. They are read only on the `WeightedUtility` path.
   - SRS BR-24/26 promises five preferences as soft constraints: transport mode, pace, food, interests and risk.
2. **The served plan's provenance is hidden.** Clients show "Tối ưu bởi Thuật toán CSP" no matter which plan was served: a heuristic fallback, a node-limited result, or a plan reordered by `TimeOfDayRefiner`. The response DTO carries no solver outcome, and `Itinerary.SourceType` is always `CSPGenerated`.
3. **The personalization evidence does not match production.** It was measured on `WeightedUtility` with AI ranking enabled, which is not the shipped configuration.

## Decisions (developer, 2026-11-10)

| ID | Decision |
|---|---|
| D1 | `WeightedUtility` becomes the production objective for Create and Regenerate. |
| D2 | Stop targets for an 8-hour trip, non-decreasing with pace: Relaxed 3 ± 1, Moderate 4–6, Fast 6–8. |
| D3 | Food stays out of auto-generation. The SRS is amended: food preferences apply when travelers add restaurants, and the preferred transport mode is a prefilled request default. |
| D4 | Rollout is shadow-first. Shadow runs at 20%, and `Csp.Mode` switches only when the ADR-002 §4 gates pass. |

## Acceptance criteria by phase

### G0: shadow on, objective pinned

- AC0.1: The shipped `appsettings.json` sets `SchedulingGeneration:Shadow` to `Enabled=true`, `SampleRate=0.2`, `TimeoutMs=5000`.
- AC0.2: Integration tests keep shadow disabled (`TripMateApiFactory`).
- AC0.3: A test pins the shipped `Csp.Mode=Lexicographic`. Switching the mode must change that test.

### G1: counterfactual evidence and observability

- AC1.1: Endpoint tests run through the real DI graph and the shipped settings, with only `Csp.Mode` overridden to `WeightedUtility`. Each pair keeps the POIs, the request and the travel matrix fixed, and asserts:
  - culture and nature interests visit different places, and the culture plan holds more culture POIs;
  - a saved profile changes the cold-start plan;
  - stop count is ordered Relaxed < Fast, with Moderate between them;
  - with only exposed-outdoor POIs available, a Low-risk traveler gets fewer stops than a High-risk one;
  - the same profile yields the same plan for different travelers.
- AC1.2: Rerunning the AC1.1 tests with `Csp.Mode=Lexicographic` fails the pace, risk and cold-start cases. This proves the tests discriminate. Verified manually; it is not a committed test.
- AC1.3: `ShadowMetrics.Pace` and both `Shadow run finished` log lines carry the pace the shadow objective used: the saved pace, or `Moderate` when none is saved. `PersonalParamsFactory.EffectivePace` is the single source of that default.
- AC1.4: A malformed `InterestTagsJson` is logged with its length and the failure type, never its content.
- Decision on the `?? new PersonalParamsFactory()` fallbacks:
  - Keep them. They give the documented no-DI Moderate fallback that ADR-001 §5.5.1 tests rely on.
  - Production resolves the configured factory through DI. AC1.1 guards this, because the pace and risk pairs fail if the Pace table is not bound.

### G2: provenance and correctness (planned)

- AC2.1: Persist and return `SchedulingProvenance`:
  - `SolverOutcome`, `ObjectiveMode`, `SearchCompleted`, `NodeLimitReached`, `TimeOfDayRefined`;
  - stored in new `Itineraries` columns, added by a migration in `database/migrations` and in `tripmate_schema_v7.sql`;
  - exposed in `SchedulingResponseDto` and the itinerary detail;
  - additive, so existing clients are unaffected.
- AC2.2: Regenerate records its own provenance. Adjust records `manual_order`.
- AC2.3: When the CSP hits its time limit and the heuristic has no plan, return `planning.generation_timeout` instead of `planning.constraints_infeasible`. Today the infeasible answer comes from `ItineraryGenerationService.cs:107-111`.
- AC2.4: The solver outcome log line carries `search_completed` and the objective mode.
- AC2.5: Fix the `CspOptions.WeightedMaxNodes` comment, which cites ADR-001 figures that ADR-001 does not contain.
  - `Csp/` and `Common/` are hashed by `SolverComparisonReport.SourceInputs`. Regenerate the solver-comparison report in the same change.

### G3: honest clients (planned; Web and Mobile repos)

- Show the CSP badge only for outcome `csp`. When `!SearchCompleted`, say "lịch tốt nhất tìm được".
- Show MSG35 on a fallback.
- Drop the fake progress text shown before the POST.
- Show the backend 422 `detail`.

### G4: calibrated personalized objective (planned)

- Calibrate λ, `RhoStops` and `MaxStopsPerDay` to D2 on Da Nang POIs with a recorded Mapbox matrix, plus the synthetic corpus. Then bump `Personalization.Version`.
- Fallback for `WeightedUtility` on time limit: serve `GreedyWeightedIncumbent` if it was built and validated. The new outcome is `weighted_greedy_fallback_csp_time_limit`.
- Add a `WeightedUtility` benchmark gate (ADR-002 §4.4), with p99 and the node-limit rate.
- A separate config PR switches `Csp.Mode`. It flips the pin test, and pins `Lexicographic` explicitly in the shadow endpoint test.

### G5 and G6 (planned)

- G5: report the optimality gap from the root upper bound; give per-POI preference reasons.
- G6: SRS BR-24/26 and MSG35 amendment; rerun the persona report on the shipped configuration.

## Out of scope

- Meal stops.
- Enabling AI ranking.
- Adaptive learning.
- Weather-driven risk.
- Split or overnight opening hours.

## Verification commands

```bash
dotnet test TripMate.slnx -c Release --no-restore --filter "Category!=SqlServer&Category!=Redis"
dotnet format TripMate.slnx --verify-no-changes --no-restore --include <changed .cs files>
```
