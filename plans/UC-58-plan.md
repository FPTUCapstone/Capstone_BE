# UC-58 View Active Trips Backend Implementation Plan

## Status and scope

Implemented in the local working tree on 2026-09-26; pending commit, push, remote CI, and final browser review.

This plan implements only the read-only UC-58 list endpoint defined in [`specs/UC-58-spec.md`](../specs/UC-58-spec.md). UC-59 details, GPS disclosure, trip/group/incident mutation, and operational intervention are excluded.

Repository: `Capstone_BE` (`https://github.com/FPTUCapstone/Capstone_BE.git`)

Branch in this repository: `feature/linhnv-view-active-trips`. The FE repository uses the same branch name but has an independent Git history and therefore a different HEAD.

Baseline HEAD: `e3aa12b2dda0a62d3dcfc28cacfdfe900c900439`

## Baseline evidence — 2026-09-26

- `dotnet format TripMate.slnx --verify-no-changes --no-restore`: passed.
- `dotnet test TripMate.slnx --configuration Release --verbosity minimal`: 921 passed, 0 failed; 87 SQL Server tests skipped because `TRIPMATE_SQLSERVER_TEST_CONNECTION` was not configured for this run.
- The first sandboxed run could not read the user's NuGet configuration; the successful run used the normal user environment. This was an execution-environment restriction, not a code failure.
- The only working-tree change at baseline is this UC-58 documentation. No implementation exists yet.

UC-58 cannot be declared complete while its own SQL Server tests are skipped.

## Implementation evidence — 2026-09-26

- `dotnet format TripMate.slnx --no-restore`: passed.
- `dotnet build TripMate.slnx --configuration Release --no-restore`: passed with 0 warnings and 0 errors.
- `dotnet test TripMate.slnx --configuration Release --no-restore --verbosity minimal`: 938 passed, 0 failed, 88 existing SQL-gated tests skipped because the variable was absent in that shell.
- UC-58's `ActiveTripsSqlServerTests` was then run separately against the Docker SQL Server on `localhost:14330` with the connection supplied from local environment configuration: 1 passed, 0 failed, 0 skipped. No credential was printed or persisted in source.

## Architectural decisions

- Reuse Clean Architecture, MediatR, `Result<T>`, `PaginatedList<T>`, `[Authorize(Roles = "Administrator")]`, `AsNoTracking()`, and the project ProblemDetails pipeline.
- Add read mappings for existing SQL objects. Do not add EF migrations or change `tripmate_schema_v7.sql`.
- Add only the entity/navigation fields needed to query existing columns. No new mutation methods are introduced.
- Use the project's injected `IDateTimeProvider.UtcNow` for current-day calculation so tests do not depend on wall-clock time. Do not introduce a parallel `.NET TimeProvider` abstraction for this feature.
- Parse query dates as strict `DateOnly` values, interpret their boundaries in `Asia/Ho_Chi_Minh`, and query with a half-open UTC interval: `fromLocalMidnight <= started_at < dayAfterToLocalMidnight`. Never use an artificial end-of-day timestamp.
- Keep filtering/counting on the server. Do not load all active sessions into memory before pagination.
- Use SQL Server tests for collation, derived Trip Code search, aggregate joins, null ordering, and stable paging; EF InMemory tests do not prove those behaviors.

## Atomic tasks

### B1 — Persistence model for live trips

Tests first:

- Add `tests/TripMate.Infrastructure.UnitTests/Persistence/ActiveTripPersistenceModelTests.cs` proving table/schema names, keys, required lengths, UTC converters, relationships, and read-only source-tour mapping.

Production changes:

- Add `src/TripMate.Domain/Entities/TripSession.cs` with approved FSM constants and existing schema fields required by UC-58.
- Add `src/TripMate.Domain/Entities/Incident.cs` with incident type constants and resolution timestamp.
- Extend `src/TripMate.Domain/Entities/Itinerary.cs` itself, not only its EF configuration: add `public const string BookedTourSourceType = "BookedTour"`, nullable `SourceTourId`, and nullable `SourceTour` navigation for the existing `planning.Itineraries.source_tour_id` column.
- Update `ItineraryConfiguration.cs` to map `SourceTourId` to `source_tour_id` and configure its optional FK/navigation to `Tour`. A configuration-only shadow property is not acceptable because the query contract requires the typed entity members.
- Extend `src/TripMate.Domain/Entities/Tour.cs` only with the inverse itinerary navigation if EF requires it.
- Add `src/TripMate.Infrastructure/Persistence/Configurations/TripSessionConfiguration.cs` and `IncidentConfiguration.cs`.
- Add `DbSet<TripSession> TripSessions` and `DbSet<Incident> Incidents` to all three required context surfaces: `IApplicationDbContext.cs`, `ApplicationDbContext.cs`, and `TestDbContext.cs`. The task is incomplete if any context omits either DbSet.

Verification:

`dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --configuration Release --filter FullyQualifiedName~ActiveTripPersistenceModelTests`

Exit: the existing live-trip tables are mapped without a SQL schema change and the full solution compiles.

### B2 — Query contract and validation

Tests first:

- Add `tests/TripMate.Application.UnitTests/Features/Admin/ActiveTrips/GetList/GetActiveTripsQueryValidatorTests.cs`.
- Cover keyword/destination post-trim limits, allowed enum-like values, page bounds, strict local calendar dates, and inverted date range.

Production changes:

- Add `src/TripMate.Application/Features/Admin/ActiveTrips/GetList/GetActiveTripsQuery.cs` with constants for `SelfPlanned`, `Tour`, `WithOpenAlerts`, `WithoutOpenAlerts`, default/max page sizes, and accepted FSM states.
- Add `GetActiveTripsQueryValidator.cs`.
- Add `ActiveTripListItemDto.cs`, `ActiveTripSummaryDto.cs`, and `ActiveTripsResponseDto.cs` with the exact approved JSON contract. `tripId` is projected from `long` to its invariant decimal string and is documented/serialized as a JSON string.
- Add a feature-scoped Vietnam calendar helper only if required; do not create a second clock abstraction. It receives UTC values from `IDateTimeProvider` and owns the explicit `Asia/Ho_Chi_Minh` conversion.

Verification:

`dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --configuration Release --filter FullyQualifiedName~ActiveTrips`

Exit: malformed criteria are rejected before database access and no query magic strings are duplicated.

### B3 — Read-only query handler

Tests first:

- Add `GetActiveTripsQueryHandlerTests.cs` for authorization defense in depth, active-state exclusion, trip-type mapping, null destination/start handling, current-day boundaries, empty page shape, and no `SaveChanges` call.
- Add cancellation coverage and a page-overflow case that cannot overflow `Skip` arithmetic.

Production changes:

- Add `GetActiveTripsQueryHandler.cs` under `Features/Admin/ActiveTrips/GetList` and inject `IDateTimeProvider` for the current UTC instant.
- Build filtered active-session keys and total count in SQL.
- Compute global summary independently of list filters.
- Project only the selected page, then obtain bounded group names/member counts, destination names, and unresolved incident counts without per-row queries.
- Join a session to groups only through their common itinerary key: `TripSessions.ItineraryId == TravelGroups.ItineraryId`. There is no `TravelGroups.SessionId` column or direct TripSession relationship, and none may be invented.
- Count members with the typed `GroupMemberStatus.Active` enum. Do not compare the status to a raw `"Active"` string.
- Treat `TravelGroups.name` as nullable database data even though the current CLR property is non-nullable: include only names that are non-null and non-whitespace, order them by `group_id`, and join them with `, `. If no usable group name remains, fall back to the session Traveler display name; never emit comma-only text.
- Search the derived `TRIP-{session_id}` contract safely. Escape LIKE wildcard characters for user text where raw SQL collation semantics require it.
- Order null `started_at` last, then `started_at DESC`, then `session_id DESC`.

Verification:

`dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --configuration Release --filter FullyQualifiedName~ActiveTrips`

Exit: handler matches the spec for InMemory-verifiable behavior and remains read-only.

### B4 — API, authorization, and OpenAPI

Tests first:

- Add `tests/TripMate.Api.IntegrationTests/ActiveTrips/ActiveTripsEndpointTests.cs` covering anonymous `401`, non-Admin `403`, Admin `200`, invalid query `400`, and empty `200`.
- Add `ActiveTripsOpenApiTests.cs` for parameter names/defaults/allowed values, string-shaped `tripId`, and documented `200/400/401/403/500` responses.

Production changes:

- Add `src/TripMate.Api/Controllers/V1/AdminActiveTripsController.cs` at `GET /api/v1/admin/trips/active`.
- Keep the controller thin and route failures through `HandleFailure`/validation middleware.
- Add a feature-scoped OpenAPI operation filter only if automatic schema generation cannot express the approved query contract; register it in `Program.cs` only when necessary.

Verification:

`dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --configuration Release --filter FullyQualifiedName~ActiveTrips`

Exit: HTTP and OpenAPI contracts match the approved spec and database details are not exposed.

### B5 — Real SQL Server evidence

Tests first:

- Add `tests/TripMate.Api.IntegrationTests/ActiveTrips/ActiveTripsSqlServerTests.cs` using `SqlServerTestDatabase` and the canonical schema.
- Seed self-planned and tour sessions in all FSM states; groups linked through `itinerary_id`; active/left/removed members; null/empty group names; multiple Tour destinations; resolved/unresolved incidents; equal/null start times; overlapping IDs such as 42 and 142.
- Verify Vietnamese/case-insensitive text matching, literal wildcard behavior, `TRIP-42` versus substring `42`, composed filters, inclusive Vietnam date bounds translated to a half-open UTC range, UTC/local midnight boundary cases, summary deduplication, stable pagination, bounded query count, and read-only persistence.

No production schema change is expected. If the canonical schema cannot support the approved contract, stop and revise the spec rather than silently adding a migration.

Verification:

```powershell
$env:TRIPMATE_SQLSERVER_TEST_CONNECTION='<approved disposable SQL Server test connection>'
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --configuration Release --filter FullyQualifiedName~ActiveTrips
```

Exit: UC-58 SQL tests execute with 0 skipped and 0 failed against a disposable database.

### B6 — Backend final verification and review

- Run `dotnet format TripMate.slnx --verify-no-changes --no-restore`.
- Run Release build and full test suite, including SQL tests with the approved test connection.
- Run `git diff --check` and inspect `origin/develop...HEAD` for unrelated files, secrets, generated artifacts, or schema drift.
- Perform a spec-compliance review followed by a code-quality review.
- Record the final commit SHA and remote CI result after commit/push; pre-commit working-tree evidence is not final-head evidence.

## Definition of done

- Every acceptance criterion in the approved BE spec has a corresponding test/evidence item.
- No SQL schema migration or write path was introduced.
- UC-58 SQL tests and full required checks pass without hiding skips.
- The branch contains only UC-58 documentation, implementation, and tests.
- No push, PR, or merge occurs without a separate explicit request.
