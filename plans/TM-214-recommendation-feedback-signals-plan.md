# TM-214 — Capture Recommendation Feedback & Behavioral Signals — Implementation Plan

Status: Approved — implemented and verified (TM-214 Step 4)
Binding design baseline: `specs/TM-214-recommendation-feedback-signals-spec.md`
(= TM-214 Step 2A FINAL FROZEN DESIGN)
Branch: `feature/phuctv-tm214-recommendation-feedback-signals`
Workflow: AGENTS.md §1 — spec-first, TDD (red → green → refactor), atomic tasks,
zero regression. Test-first is mandatory: implementation code written before its
test is deleted and redone (AGENTS.md §1.2.4).

---

## 1. Preconditions

- TM-214 Step 2A frozen design is binding; this spec is approved by the developer.
- TM-214 Step 1 / 1B audits are the evidence base (HEAD `e8013f98a49b9fa1fbe4fcc412b562f7db8c37eb`).
- No commit/push unless the developer explicitly requests it (AGENTS.md §5.1).

## 2. Baseline Gate

- Confirm branch `feature/phuctv-tm214-recommendation-feedback-signals`; HEAD is
  the verified TM-214 baseline or a developer-confirmed descendant; no tracked
  `src/`/`tests/` diff; only the two expected untracked Step-3 artifacts (this
  spec and its plan) present; any other unexpected change blocks implementation.
- Run `dotnet test` from the repository root — full suite green before any change.
- A literally empty `git status` is not required; the state above is the gate.

## 3. Workstreams

| Workstream | Contents | Tasks |
|---|---|---|
| A — Domain (enums + entity) | `RecommendationEventType`, `RecommendationCaptureSource`, `RecommendationBehaviorEvent` + factory | T1 |
| B — Infrastructure (EF configuration) | mapping to `social.RecommendationBehaviorEvents`; schema-parity test | T2 |
| C — Migration | NEW idempotent SQL file under `database/migrations/` + test-database application | T3 |
| D — Application feature | error codes, command, validator, response, handler (context + idempotency) | T4 |
| E — API | request DTO, controller, OpenAPI test | T5 |
| F — Testing | unit, persistence parity, SQL integration (incl. concurrency), OAS | T1–T6 |
| G — Regression + docs | scheduling non-change gate, handoff | T7–T8 |

## 4. Exact Production Files Expected to Be Added / Modified

**Added:**

| # | File | Contents |
|---|---|---|
| P1 | `src/TripMate.Domain/Enums/RecommendationEventType.cs` | enum {Like, Dislike, Skip, Reorder} |
| P2 | `src/TripMate.Domain/Enums/RecommendationCaptureSource.cs` | enum {Itinerary, Explore, PoiDetail} |
| P3 | `src/TripMate.Domain/Entities/RecommendationBehaviorEvent.cs` | sealed entity, private ctor + setters, factory `Create(...)` enforcing per-type position rules and the source-context invariant; `event_id` identity |
| P4 | `src/TripMate.Infrastructure/Persistence/Configurations/RecommendationBehaviorEventConfiguration.cs` | `ToTable("RecommendationBehaviorEvents", "social")`; column mappings per spec §9–11; `HasConversion<string>()` + `HasMaxLength(20)` + `IsUnicode(false)` for both enum columns; `occurred_at_utc` `.AsUtcDateTime2()` + default `SYSUTCDATETIME()`; UNIQUE `(traveler_user_id, client_event_id)`; indexes `(traveler_user_id, occurred_at_utc)`, `(poi_id)`, `(itinerary_id)`; FKs NO ACTION |
| P5 | `src/TripMate.Application/Features/RecommendationFeedback/Common/FeedbackErrorCodes.cs` | `feedback.poi_not_found`, `feedback.itinerary_not_found`, `feedback.context_mismatch`, `feedback.event_token_conflict` (pattern `SchedulingErrorCodes.cs`) |
| P6 | `src/TripMate.Application/Features/RecommendationFeedback/Capture/CaptureRecommendationFeedbackCommand.cs` | command record: `TravelerUserId` (principal-derived), `ClientEventId`, `EventType`, `PoiId`, `ItineraryId?`, `OriginalPosition?`, `NewPosition?`, `Source` |
| P7 | `src/TripMate.Application/Features/RecommendationFeedback/Capture/CaptureRecommendationFeedbackCommandValidator.cs` | FluentValidation: shape/enum rules + combination rules (source-context coupling; Skip/Reorder ⇒ Itinerary source; per-type position nullability; `ClientEventId ≠ Empty`) — **no** context/ownership/ambiguity checks (handler-owned) |
| P8 | `src/TripMate.Application/Features/RecommendationFeedback/Capture/CaptureRecommendationFeedbackCommandHandler.cs` | flow per spec §14 (frozen ordering): **idempotency pre-check FIRST** (after principal identity + shape validation) — lookup by `(traveler_user_id, client_event_id)` AsNoTracking; existing token → compare persisted payload (`eventType`, `poiId`, `itineraryId`, `originalPosition`, `newPosition`, `source`) → identical → 200 replay / different → 409 **with no revalidation of current POI/itinerary/position state** → only on miss: POI load (Active gate for direct only; existence only for contextual) → itinerary load + ownership → per-type context validation (Skip strict item match — unique per `UQ_ItineraryItems`; Like/Dislike/Reorder membership requiring **exactly one** matching item, 0 or >1 → 422; Reorder range check + `original ≠ new` → 422) → derive `was_mandatory` → append via factory + `SaveChangesAsync` → race per Step 3B: on `DbUpdateException` (Application precedent `AuditFailureBehaviour.cs:41`/`GoogleAuthCommandHandler.cs:188`; **no SqlClient dependency**): re-query winner AsNoTracking, compare → 200/409, absent → rethrow; **no second `SaveChangesAsync`, no `Entry(...)/Detach`** (not exposed by `IApplicationDbContext`) |
| P9 | `src/TripMate.Application/Features/RecommendationFeedback/Capture/CaptureRecommendationFeedbackResponse.cs` | response record + internal replay flag (`IsReplay`) driving 200 vs 201 |
| P10 | `src/TripMate.Api/Controllers/V1/Requests/RecommendationFeedbackRequests.cs` | request DTO: `ClientEventId`, `EventType`, `PoiId`, `ItineraryId?`, `OriginalPosition?`, `NewPosition?`, `Source` — no traveler/wasMandatory/schedulingRequest/rating fields |
| P11 | `src/TripMate.Api/Controllers/V1/RecommendationFeedbackController.cs` | `[Route("api/v1/recommendation-feedback")]`, `[HttpPost]`, `[Authorize(Roles = nameof(UserRole.Traveler))]` (repo convention: `SchedulingRequestsController.cs:16`); builds command with `currentUserService.UserId.Value`; maps 201 new / 200 replay / 400 / 404 / 409 / 422 via the project `Result<T>`/`HandleFailure` convention |

**Modified:**

| # | File | Change |
|---|---|---|
| M1 | `src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs` | + `DbSet<RecommendationBehaviorEvent> RecommendationBehaviorEvents { get; }` only |
| M2 | `src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs` | + the same DbSet only |

**Created later (implementation, NOT Step 3):**

| # | File | Contents |
|---|---|---|
| P12 | `database/migrations/<YYYYMMDD>_add_recommendation_behavior_events.sql` | NEW idempotent migration materializing spec §9 (guarded style of `20260914`/`20260915`; FKs NO ACTION; CHECKs `SourceContext` (incl. `was_mandatory` coupling), `TypeSource`, `TypePositions`; UNIQUE + indexes) |

## 5. Exact Test Projects / Files Expected

| # | File | Contents |
|---|---|---|
| T1 | `tests/TripMate.Application.UnitTests/Domain/RecommendationBehaviorEventTests.cs` | factory invariants: per-type position rules, source-context invariant, original ≠ new |
| T2 | `tests/TripMate.Infrastructure.UnitTests/Persistence/RecommendationBehaviorEventPersistenceModelTests.cs` | EF metadata/schema parity only (pattern `TravelerProfilePersistenceModelTests`/`PoiPersistenceModelTests`): column names, CLR types, nullability, VARCHAR(20) non-Unicode enum mappings, `AsUtcDateTime2`, PK/UNIQUE/indexes, FKs NO ACTION |
| T3 | `tests/TripMate.Application.UnitTests/Features/RecommendationFeedback/CaptureRecommendationFeedbackCommandValidatorTests.cs` | all 400-level rules incl. per-type position nullability, source coupling, empty `clientEventId` |
| T4 | `tests/TripMate.Application.UnitTests/Features/RecommendationFeedback/CaptureRecommendationFeedbackCommandHandlerTests.cs` | handler branches: 404s (missing/inactive-direct POI, missing/not-owned itinerary), 422s (strict Skip match, ambiguous membership 0/>1, range, Reorder `original == new`), `was_mandatory` derivation, idempotent 200/409 pre-check path; **ordering regressions**: identical retry after the POI later became inactive → 200 existing (never 404); token reuse with an otherwise-invalid different payload → 409 (never 404/422) |
| T5 | `tests/TripMate.Api.IntegrationTests/RecommendationFeedback/CaptureRecommendationFeedbackSqlServerTests.cs` | real SQL Server end-to-end: seeds via the existing `SqlServerTestDatabase`/`SeedAsync` raw-SQL pattern (this project's own pattern — no cross-test-project helpers); full required-scenario list + migration applied + concurrent-duplicate race + CHECK-constraint defense-in-depth (adversarial `Skip`+`Explore`+NULL-itinerary INSERT rejected by the schema) + retry-after-POI-inactive ordering case (200 replay, never 404) |
| T6 | `tests/TripMate.Api.IntegrationTests/RecommendationFeedback/RecommendationFeedbackOpenApiTests.cs` | OpenAPI gains exactly the new operation (pattern `CreateSchedulingRequestOpenApiTests`) |

Unchanged: every existing test file (their green run is the regression gate).
A test-data seeding helper, if needed, lives inside the Application.UnitTests
test files only (builder-ownership rule from TM-213 Step 2A).

## 6. Migration Workstream

- T3 creates **one new** file `database/migrations/<YYYYMMDD>_add_recommendation_behavior_events.sql`
  (actual date at implementation), idempotent and guarded per the
  `20260914`/`20260915` style; materializes spec §9 exactly (columns, three
  FKs NO ACTION, three CHECK constraints (`SourceContext` with `was_mandatory`
  coupling, `TypeSource`, `TypePositions`), UNIQUE + three indexes).
- Migration-application evidence (audited): `SqlServerTestDatabase.InitializeAsync`
  (`SqlServerTestDatabase.cs:230–252`) applies **only** `tripmate_schema_v7.sql`
  — migrations are NOT applied by the fixture today. The **one** allowed
  test-infrastructure change is
  `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerTestDatabase.cs`:
  after schema execution, enumerate `database/migrations/*.sql` (repo-root
  resolution pattern already exists, `PasswordResetSchemaGuardTests.cs:42`) and
  execute them in filename order via the existing `ExecuteScriptAsync`,
  mirroring the seeded-Docker flow (`DatabaseScriptSafetyTests.cs:15–16`).
- Historical migrations are never modified; no DDL outside `database/migrations/`.

## 7. Entity / Enums / Configuration Workstream

T1–T2 (P1–P4, T1, T2). The factory is the single enforcement point for
per-type position rules and the source-context invariant; the configuration is
mapping-only. Parity test asserts metadata only — no behavior.

## 8. Application Command / Validator / Handler / DTO Workstream

T4 (P5–P9, T3, T4). Status-code ownership split (frozen):
- **Frozen handler ordering**: the idempotency lookup (200/409) runs **first**
  — after principal identity + shape validation, and **before** all
  resource/context validation; the 404/422 checks below apply **only to the
  append path** (an existing token never revalidates current POI/itinerary/
  position state).
- **Validator → 400**: shape/enum; per-type position nullability; `source`
  coupling; Skip/Reorder ⇒ Itinerary source; empty `clientEventId`.
- **Handler → 404 (append path only)**: POI missing; direct feedback on
  inactive POI; itinerary missing/not owned (same 404 code, repo convention).
- **Handler → 422 (append path only)**: Skip strict item match fails; POI
  resolves to zero or multiple itinerary items (ambiguous membership is
  fail-closed); Reorder positions out of `1..count`; Reorder
  `original == new`.
- **Handler → 409 (before all 404/422)**: same token + different payload
  (compares persisted client-supplied fields; server-derived `was_mandatory`
  excluded) — even when the new payload would otherwise be invalid.
- **Handler → 200**: same token + same payload (existing event returned).

## 9. API / Controller / OpenAPI Workstream

T5 (P10–P11, T6). Controller maps `IsReplay` → 200 vs 201; failure mapping via
`HandleFailure` (AGENTS.md §2.3). OpenAPI test asserts exactly one added
operation and unchanged existing contract snapshots.

## 10. Authorization / Context Validation Workstream

Within T4/T5: `traveler_user_id` from `ICurrentUserService` only; itinerary
ownership → 404 (not-owned same code, per `CreateTravelGroupCommandHandler.cs:17–23`);
contextual POI membership → 422; direct POI Active gate → 404; inactive
contextual POI accepted (historical preservation). All 404/422 resource/context
checks run **only on the append path** — the idempotency lookup precedes them
and never revalidates current POI/itinerary/position state for an existing
token.

## 11. Idempotency / Concurrency Workstream

Within T4 (logic) and T6 (race proof): **idempotency-first ordering
unit-tested** — the `(traveler_user_id, client_event_id)` lookup is the first
handler step and precedes all resource/context validation; on
`DbUpdateException` (Application precedent `AuditFailureBehaviour.cs:41` /
`GoogleAuthCommandHandler.cs:188` — **no provider-specific `SqlException`
inspection; `TripMate.Application` has no `Microsoft.Data.SqlClient`
reference**): re-query the winner AsNoTracking by
`(traveler_user_id, client_event_id)`, compare payload → 200/409; winner
absent → rethrow (fail-closed). **No second `SaveChangesAsync` and no
`Entry(...)/Detach`** — `IApplicationDbContext` exposes neither `Entry` nor
`ChangeTracker` (verified: DbSets, `SaveChangesAsync`, `ClearTrackedEntities()`,
transaction executors only); the abandoned `Added` entity is discarded with the
scoped context, and `ClearTrackedEntities()` (`IApplicationDbContext.cs:82–87`)
is the documented fallback only if a future variant retries saving in-request.
Concurrent-duplicate SQL test proves exactly one row. No new abstraction
required — the `DbUpdateException` catch is already precedented in Application.

## 12. Testing Workstream

T1–T6 deliver the full required scenario list (spec §24; every scenario named
in the Step-3 brief is mapped to T3/T4/T5/T6 above). Builder/seed helpers stay
inside their own test projects.

## 13. Regression / Scheduling Non-Change Gate

- T7: full `dotnet test` green **including all pre-existing suites unmodified**.
- `git status --short -- src tests database` proves no unexpected tracked
  changes; frozen files (§17) byte-identical; scheduling test files untouched.

## 14. Documentation / Handoff Workstream

T8: XML docs on the entity/factory (capture-only semantics, append-only),
handoff notes to TM-215 (query patterns) and the future Reviews UC (spec §18),
privacy/consent flag for TM-215 consumption (spec §22).

## 15. Implementation Task Sequence

- **T0 — Baseline gate** (§2): verified baseline + full suite green.
- **T1 — Domain** (RED→GREEN): `RecommendationBehaviorEventTests` first
  (per-type position rules, source-context invariant), then P1–P3.
- **T2 — Infrastructure** (RED→GREEN): parity test first, then P4.
- **T3 — Migration**: P12 (new guarded SQL file); fixture-application
  checkpoint; historical migrations untouched.
- **T4 — Application feature** (RED→GREEN): validator tests, then handler
  tests, then P5–P9 (+ M1/M2 DbSets).
- **T5 — API** (RED→GREEN): OpenAPI test first, then P10–P11.
- **T6 — SQL integration**: T5 file scenarios + concurrency race.
- **T7 — Regression gate**: full suite; frozen-file byte-identity check.
- **T8 — Docs/handoff + final two-stage review** (spec compliance, then code
  quality — AGENTS.md §1.2.4–5). No commit/push unless the developer asks;
  suggested conventional commit: `feat(feedback): capture recommendation behavior signals`.

Each task ends with the full suite green.

## 16. Stop Conditions / Blocked Conditions

- T0 gate red or unexpected tracked changes → STOP.
- Any task requiring modification of a §17 file → STOP (design conflict).
- Migration apply fails the contract-`THROW` checks → STOP (schema conflict).
- Any requirement to change ranking/scoring/feasibility → OUT OF SCOPE, STOP.
- Verified tool-environment contradiction (session protocol) → halt writes,
  report, await developer verification.

## 17. Explicit Files That MUST NOT Change

`src/TripMate.Application/Features/Scheduling/**` (including
`TravelerPreferenceScoring.cs`, `CreateSchedulingRequest*`,
`ItineraryGenerationService.cs`, `GenerationInput.cs`,
`SchedulingGenerationOptions.cs`, `SchedulingErrorCodes.cs`),
`src/TripMate.Api/Controllers/V1/SchedulingRequestsController.cs`,
`src/TripMate.Domain/Entities/Review.cs` + `ReviewConfiguration.cs`,
`Itinerary.cs`, `ItineraryItem.cs`, `SchedulingRequest.cs`, `TravelerProfile.cs`,
`src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs` and
`ApplicationDbContext.cs` (beyond the one DbSet each),
`database/tripmate_schema_v7.sql`, all historical files under
`database/migrations/`, `catalog.FavoritePOIs` (no mapping, no DDL),
`Program.cs` / `DependencyInjection.cs` (no new services), and every existing
test file **except** `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerTestDatabase.cs`
(migration-application extension only, per §6 — evidence-backed).

## 18. Definition of Done

All spec acceptance criteria (spec §24) demonstrably met; full suite green
including unmodified existing tests; §17 byte-identity verified; working tree
left uncommitted for developer review.
