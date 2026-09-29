# TM-213 — Traveler Preference Profile & Constraint Inputs — Implementation Plan

Status: Approved — implemented and verified (TM-213 Step 4)
Binding design baseline: `specs/TM-213-traveler-preference-profile-spec.md`
(TM-213 STEP 2 — FINAL CORRECTED CONTRACT & DESIGN REPORT)
Branch: `feature/phuctv-tm213-traveler-preference-profile` (already checked out; clean at plan creation)
Workflow: AGENTS.md §1 — spec-first, TDD (red → green → refactor), atomic tasks, zero regression. Test-first is mandatory: implementation code written before its test is deleted and redone (AGENTS.md §1.2.4).

---

## 1. Preconditions

- T0 baseline gate: implementation starts only from the verified TM-213
  baseline state — expected HEAD unchanged, no tracked `src/`/`tests/` diff,
  and only the two expected untracked Step-3 artifacts (spec + plan) present;
  any other unexpected change blocks implementation. Then `dotnet test` green
  before any change (AGENTS.md §1.2).
- Spec approved by the developer.
- No commit/push unless the developer explicitly requests it (AGENTS.md §5.1).

## 2. Workstream Separation

| Workstream                        | Contents                                                                                                                     | Production files | Test files           |
| --------------------------------- | ---------------------------------------------------------------------------------------------------------------------------- | ---------------- | -------------------- |
| A — EF persistence/schema parity | Mapping of the six preference fields; EF metadata parity tests only (real SQL round-trip lives in the integration layer, T4) | P2               | T1                   |
| B — Domain/model changes         | Five nullable parity properties; two new enums                                                                               | P1, E1, E2       | T1 (asserts mapping) |
| C — InterestTags hardening       | Behavior-preserving extraction of parse/score/normalize into a testable unit; XML docs pin degradation semantics             | P3, P4           | T2                   |
| D — Tests                        | Profile-state matrix, idempotency/replay, SQL integration                                                                    | —               | T3, T4, U1           |
| E — Documentation                | XML docs on extracted unit and parity fields; handoff verification                                                          | —               | —                   |

## 3. Production Files — Exact Change List

| #  | File                                                                                             | Change                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                   | Workstream | Constraints                                             |
| -- | ------------------------------------------------------------------------------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------- | ------------------------------------------------------- |
| P1 | `src/TripMate.Domain/Entities/TravelerProfile.cs`                                              | Add five nullable EF-materializable properties with private setters: `TransportMode? PreferredTransportMode`, `TravelerPace? TravelPace`, `RiskToleranceLevel? RiskTolerance`, `string? FoodPreferencesJson`, `decimal? DefaultBudget`. Factory `Create(...)` stays UNCHANGED (tests only need InterestTags, which it already supports; an unchanged factory also physically prevents constructing Tier C/D values in code). XML docs on Tier C/D members: "persisted for schema parity; no scheduling behavior in TM-213". | B          | parity only (C-5); no behavior for pace/risk/food (C-7) |
| P2 | `src/TripMate.Infrastructure/Persistence/Configurations/TravelerProfileConfiguration.cs`       | Add mappings: `preferred_transport_mode`, `travel_pace`, and `risk_tolerance` each use `HasConversion<string>()`, `HasMaxLength(20)`, and `IsUnicode(false)` to preserve the schema's `varchar(20)` contract; `food_preferences_json` uses `HasMaxLength(1000)`; `default_budget` uses `HasPrecision(12,2)` (follow the existing precision-mapping pattern used for POI `estimated_visit_cost`). Existing `user_id` / `interest_tags_json` / `updated_at` mappings and `AsUtcDateTime2()` unchanged.                                                                                                               | A          | no schema change (C-3/4)                                |
| E1 | `src/TripMate.Domain/Enums/TravelerPace.cs`                                                    | NEW enum {Relaxed, Moderate, Fast} — member names exactly match the DB CHECK values.                                                                                                                                                                                                                                                                                                                                                                                                                                                                    | B          | no magic strings (C-9/AGENTS.md §4.1)                  |
| E2 | `src/TripMate.Domain/Enums/RiskToleranceLevel.cs`                                              | NEW enum {Low, Medium, High} — member names exactly match the DB CHECK values.                                                                                                                                                                                                                                                                                                                                                                                                                                                                          | B          | no magic strings                                        |
| P3 | `src/TripMate.Application/Features/Scheduling/Common/TravelerPreferenceScoring.cs`             | NEW internal static class:`ParsePreferenceTokens(string?)`, `CalculatePreferenceScore(PointOfInterest, IReadOnlySet<string>)`, private `NormalizePreferenceToken`. Moved verbatim from the handler (behavior-identical extraction). XML docs pin the §10/§11 degradation and §6 ranking semantics of the spec.                                                                                                                                                                                                                                  | C          | consume only InterestTags (C-6)                         |
| P4 | `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs` | Replace the three private statics with delegation to`TravelerPreferenceScoring`. No other change: no scoring, ordering, hashing, validation, persistence, or response changes.                                                                                                                                                                                                                                                                                                                                                                         | C          | preserve behavior (C-4); no request_hash change (C-11)  |

**Files that must NOT change:** `Controllers/V1/Requests/CreateSchedulingRequest.cs`, `Controllers/V1/SchedulingRequestsController.cs`, `Features/Scheduling/Create/CreateSchedulingRequestCommand.cs`, `CreateSchedulingRequestCommandValidator.cs`, `Features/Scheduling/Common/GenerationInput.cs`, `ItineraryGenerationService.cs`, `SchedulingGenerationOptions.cs`, `SchedulingErrorCodes.cs`, `IRouteDurationProvider.cs`, `ISchedulingRequestLock.cs`, `Common/Interfaces/IApplicationDbContext.cs`, `Infrastructure/Persistence/ApplicationDbContext.cs`, all OpenAPI filters, all files under `database/`.

## 4. Test Files — Exact Change/Add List

| #  | File                                                                                                              | Change                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                                | Workstream |
| -- | ----------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------- |
| T1 | `tests/TripMate.Infrastructure.UnitTests/Persistence/TravelerProfilePersistenceModelTests.cs`                   | NEW — EF metadata/schema-parity test only (pattern of`PoiPersistenceModelTests`): column names, CLR types, nullability, enum value conversions, `MaxLength = 20`, and non-Unicode/`varchar(20)` for `preferred_transport_mode`, `travel_pace`, and `risk_tolerance`; `MaxLength = 1000` / precision, PK, 1:1 FK → User with cascade. No raw-SQL round trip at this layer — the real SQL round-trip case lives in T4 (established SQL Server integration layer). Mapping assertions only — no behavior assertions for Tier C/D fields.                                                                                                                                          | A/B        |
| T2 | `tests/TripMate.Application.UnitTests/Features/Scheduling/Common/TravelerPreferenceScoringTests.cs`             | NEW — direct unit tests: degradation branches (null/empty/whitespace/malformed → empty token set), diacritic/case normalization, score = +100 category match, +100 per matching tag, 0 for unknown tokens, determinism.                                                                                                                                                                                                                                                                                                                             | C          |
| T3 | `tests/TripMate.Application.UnitTests/Features/Scheduling/Create/CreateSchedulingRequestCommandHandlerTests.cs` | MODIFY — extend (do not duplicate) the profile-state matrix: row absent; null/empty/whitespace/malformed`InterestTagsJson` → generation succeeds with fallback ordering; boost ordering with seeded tags (extends the existing `Handle_UsesSavedTravelerInterestTagsToRankOptionalPois`); replay test: attempt 1 succeeds → profile row added/changed/removed → attempt 2 with same key + payload replays the original itinerary (never a conflict). Existing tests stay untouched and green.                                                 | D          |
| T4 | `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestSqlServerTests.cs`                       | MODIFY — uses this project's existing SQL Server integration infrastructure (`SqlServerTestDatabase`, `CreateDbContext()`, local `SeedAsync` raw-SQL seeding): (a) six-field round trip — raw-SQL INSERT into `dbo.TravelerProfiles` with all six preference columns populated → `ApplicationDbContext` materialization → assert all six fields; (b) end-to-end: tagged POI outranks a higher-scenic untagged POI; (c) row-absent parity case. Seeds through its own fixture pattern — no dependency on Application.UnitTests helpers. | D          |
| U1 | Application unit-test fixture seeding                                                                            | No additional fixture helper was required: T3 seeds through its local `TestDbContext` pattern. `TripMate.Api.IntegrationTests` independently seeds through its existing fixture/context pattern; no test-project-to-test-project reference is introduced.                                                                                                                                                                                                                                                                                        | D          |

**Files that must NOT change (tests):** `CreateSchedulingRequestEndpointTests.cs`, `CreateSchedulingRequestOpenApiTests.cs` (their unmodified green run is the proof of no OAS drift), `CreateSchedulingRequestCommandValidatorTests.cs`, `ItineraryGenerationServiceTests.cs`, `SchedulingGenerationPersistenceModelTests.cs`.

## 5. Constraint Register (binding — a violated item blocks the task)

1. TM-213 is define-and-consume only.
2. No TravelerProfile write API.
3. No SchedulingRequest HTTP/OAS contract change.
4. No database schema change; no EF migration.
5. Six DB fields mapped for persistence/schema parity only.
6. InterestTags is the only active scheduling preference.
7. No behavior added for TravelPace, RiskTolerance, FoodPreferences.
8. No server fallback for PreferredTransportMode or DefaultBudget.
9. No system defaults for null TravelPace/RiskTolerance (unspecified stays unspecified).
10. InterestTags remains soft ranking; PreferenceScore remains derived.
11. No `request_hash` change; live read; replay returns the original itinerary.
12. Backward compatibility: absent row ⇒ output identical to current behavior.
13. Mock/seed data only in test/dev.
14. No AI, no CSP, no multi-day work.

## 6. Tasks (atomic, sequential, TDD — each ends with the full suite green)

### T0 — Baseline gate

- Verify approved baseline state: expected HEAD, no tracked `src/`/`tests/`/`database/` diff, and only the expected untracked spec + plan are allowed; run `dotnet test` from the repository root.
- DoD: full suite green. If red, STOP — never start on a broken baseline (AGENTS.md §1.2).

### T1 — Schema parity (workstreams A + B, test-first)

1. RED: write `TravelerProfilePersistenceModelTests` (T1) asserting EF metadata only — all six field mappings + PK/FK/cascade per spec §4 (column names, CLR types, nullability, max length/precision, enum conversions). No raw-SQL round trip at this layer. Run → fails (fields missing).
2. GREEN: implement P1, P2, E1, E2. Checkpoint before wiring enum conversions: confirm `TransportMode` member names exactly match the DB CHECK values ('Walking','Motorbike','Car','PublicTransit'); if any member name differs, map that enum with explicit named conversion constants — never string literals at usage sites (AGENTS.md §4.1).
3. Verify: `dotnet test` green — zero production behavior change (suite green unmodified).

### T2 — InterestTags hardening (workstream C)

1. RED: write `TravelerPreferenceScoringTests` (T2) against the new class API. Run → fails (class missing).
2. GREEN: create P3 by moving `ParsePreferenceTokens`, `CalculatePreferenceScore`, `NormalizePreferenceToken` verbatim out of the handler; update P4 to delegate. XML docs state: tokens degrade to empty on null/empty/whitespace/malformed; no length branch; score formula per spec §6.
3. Verify: `dotnet test` green — all pre-existing handler tests pass unmodified (behavior-identical refactor proof).

### T3 — Handler profile-state matrix + idempotency proof (workstream D)

1. RED: extend handler tests (T3): seeded profile with matching/mismatching tags; null/empty/whitespace/malformed JSON; row absent; and the replay test (attempt 1 succeeds → profile row added/changed/removed → attempt 2 with same key + payload must replay the original itinerary, never conflict).
2. GREEN: expected to pass with no production change beyond T1/T2; if any case fails, fix in the hardening layer only — never in generation gates.
3. Verify: `dotnet test` green.

### T4 — SQL integration coverage (workstream D)

1. RED: extend SQL-server tests (T4) on the file's existing `SqlServerTestDatabase` / `CreateDbContext()` / `SeedAsync` pattern: (a) six-field round trip — raw-SQL INSERT into `dbo.TravelerProfiles` with all six preference columns populated → load via `ApplicationDbContext` → assert all six materialize; (b) fixture seeds one profile row; tagged POI outranks a higher-scenic untagged POI in the persisted itinerary order; (c) row-absent case keeps current ordering.
2. GREEN: no production change expected; fixture-only.
3. Verify: `dotnet test` green.

### T5 — Documentation & handoff (workstream E)

- Confirm XML docs on P1 Tier C/D members and P3 degradation semantics match spec §5/§6/§10/§11.
- Validate the handoff package (spec §14) is complete and internally consistent.
- Verify: `dotnet test` green; no doc/code drift against the spec.

### T6 — Final verification & review

- Full `dotnet test`; two-stage review per task (spec compliance, then code quality) per AGENTS.md §1.2.4–5.
- Critical findings block completion; minor findings deferred only with developer agreement.
- No commit/push unless the developer explicitly requests it (AGENTS.md §5.1); suggested conventional commit: `feat(user): define traveler preference profile contract`.

## 7. Explicit Non-Changes (audit checklist for review)

- `CreateSchedulingRequestCommandHandler`: only the three statics move; hashing (`ComputeRequestHash`), canonicalization, transaction/lock structure, infeasibility paths, response mapping untouched.
- `ItineraryGenerationService`: untouched — it already orders by the candidate-supplied `PreferenceScore`.
- OpenAPI/endpoint surface: byte-identical (unmodified test files prove it).
- `request_hash`: no profile fields (proven by the T3 replay test).
- No new error codes; no new validation rules; no DI/Program changes.
- No server-side application of PreferredTransportMode/DefaultBudget; no default substitution for null TravelPace/RiskTolerance anywhere.

## 8. Dependencies & Unresolved Issues (identified before implementation)

1. **Baseline green (T0)** — hard precondition; blocks all tasks if the suite is red. Not expected to be an issue (tree clean at plan creation).
2. **TransportMode member-name ↔ CHECK parity** — in-task checkpoint in T1 with a defined fallback (explicit conversion constants). Not a blocker.
3. **SQL fixture seeding hook** — if the existing integration factory lacks a TravelerProfile seeding path, seed via the context in fixture setup (existing pattern). Contingency, not a blocker.
4. **Future Traveler Preference UC** — owns write validation (tag referential policy; 1000-char limit enforcement) per spec §14. Outside TM-213; not a blocker.

## 9. Definition of Done

All ten spec acceptance criteria (spec §17) demonstrably met; full suite green including unmodified endpoint/OAS tests; the §7 non-change checklist verified in review; working tree left uncommitted for developer review.
