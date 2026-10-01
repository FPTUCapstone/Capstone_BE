# TM-217 Implementation Plan — UC-10 LLM Explanation for CSP-Validated Itinerary

## 1. Document Control

| Field | Value |
| --- | --- |
| Task ID | TM-217 |
| Use case | UC-10 — Generate LLM Explanation for CSP-Validated Itinerary |
| Approved spec | `specs/TM-217-itinerary-explanation-spec.md` (Status: Spec Ready) |
| Date | 2026-10-01 |
| Branch | `feature/phuctv-tm217-itinerary-explanation` |
| Baseline | `develop` @ `37dfabbe77714eba5f4b6a58d572d32a9c8d5692` (TM-216 present; HEAD = merge-base = local develop = origin/develop at plan time) |
| Execution model | TDD per AGENTS.md §1.2 stage 4: failing test first, minimal code, verify, then next item |

This plan preserves every binding decision in the approved spec (§5 decisions 1–20,
§7 two-phase flow, §10 whole-response validation, §11 fallback strings, §12–§15
persistence/replay/timeout semantics). It introduces no new architecture.

## 2. Verified Preconditions and Baselines (recorded on this branch, 2026-10-01)

- Branch verified: `feature/phuctv-tm217-itinerary-explanation`; working tree clean
  except the untracked approved spec.
- Merge-base check: HEAD = local `develop` = `origin/develop` = `37dfabb`; TM-216
  commits (`29b0a1f`, `7885a66`, `cdc56a8`, `37dfabb`) are in the baseline. Not stale —
  no rebase/merge required or performed.

Baseline gates recorded on `37dfabb` before any TM-217 change:

| Gate | Command | Recorded baseline |
| --- | --- | --- |
| Build | `dotnet build TripMate.slnx` | 0 warnings / 0 errors |
| Application | `dotnet test tests/TripMate.Application.UnitTests` | 925 passed / 0 failed |
| Infrastructure | `dotnet test tests/TripMate.Infrastructure.UnitTests` | 173 passed / 0 failed / 1 skipped (`CloudinarySmokeTest`) |
| Generator | `dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~ItineraryGenerationServiceTests"` | 28 passed / 0 failed |
| SQL API | `TRIPMATE_SQLSERVER_TEST_CONNECTION=… dotnet test tests/TripMate.Api.IntegrationTests` | 428 total; observed 427/1 — the 1 is the documented environmental flake `ConcurrentRejectRequests_OneSucceedsOneConflicts_AndCreatesOneDecision` (`The entry point exited without ever building an IHost` at factory startup, pre-request); isolated re-run passes 1/1 in 5 s |
| Whitespace | `git diff --check` | clean |

SQL gate rule for all TM-217 work items: the suite must be green **except** the
documented flake family (host-startup race, log-file lock). Any other failure, or a
documented-flake failure that does not pass an isolated re-run, is a STOP condition.

## 3. Binding Architecture (from approved spec — not negotiable)

- Scheduler final authority; explanation only after a successfully validated itinerary.
- T1 (existing serializable scheduling transaction) commits **unchanged** before
  explanation begins; provider invoked exactly once per fresh successful generation,
  never on replay/infeasible paths, never inside T1.
- LLM output is text-only and cannot modify POI, order, arrival/departure, duration,
  cost, mandatory status, travel decisions, or feasibility.
- `RecommendationReason` unchanged; new separate persisted per-item
  `FriendlyExplanation`; no itinerary-level summary.
- Normally completed Phase 2 persists validated LLM text or deterministic Vietnamese
  fallback text; accepted NULL cases: caller cancellation after T1 commit, process
  termination between T1/T2, T2 persistence failure (never weaken/rollback scheduling).
- Timeout 5 s; whole-response fallback on any §10 validation violation; Vietnamese text.
- Payload limits per spec §8/§9 (preference tokens, POI name/category/tags, timing/cost
  context only — no identity, coordinates, scores, aggregates, auth material).

## 4. Work Items

### W00 — Approved specification and implementation plan

- **Goal:** add the approved TM-217 specification and implementation plan to version
  control; documentation only, with zero production, test, database, or configuration
  changes.
- **Files:**
  - `specs/TM-217-itinerary-explanation-spec.md`
  - `plans/TM-217-itinerary-explanation-plan.md`
- **Gates:** `git diff --check`; verify W00 includes only these two documentation files
  and no production, test, database, or configuration files.
- **Expected:** documentation only; implementation has not started.
- **STOP condition:** either document contains unresolved BLOCKED architecture decisions.
- **Commit:** `docs(trip): add approved TM-217 spec and implementation plan`

### W01 — Domain + DTO contract

- **Goal:** add the `FriendlyExplanation` domain field/guard and the additive DTO
  field; no provider integration yet.
- **Files:**
  - `src/TripMate.Domain/Entities/ItineraryItem.cs` — `FriendlyExplanationMaxLength = 500`,
    `string? FriendlyExplanation { get; private set; }`,
    `internal void AttachFriendlyExplanation(string? text)` using the same
    normalization as `RecommendationReason` (trim → null-if-empty → throw on >500).
  - `src/TripMate.Application/Features/Scheduling/Common/SchedulingResponseDto.cs` —
    `SchedulingItemDto` gains trailing `string? FriendlyExplanation`.
  - `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs` —
    mechanical DTO-argument update only: fresh path passes `null`, replay path passes
    `item.FriendlyExplanation`. No Phase 2 logic in this item.
- **Behavior added:** domain guard; optional DTO field. Zero scheduling semantics change.
- **Tests:**
  - `tests/TripMate.Application.UnitTests/Domain/ItineraryItemTests.cs` —
    AttachFriendlyExplanation: sets trimmed text; whitespace → null; 500 chars OK;
    501 chars throws; `RecommendationReason` untouched by the new member.
- **Gates:** build; Application tests; Generator suite; `git diff --check`.
- **Expected:** build 0/0; Application 925 + new domain tests, 0 failed; Generator 28/0.
- **Production risk:** low (additive). DTO is a positional record — the two handler
  constructions are the only call sites in `src/**`.
- **STOP condition:** any existing test asserting exact `SchedulingItemDto` arity breaks
  in a way that forces behavior (not plumbing) changes in the handler.
- **Commit:** `feat(trip): add itinerary item friendly explanation field`

### W02 — Database + EF persistence

- **Goal:** EF mapping + one idempotent migration for the new nullable column only.
- **Files:**
  - `src/TripMate.Infrastructure/Persistence/Configurations/ItineraryItemConfiguration.cs` —
    `builder.Property(item => item.FriendlyExplanation).HasColumnName("friendly_explanation")
    .HasMaxLength(ItineraryItem.FriendlyExplanationMaxLength)` (Unicode → NVARCHAR(500) NULL).
  - `database/migrations/YYYYMMDD_add_itinerary_item_friendly_explanation.sql` — idempotent
    script following `20260918_add_audit_result_reason.sql` pattern: `SET XACT_ABORT ON`,
    transaction, existence guard on `planning.ItineraryItems`, `COL_LENGTH` guard,
    `ALTER TABLE … ADD friendly_explanation NVARCHAR(500) NULL`; no backfill, no
    constraint, no other object. Date prefix = authoring date.
- **Behavior added:** persistence capability only; historical rows stay NULL.
- **Tests:**
  - `tests/TripMate.Api.IntegrationTests/Infrastructure/ItineraryFriendlyExplanationMigrationSqlServerTests.cs`
    (mirror `AuditMigrationSqlServerTests`): column added on a fresh database; column
    added on an existing database with rows (historical rows remain NULL); script
    applied twice succeeds (idempotency).
  - Existing `DatabaseScriptSafetyTests` must stay green (script conventions).
- **Gates:** build; migration SQL tests (SQL env); `DatabaseScriptSafetyTests`;
  `git diff --check`; confirm `git diff develop --stat -- database` shows exactly one
  new migration file.
- **Expected:** migration applies clean on fresh + existing DB and re-runs harmlessly.
- **Production risk:** low; single nullable additive column.
- **STOP condition:** `SqlServerTestDatabase` schema application conflicts with the new
  script, or `DatabaseScriptSafetyTests` rejects the pattern — resolve with developer,
  do not invent a second migration mechanism.
- **Commit:** `feat(db): add itinerary item friendly explanation column`

### W03 — Application explanation contracts

- **Goal:** provider port, immutable input/output records, all-or-nothing validator,
  deterministic Vietnamese fallback constants, privacy-safe input builder.
- **Files (all new, `src/TripMate.Application/Features/Scheduling/Explanation/`):**
  - `IItineraryExplanationProvider.cs` — `Task<Result<ItineraryExplanationResult>>
    ExplainAsync(ItineraryExplanationInput input, CancellationToken cancellationToken)`.
  - `ExplanationProviderErrorCodes.cs` — `Network`, `Quota`, `ServerError`,
    `InvalidResponse` (mirror `PoiRankingProviderErrorCodes`).
  - `ItineraryExplanationInput.cs` — `ItineraryExplanationInput` / `…Item`
    (`SequenceNo`, `PoiId`, `PoiName`, `CategoryName`, `TagNames`, `Kind`,
    `IsMandatory`, arrival/departure, `StayDurationMinutes`, `EstimatedCost`,
    `TravelDurationToNextMinutes`, `CspRecommendationReason`) / `…Context`
    (normalized `PreferenceTokens`, `StartAtUtc`, `TimeZoneId`, `TotalDurationMinutes`).
    Static `Create(...)` factory accepts the validated `GeneratedItineraryPlan`, an
    explicit immutable descriptive metadata lookup keyed by `PoiId` (conceptually
    `IReadOnlyDictionary<long, ExplanationPoiMetadata>` containing only `PoiName`,
    `CategoryName`, and `TagNames`), and normalized `PreferenceTokens`. It normalizes/
    deduplicates `TagNames` (trim, dedupe, stable order) and preference tokens — the
    only place payload shaping happens. Only items whose `PoiId` exists in the lookup
    receive descriptive metadata. Missing metadata maps to `CategoryName = null` and
    `TagNames = []`; POI-less rest items map to `PoiId = null`, `PoiName = null`,
    `CategoryName = null`, and `TagNames = []`. Missing descriptive metadata never
    fails scheduling. Do not add these fields to `GeneratedItineraryPlan` or other CSP
    records, and do not issue a post-T1 DB query solely to obtain them.
  - `ItineraryExplanationResult.cs` — `…Result` / `…ItemResult`
    (`SequenceNo`, `PoiId`, `FriendlyExplanation`).
  - `ItineraryExplanationValidator.cs` — spec §10 rules, all-or-nothing: exact count;
    every input sequence present exactly once; no unknown/duplicate sequence; exact
    `poiId` equality (null only for POI-less rest items); text non-empty after trim;
    ≤ 500 chars (no truncation); transport-level single-output rule enforced upstream.
  - `ItineraryExplanationFallback.cs` — the four exact Vietnamese constants from spec
    §11 + resolver by (Kind, IsMandatory, has-PoiId).
  - `ItineraryExplanationExecutionOptions.cs` — Application execution policy with the
    following frozen semantics (the exact C# form may follow repository conventions):
    ```csharp
    sealed class ItineraryExplanationExecutionOptions
    {
        public static readonly TimeSpan DefaultProviderTimeout = TimeSpan.FromSeconds(5);

        public TimeSpan ProviderTimeout { get; init; } = DefaultProviderTimeout;
    }
    ```
    The default is exactly 5 seconds and `ProviderTimeout` must be greater than zero.
    This is Application execution policy, not Gemini transport authority. TM-217 adds
    no user-facing/configurable timeout and no `AiExplanation:Timeout` appsettings field.
- **Behavior added:** contracts and pure logic only; nothing wired into the handler yet.
- **Tests (new, `tests/TripMate.Application.UnitTests/Features/Scheduling/Explanation/`):**
  - `ItineraryExplanationValidatorTests` — full §10 matrix (valid; wrong count; missing
    item; unknown item; duplicate sequence; poiId mismatch incl. null-cases; empty text;
    whitespace text; 501 chars fails; 500 chars passes; null payload).
  - `ItineraryExplanationFallbackTests` — exact constant per condition; total length ≤ 500.
  - `ItineraryExplanationInputPrivacyTests` — serialize `ItineraryExplanationInput` and
    assert: category/tags are supplied when metadata exists; tags are normalized and
    deduplicated; missing metadata safely becomes null/empty and does not affect the
    scheduler result; POI-less rest gets null/empty metadata; NO traveler identity,
    email/account id, coordinates, request hash, idempotency key, `BaseScore`, `AiScore`,
    `EffectiveDesirabilityScore`, behavior aggregates, or auth material.
  - `ItineraryExplanationExecutionOptionsTests` — default provider timeout is exactly
    5 seconds; a non-positive timeout is rejected if options validation is implemented.
- **Gates:** build; Application tests; `git diff --check`.
- **Expected:** Application 925 + W01 + W03 tests, 0 failed.
- **Production risk:** none (new isolated files).
- **STOP condition:** if validator semantics require modifying scheduling types or the
  plan records (they must only be read), stop — spec violation.
- **Commit:** `feat(trip): add itinerary explanation contracts and validator`

### W04 — Infrastructure Gemini provider

- **Goal:** HTTP adapter for `IItineraryExplanationProvider` mirroring
  `HttpPoiRankingProvider` transport exactly; options + validator + DI.
- **Files:**
  - `src/TripMate.Infrastructure/AiExplanation/HttpItineraryExplanationProvider.cs` (new) —
    native Gemini `v1/interactions` via `HttpClient` (no SDK): `x-goog-api-key` header,
    `Store: false`, strict JSON `response_format` schema
    `{items: [{sequenceNo (integer, required), poiId (integer | null, required), friendlyExplanation (string, required)}]}`,
    where the structured-output schema uses the exact nullable representation supported
    by the repository's existing Gemini pattern. `poiId` is an integer for POI-backed
    items and null for POI-less rest/free-time items. Output `sequenceNo` must match an
    expected item and output `poiId` must exactly equal that input item's `PoiId`; null
    is valid only when the corresponding input `PoiId` is null. Any wrong null/non-null
    value invalidates the whole response and triggers fallback, with no partial repair,
    system instruction requiring one short Vietnamese sentence (≤ 500 chars) per item in
    order, no scheduling changes, no identity; error mapping HttpRequestException/IOException
    → `Network`, 429 → `Quota`, 5xx → `ServerError`, all other statuses / malformed JSON /
    missing or multiple model outputs → `InvalidResponse`.
  - `src/TripMate.Infrastructure/AiExplanation/ExplanationProviderOptions.cs` (new) —
    section `AiExplanation`: `Enabled` (default false), `Endpoint`, `ApiKey`, `ModelName`.
  - `src/TripMate.Infrastructure/AiExplanation/ExplanationProviderOptionsValidator.cs` (new) —
    when Enabled: absolute HTTPS `Endpoint`, non-empty `ApiKey` and `ModelName`; when
    disabled: success (mirror `PoiRankingProviderOptionsValidator`).
  - `src/TripMate.Infrastructure/DependencyInjection.cs` —
    `AddOptions<ExplanationProviderOptions>().Bind(configuration.GetSection("AiExplanation"))
    .ValidateOnStart()` + validator + `AddHttpClient<IItineraryExplanationProvider,
    HttpItineraryExplanationProvider>()`; wire the already-defined Application-side
    `ItineraryExplanationExecutionOptions` if DI registration is needed, and register
    the `explanationProviderEnabled` bool (from
    `IOptions<ExplanationProviderOptions>.Value.Enabled`) so the handler's new
    constructor parameters resolve. W04 does not create or own the Application timeout
    policy and does not add an appsettings timeout field.
  - `src/TripMate.Api/appsettings.json` — add `AiExplanation` section with
    `Enabled: false`, the same interactions endpoint/model defaults as `AiRanking`,
    no committed ApiKey (AGENTS.md §5.4).
- **Behavior added:** transport + configuration only; the handler is not yet changed.
- **Tests (new, `tests/TripMate.Infrastructure.UnitTests/AiExplanation/`):**
  - `HttpItineraryExplanationProviderTests` — mirror `HttpPoiRankingProviderTests` with
    `StubHttpMessageHandler`/`ThrowingHttpMessageHandler` (controlled handlers only,
    zero real network): valid body → parsed `ItineraryExplanationResult`; the
    status-code error matrix (429→Quota, 500/503→ServerError, 400/401/403/404→InvalidResponse);
    transport exception→Network; malformed JSON / missing or multiple `model_output` →
    InvalidResponse; request assertions: POST to configured endpoint, `x-goog-api-key`
    header, `store=false`, schema present, serialized input contains no identity fields.
  - `ExplanationDependencyInjectionTests` — mirror `AiRankingDependencyInjectionTests`:
    Enabled=true without endpoint/key/model fails validation; Enabled=false passes;
    the already-defined Application execution option resolves if DI registration is used.
- **Gates:** build; Infrastructure tests; one API factory boot smoke (existing
  integration test) to prove `ValidateOnStart` passes with `Enabled=false`.
- **Expected:** Infrastructure 173/0/1 + new tests, 0 failed.
- **Production risk:** low (additive); DI misconfiguration surfaces at startup via
  `ValidateOnStart` — covered by the boot smoke.
- **STOP condition:** if the interactions payload shape cannot match the ranking
  provider's proven transport, stop and align with `HttpPoiRankingProvider` — do not
  invent a new wire format.
- **Commit:** `feat(trip): add Gemini itinerary explanation provider`

### W05 — Post-commit orchestration

- **Goal:** integrate Phase 2 into `CreateSchedulingRequestCommandHandler` exactly per
  spec §7: T1 unchanged and commits first; one provider attempt; fallback on any
  failure; short T2 persistence; response carries computed explanations.
- **Files:**
  - `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs` —
    constructor gains `IItineraryExplanationProvider? explanationProvider = null`,
    `ItineraryExplanationExecutionOptions? explanationOptions = null`,
    `bool explanationProviderEnabled = false`,
    `ILogger<CreateSchedulingRequestCommandHandler>? logger = null` (defaults keep every
    existing direct construction compiling and behave as provider-disabled).
    Restructure: the T1 lambda captures `(schedulingRequest, itinerary, plan)` into
    locals instead of returning the DTO on the success path (infeasible and replay
    paths return early exactly as today and never reach Phase 2). After the T1 helper
    returns success: check cancellation (propagate — decision-10 exception), then build
    `ItineraryExplanationInput.Create(...)` from the validated plan, the immutable POI
    descriptive metadata lookup, and normalized preference tokens. Build or retain that
    lookup during the existing pre-generation candidate-loading/ranking phase and reuse
    it after T1 commits; do not mutate scheduler/CSP records and do not query the DB after
    T1 solely for category/tags. Run one provider call under a linked CTS using
    `CancelAfter(explanationOptions.ProviderTimeout)` (default exactly 5 seconds; no
    magic timeout literal in the handler), validate
    (W03) → LLM text or fallback, log outcome (provider, enabled, itemCount, latencyMs,
    outcome, fallbackUsed, timedOut), persist via **one** T2 attempt: load the
    itinerary's `ItineraryItem`s with change tracking (no `AsNoTracking`, AGENTS.md
    §3.2), `AttachFriendlyExplanation` per item, single `SaveChangesAsync` (EF implicit
    transaction = T2); T2 failure → log `persistence-failed` (warning), swallow, keep
    computed text in the response; map and return the DTO with per-item explanations.
    T1 logic, guards, `PersistInfeasibleAsync`, replay, request hash: byte-for-byte
    unchanged.
- **Behavior added:** Phase 2 only. Scheduler semantics, feasibility, replay identity:
  unchanged (spec decision 20).
- **Tests (`tests/TripMate.Application.UnitTests/Features/Scheduling/Create/CreateSchedulingRequestCommandHandlerTests.cs`):**
  - provider success → DTO items carry validated LLM text; persisted
    `FriendlyExplanation` == LLM text; provider called exactly once (counting fake).
  - provider disabled → deterministic fallback persisted/returned; zero provider calls.
  - provider `Result` failure per error code (Network/Quota/ServerError/InvalidResponse)
    → fallback; zero scheduling impact.
  - provider timeout (fake that honors the token) → fallback.
  - input metadata mapping → category/tags supplied when available; tags normalized and
    deduplicated; missing metadata becomes null/empty without affecting the scheduling
    result; POI-less rest gets null/empty metadata.
  - one representative invalid LLM response → whole-response fallback (full matrix
    already covered at validator level in W03).
  - **invariance test:** run the same input with provider ON (valid text) and OFF;
    assert identical POI IDs, sequence/order, arrival/departure, stay durations,
    estimated costs, mandatory flags, travel minutes, feasibility outcome, and
    `RecommendationReason` — only `FriendlyExplanation` differs.
  - infeasible path → provider zero calls, response shape unchanged.
- **Gates:** build; Application tests; Generator suite; `git diff --check`.
- **Expected:** Application grows by W05 tests; all pre-existing scheduling assertions
  unchanged and green.
- **Production risk:** medium — this is the only handler restructure. Mitigation: the
  925-test suite plus the explicit invariance test; T1 body kept untouched.
- **STOP condition:** if Phase 2 cannot be implemented without moving the provider call
  inside T1, changing T1's isolation/semantics, or altering `PersistInfeasibleAsync`/
  replay behavior — stop; that violates spec §7.
- **Commit:** `feat(trip): attach explanation after scheduling commit`

### W06 — T2 failure / cancellation / replay semantics

- **Goal:** prove the critical best-effort edges (spec §7 binding clarification,
  decision-10 exceptions).
- **Files:**
  - `tests/TripMate.Application.UnitTests/Features/Scheduling/Create/CreateSchedulingRequestCommandHandlerTests.cs`
    (+ test fixture helper): a controllable persistence seam — decorate/wrap
    `IApplicationDbContext` (or use a `SaveChangesInterceptor`-style fake) that can fail
    the Nth `SaveChangesAsync` (T1 = call 1, T2 = call 2) and a provider fake that can
    observe/trigger caller cancellation.
- **Tests to add:**
  - T2 persistence failure → handler still returns success (HTTP 201 semantics) with the
    computed explanation in the DTO; `SchedulingRequest` remains `Completed`; itinerary
    item values unchanged; persisted `FriendlyExplanation` stays NULL; warning logged
    with outcome `persistence-failed`; `SaveChangesAsync` attempted exactly twice (no retry).
  - Caller cancellation after T1 commit (token cancelled once T1 completes) →
    `OperationCanceledException` propagates; `SchedulingRequest` remains `Completed`;
    itinerary remains persisted with committed values; no fallback persisted.
  - Replay after a persisted-NULL row → DTO `FriendlyExplanation` null (spec decision 18
    applies to what is persisted); replay performs zero provider calls (asserted).
- **Gates:** build; Application tests; `git diff --check`.
- **Expected:** all new edge tests green; no scheduling rollback anywhere.
- **Production risk:** none (tests only, unless a tiny fixture seam is needed — test-side only).
- **STOP condition:** if simulating T2 failure requires changing the
  `IApplicationDbContext` contract or production code, stop and consult the developer.
- **Commit:** `test(trip): prove best-effort explanation failure semantics`

### W07 — SQL integration / final regression

- **Goal:** prove the whole feature end-to-end on SQL Server with fakes only, and run
  the full recorded baseline as the closing gate.
- **Files:**
  - `tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs` —
    add a shared fake/counting `IItineraryExplanationProvider` (mirror
    `ProviderDisabledPoiRankingProvider`) injectable via the existing
    `configureTestServices` seam; no production-code changes.
  - `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestExplanationSqlServerTests.cs` (new) —
    mirror `CreateSchedulingRequestRankingSqlServerTests`:
    1. fresh generation with a fake provider returning valid Vietnamese text → 201;
       `planning.ItineraryItems.friendly_explanation` == LLM text per item;
       `recommendation_reason` unchanged; replay returns the persisted text.
    2. fake provider returns an invalid response → fallback text persisted.
    3. provider disabled → fallback text persisted, zero calls.
    4. invariance: provider ON vs OFF produce identical scheduling columns.
  - Migration-on-fresh/existing/double-run coverage from W02 re-verified here if the
    harness schema path needs the migration registered.
- **Gates (final regression, in order):**
  1. `dotnet build TripMate.slnx` → 0/0
  2. `dotnet test tests/TripMate.Application.UnitTests` → baseline 925 + all new, 0 failed
  3. `dotnet test tests/TripMate.Infrastructure.UnitTests` → 173/0/1 + new, 0 failed
  4. Generator filter → 28 passed / 0 failed. TM-217 must not modify
     `ItineraryGenerationServiceTests` or scheduler/CSP behavior, so the generator count
     remains the develop baseline 28/0. If implementation requires adding/changing
     generator tests or changes this count, STOP and audit scope before proceeding.
  5. SQL API suite with `TRIPMATE_SQLSERVER_TEST_CONNECTION` → green except the
     documented flake family; any failure must pass an isolated re-run
  6. `git diff --check` → clean
- **Expected:** full regression green under the documented flake policy.
- **Production risk:** none (tests + test infrastructure only).
- **STOP condition:** any test attempting a real external call (forbidden), or SQL
  failures outside the documented flake family.
- **Commit:** `test(trip): verify explanation persistence end-to-end`

## 5. Regression Strategy (binding)

- Baselines in §2 are the reference; every work item re-runs its gates and may not
  regress any suite.
- The W05 invariance test (and W07 integration twin) proves provider ON/OFF cannot
  change: POI IDs, sequence/order, arrival/departure, stay duration, estimated cost,
  mandatory status, travel minutes, feasibility outcome, `RecommendationReason`.
  Only `FriendlyExplanation` may differ.
- SQL flake policy: documented environmental failures (factory host-startup race,
  log-file lock) are reported separately and must pass isolated re-runs; they never
  count as TM-217 regressions.

## 6. Security / Observability Verification Plan

- W03 privacy serializer test: no traveler identity, email/account id, coordinates,
  request hash, idempotency key, BaseScore, AiScore, EffectiveDesirabilityScore,
  behavior aggregates, or auth material in the serialized provider input.
- W04 request assertions: `x-goog-api-key` from options only; `store=false`; payload
  contains only spec §8 fields.
- Logging assertions (W04/W05/W06, captured test logger): log template contains only
  `provider`, `enabled`, `itemCount`, `latencyMs`, `outcome`, `fallbackUsed`,
  `timedOut` (+ `persistence-failed` outcome); **no API key, no provider payload, no
  explanation text** ever logged.
- `Enabled=false` requires zero real provider calls (W04 + W05 + W07 prove it);
  `Enabled=true` requires valid endpoint/key/model at startup (`ValidateOnStart`).

## 7. Non-Goals (explicit)

Itinerary-level summary; FE/mobile implementation; explanation regenerate/edit
endpoints; background retries/backfill; TM-215 ranking-reason reuse; multi-language
support beyond Vietnamese; changes to request hash/idempotency identity; changes to
scheduler/CSP logic or formal solver; real Gemini calls in automated tests; committing
or pushing from this plan.

## 8. Plan Validation Checklist

1. Plan is consistent with the approved spec (§5 decisions 1–20, §7, §10–§15, §17–§20). ✔
2. Every planned production change has corresponding tests (W01↔domain tests,
   W02↔migration tests, W03↔validator/fallback/privacy tests, W04↔transport/DI tests,
   W05↔orchestration+invariance tests, W06↔edge tests, W07↔integration tests). ✔
3. Each work item is independently implementable/auditable (W00 approved docs only;
   W01 domain+DTO plumbing;
   W02 persistence; W03 pure contracts; W04 transport; W05 handler orchestration;
   W06 edge proofs; W07 end-to-end). ✔
4. Migration scope is exactly one nullable column on `planning.ItineraryItems`. ✔
5. No work item grants the LLM scheduling authority — provider receives an immutable
   projection and returns strings only; all plan/scheduling writes remain in
   Application handler code paths that predate TM-217. ✔
6. No implementation performed: only this plan file was created. ✔
7. `git diff --check` clean; `git status` shows only the untracked spec + plan files. ✔

## 9. Commit Sequence Overview

| Order | Work item | Commit |
| --- | --- | --- |
| 1 | W00 | `docs(trip): add approved TM-217 spec and implementation plan` |
| 2 | W01 | `feat(trip): add itinerary item friendly explanation field` |
| 3 | W02 | `feat(db): add itinerary item friendly explanation column` |
| 4 | W03 | `feat(trip): add itinerary explanation contracts and validator` |
| 5 | W04 | `feat(trip): add Gemini itinerary explanation provider` |
| 6 | W05 | `feat(trip): attach explanation after scheduling commit` |
| 7 | W06 | `test(trip): prove best-effort explanation failure semantics` |
| 8 | W07 | `test(trip): verify explanation persistence end-to-end` |

The planned implementation sequence is exactly eight commits, W00 through W07.

All scopes/types comply with AGENTS.md §5.2 (`trip` and `db` are allowed scopes;
`tm217`/`scheduling`/`ai` are not used). No commit/push happens without the
developer's explicit request.
