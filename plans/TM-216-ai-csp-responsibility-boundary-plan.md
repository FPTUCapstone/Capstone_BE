# TM-216 Backend Implementation Plan

## 1. Goal

Implement the approved TM-216 specification (`specs/TM-216-ai-csp-responsibility-boundary-spec.md`) exactly: prove the frozen AI-CSP responsibility boundary with the six required tests (Spec §9 / T-216-01…06), with **zero production code change** and **no implementation beyond the backward-compatible `Candidate(...)` test-fixture extension**. The boundary itself is already enforced at baseline `8093e6d`; TM-216 adds the missing proof tests only.

## 2. Preconditions

- Spec approved: `specs/TM-216-ai-csp-responsibility-boundary-spec.md` (frozen, no edits during implementation).
- Baseline HEAD `8093e6d1472f9413807dfa6987994e03244ccb79` (TM-215 final) — all TM-216 tests target behavior already present at this commit.
- `dotnet build` and `dotnet test` green on the clean checkout before any change (repo rule: never start on a broken baseline).
- Discovery facts locked (verified at baseline): every existing scheduling fixture uses permissive Tuesday 07:00–20:00 hours; `StartAtUtc = 2026-10-20 01:00 UTC` = **Tuesday 08:00 ICT** (`Asia/Ho_Chi_Minh`, fixed UTC+7, no DST); `TransitionBufferMinutes = 10`, `FinalReturnBufferMinutes = 15`, `RestDurationMinutes = 30`; `CreateInput` default budget `200_000m`.

## 3. Baseline / Branch Strategy

- Working tree at planning time: clean, branch `feature/phuctv-tm215-ai-poi-ranking`, HEAD `8093e6d…`.
- **Dedicated TM-216 branch (frozen):** cut `feature/phuctv-tm216-ai-csp-boundary` from `8093e6d…` at implementation start. Never commit TM-216 work onto the TM-215 branch, `main`, or `develop`.

## 4. Implementation Principles

- **Test-only ticket.** Any diff under `src/`, `database/`, or solution/config files is a defect of this plan — stop and escalate.
- Each work item ends with the targeted suite **and** the full unit-test project green.
- New tests mirror the existing `ItineraryGenerationServiceTests` idioms exactly (xUnit `[Fact]`, FluentAssertions, `FixedRouteDurationProvider`, `CreateInput`).
- Fixture math is frozen in §6 — the implementer does not re-derive times or pick new numbers.
- Assertions assert observable plan outcomes (item sets, exact planned times, error code), not internal ordering state.
- No Gemini/provider/network work, no DB migration, no new test doubles, no new production seams.

## 5. File-by-File Change Map

### MODIFIED FILES

| Path | Current Responsibility | Exact Future Change | Risk | Tests |
|---|---|---|---|---|
| `tests/TripMate.Application.UnitTests/Features/Scheduling/ItineraryGenerationServiceTests.cs` | 14 generator unit tests + shared `Candidate(...)` fixture factory (:509–530) + `FixedRouteDurationProvider` (:532–540) | **W01:** extend `Candidate(...)` with one trailing optional `GenerationOpeningHours[]? openingHours = null` parameter defaulting (via null-coalesce to a static field — optional parameters must be compile-time constants and `TimeOnly` is not one) to the existing permissive Tuesday hours. **W02/W03:** append the six required tests. No existing assertion changes. | low (backward-compatible signature; all existing call sites compile unchanged) | existing suite stays green; new T-216-01…06 |

### INSPECTED / MUST NOT CHANGE (audit list for the final `git diff`)

| Path/Area | Reason |
|---|---|
| `src/**` (every file) | Spec §3/§11: no production change of any kind |
| `ItineraryGenerationService.cs` — incl. `AlignToOpeningHours`/`FitsOpeningHours` | frozen; tests assert current behavior, never "fix" it (e.g., no cross-midnight closing support) |
| `CreateSchedulingRequestCommandHandler.cs`, `CreateSchedulingRequestCommandValidator.cs` | frozen |
| `PoiRankingOrchestrator.cs`, `PoiRankingContracts.cs`, `HttpPoiRankingProvider.cs`, all options | TM-215 boundary frozen; blend/validation already tested |
| `database/**` | no migration (Spec §11) |
| `TripMate.slnx`, appsettings, DI | no config/registration change |
| All other test files (handler tests, orchestrator tests, integration suites) | unchanged; only the generator test file is touched |
| `specs/`, `docs/` | approved spec untouched during implementation |

## 6. Work Items

Dependency order: **W01 → W02 → W03.** Each is small and independently auditable.

### W01 — Backward-compatible `Candidate(...)` fixture extension (helper only, zero new tests)

| Field | Content |
|---|---|
| Goal | Let individual tests override opening hours while every existing call site keeps today's permissive-Tuesday behavior byte-for-byte. |
| Files | `tests/TripMate.Application.UnitTests/Features/Scheduling/ItineraryGenerationServiceTests.cs` |
| Exact changes | 1. Add `private static readonly GenerationOpeningHours[] DefaultPermissiveTuesdayHours = [new GenerationOpeningHours(2, new TimeOnly(7, 0), new TimeOnly(20, 0))];` (mirrors current literal at :525). 2. Append trailing optional parameter to `Candidate(...)`: `GenerationOpeningHours[]? openingHours = null`. 3. Factory body uses `openingHours ?? DefaultPermissiveTuesdayHours` where the hours literal is used today. Nothing else moves. |
| Why this shape | C# optional parameters must be compile-time constants; `TimeOnly(7,0)` is not one, hence nullable-array + null-coalesce. Trailing position keeps all ~40 existing positional call sites compiling with identical semantics. This is the ONLY code change allowed in TM-216 (Spec §9, AC-3). |
| Exit criteria | Project builds; all 14 existing generator tests pass with **no assertion edits**; `git diff --stat` shows exactly one file. |
| Targeted validation | `dotnet build` then `dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~ItineraryGenerationServiceTests"` (green, still 14 tests) |

### W02 — Opening-hours gate tests (T-216-01 … T-216-05)

| Field | Content |
|---|---|
| Goal | Direct coverage of `AlignToOpeningHours` (:471–489) and `FitsOpeningHours` (:491–506) through the five spec-required hours scenarios. |
| Files | `tests/TripMate.Application.UnitTests/Features/Scheduling/ItineraryGenerationServiceTests.cs` (append only) |
| Shared fixture math (frozen) | 4×4 fixed matrix of 10-minute legs (indices 0=start, 1–2=candidates, 3=end) for two-candidate tests; 3×3 for single-candidate tests. First-candidate arrival = 08:00 + 10 travel + 10 buffer = **08:20 ICT Tuesday**. `RestPreference.None` (rest rules never fire: continuous minutes stay < 120). Costs ≤ 100_000 so the default 200_000 budget never confounds the hours assertions. `availableMinutes: 240` → window closes 12:00 ICT. |
| T-216-01 `Generate_TopRankedOptionalClosedBeforeArrival_IsExcluded` | A id 12: visit 30, cost 40_000, effective **0.9** (top), hours Tue **07:00–08:00** → arrival 08:20 is after close → `AlignToOpeningHours` returns `MinValue` → skipped (:207–210). B id 28: visit 30, cost 40_000, effective 0.1, hours Tue 07:00–20:00 → included (endTime 09:15 ≤ 12:00). Then: success; exactly one visit, `PointOfInterestId == 28`. |
| T-216-02 `Generate_OptionalArrivingBeforeOpening_IsAlignedToOpeningTime` | Single candidate id 12: visit 60, hours Tue **10:00–18:00**, effective 1.0. Arrival 08:20 < open → aligned up to 10:00 (:484–488); departure 11:00; return 10+15 → 11:25 ≤ 12:00. Then: success; convert `PlannedArrivalUtc` to ICT and assert time-of-day == 10:00 exactly (and departure 11:00). |
| T-216-03 `Generate_OptionalVisitOverrunningClose_IsExcluded` | A id 12: visit **120**, hours Tue 07:00–**09:30** → arrival stays 08:20, departure 10:20 > close → `FitsOpeningHours` false → excluded (:243–247). B id 28: visit 30, Tue 07:00–20:00 → included. Then: success; exactly one visit = B; no partial/clipped visit for A. |
| T-216-04 `Generate_OptionalWithoutHoursForArrivalDay_IsExcluded` | A id 12: visit 30, effective 0.9, hours **Monday (day 1) only** → no record for Tuesday arrival day → `opening is null` branch (:477–482) → excluded. (Equivalence note in a code comment: `ToCandidate` filters `IsClosed` rows (handler :370–371), so a closed day IS the missing-record case at the generator boundary.) B id 28: Tue 07:00–20:00 → included. Then: success; exactly one visit = B. |
| T-216-05 `Generate_MandatoryWithImpossibleHours_ReturnsConstraintsInfeasible` | Mandatory `[12]`: visit 60, hours Tue 07:00–08:00 → arrival 08:20, departure 09:20 > close → `TryBuildPlan` null for every permutation → failure (:528–529). Then: `result.IsFailure`; error code == `"planning.constraints_infeasible"`; no items. Mirror the assertion style of the existing `Generate_WhenOnlyUnbufferedScheduleFits_ReturnsInfeasible`. |
| Exit criteria | Five new tests green; still zero production diff; all pre-existing generator tests still green. |
| Targeted validation | `dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~ItineraryGenerationServiceTests"` (green, 19 tests) then `dotnet test tests/TripMate.Application.UnitTests` (whole unit project green) |

### W03 — AI-maximum score vs. feasibility tests (T-216-06, two methods)

| Field | Content |
|---|---|
| Goal | Pin Spec G-1/G-5 at the generator seam: `EffectiveDesirabilityScore = 1.0m` — the exact post-blend value the AI layer contributes — cannot bypass the hours gate or the available-minutes gate. Mirrors the budget proof of `Generate_HigherRankedCandidateStillYieldsToCurrentBudgetFeasibility` (:458). |
| Files | `tests/TripMate.Application.UnitTests/Features/Scheduling/ItineraryGenerationServiceTests.cs` (append only) |
| T-216-06a `Generate_MaxEffectiveScoreCandidate_ViolatingHours_StillExcluded` | X id 12: visit 30, effective **1.0m**, hours Tue 07:00–08:00 (closes before the 08:20 arrival) → excluded. Y id 28: visit 30, effective **0.0m**, Tue 07:00–20:00 → included. Then: success; exactly one visit = Y. |
| T-216-06b `Generate_MaxEffectiveScoreCandidate_ViolatingTimeWindow_StillExcluded` | X id 12: visit **300**, effective **1.0m**, hours Tue 00:00–23:59 (hours fully feasible) → arrival 08:20, departure 13:20, endTime 13:20+10+15 = 13:45 > 12:00 → skipped by the available-minutes gate (:245). Y id 28: visit 30, effective 0.0m, Tue 07:00–20:00 → included. Then: success; exactly one visit = Y; total duration ≤ 240. |
| Exit criteria | Both tests green; zero production diff; full unit project green. |
| Targeted validation | `dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~ItineraryGenerationServiceTests"` (green, 21 tests) then `dotnet test tests/TripMate.Application.UnitTests` |

## 7. Test Mapping (Spec §9 → plan)

T-216-01 → W02 · T-216-02 → W02 · T-216-03 → W02 · T-216-04 → W02 · T-216-05 → W02 · T-216-06 → W03 (two methods). Spec §8 guarantees: G-1/G-5 proven by W03 (plus the existing budget proof); G-2/G-3/G-4 already covered by existing TM-215 suites (handler :200–208 mandatory-pool exemption; orchestrator validation/fallback; Phase-2 current-value tests) — re-tested only via regression, not duplicated.

## 8. Migration Assessment

**NO MIGRATION.** Test-only ticket; nothing touches `database/`. If implementation appears to require one: STOP — that means the plan was violated, not that a migration is needed.

## 9. Final Regression Gate (before done)

1. `dotnet build` — clean.
2. `dotnet test` (solution root, `TripMate.slnx`) — all green. SQL Server–tagged suites (`[SqlServerFact]`) require the local SQL Server per repo convention (docker-compose); run them where the environment provides it and record the outcome — they are unaffected by this test-only change and must not regress.

   **Expected post-TM-216 baselines (gate numbers):**

   | Suite | Expected result |
   |---|---|
   | Build | 0 errors |
   | Application (`TripMate.Application.UnitTests`) | 912 passed / 0 failed |
   | Infrastructure (`TripMate.Infrastructure.UnitTests`) | 173 passed / 0 failed / 1 skipped |
   | SQL API (`TripMate.Api.IntegrationTests`, SQL Server) | 417 passed / 0 failed |

   TM-216 adds exactly **7 test methods total** (W02 +5, W03 +2), raising the generator suite from 14 to 21; the baselines above are the expected post-TM-216 counts including those 7.
3. `git diff --stat` audit — the **only** changed file is `tests/TripMate.Application.UnitTests/Features/Scheduling/ItineraryGenerationServiceTests.cs`; additions only (helper parameter + six tests + one static field + comment); zero deletions of existing assertions.
4. Spec AC checklist: AC-1 boundary holds at baseline (discovery evidence) · AC-2 six tests green · AC-3 only the `Candidate(...)` extension · AC-4 suites green · AC-5 no new AI capability/contract/config/schema/HTTP/solver (grep `AiScore|HttpPoiRankingProvider|GenerationCandidate` diff = empty under `src/`).

## 10. Git Delivery Plan (do not execute during planning)

Single PR on `feature/phuctv-tm216-ai-csp-boundary`. Commit groups: (1) `test(scheduling): allow per-test opening hours in generator fixture` (W01); (2) `test(scheduling): cover opening-hours feasibility gates` (W02); (3) `test(scheduling): prove max AI score cannot bypass feasibility` (W03). No merge to `main` without the §9 gate.

## 11. Risk Register

| Risk | Mitigation (frozen in this plan) |
|---|---|
| Optional-parameter compile error (`TimeOnly` is not a constant) | W01 prescribes the nullable-array + static-field null-coalesce pattern explicitly |
| DST/timezone nondeterminism | `Asia/Ho_Chi_Minh` is fixed UTC+7 (no DST); `StartAtUtc` frozen at 2026-10-20 (Tuesday) |
| Rest insertion polluting hours scenarios | `RestPreference.None` is used in all TM-216 scenarios, so rest insertion cannot interfere with the assertions |
| Budget gate confounding hours assertions | Costs ≤ 100_000 vs default 200_000 budget; budget behavior already proven by the existing dedicated test |
| Matrix size mismatch throw (`ItineraryGenerationService.cs:50–53`) | Fixture matrices must match candidate count exactly: 4×4 for two candidates, 3×3 for one (matrix = start + candidates + end) |
| Scope creep — "fixing" the scheduler while writing tests (e.g., cross-midnight closing) | Spec §11 non-goal; §5 audit list; final `git diff` gate blocks any `src/` change |
| SQL integration suites unavailable in some environments | §9 gate states the expectation explicitly; they are untouched by this change and must be green where SQL Server exists |
| Fragile assertions on internal ordering | Assertions restricted to item-set membership, exact planned times, total duration, and error code |

## 12. Open Issues

None. The six tests are fully determined by the approved spec and the frozen fixture math above.
