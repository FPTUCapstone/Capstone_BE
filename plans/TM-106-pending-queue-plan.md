# TM-106 Pending Tour Queue & Review Detail Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (native, in-session). Steps use checkbox syntax. TDD: every code step is preceded by a test step that was seen to fail.

**Goal:** Give Staff a paginated queue of `Pending` Tours and a review-detail read model so the existing FE Screens 13/14 can work on real data.

**Architecture:** Two read-only MediatR queries under `Features/Admin/Tours/Review/` following the `Admin/AuditLogs/GetList` pattern (`PaginatedList<T>`, FluentValidation, `Result<T>` + `HandleFailure`, `AsNoTracking`), exposed by two `[HttpGet]` actions on `AdminToursController`. Staff-only check lives in the handler, identical to `ApproveTourCommandHandler`.

**Tech Stack:** .NET 10, EF Core (SQL Server), MediatR, FluentValidation, xUnit + FluentAssertions, `TestDbContext` (in-memory) and `SqlServerTestDatabase` (disposable DB).

**Spec:** `specs/TM-106-pending-queue-spec.md`

## Global Constraints
- Staff only (`Role == nameof(UserRole.Staff)`); others get `OperatorTourErrorCodes.AdminAccessRequired` (403). Administrator is **not** allowed.
- No schema change, no migration, no write to any table, read queries use `AsNoTracking`.
- Queue: `PageSize` 1..100 default 20; `PageNumber` >= 1; `Keyword` <= 100 chars; order `UpdatedAtUtc` asc then `Id` asc; only `TourStatus.Pending`.
- Never expose operator phone/address/tax code/email/business license.
- Detail of a non-Pending tour -> `OperatorTourErrorCodes.InvalidState` (409); unknown id -> `OperatorTourErrorCodes.TourNotFound` (404).
- `.cs` files must have **no final newline** (`insert_final_newline = false`); run `dotnet format --include <files>` before committing.

## Review Focus
- Keyword containing `%` or `_` must match literally, not act as a wildcard (queue must not leak every Pending tour through `%`).
- Page beyond the last page returns an empty `items` with correct `totalCount`, not an error.
- Pending tour with no category, no schedule, no media, no itinerary still renders (nulls/empty arrays, no crash).
- Soft-deleted / non-active media is excluded from `imageCount` and `images`.
- `Inclusions`/`Exclusions` with `\r\n`, blank lines and null -> clean string arrays.
- A Pending tour whose operator is locked is still listed (reviewer must still be able to reject it).

---

### Task 1: Queue query (list)

**Files:**
- Create: `src/TripMate.Application/Features/Admin/Tours/Review/PendingTours/GetPendingToursQuery.cs` (query, validator, `PendingTourSummaryDto`)
- Create: `src/TripMate.Application/Features/Admin/Tours/Review/PendingTours/GetPendingToursQueryHandler.cs`
- Test: `tests/TripMate.Application.UnitTests/Features/Admin/Tours/Review/GetPendingToursQueryHandlerTests.cs`, `GetPendingToursQueryValidatorTests.cs`

**Interfaces:**
- Produces: `record GetPendingToursQuery(string? Keyword = null, int PageNumber = 1, int PageSize = 20) : IRequest<Result<PaginatedList<PendingTourSummaryDto>>>`
- Produces: `record PendingTourSummaryDto(long TourId, string TourName, string OperatorName, string Location, string? Category, DateTimeOffset SubmittedAtUtc, int DurationDays, decimal PriceVnd, DateTimeOffset? NextDepartureAtUtc, int? NextDepartureCapacity, int ImageCount)`

- [ ] **Step 1: Write failing tests** (names): `Handle_WhenCallerNotStaff_ReturnsForbidden` (Administrator, null user), `Handle_ListsOnlyPendingTours` (Draft/Approved/Rejected/Inactive excluded), `Handle_OrdersOldestSubmittedFirstThenById`, `Handle_FiltersByTitleOrOperatorCompanyName`, `Handle_TreatsPercentAndUnderscoreInKeywordLiterally`, `Handle_PagesWithCorrectTotals` (25 tours -> page 2 size 20 has 5, `TotalPages == 2`), `Handle_PageBeyondLastReturnsEmptyItemsWithTotals`, `Handle_MapsSummaryFields` (category name, price, earliest departure + its capacity, `ImageCount` counts only active non-deleted media), `Handle_TourWithoutCategoryScheduleOrMedia_ReturnsNullsAndZero`, `Handle_StillListsPendingTourOfLockedOperator`. Validator: page 0, size 0/101, keyword 101 chars invalid; defaults valid.
- [ ] **Step 2: Run** `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --no-restore --filter "FullyQualifiedName~GetPendingTours"` -> Expected: build error / FAIL (types missing).
- [ ] **Step 3: Implement** the query, `GetPendingToursQueryValidator : AbstractValidator<GetPendingToursQuery>` and handler. Escape `%`, `_`, `[` in the keyword before `EF.Functions.Like(..., $"%{escaped}%", "\\")`. Next departure = earliest `Schedules` by `StartAtUtc` (project with a subquery, no N+1). Image count = media with `LifecycleStatus == Active` and `DeletedAtUtc == null` (confirm enum name in `TourMediaLifecycleStatus`).
- [ ] **Step 4: Run** same filter -> Expected: all PASS.
- [ ] **Step 5: Commit** `feat(uc60): add Staff pending tour queue query (TM-106)`.

### Task 2: Review detail query

**Files:**
- Create: `.../PendingTours/GetPendingTourReviewQuery.cs` (query + `PendingTourReviewDto` + nested DTOs), `GetPendingTourReviewQueryHandler.cs`
- Test: `tests/.../GetPendingTourReviewQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `PendingTourSummaryDto` fields from Task 1 (the detail DTO repeats them, it does not inherit).
- Produces: `record GetPendingTourReviewQuery(long TourId) : IRequest<Result<PendingTourReviewDto>>`; `PendingTourReviewDto` = summary fields + `string? Overview, string? Highlights, IReadOnlyList<string> IncludedServices, IReadOnlyList<string> ExcludedServices, string? TravellerRequirements, IReadOnlyList<CancellationRuleDto> CancellationPolicy(int DaysBeforeDeparture, decimal FeePercent), IReadOnlyList<ScheduleDto> Schedules(DateTimeOffset StartAtUtc, DateTimeOffset EndAtUtc, string? MeetingPoint, int Capacity, decimal? AdultPrice, decimal? ChildPrice, decimal? InfantPrice), IReadOnlyList<ItineraryStopDto> Itinerary(int DayNo, int SequenceNo, string PoiName, TimeOnly? PlannedTime, int StayMinutes, string? Notes), IReadOnlyList<ImageDto> Images(string Url, string AltText, string? Caption, bool IsPrimary)`.

- [ ] **Step 1: Write failing tests:** `Handle_WhenCallerNotStaff_ReturnsForbidden`, `Handle_WhenTourMissing_ReturnsNotFound`, `Handle_WhenTourNotPending_ReturnsInvalidState` (theory Draft/Approved/Rejected/Published), `Handle_MapsEveryChecklistInput` (overview, price, policy rules ordered by days desc, schedules ordered by start, itinerary ordered by day/sequence with POI name, images ordered by `SortOrder` excluding deleted), `Handle_SplitsServicesOnLineBreaksAndDropsBlanks` (`"A\r\n\r\n B \n"` -> `["A","B"]`; null -> empty), `Handle_ResponseContainsNoOperatorContactData` (serialize DTO to JSON, assert it contains none of the operator's phone/address/tax code/email), `Handle_TourWithNothingOptional_ReturnsEmptyCollections`.
- [ ] **Step 2: Run** `--filter "FullyQualifiedName~GetPendingTourReview"` -> Expected: FAIL.
- [ ] **Step 3: Implement** handler with one `AsNoTracking` projection query (`Select` into the DTO); status check after load so 404 vs 409 are distinguishable.
- [ ] **Step 4: Run** -> Expected: PASS.
- [ ] **Step 5: Commit** `feat(uc60): add Staff tour review detail query (TM-106)`.

### Task 3: HTTP endpoints

**Files:**
- Modify: `src/TripMate.Api/Controllers/V1/AdminToursController.cs` (add `[HttpGet("pending")]` returning `GetPendingToursQuery` via `[FromQuery]`, and `[HttpGet("{tourId:long}/review")]`; both `[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]`; route `pending` must not collide with `{tourId:long}`)
- Test: `tests/TripMate.Api.IntegrationTests/Admin/PendingTourQueueEndpointTests.cs`

- [ ] **Step 1: Write failing tests:** anonymous -> 401; Administrator, Traveler, TourOperator -> 403 on both routes; Staff -> 200 with `items`, `totalCount`, `pageNumber`, `pageSize`, `totalPages`; `?pageSize=101` -> 400; `?pageNumber=0` -> 400; detail unknown id -> 404; detail of an Approved tour -> 409; `Cache-Control` contains `no-store`.
- [ ] **Step 2: Run** `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --no-restore --filter "FullyQualifiedName~PendingTourQueueEndpointTests"` -> Expected: FAIL (404/405).
- [ ] **Step 3: Implement** the two actions (pattern: `result.IsSuccess ? Ok(result.Value) : HandleFailure(result)`). Confirm `HandleFailure` maps `TourNotFound`/`InvalidState`/`AdminAccessRequired` already (it does for Approve).
- [ ] **Step 4: Run** -> Expected: PASS.
- [ ] **Step 5: Commit** `feat(uc60): expose pending tour queue and review detail endpoints (TM-106)`.

### Task 4: Real SQL Server proof

**Files:**
- Test: `tests/TripMate.Api.IntegrationTests/Admin/PendingTourQueueSqlServerTests.cs` (`[SqlServerFact]`, `[Trait("Category","SqlServer")]`, `[Trait("Classification","RequiredBehavior")]`)

- [ ] **Step 1: Write the test:** seed one Pending and one Approved tour (reuse the `SeedPendingTourAsync` approach from `ApproveTourSqlServerTests`, **with explicit `+07:00` offsets**), call `GET /api/v1/admin/tours/pending?keyword=<part of title>` as Staff -> exactly the Pending tour; `keyword=%` -> zero items; `GET .../{id}/review` -> 200 with itinerary and policy; Approved id -> 409.
- [ ] **Step 2: Run** with `TRIPMATE_SQLSERVER_TEST_CONNECTION="Server=localhost;Database=master;Integrated Security=True;Encrypt=False;TrustServerCertificate=True"` -> Expected: PASS (it exercises EF translation that the in-memory provider hides; if it fails, fix the query, not the test).
- [ ] **Step 3: Verify** no `TripMate_Test_*` database is left behind.
- [ ] **Step 4: Commit** `test(uc60): prove pending tour queue on real SQL Server (TM-106)`.

### Task 5: Verify and publish
- [ ] `dotnet format TripMate.slnx --verify-no-changes --no-restore --include <changed .cs files>` -> exit 0.
- [ ] Application unit (excl. `Golden`) and Api non-SQL tests -> 0 failures; SQL suite for the new tests passes locally.
- [ ] Push branch, open a PR **against `feature/tm-106-approve-tour-post`** (stacked on #188, or `develop` once #188 merges) with evidence; do not merge.
