# TM-58 — Explore Points of Interest Backend Implementation Plan

Status: **COMPLETED AND VERIFIED — all 9 tasks delivered on 2026-09-13**

Jira: [TM-58](https://tripmate-capstone.atlassian.net/browse/TM-58)

Use case: UC-12 — Explore Points of Interest

Branch: `feature/datmnt-explore-pois`

Approved specification: `specs/TM-58-spec.md`

Canonical Docs contracts:

- `Capstone_Docs/requirements/mvp/explore-points-of-interest-mvp.md`;
- `Capstone_Docs/api/contracts/poi-exploration-api.md`;
- `Capstone_Docs/api/contracts/api-response-standard.md`.

## 1. Preconditions and verified baseline

The following gates were satisfied on 2026-09-12:

1. D1-D6 were explicitly approved using the proposed decisions.
2. TM-98 PR #8 was merged into Backend `develop` at `e6d64ec`.
3. `feature/datmnt-explore-pois` was fast-forwarded to `e6d64ec`.
4. The reconciled solution builds with zero errors and zero warnings.
5. The Application, Infrastructure, and API test projects discover 126 tests. The current shell ran
   123 successfully; the three SQL Server tests skipped because
   `TRIPMATE_SQLSERVER_TEST_CONNECTION` was not configured in that shell.

Before Task 1 starts, configure the SQL Server test connection without printing it and run all 126
baseline tests with zero failures and zero skips. If that cannot be done, implementation remains
blocked; do not weaken or remove the SQL test skip guard.

Unrelated local file `package-lock.json` must remain unstaged and unchanged.

## 2. Non-negotiable implementation constraints

- Follow Red -> Green -> Refactor for every task below. Confirm the new focused test fails for the
  intended reason before changing production code.
- Reuse the merged TM-98 POI aggregate, category, opening-hours, tag mappings, enum converters,
  RFC 7807 pipeline, OpenAPI filters, database test harness, and test contexts.
- Do not add an API envelope, generic repository, EF migration, SQL schema change, area model,
  geocoding dependency, commercial-service data, favorite behavior, or itinerary behavior.
- Public endpoints live separately from the Administrator create controller so authorization and
  route scopes cannot leak into each other.
- Controllers only bind requests, dispatch MediatR requests, and map `Result<T>`.
- All read queries use `AsNoTracking`, pass cancellation tokens, and execute filtering,
  aggregation, deterministic sorting, and pagination in SQL.
- Do not call `ToListAsync` before filters, sort, aggregate projection, `Skip`, and `Take` have been
  applied.
- List retrieval may execute one count query and one bounded result query. It must not execute a
  query per POI.
- Use the fixed Vietnam offset `UTC+07:00`; do not depend on the host operating system timezone.
- Only `Active` POIs are public. Missing and Inactive detail records return the same
  `Poi.NotFound` response.
- Do not commit, push, create a PR, or modify Jira unless the developer explicitly requests it.

## 3. Verification commands used after every task

Use the repository SDK:

```powershell
$dotnet = 'D:\CapStone\.codex-tmp\dotnet-sdk-10\dotnet.exe'
```

Run the focused red test before implementation, then the focused green test after the minimal fix.
After each task is green:

```powershell
& $dotnet format TripMate.slnx --include <changed-csharp-files> --no-restore
& $dotnet format TripMate.slnx --include <changed-csharp-files> --no-restore --verify-no-changes
& $dotnet build TripMate.slnx --no-restore -nr:false
& $dotnet test TripMate.slnx --no-build --no-restore -nr:false
git diff --check
git status --short
```

The full test command must report zero skipped tests once the SQL connection is configured. Never
print the value of `TRIPMATE_SQLSERVER_TEST_CONNECTION`.

Before any requested commit, also run:

```powershell
& $dotnet list TripMate.slnx package --vulnerable --include-transitive
git diff
git diff --cached
```

## 4. Atomic implementation tasks

### Task 1 — Add the minimum POI photo and review read mappings

Test first:

- Add `PoiExplorationPersistenceModelTests` to the Infrastructure unit-test project.
- Prove `PoiPhoto` maps to `catalog.POIPhotos` with key `photo_id`, FK-shaped `poi_id`, URL length
  500, nullable caption length 200, and `sort_order`.
- Prove `Review` maps to `social.Reviews` with the SQL v7 key and required fields used by TM-58:
  `target_type`, `target_id`, and `rating`. Verify the existing `IX_Reviews_Target` index shape.
- Prove no migration or schema script is added or modified.

Minimal implementation files:

- `src/TripMate.Domain/Entities/PoiPhoto.cs`;
- `src/TripMate.Domain/Entities/Review.cs`;
- `src/TripMate.Infrastructure/Persistence/Configurations/PoiPhotoConfiguration.cs`;
- `src/TripMate.Infrastructure/Persistence/Configurations/ReviewConfiguration.cs`;
- `src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs`;
- `src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs`;
- `tests/TripMate.Application.UnitTests/TestUtilities/TestDbContext.cs`;
- `tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs`;
- `tests/TripMate.Infrastructure.UnitTests/Persistence/PoiExplorationPersistenceModelTests.cs`.

Implementation rules:

- Map only the existing SQL v7 columns. Do not alter `tripmate_schema_v7.sql`.
- Do not introduce navigation from `Review` to POI because `target_id` is polymorphic and has no
  SQL foreign key.
- Entity construction used by tests may enforce existing SQL invariants, but must not introduce a
  Review or Photo write use case.

Focused verification:

```powershell
& $dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --filter "FullyQualifiedName~PoiExplorationPersistenceModelTests" --no-restore -nr:false
```

Suggested commit if explicitly requested:

```text
feat(poi): map exploration read sources
```

### Task 2 — Define list/detail DTOs and validate query contracts

Test first:

- Add validator tests for every approved boundary and dependent-field rule.
- Prove defaults: `page=1`, `pageSize=20`, `openNow=false`, `sort=name`.
- Prove trimmed search length is at most 200.
- Prove `categoryId > 0` when supplied.
- Prove latitude `-90..90`, longitude `-180..180`, and coordinates must be supplied as a pair.
- Prove `maxDistanceKm > 0` and requires the coordinate pair.
- Prove `sort` accepts only `name`, `distance`, and `rating`; distance sort requires origin.
- Prove detail ID must be greater than zero.

Minimal implementation files:

- `src/TripMate.Application/Features/PointsOfInterest/Explore/ExplorePoisQuery.cs`;
- `src/TripMate.Application/Features/PointsOfInterest/Explore/ExplorePoisQueryValidator.cs`;
- `src/TripMate.Application/Features/PointsOfInterest/Explore/PoiListItemDto.cs`;
- `src/TripMate.Application/Features/PointsOfInterest/Explore/PagedPoiResponseDto.cs`;
- `src/TripMate.Application/Features/PointsOfInterest/Detail/GetPoiDetailQuery.cs`;
- `src/TripMate.Application/Features/PointsOfInterest/Detail/GetPoiDetailQueryValidator.cs`;
- `src/TripMate.Application/Features/PointsOfInterest/Detail/PoiDetailDto.cs`;
- `src/TripMate.Application/Features/PointsOfInterest/Detail/PoiPhotoDto.cs`;
- `src/TripMate.Application/Features/PointsOfInterest/Detail/PoiTagDto.cs`;
- focused validator test files under
  `tests/TripMate.Application.UnitTests/Features/PointsOfInterest/Explore` and `Detail`.

Implementation rules:

- Keep query defaults in one canonical location used by runtime and OpenAPI.
- Use the existing strict POI enum JSON converter for `IndoorOutdoorType` and
  `PointOfInterestStatus` response fields.
- Nullable fields remain present in serialized DTOs.
- Do not add controller annotations or database queries in this task.

Focused verification:

```powershell
& $dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter "FullyQualifiedName~ExplorePoisQueryValidatorTests|FullyQualifiedName~GetPoiDetailQueryValidatorTests" --no-restore -nr:false
```

Suggested commit if explicitly requested:

```text
feat(poi): define exploration query contracts
```

### Task 3 — Implement the deterministic Vietnam opening-state rule

Test first:

- Add a pure calculator test suite covering Sunday day `0`, a missing day, `isClosed=true`, exact
  opening time, one tick before close, exact closing time, and a normal closed interval.
- Prove conversion from arbitrary UTC timestamps to fixed `UTC+07:00` is independent of the host
  timezone.
- Prove overnight intervals are not interpreted because TM-98 rejects `openTime >= closeTime`.

Minimal implementation files:

- `src/TripMate.Application/Features/PointsOfInterest/Common/PoiOpeningState.cs`;
- `tests/TripMate.Application.UnitTests/Features/PointsOfInterest/Common/PoiOpeningStateTests.cs`.

Implementation rules:

- The helper may calculate the approved local day/time constants, but query handlers must still
  express the matching `Any` predicate in EF-translatable LINQ.
- Use `IDateTimeProvider.UtcNow`; do not call `DateTimeOffset.Now`, `DateTime.Now`, or platform
  timezone lookup APIs.
- The response and `openNow=true` filter must use the same predicate semantics.

Focused verification:

```powershell
& $dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter "FullyQualifiedName~PoiOpeningStateTests" --no-restore -nr:false
```

Suggested commit if explicitly requested:

```text
feat(poi): calculate Vietnam opening state
```

### Task 4 — Implement Active-only list search and stable pagination

Test first:

- Add handler tests proving only Active POIs are returned.
- Prove trimmed, case-insensitive name search and category filtering.
- Prove an unknown category returns an empty page rather than an error.
- Prove empty results contain `totalCount=0`, `totalPages=0`, and an empty `items` collection.
- Prove name sort uses POI ID as its final tie-breaker.
- Prove `Skip`/`Take`, page metadata, and page-size bounds behave at page boundaries.
- Prove the query does not change tracked state or create an AuditLog.

Minimal implementation files:

- `src/TripMate.Application/Features/PointsOfInterest/Explore/ExplorePoisQueryHandler.cs`;
- `tests/TripMate.Application.UnitTests/Features/PointsOfInterest/Explore/ExplorePoisQueryHandlerTests.cs`.

Implementation rules:

- Start from `_dbContext.PointsOfInterest.AsNoTracking()`.
- Project only fields needed by `PoiListItemDto`.
- Execute one count query and one bounded page query.
- Do not add `Include` chains or materialize before pagination.
- At the end of this task, fields introduced in later tasks may temporarily return approved neutral
  values (`null`, zero, or `false`) only inside the internal development branch; the complete
  contract is not exposed until Task 7.

Focused verification:

```powershell
& $dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter "FullyQualifiedName~ExplorePoisQueryHandlerTests" --no-restore -nr:false
```

Suggested commit if explicitly requested:

```text
feat(poi): query active POI catalogue
```

### Task 5 — Add distance, opening, rating, and thumbnail projections

Test first:

- Distance: normalize input coordinates to six decimals; prove zero distance, a known reference
  distance, boundary inclusion for `maxDistanceKm`, unrounded filtering, two-decimal response
  rounding, and deterministic distance sorting.
- Opening: prove `openNow=true` and returned `isOpenNow` use identical day/time semantics.
- Rating: include only rows with `target_type='POI'` and matching `target_id`; prove no-review
  output, one-decimal rounding, rated-first descending sort, review-count tie-breaker, and final
  name/ID tie-breakers.
- Thumbnail: select the first photo by `sort_order`, then `photo_id`; prove no-photo returns null.

Minimal implementation files:

- extend `ExplorePoisQueryHandler.cs`;
- add focused cases to `ExplorePoisQueryHandlerTests.cs`;
- introduce only a small private/static EF expression helper if it prevents duplicated distance or
  opening predicates without breaking SQL translation.

Implementation rules:

- Distance uses an EF SQL Server-translatable great-circle expression and Earth radius
  `6371.0088 km`.
- Filtering and ordering use the unrounded distance. Round only the DTO value.
- Use correlated SQL subqueries for rating and thumbnail; never query inside a loop.
- Do not add a cache, spatial package, database function, or schema/index change in this task.

Focused verification:

```powershell
& $dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter "FullyQualifiedName~ExplorePoisQueryHandlerTests" --no-restore -nr:false
```

The task is not complete until its SQL translation is also proven by Task 8.

Suggested commit if explicitly requested:

```text
feat(poi): project exploration metrics
```

### Task 6 — Implement public POI detail retrieval

Test first:

- Prove an Active POI returns all approved core fields without creator/account data.
- Prove missing and Inactive POIs both return `Poi.NotFound`.
- Prove opening hours order by day, photos by `sortOrder` then ID, and tags by name then ID.
- Prove rating summary and `isOpenNow` use the same rules as the list.
- Prove nullable description, address, scores, captions, and empty collections are preserved.
- Prove the query uses no tracking and creates no AuditLog.

Minimal implementation files:

- `src/TripMate.Application/Features/PointsOfInterest/Detail/GetPoiDetailQueryHandler.cs`;
- `src/TripMate.Application/Features/PointsOfInterest/Common/PoiErrorCodes.cs`;
- `src/TripMate.Application/Features/PointsOfInterest/Common/PoiErrorMessages.cs`;
- `tests/TripMate.Application.UnitTests/Features/PointsOfInterest/Detail/GetPoiDetailQueryHandlerTests.cs`.

Implementation rules:

- Add the stable error code `Poi.NotFound` and map it to HTTP 404 in Task 7.
- Detail may use a small fixed number of bounded queries for the single requested POI, but never a
  query loop. Document the final query count in the SQL integration tests.
- Do not expose `CreatedById` or User navigation data.

Focused verification:

```powershell
& $dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter "FullyQualifiedName~GetPoiDetailQueryHandlerTests" --no-restore -nr:false
```

Suggested commit if explicitly requested:

```text
feat(poi): query public POI details
```

### Task 7 — Expose public endpoints and complete OpenAPI contracts

Test first:

- Add API tests for anonymous list/detail and authenticated Traveler list/detail access.
- Prove invalid route/query inputs return `400 application/problem+json` with camelCase validation
  keys.
- Prove empty list returns `200`, and missing/Inactive detail returns 404 with
  `errorCode=Poi.NotFound`.
- Prove injected persistence failure returns sanitized `500 application/problem+json`.
- Add OpenAPI tests for both operations, query defaults/ranges/enums/dependencies, response schemas,
  property requiredness/nullability, and exact `200/400/404/500` responses.
- Prove public operations do not advertise a Bearer security requirement even though the
  Administrator create operation remains protected.

Minimal implementation files:

- `src/TripMate.Api/Controllers/V1/PublicPointsOfInterestController.cs`;
- `src/TripMate.Api/Common/ApiControllerBase.cs` for `Poi.NotFound` mapping;
- extend `src/TripMate.Api/OpenApi/PoiContractSchemaFilter.cs` only for DTO schemas;
- add `src/TripMate.Api/OpenApi/AllowAnonymousOperationFilter.cs` only if the existing global
  Swagger security requirement incorrectly marks public operations as protected;
- `src/TripMate.Api/Program.cs` only to register a required filter;
- `tests/TripMate.Api.IntegrationTests/PointsOfInterest/ExplorePoisEndpointTests.cs`;
- `tests/TripMate.Api.IntegrationTests/PointsOfInterest/GetPoiDetailEndpointTests.cs`;
- extend `PointsOfInterestOpenApiTests.cs` without weakening TM-98 assertions.

Implementation rules:

- Use a new controller with `[AllowAnonymous]` and route `api/v1/pois`; do not relax the existing
  Administrator controller authorization.
- Use `[FromQuery]` for the list query and dispatch through `ISender`.
- Swagger must describe actual runtime bodies, not aspirational schemas.
- The existing TM-98 Create POI endpoint and its tests must remain unchanged and green.

Focused verification:

```powershell
& $dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ExplorePoisEndpointTests|FullyQualifiedName~GetPoiDetailEndpointTests|FullyQualifiedName~PointsOfInterestOpenApiTests" --no-restore -nr:false
```

Suggested commit if explicitly requested:

```text
feat(api): expose public POI exploration
```

### Task 8 — Prove SQL Server translation, bounded queries, and read-only behavior

Test first against the isolated SQL test database:

- Seed POIs, photos, and reviews using parameterized SQL or the test database helper.
- Prove Vietnamese case-insensitive name search under `Vietnamese_100_CI_AS`.
- Prove list distance calculation, radius boundary, all three sort orders, rating aggregation,
  thumbnail selection, opening-now filtering, stable pagination, and Active-only behavior execute
  successfully on SQL Server.
- Prove detail returns ordered hours/photos/tags and rating summary.
- Capture commands with a test interceptor and prove list uses one count plus one bounded result
  query with no N+1 behavior. Record the fixed detail query count and prove it does not depend on
  the number of children.
- Snapshot row counts or values before and after list/detail calls and prove POIs, tags, reviews,
  photos, and audit rows are unchanged.
- Pass a cancelled token and prove async database execution observes cancellation without returning
  a partial response.

Minimal implementation files:

- `tests/TripMate.Api.IntegrationTests/PointsOfInterest/ExplorePoisSqlServerTests.cs`;
- extend `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerTestDatabase.cs` only with
  reusable parameterized seed/read helpers that do not log credentials;
- a test-only command-count interceptor under the integration-test `Infrastructure` directory if
  required.

Implementation rules:

- Do not mark these tests optional for completion. Zero skips is required.
- Do not embed credentials, use the developer database, or reuse a persistent application database.
- The harness must continue to create and safely drop only databases with the existing isolated
  `TripMate_Test_` naming convention.
- If EF cannot translate the approved expression, change the LINQ shape minimally; do not move
  catalogue filtering to memory.

Focused verification:

```powershell
& $dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ExplorePoisSqlServerTests" --no-restore -nr:false
```

Suggested commit if explicitly requested:

```text
test(poi): verify exploration on SQL Server
```

### Task 9 — Final reconciliation, regression, and security review

Required verification:

1. Run formatter verification, clean build, and the entire solution suite with SQL Server enabled.
2. Require zero failures and zero skips; report the real final test count and breakdown by project.
3. Run package vulnerability inspection and document the actual result.
4. Run `git diff --check`, `git status --short`, `git diff`, and `git diff --cached`.
5. Confirm no `bin`, `obj`, `.env`, credentials, connection strings, generated database files,
   `package-lock.json`, migrations, or unrelated changes are staged.
6. First self-review: compare every acceptance criterion and DTO field against the approved spec
   and canonical Docs contract.
7. Second self-review: architecture, cancellation, null handling, query count, performance,
   security, error sanitization, and regression impact.
8. Update `specs/TM-58-spec.md` and this plan with only measured final verification results; never
   guess counts.
9. If runtime behavior changes the API contract, stop and update/approve Docs before continuing.

Suggested documentation commit if explicitly requested:

```text
docs(poi): reconcile TM-58 delivery evidence
```

Measured verification results (2026-09-13):
- Formatter verification: `dotnet format TripMate.slnx --include <TM-58 files> --verify-no-changes --no-restore` PASSED with zero formatting errors.
- Solution build: `dotnet build TripMate.slnx --no-restore -nr:false` succeeded with 0 Warning(s) and 0 Error(s).
- Test execution: Full solution test suite executed with `TRIPMATE_SQLSERVER_TEST_CONNECTION` against container `tripmate-uc52-e2e`. Result: **230 passed, 0 failed, 0 skipped**.
  - `TripMate.Infrastructure.UnitTests`: 4 passed, 0 failed, 0 skipped (including `PoiExplorationPersistenceModelTests`).
  - `TripMate.Application.UnitTests`: 170 passed, 0 failed, 0 skipped (including explore/detail validators, handlers, `PoiOpeningStateTests`, Haversine projection, sorting, and pagination).
  - `TripMate.Api.IntegrationTests`: 56 passed, 0 failed, 0 skipped (including anonymous access, authenticated access, validation problem details, OpenAPI contract tests, and all 9 SQL Server integration tests in `ExplorePoisSqlServerTests`).
- Vulnerability audit: `dotnet list package --vulnerable --include-transitive` returned 0 vulnerable packages across all 7 projects.
- Database execution on SQL Server:
  - Bounded list query: exactly 1 count query + 1 bounded items query (2 queries total, 0 N+1 queries).
  - Bounded detail query: exactly 3 queries (main POI + split queries for Photos and Tags, 0 N+1 queries).
  - Vietnamese collation: case-insensitive search verified under `Vietnamese_100_CI_AS`.
  - Read-only integrity: POIs, tags, tag mappings, photos, reviews, and audit logs remained completely unchanged before and after query execution.
  - Cancellation observation: `CancellationToken` cancellation observed and propagated without returning partial data.
- Git and secret hygiene: `git diff --check`, `git status --short`, `git diff`, and `git diff --cached` verified clean. No secrets, credentials, migrations, `bin`/`obj`, or `package-lock.json` are staged.

## 5. Definition of Done for the backend increment

- Both public endpoints satisfy the approved D1-D6 contract.
- Code, runtime JSON, OpenAPI, database mappings, BE spec, and Docs agree.
- Active-only behavior, validation, not-found handling, calculated fields, ordering, and pagination
  have automated coverage.
- SQL Server proves translation, bounded query count, no N+1 behavior, and read-only operation.
- Full build, format, tests, vulnerability inspection, diff, secret, and generated-file checks pass.
- No SQL test is skipped.
- No schema or migration is added.
- TM-98 Administrator creation behavior remains green.
- A human cross-review approves the final commit and no blocking comment remains.
- The PR is merged only after the developer explicitly requests or performs that action.

This backend Definition of Done does not claim completion of responsive Web, Flutter Mobile, UI
review, or end-to-end UAT; those remain later delivery gates for the full UC-12 use case.

## 6. Plan approval gate

Approval of D1-D6 authorized creation of this plan, not its execution. Production-code Task 1 may
start only after the developer explicitly approves this plan and the SQL Server baseline completes
with zero skipped tests.
