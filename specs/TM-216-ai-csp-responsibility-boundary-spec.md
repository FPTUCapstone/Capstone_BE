# TM-216 Backend Technical Specification

## 1. Document Control

- Ticket: TM-216 / [UC-10] Enforce AI-CSP Responsibility Boundary
- Repository: `D:\FPTUCapstone\Capstone_BE`
- Branch: `feature/phuctv-tm215-ai-poi-ranking` (working tree clean at spec creation)
- Baseline HEAD: `8093e6d1472f9413807dfa6987994e03244ccb79` (TM-215 final commit)
- Discovery input: TM-216 discovery audit (same baseline) — rules 1, 2, 4–9 implemented and tested; rule 3 (opening hours) implemented but untested; no AI-score bypass path found
- Nature of work: **boundary freeze + test specification only.** No production code change is in scope; the existing implementation is the source of truth. TM-216 does not introduce new behavior.
- Related artifacts: `specs/TM-215-ai-poi-ranking-provider-spec.md` (landed), `specs/TM-213-*`, `specs/TM-214-*`

## 2. Executive Summary

TM-216 freezes the responsibility boundary between the AI ranking layer (TM-215) and the deterministic constraint/scheduling layer, and specifies the missing tests that prove the boundary holds. The AI layer may only reorder/propose optional POIs through `EffectiveDesirabilityScore`; every hard constraint — planning readiness, search radius, opening hours, travel time, budget, available duration, mandatory inclusion, rest/buffer — is owned and enforced exclusively by the scheduler/handler using current Phase-2 database and OpenRouteService values. All six required behaviors already exist in code; the only gap found is test coverage for the opening-hours gates and for an AI-maximum score against time/hours feasibility. TM-216 specifies exactly those tests and nothing else.

## 3. Scope

### In Scope

- Frozen statement of the AI↔scheduler responsibility boundary.
- Frozen list of authoritative hard constraints and their single enforcement locations.
- Frozen list of data the AI may never provide authoritatively.
- Required behavior guarantees (score cannot create feasibility; mandatory retention; fallback without constraint weakening; Phase-2/current-value authority).
- The six required TM-216 tests (unit-level, `ItineraryGenerationServiceTests`), specified given/when/then.

### Out of Scope

- Any production code change (handlers, generator, orchestrator, provider, options, schema).
- Any change to AI ranking algorithms, blend weights, provider contract, or configuration.
- Any new database schema, migration, or persistence.
- Any new HTTP contract or response shape.

## 4. Source of Truth — Verified Baseline Architecture

All statements verified at `8093e6d`. The scheduling flow, in execution order:

| Step | Location | Responsibility |
|---|---|---|
| HTTP entry | `src/TripMate.Api/Controllers/V1/SchedulingRequestsController.cs` | `POST api/v1/scheduling-requests`, Traveler role |
| Validation | `CreateSchedulingRequestCommandValidator.cs:10–45` | available minutes 60–720, budget > 0 when present, mandatory ≤ 6/distinct/positive, same-local-day, time zone |
| Phase 1 ranking (optional POIs only) | `CreateSchedulingRequestCommandHandler.cs:46–95` → `PoiRankingOrchestrator.BuildRankingAsync` | base score, bounded pool (≤ 60), optional AI call, validation, 60/40 blend |
| Phase 2 authoritative transaction | `CreateSchedulingRequestCommandHandler.cs:97–281` | replay, current POI re-read, planning-ready + radius gates, pool bounding, matrix selection |
| Candidate mapping | `CreateSchedulingRequestCommandHandler.ToCandidate` (:360–386) | current DB values for cost/duration/hours + frozen ranking fields |
| Constraint solving | `ItineraryGenerationService.GenerateAsync` (`…/Scheduling/Common/ItineraryGenerationService.cs`) | all hard constraints, deterministic plan |
| Persistence | `CreateSchedulingRequestCommandHandler.cs:249–279` | `Itinerary.CreateCspGenerated`, items, request completion |

AI touchpoints (complete, grep-verified): `IPoiRankingProvider` / `HttpPoiRankingProvider` / `PoiRankingOrchestrator` only. No other feature consumes AI output.

## 5. Frozen Responsibility Boundary

- **AI may only rank/propose POIs.** The provider receives semantic metadata only — `PreferenceTokens` + per-candidate `PoiId/Name/CategoryName/TagNames` (`PoiRankingContracts.cs:12–22`; payload built at `HttpPoiRankingProvider.cs:44–52`) — and returns only `PoiId`, `AiScore` ∈ [0,1], optional `Reason` (`HttpPoiRankingProvider.cs:193–213` schema; `PoiRankingContracts.cs:26`). Its system instruction explicitly forbids itinerary scheduling (`HttpPoiRankingProvider.cs:17–27`).
- **AI output is advisory only.** It enters scheduling solely as `EffectiveDesirabilityScore = 0.60·Base + 0.40·Ai` (`PoiRankingOrchestrator.cs:224–226`), consumed only in two ordering chains: matrix preselection (`CreateSchedulingRequestCommandHandler.cs:344`) and optional-insertion order (`ItineraryGenerationService.cs:180`). It is never compared against, fed into, or able to relax any feasibility check. The AI `Reason` is trimmed/truncated for safety but never persisted, never carried into `GenerationCandidate`, and never used for scheduling (`PoiRankingSnapshot.cs:21–28` has no reason field).
- **Scheduler/constraint layer is final authority.** `ItineraryGenerationService` is the only plan producer (`CreateSchedulingRequestCommandHandler.cs:238`); itineraries are created exclusively via `Itinerary.CreateCspGenerated`. Any AI failure (disabled/timeout/429/5xx/network/invalid response) degrades to base-score ranking — never to an AI-authored plan (`PoiRankingOrchestrator.cs:105–207`).

## 6. Authoritative Hard Constraints

Each constraint below has exactly one authority and is enforced against **current Phase-2 values**, independent of any ranking score.

| # | Constraint | Authority / enforcement location | Mechanism |
|---|---|---|---|
| 1 | Active + planning-ready state | `CreateSchedulingRequestCommandHandler.IsPlanningReady` (:324–327) + `Status == Active` (:122–128) | `SourceUrl` present, `VerifiedAtUtc` present, ≥ 1 open day with times |
| 2 | Search radius | handler :66–71 (Phase 1) and :164–171 (Phase 2, authoritative) | `GeoDistance.EquirectangularKilometers` ≤ `SearchRadiusKm` using current coordinates |
| 3 | Opening hours | `ItineraryGenerationService.AlignToOpeningHours` / `FitsOpeningHours` (:471–506); gates at :94–104 (mandatory), :206–210 and :243–247 (optional) | arrival aligned up to open time; visit must fit within `[OpenTime, CloseTime]` on the arrival local date; closed/unlisted day → candidate infeasible |
| 4 | Route / travel time | `IRouteDurationProvider.GetMatrixAsync` (OpenRouteService, `OpenRouteServiceRouteDurationProvider.cs`), consumed at `ItineraryGenerationService.cs:46–53, 89–92, 202–205, 236–245, 275–281` | real matrix travel minutes + `TransitionBufferMinutes` (10) before every arrival; return leg + `FinalReturnBufferMinutes` (15) checked against the available window; matrix size mismatch throws |
| 5 | Budget | `ItineraryGenerationService.cs:37–41, 106–110, 193–196, 213, 243–247` using current `EstimatedVisitCost` | running total ≤ `BudgetVnd`; unknown-cost mandatory → infeasible; unknown-cost optional → skipped |
| 6 | Available duration | `ItineraryGenerationService.cs:245, 275–281`; request-level `CreateSchedulingRequestCommandValidator.cs:22, 42–44` | every insertion and the final return must end by `StartAtUtc + AvailableMinutes`; 60–720 min; same local calendar day |
| 7 | Mandatory POIs | handler :173–180, :200–208 (always admitted to matrix, exempt from pool); `ItineraryGenerationService.cs:34–41, 55–63, 508–526` | all mandatory permutations tried, shortest feasible plan wins; mandatory POIs get neutral ranking scores (`ToCandidate_MandatoryUsesExplicitNeutralScores`); impossible mandatory → `planning.constraints_infeasible`, never dropped |
| 8 | Rest / buffer rules | `ItineraryGenerationService.NeedsRest` (:324–332), rest insertion :126–174, :383–434; constants :11–12 (`AutoRestThresholdMinutes` 150, `RestDurationMinutes` 30) | rest policy is deterministic; AI has no input channel; inserted rest stops are hour- and time-verified (:346–381) |

Request-level guards that bound the solver: mandatory count ≤ 6 (`CreateSchedulingRequestCommandValidator.cs:27–33` and `ItineraryGenerationService.cs:21`), matrix capacity 40 default / min 6 (`SchedulingGenerationOptions.cs:6–17`), public transit rejected as unsupported (:191–198).

## 7. AI Must Never Provide Authoritative Data

The AI provider is structurally incapable of, and must never be extended to, authoritatively supply:

- **Cost** — budget uses current Phase-2 `EstimatedVisitCost` (`ItineraryGenerationService.cs:106, 213`); `EstimatedVisitCostForRanking` is a frozen Phase-1 tie-break only, proven by `PhaseTwoCurrentCost_ControlsBudgetWhileFrozenCostRemainsRankingOnly`.
- **Visit duration** — `poi.AverageVisitDurationMinutes` from DB (`ToCandidate`, handler :368).
- **Opening hours** — `poi.OpeningHours` from DB (`ToCandidate`, handler :370–376).
- **Travel time** — OpenRouteService matrix only; no AI or candidate-supplied durations.
- **Feasibility** — no AI output participates in any feasibility predicate; scores appear only in `OrderBy`/`ThenBy` chains.
- **Final itinerary** — the plan is produced solely by `ItineraryGenerationService`; AI cannot add, remove, reorder (beyond ordering input), retime, or reprice any item.

Any future change that lets AI output carry cost/duration/hours/travel data, or that reads AI output outside an ordering chain, violates this specification.

## 8. Required Behavior Guarantees

- **G-1 — AI score cannot make an infeasible POI feasible.** Every feasibility gate is evaluated per candidate regardless of score (`ItineraryGenerationService.cs:94–110, 193–247`). Reference proof in tests: `Generate_HigherRankedCandidateStillYieldsToCurrentBudgetFeasibility` (`ItineraryGenerationServiceTests.cs:458`).
- **G-2 — Mandatory POIs cannot be removed because of low AI score.** Mandatory POIs bypass the provider pool entirely (handler :200–208), receive neutral scores (handler :234–236), and are permuted to a feasible plan or the request is infeasible.
- **G-3 — Invalid/failed AI falls back without weakening constraints.** All-or-nothing validation — exact pool count, PoiIds ⊆ pool, no duplicates, `AiScore` ∈ [0,1] (`PoiRankingOrchestrator.TryValidateProviderResult` :230–272) — reverts the entire pool to base scores; every operational failure maps to a base snapshot (:105–207). Fallback changes ordering only, never constraint enforcement.
- **G-4 — Phase 2 / current values remain authoritative.** Phase 2 re-checks Active, planning-ready, and radius with current coordinates; frozen ranking fields never substitute for current constraint inputs; no backfill, no second AI call, no Phase-2 personalization recomputation (TM-215 spec §28–29, verified unchanged at baseline).
- **G-5 — Score influence is selection/order only.** AI can change *which* feasible optional POIs are chosen and in what order; it can never change whether a candidate is feasible, the plan's constraint compliance, or the handling of mandatory POIs.

## 9. Required TM-216 Tests

All six tests are unit tests in `tests/TripMate.Application.UnitTests/Features/Scheduling/ItineraryGenerationServiceTests.cs`, using the existing `Candidate(...)` fixture factory and `FixedRouteDurationProvider`. The generator seam is the correct level: `GenerationCandidate.EffectiveDesirabilityScore` is exactly the post-blend value the AI layer contributes, so setting it to a maximum there proves no bypass regardless of blend source. Baseline fixtures are all permissive (Tuesday 07:00–20:00, start 2026-10-20 01:00 UTC = Tuesday 08:00 ICT) — these tests are the first to exercise the hours gates. Existing helper must be extended with an optional opening-hours parameter (test-code change only); no production signature changes.

| ID | Required test | Given / When / Then (frozen) |
|---|---|---|
| T-216-01 | Top-ranked optional closed before arrival → excluded | Optional A with `EffectiveDesirabilityScore` highest, opening hours ending before its computed arrival; optional B lower-ranked, open. Then: A excluded, B included; plan success. Proves :479–482 (`AlignToOpeningHours` after-close → `MinValue` → skip). |
| T-216-02 | Arrival before opening → aligned to opening time | Optional arriving before `OpenTime` with time available. Then: visit starts exactly at `OpenTime`, departure = open + visit duration, within close; item included. Proves :484–488. |
| T-216-03 | Visit overruns closing time → excluded | Optional whose `OpenTime + visit duration` exceeds `CloseTime`. Then: excluded (no partial/clipped visit); next feasible candidate may fill its place. Proves :491–506 + :243–247. Same predicate with a mandatory candidate → plan null → infeasible (may be asserted in T-216-05 instead). |
| T-216-04 | No opening-hours record for that day → excluded | Optional with hours only on a day ≠ arrival local day (or none at all). Then: excluded. Proves :477–482 (`opening is null` branch). Notes: `ToCandidate` already filters `IsClosed` rows (handler :370–371), so a "closed day" is exactly the missing-record case at the generator boundary. |
| T-216-05 | Mandatory POI with impossible hours → constraints_infeasible | Mandatory-only (or mandatory + optional) input where the mandatory candidate cannot fit hours on the arrival day. Then: `Result` failure with code `planning.constraints_infeasible` (:528–529); no items emitted. All permutations null → infeasible path. |
| T-216-06 | `EffectiveDesirabilityScore = 1.0` still cannot bypass hours/time feasibility | Two optional candidates: X with score 1.0 violating opening hours *and* (in a second scenario) the available-minutes window (`endTime > StartAtUtc + AvailableMinutes`, :245); Y with score ~0 feasible. Then: X excluded in both scenarios, Y included, plan success and constraint-compliant. Mirrors the budget proof of `Generate_HigherRankedCandidateStillYieldsToCurrentBudgetFeasibility` for the hours and time gates. |

Test rules (frozen): no new production seams, no new test doubles beyond existing patterns; fixtures must state day-of-week and times explicitly; assertions assert exclusion/inclusion and constraint compliance, never internal ordering internals beyond the observable item set. The orchestrator blend, provider validation, and fallback behaviors are already covered by TM-215 suites and are out of TM-216 test scope.

## 10. Existing Test Baseline (verified, unchanged by TM-216)

- Generator: 14 unit tests including mandatory inclusion, travel-duration preservation, auto/frequent rest, budget-yields-to-feasibility, deterministic tie-breaks, infeasible-empty cases.
- Handler: ~27 unit tests — replay/idempotency, pool bounding with mandatory retention, frozen snapshot tie-breakers, phase-2 drop/no-backfill, neutral mandatory scores.
- Ranking: orchestrator unit suite (blend, pool cap, validation, fallback, cancellation, logging) + provider adapter suite; SQL integration suites incl. `AiEnabled_ValidScoresReverseBaseOrdering` (AI reorders visit order only), `PhaseTwoCurrentCost_ControlsBudgetWhileFrozenCostRemainsRankingOnly`, `Replay_ReturnsPersistedResultWithoutAnotherProviderCall`.
- Known coverage gap (fixed by §9): no test exercises `AlignToOpeningHours`/`FitsOpeningHours` — every existing scheduling fixture seeds the same permissive Tuesday 07:00–20:00 hours (`ItineraryGenerationServiceTests.cs:525`, `CreateSchedulingRequestEndpointTests.cs:141`, `CreateSchedulingRequestSqlServerTests.cs:532`, ranking suite :525).

## 11. Explicit Non-Goals

- No new AI ranking algorithm, scorer, feature, or weight change.
- No new Gemini/provider contract, payload, schema, or adapter.
- No new database schema, migration, or persistence of scores/reasons.
- No multi-day scheduling (same-local-day rule stays; post-midnight closing remains unsupported by design).
- No LLM-generated explanations (TM-217 owns presentation; `Reason` stays unpersisted).
- No formal solver introduction (no OR-Tools/CP-SAT; the greedy + permutation scheduler stays).
- No production code modification of any kind in TM-216.

## 12. Acceptance Criteria

- AC-1 §5–§8 boundary statements hold at HEAD `8093e6d` with no production change required.
- AC-2 All six tests of §9 implemented, green, and asserting the frozen given/when/then.
- AC-3 No existing test modified except the `Candidate(...)` fixture factory gaining an optional opening-hours parameter (backward-compatible for existing call sites).
- AC-4 Full scheduling test suites (unit + integration) remain green.
- AC-5 No new AI capability, contract, config, schema, HTTP change, or solver dependency is introduced (grep-verifiable).

## 13. Open Issues / Unresolved Questions

None blocking. Two recorded clarifications: (1) T-216-03's mandatory-variant assertion may live in T-216-05 to avoid duplication — either placement satisfies the spec; (2) a future closing-times-past-midnight model would be a separate ticket; it is explicitly out of TM-216 scope and must not be smuggled into the new tests.

## 14. Verdict

SPEC READY — the boundary is already enforced by the implementation at baseline; TM-216 freezes it and specifies only the missing proof tests.
