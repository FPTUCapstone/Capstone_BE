# TM-98 — Create POI Backend Implementation Plan

Status: **APPROVED — 2026-09-08**

Spec: `specs/TM-98-spec.md` (approved 2026-09-08)

Amendment approved 2026-09-09: Task 6 returns the created DTO without a `Location` header until
an approved read endpoint exists; see the corresponding specification amendment.

Branch: `feature/datmnt-create-poi`

Delivery order: Backend implementation first; Frontend and end-to-end UAT remain later gates.

## 1. Baseline

The feature branch was created from `develop` before implementation.

The host does not have the .NET SDK in PATH, so verification uses the repository's .NET 10
Docker toolchain:

```powershell
$repoPath = (Get-Location).Path
docker run --rm --mount "type=bind,source=$repoPath,target=/src" --workdir /src `
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test TripMate.slnx
```

Baseline result: **19 passed, 0 failed, 0 skipped**.

Existing non-blocking warning: `NU1510` for `System.Security.Cryptography.Xml`; it predates
TM-98 and is not changed by this task.

## 2. Implementation constraints

- Follow Red → Green → Refactor for every task below.
- Do not create EF Core migrations.
- Preserve Clean Architecture dependency direction.
- Use the existing `Result<T>`, MediatR, FluentValidation, and `HandleFailure` patterns.
- Persist POI children through navigation properties.
- Do not implement commercial services, contact information, images, list/detail, update,
  deactivate, routes, algorithm configuration, Frontend, or storage integration.
- Run a spec-compliance review and then a code-quality review after every task.
- Do not commit, push, open a PR, or merge without a separate explicit request.

## 3. Atomic tasks

### Task 1 — Add structured failure metadata to Result

Purpose: a duplicate conflict must return `existingPoiId` in RFC-7807 extensions without
creating a custom response envelope.

Write failing tests first:

- `tests/TripMate.Application.UnitTests/Common/Models/ResultTests.cs`

Then change:

- `src/TripMate.Application/Common/Models/Result.cs`

Required behavior:

- Successful results expose no error metadata.
- Failure factories retain an immutable/read-only metadata dictionary.
- Existing Result callers remain source-compatible.

Verification:

```text
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj
  --filter FullyQualifiedName~ResultTests
```

Definition of Done: focused tests pass and existing auth Result behavior is unchanged.

### Task 2 — Introduce the POI domain aggregate

Write failing domain tests first:

- `tests/TripMate.Application.UnitTests/Domain/PointOfInterestTests.cs`
- `tests/TripMate.Application.UnitTests/Domain/PoiOpeningHourTests.cs`

Then add:

- `src/TripMate.Domain/Enums/PointOfInterestStatus.cs`
- `src/TripMate.Domain/Enums/IndoorOutdoorType.cs`
- `src/TripMate.Domain/Entities/PoiCategory.cs`
- `src/TripMate.Domain/Entities/PointOfInterest.cs`
- `src/TripMate.Domain/Entities/PoiOpeningHour.cs`
- `src/TripMate.Domain/Entities/Tag.cs`
- `src/TripMate.Domain/Entities/PoiTag.cs`
- `src/TripMate.Domain/Entities/AuditLog.cs`

Required behavior:

- Factory creation trims the name and optional text.
- Coordinates are normalized to six decimal places and remain within geographic bounds.
- New POIs are `Active`; average duration defaults to 60; indoor/outdoor defaults to `Outdoor`;
  shelter defaults to false; scenic/photo scores remain null.
- Opening hours enforce the approved Sunday=0, closed/open, and non-overnight invariants.
- Collections are controlled by domain methods; EF-only constructors/setters are non-public.

Verification: run the two focused domain test classes.

Definition of Done: the aggregate represents only the approved SQL v7 POI subset and has no
EF Core or ASP.NET dependency.

### Task 3 — Add database-first persistence mappings and transaction boundary

Write failing model-metadata tests first in a new infrastructure test project:

- `tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj`
- `tests/TripMate.Infrastructure.UnitTests/Persistence/PoiPersistenceModelTests.cs`
- update `TripMate.slnx` to include the test project

Then change:

- `src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs`
- `src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs`
- `tests/TripMate.Application.UnitTests/TestUtilities/TestDbContext.cs`

Then add mappings:

- `src/TripMate.Infrastructure/Persistence/Configurations/PoiCategoryConfiguration.cs`
- `src/TripMate.Infrastructure/Persistence/Configurations/PointOfInterestConfiguration.cs`
- `src/TripMate.Infrastructure/Persistence/Configurations/PoiOpeningHourConfiguration.cs`
- `src/TripMate.Infrastructure/Persistence/Configurations/TagConfiguration.cs`
- `src/TripMate.Infrastructure/Persistence/Configurations/PoiTagConfiguration.cs`
- `src/TripMate.Infrastructure/Persistence/Configurations/AuditLogConfiguration.cs`

Required behavior:

- Map exactly to `catalog.POICategories`, `catalog.POIs`, `catalog.POIOpeningHours`,
  `catalog.Tags`, `catalog.POITagMap`, and `dbo.AuditLogs`.
- Match keys, composite keys, column names, lengths, precision, indexes, enum conversions,
  cascade behavior, and UTC `datetime2` conversion from SQL v7.
- Extend the context abstraction with an execution-strategy-aware transaction method.
- Production uses one explicit transaction around the two saves required to obtain the POI
  identity and then persist the audit row; the InMemory test context uses a pass-through
  transaction implementation.

Verification: run infrastructure model tests, then all Application unit tests.

Definition of Done: EF model metadata matches SQL v7 and no migration files exist.

### Task 4 — Define and validate the Create POI command contract

Write failing validator tests first:

- `tests/TripMate.Application.UnitTests/Features/PointsOfInterest/Create/CreatePoiCommandValidatorTests.cs`

Then add:

- `src/TripMate.Application/Features/PointsOfInterest/Common/PoiErrorCodes.cs`
- `src/TripMate.Application/Features/PointsOfInterest/Common/PoiResponseDto.cs`
- `src/TripMate.Application/Features/PointsOfInterest/Create/CreatePoiCommand.cs`
- `src/TripMate.Application/Features/PointsOfInterest/Create/CreatePoiOpeningHourInput.cs`
- `src/TripMate.Application/Features/PointsOfInterest/Create/CreatePoiCommandValidator.cs`

Required validation:

- Required/length rules for name and a positive category ID.
- Latitude `[-90, 90]`, longitude `[-180, 180]`.
- Optional text lengths match SQL v7.
- Positive average duration when supplied.
- Distinct positive tag IDs.
- At most one opening-hours item per day; days 0–6.
- Closed days have null times; open days have two times with open earlier than close.

Verification: run focused validator tests.

Definition of Done: the command contains only fields approved by the spec and validation errors
flow through the existing FluentValidation ProblemDetails path.

### Task 5 — Implement the Create POI handler atomically

Write failing handler tests first:

- `tests/TripMate.Application.UnitTests/Features/PointsOfInterest/Create/CreatePoiCommandHandlerTests.cs`
- extend `tests/TripMate.Application.UnitTests/TestUtilities/FakeServices.cs` only if a focused
  fake is required

Then add:

- `src/TripMate.Application/Features/PointsOfInterest/Create/CreatePoiCommandHandler.cs`

Handler order:

1. Resolve the current user and verify the persisted account is an Active Administrator.
2. Verify the category exists.
3. Verify every distinct tag exists.
4. Normalize name and coordinates.
5. Search for an Active duplicate using normalized case-insensitive name and exact six-decimal
   coordinates.
6. Return `Poi.PossibleDuplicate` with `existingPoiId` when confirmation is false.
7. Build the POI aggregate and approved children.
8. In one transaction, save the aggregate, create `POI_CREATE` audit JSON with the generated ID,
   save the audit row, and commit.
9. Return the persisted DTO.

Required tests:

- valid creation and defaults;
- inactive/non-Administrator denial;
- missing category;
- missing tag IDs;
- duplicate conflict metadata;
- confirmed duplicate creation;
- opening-hours and tag persistence;
- creator and UTC timestamps;
- audit row content;
- no audit row for rejected commands.

Verification: run focused handler tests, then the full Application test project.

Definition of Done: all approved writes occur atomically and no adjacent feature is introduced.

### Task 6 — Expose and verify the Administrator HTTP endpoint

Write failing API integration tests first:

- `tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj`
- `tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs`
- `tests/TripMate.Api.IntegrationTests/PointsOfInterest/CreatePoiEndpointTests.cs`
- update `TripMate.slnx` to include the test project

Then change/add:

- `src/TripMate.Api/Common/ApiControllerBase.cs`
- `src/TripMate.Api/Controllers/V1/PointsOfInterestController.cs`
- `src/TripMate.Api/Program.cs` only to expose the test host entry point when required

Required behavior:

- `POST /api/v1/admin/pois`.
- `[Authorize(Roles = "Administrator")]` protects the endpoint.
- `201 Created` returns the POI DTO without publishing a `Location` until an approved GET-by-ID
  route exists. A future read endpoint must expose a named route before Create uses
  `CreatedAtRoute(...)`.
- Missing authentication returns 401. An authenticated non-Administrator returns 403 RFC 7807
  ProblemDetails with `errorCode = Poi.AdminAccessRequired`; endpoint metadata and a shared
  authorization result handler keep this contract consistent before controller execution.
- Missing references return 404.
- Possible duplicate returns 409 RFC-7807 with `errorCode` and `existingPoiId`.
- Validation uses standard RFC-7807 `ValidationProblemDetails`.

The integration-test host replaces persistence with an isolated InMemory context and test
authentication; production configuration remains unchanged.

Verification: run the API integration test project.

Definition of Done: the real HTTP pipeline proves authorization, status mapping, response body,
the intentional absence of a premature Location header, and duplicate metadata.

### Task 7 — Full verification and two-pass review

Run:

```powershell
$repoPath = (Get-Location).Path
docker run --rm --mount "type=bind,source=$repoPath,target=/src" --workdir /src `
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet test TripMate.slnx
docker run --rm --mount "type=bind,source=$repoPath,target=/src" --workdir /src `
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet build TripMate.slnx --no-restore
git diff --check
```

Review pass 1 — spec compliance:

- Trace every approved spec statement to code and test evidence.
- Confirm excluded commercial/image/FE scope was not added.
- Confirm no EF migrations or schema edits were introduced.

Review pass 2 — code quality:

- Review architecture direction, domain invariants, query tracking, cancellation, atomicity,
  authorization, error disclosure, JSON audit content, and naming.
- Classify Critical findings as blocking and fix them before reporting completion.

Definition of Done:

- Full solution build/test passes.
- No Critical review finding remains.
- Backend completion evidence is reported honestly.
- TM-98 remains pending Frontend/UI/UAT and is not moved to Done as part of this backend plan.

## 4. Approval gate result

The developer approved this plan on 2026-09-08. Implementation may proceed atomically.
