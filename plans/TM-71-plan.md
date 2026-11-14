# TM-71 UC-25 Backend Implementation Plan

Status: Approved by the TM-71 Task Owner on 2026-11-04 with amendments A1-A3 below

Binding specification: `specs/TM-71-spec.md`

Implementation branch: `feature/tm71-tour-recommendations`

Baseline: `origin/develop` commit `448c8ec14c22f1421a27edb77f0bba0e68b09559`; full `dotnet test` passed 5,429 tests with 559 environment-conditioned skips and zero failures before feature work.

Execution checkpoint (updated 2026-11-06):

- Task 1 is Green: database-first entities/mappings and domain invariants are implemented without a migration.
- Task 2 is Green: the pure scorer proves normalized active weights, date-as-score-only behavior, deterministic reasons, rounding, and strict 80.00/80.01 semantics.
- Task 3 is Green: validation, public/current candidate filtering and deterministic multi-schedule selection are covered.
- Task 4 is Green: snapshot-first insertion, atomic rollback, exact persisted 80.00/80.01 behavior and request-order concurrency are proven against SQL Server.
- Task 5 is Green: latest-snapshot isolation, current availability, 20-item pagination, bounded query count and stale-score/explicit-refresh behavior are covered.
- Task 6 is Green: endpoint and OpenAPI contract tests pass with D7's required non-null empty `matchingReasons` array.
- Task 7 is Green: seven SQL Server tests execute without skips and cover mapping, refresh-to-GET, rollback, concurrency, isolation, final-response availability, multi-schedule selection, threshold boundaries and paging.
- Task 8 is complete for TM-71-focused unit/API/OpenAPI/SQL verification and two-pass review. The repository-wide Application suite remains explicitly unresolved: a parallel run had one scheduling golden failure, the isolated golden method passed 55/55 sequentially, and a subsequent full sequential run was stopped after about 14 minutes without a final result. It must not be reported as a full-suite pass.

Verification addendum (2026-11-09):

- The D7 response example in the binding spec now uses `matchingReasons: []`.
- UC-25 now uses the existing `ActiveTraveler` authorization policy. Endpoint tests prove both GET latest and POST refresh reject an authenticated `AccountStatus.Inactive` Traveler with HTTP 403.
- GET latest executes its count, page and supporting projections inside one serializable read transaction. A SQL Server concurrency test pauses immediately after the recommendation count, attempts to fill the schedule concurrently, proves that update waits, then verifies the first response is internally consistent (`totalCount=1`, one item) and the subsequent response is consistently empty.
- A SQL Server catalogue test refreshes 250 eligible Tours in batches `100/100/50`, stores all 250 recommendation logs, returns the fixed top 20 with `totalCount=250`, and keeps SELECT count bounded by batches rather than Tours. There is no arbitrary candidate cap.
- Controlled parallel comparison used the same command and machine for `origin/develop` commit `448c8ec` and TM-71. Baseline completed 4,436 tests with 4,435 pass and one unrelated shared-state failure in `AuditLogOutcomeTests`; it did not fail golden scheduling in that run. TM-71 completed 4,471 tests with 4,470 pass and the known `incumbent-valid-c20-m2-none-s10` golden failure. On TM-71, excluding the 35 recommendation tests produced 4,436/4,436 pass; running the 55-scenario golden class together with the 35 recommendation tests produced 160/160 pass. This does not establish a production scheduling regression. The strongest identified explanation is load/order sensitivity in the golden fixture: `GoldenScenarioFactory` computes its supposedly optimal incumbent by running a solver with the default hard 2,500 ms wall-clock deadline, whose own comments document machine-load dependence. The exact trigger remains unproven, so the full suite is still not reported as PASS.

D7 resolution: Option B is binding. `matchingReasons` is a required, non-null JSON array and is always `[]` on POST refresh and GET latest. Neither path recomputes reasons; no schema object or migration is added. Contract/OpenAPI tests must require the field and verify the empty array. Web and Mobile hide the explanation area when it is empty, while continuing to display the persisted `matchingScore`.

Recovery invariants:

- Keep backup `D:\CapStone\_safety_backups\Capstone_BE_tm71_mappings_20261104_180433` unchanged.
- Keep recovery stash `7175d7ceba5f0bacc8be3a37244fd04db88c0b90` unchanged; it contains all original 24 dirty paths.
- Keep baseline-generated-artifact stash `dff9203aa6fcd42e4a61eb20d2deed136d2afb20` unchanged.
- Do not rebase, reset, clean, push, open a PR, merge, or create an EF migration.

## Design locked by the approved spec

- Endpoints: `POST /api/v1/tour-recommendations/refresh` and `GET /api/v1/tour-recommendations?page=1`.
- Traveler authorization only; active-account enforcement stays in the existing authentication/authorization pipeline.
- Page size is fixed at 20.
- Latest committed snapshot is selected by `search_request_id DESC`. Refresh inserts the snapshot as the first write inside its transaction, so the identity records refresh-request order; only committed rows are visible, and an older transaction committing later cannot supersede a newer request.
- Active score components are normalized by `sum(weight * similarity) / sum(active weights) * 100`, rounded once to two decimals with `AwayFromZero`.
- Weights: interest 40, destination 20, date 15, budget 15, completed-trip history 10.
- Interest tags are always active and required. Destination/date/budget are active only when the corresponding criteria exist. History is active only when the Traveler has completed Tour-booking history.
- Effective threshold is `max(parsed SystemConfigs threshold, 80.00)` and qualification is strict `score > effectiveThreshold`; configuration can never admit a score at or below 80.00.
- Date range is scoring-only. It never filters an otherwise eligible Tour. Date similarity is 1 when any bookable schedule falls inside the inclusive range and 0 otherwise.
- A representative schedule is the earliest currently bookable schedule that is Scheduled, starts in the future, has open registration, and has a valid capacity snapshot with remaining slots. When a date range exists and at least one bookable schedule matches it, choose the earliest matching schedule; otherwise choose the earliest bookable schedule overall. Ties use `schedule_id ASC`.
- Public visibility reuses `TourVisibility.PublicTour(nowUtc)`. Availability is rechecked in the final page query, and both `items` and `totalCount` are computed from the same current eligibility predicate.
- Stored scores remain snapshot values. Explicit refresh is the only approved way to recompute them.

## Task 1 Domain and database-first mappings

Red tests:

- Add persistence-model tests proving exact table/schema/column names, decimal precision, UTC mapping, foreign keys and delete behavior for `TourSearchRequest` and `TourRecommendationLog`.
- Add domain tests for guarded creation, normalized UTC timestamps, score range, strict `is_recommended` derivation, and navigation assignment for same-transaction snapshot/log insertion.

Implementation files:

- `src/TripMate.Domain/Entities/TourSearchRequest.cs`
- `src/TripMate.Domain/Entities/TourRecommendationLog.cs`
- `src/TripMate.Infrastructure/Persistence/Configurations/TourSearchRequestConfiguration.cs`
- `src/TripMate.Infrastructure/Persistence/Configurations/TourRecommendationLogConfiguration.cs`
- `src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs`
- `src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs`
- `tests/TripMate.Application.UnitTests/TestUtilities/TestDbContext.cs`
- `tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs`
- `tests/TripMate.Infrastructure.UnitTests/Persistence/TourRecommendationPersistenceModelTests.cs`
- `tests/TripMate.Application.UnitTests/Domain/TourRecommendationSnapshotTests.cs`

Verification:

`dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --filter FullyQualifiedName~TourRecommendationPersistenceModelTests`

`dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter FullyQualifiedName~TourRecommendationSnapshotTests`

Definition of done: the two existing SQL tables are mapped without schema changes, and entity invariants use private setters/factories with no EF dependency.

## Task 2 Pure scoring and normalization policy

Red tests:

- Interest/category/POI-tag token normalization and Jaccard behavior.
- Component activation and weight normalization for every missing optional criterion and missing completed-trip history combination.
- Binary destination/date/budget signals and history Jaccard.
- Final rounding and the exact 80.00 excluded / 80.01 included boundary.
- Deterministic matching-reason ordering.

Implementation files:

- `src/TripMate.Application/Features/TourRecommendations/Common/TourRecommendationScoring.cs`
- `src/TripMate.Application/Features/TourRecommendations/Common/TourRecommendationContracts.cs`
- `tests/TripMate.Application.UnitTests/Features/TourRecommendations/TourRecommendationScoringTests.cs`

Verification:

`dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter FullyQualifiedName~TourRecommendationScoringTests`

Definition of done: the scorer is pure, deterministic, framework-free at its calculation boundary, and exhaustively proves normalization and threshold semantics.

## Task 3 Refresh request validation and candidate projection

Red tests:

- FluentValidation for date ordering, whole/non-negative VND, min/max ordering, and destination length/normalization.
- Missing/blank/malformed interest tags return `tour_recommendation.interest_tags_required` without a snapshot.
- Candidate projection excludes non-public Tours, inactive operators, past/cancelled/completed schedules, invalid capacity and sold-out schedules. It does not exclude schedules or Tours solely for date mismatch.
- A Tour with multiple schedules selects only the earliest actually eligible schedule.
- Candidate input projection includes only fields required by the approved scoring formula and streams in stable Tour-ID order rather than materializing the full entity graph.

Implementation files:

- `src/TripMate.Application/Features/TourRecommendations/Refresh/RefreshTourRecommendationsCommand.cs`
- `src/TripMate.Application/Features/TourRecommendations/Refresh/RefreshTourRecommendationsCommandValidator.cs`
- `src/TripMate.Application/Features/TourRecommendations/Refresh/TourRecommendationCandidateReader.cs`
- `src/TripMate.Application/Features/TourRecommendations/Common/TourRecommendationErrorCodes.cs`
- `tests/TripMate.Application.UnitTests/Features/TourRecommendations/RefreshTourRecommendationsValidationTests.cs`
- `tests/TripMate.Application.UnitTests/Features/TourRecommendations/TourRecommendationCandidateReaderTests.cs`

Verification:

`dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter FullyQualifiedName~TourRecommendation`

Definition of done: invalid inputs and ineligible candidates are rejected before snapshot publication, with no N+1 entity loading.

## Task 4 Atomic refresh persistence and concurrency

Red tests:

- One refresh creates exactly one request snapshot and all candidate logs in one transaction.
- A forced log/save failure rolls back both snapshot and logs.
- Candidates at/below the threshold are logged with `is_recommended = false`; qualifying candidates are true.
- Missing history and optional criteria use normalized active weights in persisted scores.
- Concurrent refreshes for the same Traveler insert snapshot identity first, remain separate atomic snapshots, and select the highest committed `search_request_id` as latest even with equal timestamps and when the older request commits later.
- Concurrent refreshes for different Travelers never cross-link snapshots or logs.

Implementation files:

- `src/TripMate.Application/Features/TourRecommendations/Refresh/RefreshTourRecommendationsCommandHandler.cs`
- `tests/TripMate.Application.UnitTests/Features/TourRecommendations/RefreshTourRecommendationsCommandHandlerTests.cs`
- `tests/TripMate.Api.IntegrationTests/TourRecommendations/TourRecommendationRefreshSqlServerTests.cs`

Verification:

`dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter FullyQualifiedName~RefreshTourRecommendationsCommandHandlerTests`

`dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter FullyQualifiedName~TourRecommendationRefreshSqlServerTests`

Definition of done: SQL Server tests prove rollback, concurrent same-Traveler latest selection, different-Traveler isolation and persisted 80.00/80.01 outcomes.

## Task 5 Latest-snapshot read and current-availability projection

Red tests:

- No snapshot returns page 1/20 with empty items, zero totals and null criteria.
- Only the authenticated Traveler's latest committed snapshot can be read.
- Ordering is `matchingScore DESC, tourId ASC`; paging and totals use the fixed size 20.
- Public status, operator activity and schedule availability are rechecked at read time.
- A Tour that becomes unavailable after refresh but before response projection is absent from both `items` and `totalCount`.
- A multi-schedule Tour returns the earliest currently eligible matching schedule and current remaining slots.
- Price/preference changes after the snapshot do not silently recompute the stored score; an explicit refresh produces the new score.

Implementation files:

- `src/TripMate.Application/Features/TourRecommendations/GetLatest/GetLatestTourRecommendationsQuery.cs`
- `src/TripMate.Application/Features/TourRecommendations/GetLatest/GetLatestTourRecommendationsQueryHandler.cs`
- `src/TripMate.Application/Features/TourRecommendations/Common/TourRecommendationDtos.cs`
- `tests/TripMate.Application.UnitTests/Features/TourRecommendations/GetLatestTourRecommendationsQueryHandlerTests.cs`
- `tests/TripMate.Api.IntegrationTests/TourRecommendations/TourRecommendationReadSqlServerTests.cs`

Verification:

`dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter FullyQualifiedName~GetLatestTourRecommendationsQueryHandlerTests`

`dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter FullyQualifiedName~TourRecommendationReadSqlServerTests`

Definition of done: the page and count share current eligibility, isolation is proven, and the approved stale-score limitation has an explicit regression test.

## Task 6 API, contract and OpenAPI surface

Red tests:

- Traveler can GET and POST; anonymous receives 401; non-Traveler receives 403.
- Refresh binds the approved optional criteria request and returns page 1.
- Invalid request returns validation ProblemDetails; missing interest tags returns 422 with stable code.
- JSON shape preserves Web's nested `tour`, `matchingScore`, and `matchingReasons` contract and serializes BIGINT identifiers as strings.
- OpenAPI contains both routes, request/response schemas, fixed error statuses and Traveler authorization.

Implementation files:

- `src/TripMate.Api/Controllers/V1/TourRecommendationsController.cs`
- `src/TripMate.Api/Controllers/V1/Requests/RefreshTourRecommendationsRequest.cs`
- `tests/TripMate.Api.IntegrationTests/TourRecommendations/TourRecommendationEndpointTests.cs`
- `tests/TripMate.Api.IntegrationTests/TourRecommendations/TourRecommendationContractTests.cs`
- `tests/TripMate.Api.IntegrationTests/TourRecommendations/TourRecommendationOpenApiTests.cs`

Verification:

`dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter FullyQualifiedName~TourRecommendationEndpointTests|FullyQualifiedName~TourRecommendationContractTests|FullyQualifiedName~TourRecommendationOpenApiTests`

Definition of done: endpoint behavior, RFC 7807 failures and generated OpenAPI match the approved spec without an envelope.

## Task 7 Canonical SQL mapping and end-to-end SQL Server coverage

Red tests:

- Query and insert both mapped tables against the canonical schema.
- Full refresh-to-GET flow covers threshold boundary, missing tags, unpublished Tour, sold-out Tour, past departure, pagination, refresh, multi-schedule selection, concurrent refresh, rollback, isolation and stale availability.
- SQL command count remains bounded per page and does not grow with the number of returned Tours.

Implementation files:

- `tests/TripMate.Api.IntegrationTests/TourRecommendations/TourRecommendationMappingSqlServerTests.cs`
- `tests/TripMate.Api.IntegrationTests/TourRecommendations/TourRecommendationEndToEndSqlServerTests.cs`

Verification:

`dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter FullyQualifiedName~TourRecommendation --logger "console;verbosity=normal"`

Definition of done: all requested edge cases execute against SQL Server when the test fixture is available; skips are reported separately and never described as passes.

## Task 8 Final regression and two-pass review

Checks:

1. Spec-compliance review against every acceptance criterion and Owner-added edge case.
2. Code-quality review for Clean Architecture, cancellation, tracking discipline, SQL translation/bounds, transaction scope, deterministic ordering, no N+1 and no secret/schema drift.
3. `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj`
4. `dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj`
5. `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter FullyQualifiedName~TourRecommendation`
6. Full `dotnet test`.
7. `git diff --check` and final branch/status/diff/untracked inventory.

Final report must separate:

- production and test diff;
- unit, contract, OpenAPI and SQL Server results, including skipped tests;
- unverified points and environment limitations;
- Web and Mobile work still required at the original Backend-only checkpoint (superseded by the 2026-11-13 execution record below);
- limitation 1: clients must POST refresh on first screen open because GET-without-snapshot is empty;
- limitation 2: stored scores can become stale after Tour/preference changes, with the explicit-refresh-only behavior and its tests;
- confirmation that neither recovery stash nor backup was altered and that nothing was pushed, merged or opened as a PR.

## 2026-11-09 patch-review findings

- Finding 1 (stale POST availability): fixed. POST now persists/scans every candidate, then uses a fresh clock and serializable final reads to rebuild the returned schedule, price, availability and `remainingSlots`. SQL tests cover the original representative schedule becoming full while another schedule remains bookable, and an 8-to-1 slot change.
- Finding 2 (time drift): fixed. The final availability projection takes a new `UtcNow`; a SQL test advances both registration deadline and departure while refresh is scoring and verifies the candidate remains logged but is absent from `items`/`totalCount`.
- Finding 3 (catalogue memory/tracking): fixed. Logs are saved in the same transaction per 100-candidate batch and the tracker is cleared after every batch. No candidate cap is introduced. The 250-tour SQL test proves `100/100/50`, all 250 logs, zero retained tracked entries after each batch, and exactly 13 SELECTs (fixed reads plus three batches and response projections).
- Finding 4 (very large page): fixed. Offset arithmetic uses `long`; pages beyond SQL's `int` OFFSET range return an empty `items` list while preserving `totalCount` and `totalPages`.

Final verification on the amended code: recommendation unit `35/35`, API/OpenAPI `7/7`, SQL Server `11/11` with zero skips. The full Application suite is not green: `4470/4471` pass with the baseline `AuditLogOutcomeTests` redaction failure; no golden failure occurred in this final parallel run. On isolated `origin/develop` `f28acd6`, the patch has one additive DI hunk conflict with new Weather registrations; after retaining both registrations, unit `35/35` and API/OpenAPI `7/7` pass.

## 2026-11-13 latest-upstream integration and client execution

- Backend integration baseline: `origin/develop@999e6af6e939f0efdf2f735d20eabcef686843c5` in an isolated detached worktree. The only manual integration conflict was additive DI registration; Weather and upstream availability registrations were retained and the TM-71 candidate reader was added.
- Latest upstream added `aggregateRating` to `TourSearchItemDto`. POST and GET now project the published aggregate in the existing ranked-row SQL query. The first attempt used one extra query and correctly failed SQL query-budget tests; the final implementation removed that regression.
- Actual final Backend verification: recommendation unit `35/35`, API/OpenAPI `7/7`, SQL Server `11/11` with zero skips, full integrated Application `4788/4788`, and same-command clean baseline Application `4753/4753`. Neither AuditLog nor golden scheduling failed in this controlled pair. This run does not erase earlier observed instability; it establishes no TM-71 regression on the latest compared SHA.
- Web integration baseline: `origin/develop@126174bb2e1bd0a14b8e1fc3cd5e81114742a9c8`. TypeScript typecheck passes. UC-25 targeted Vitest passes `46/46` after the strict-boundary test was added.
- Mobile integration baseline: `origin/develop@8c3ebdc4c6dba598f8a284ba8323b7876b168c3f`. UC-25 targeted Flutter tests pass `30/30`, shared Tour card compatibility passes `13/13`, UC-26 route coverage passes `3/3`, and `flutter analyze` reports no issues. Mock transport tests are not described as end-to-end.
- Web-to-API-to-SQL and Mobile-to-API-to-SQL live flows remain separate integration-environment checks. Unit/contract mocks prove request/response wiring but are not substitutes for authenticated deployed-client E2E.
- TM-71 must remain not Done until the remaining live client-to-SQL checks are resolved or explicitly waived by the Owner.
