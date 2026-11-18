# TM-106 — Approve Tour Post (UC-60) Technical & Ownership Handoff

> **Handoff Status:** Backend locally verified within TM-106 scope; Draft PR published for independent external review, remote CI observation, and continuation by the receiving team member. **Not merged, not deployed, not Jira Done, and not full UC-60 acceptance.**
>
> **Updated 2026-11-18 (receiving member):** CI formatting/fixture blockers fixed, TM-107 Reject brought under the shared serializable invariant, SRS source still unverified, Booking blocker still open. See **Section J** for the dated log and evidence; Sections A–I are the original handoff record.

---

## A. Task Identity

| Field | Value |
|---|---|
| **Task Key** | `TM-106` |
| **Task Title** | Approve Tour Post |
| **Authoritative SRS Function** | **UC-60 — Approve Tour Post** (SRS Table 2, Row 60, §3.9.7.1) |
| **Jira Label Discrepancy** | Jira currently displays `[UC-59]` in the title/label; authoritative SRS mapping is **UC-60** (§3.9.7.1). |
| **Repository** | `FPTUCapstone/Capstone_BE` |
| **Feature Branch** | `feature/tm-106-approve-tour-post` |
| **Target Base Branch** | `develop` (base SHA verified prior to push: `dd0ff7507cb9f55143f83578f9d20fe5a7198203`) |
| **Original Implementer** | Mai Nguyễn Tiến Đạt |
| **New Ownership** | Handed over by Project Owner |
| **New Member Name / Handle** | `HANDOFF_RECIPIENT_NOT_SPECIFIED` |
| **Jira Status at Handoff** | `In Progress` (unmodified during handoff) |
| **Handoff Date** | `2026-11-18` |
| **Implementation Code HEAD (prior to handoff doc commit)** | `591863e62daa47f77a9043c14f66504028ccc67d` |
| **SRS Checksum Status** | `SOURCE_VERSION_NOT_VERIFIED` — Official Project SRS SHA-256 is `a4ceabc0389bcffe8263254927ebe417abef6923d898f92c3003171add018298`; the local workspace copy differed (`c7636775d4208d8284905a04af5f79eacd4d2d05cb72d1022b12e7e830390be5`). Neither the SRS binary nor the read-only DB BACPAC is committed in this branch. |

---

## B. Business Purpose & SRS Traceability

### Core Business Flow (UC-60, §3.9.7.1)
An authenticated, active **Staff** member reviews a `Pending` Tour submitted by a Tour Operator (Approval Center / Screens 13 & 14), verifies all **five mandatory quality-review checklist criteria**, and approves that submitted version:

1. `ContentCompleteness`
2. `ImageAppropriateness`
3. `PricePlausibility`
4. `PolicyCompliance`
5. `ItineraryFeasibility`

Upon successful Backend approval:
- The Tour transitions strictly from `TourStatus.Pending` → **`TourStatus.Approved`** (`RejectionReason` cleared).
- **Owner-approved publication semantics:** `PublishedAtUtc` is set to the approval timestamp (`reviewedAtUtc`), making the approved version immediately eligible for public discovery (Search / Detail) and traveler booking under existing operator/schedule eligibility rules without requiring a separate Publish click.
- Reviewer, timestamp, and version lineage metadata (`ReviewedBy`, `ReviewedAtUtc`, `PublishedAtUtc`, `VersionNo`, `VersionRootTourId`) are recorded alongside a rich `TourApproved` audit log entry in the same atomic SQL transaction.

### Applicable SRS Rules, Messages, and Postconditions

| Reference | Summary & Implementation Boundary |
|---|---|
| **BR-115** | Staff-only actor authorization (`UserRole.Staff` enforced by `[Authorize(Roles = nameof(UserRole.Staff))]`, active session/account DB check in `JwtBearerEvents.OnTokenValidated` → `UserSessionValidation.ValidateAsync`, and handler-level `AuthorizeStaff()`). Note: an inconsistent "Administrator" mention in the SRS validation subsection is recorded as a source inconsistency; UC-60 and BR-115 designate **Staff**. |
| **BR-125** | Mandatory 5-item checklist verification before approval can succeed; enforced in both FluentValidation (`ApproveTourCommandValidator`) and handler defensive guard. |
| **BR-104** | Approval is permitted **only** when the tour is in `Pending` status; non-pending or already-moderated tours are rejected with `409 Conflict` (`operator_tour.invalid_state`). |
| **BR-56** | Public visibility & bookability eligibility predicates are preserved (e.g., `TourStatus.Approved` with activated `PublishedAtUtc <= now`, active approved operator, valid schedules). Visibility predicates were **not** bypassed or weakened. |
| **BR-103** | Version lineage (`VersionNo`, `VersionRootTourId`) is preserved on approval and unpublish and recorded in audit payloads. |
| **BR-118** | Approval records immutable moderation metadata (`ReviewedBy`, `ReviewedAtUtc`) and audit provenance (`TourApproved`). |
| **BR-130** | Concurrent / duplicate moderation guard: Serializable transaction + bounded deadlock retry ensures only one approval commits (`200 OK`) and concurrent/repeated approval receives `409 Conflict` (`MSG133` wording). |
| **MSG01** | Required field validation error code (`"MSG01"`, `"This field is required."`) emitted by `ApproveTourCommandValidator` for invalid `TourId`, null `Checklist`, or any checklist criterion not equal to `true`. |
| **MSG126** | Access denied / permission failure (`403 Forbidden`, `operator_tour.admin_access_required`, `"You do not have permission to access this function."`). |
| **MSG127** | Persistence / service unavailability (`503 ServiceUnavailable`, `errorCode: "MSG127"`, `"TripMate is temporarily unable to process your request. Please check your connection and try again."`) on genuine persistence failures (`DbUpdateException`, `DbException`, `TimeoutException`, or exhausted deadlock retries), with full transaction rollback. |
| **MSG133** | Conflict when the tour is no longer in `Pending` status (`409 Conflict`, `operator_tour.invalid_state`, `"The selected package is no longer in the Pending Approval status."`). |
| **MSG144** | Frontend approval success feedback message — **TM-38 Frontend responsibility** (Backend returns `200 OK` with `TourReviewResponse`). |
| **CR-05** | Confirmation modal prior to submitting approval — **`TM-38 FRONTEND_DEPENDENCY`** (`NOT_VERIFIED` under Backend scope). |
| **PC-01** | Approved tour status updated to `Approved` and immediately eligible for public visibility/booking — **IMPLEMENTED (Backend)**. |
| **PC-02** | Reviewer ID, timestamp, version lineage, and approval audit persisted atomically — **IMPLEMENTED (Backend)**. |
| **PC-03** | Operator notification — **`PC03_PARTIAL_INAPP_ONLY`** (updated 2026-11-18, see Section J): an in-app `TourApproved` notice is now written atomically with the approval; **email delivery remains deferred** (no outbox/worker/schema change). Original Owner scope note preserved in Dependency D. |
| **PC-04** | Tour removed from Pending Approval queue once status leaves `Pending` — **IMPLEMENTED (Backend state transition)**. |
| **PC-05** | Confirmation UI / toast display (`MSG144`) — **`TM-38 FRONTEND_DEPENDENCY`**. |

---

## C. Implemented Functionality & Exact API Contracts

### Modified / Added Production Files (7 files — Zero Migrations)
1. `src/TripMate.Api/Controllers/V1/AdminToursController.cs` — Updated `POST /api/v1/admin/tours/{tourId:long}/approve` to accept `[FromBody] ApproveTourRequest? request` (`Checklist`, optional `Note`) and dispatch `ApproveTourCommand`. Preserved `[Authorize(Roles = nameof(UserRole.Staff))]` and `[ForbiddenProblemDetails(OperatorTourErrorCodes.AdminAccessRequired, ...)]`.
2. `src/TripMate.Application/Features/Admin/Tours/Review/ReviewTourCommands.cs` — Added `TourReviewChecklistDto` and extended `ApproveTourCommand(long TourId, TourReviewChecklistDto? Checklist = null, string? Note = null)`.
3. `src/TripMate.Application/Features/Admin/Tours/Review/ApproveTourCommandValidator.cs` *(NEW)* — FluentValidation validator enforcing `TourId > 0`, non-null `Checklist`, all 5 booleans `== true` with error code `MSG01` (`"This field is required."`), and optional `Note` max length 1000.
4. `src/TripMate.Application/Features/Admin/Tours/Review/TourReviewCommandHandlers.cs` — Implemented hardened `ApproveTourCommandHandler` (staff check, checklist defensive guard, Serializable transaction, bounded deadlock retry, immediate publication `tour.Approve(reviewerId, now, publishedAtUtc: now)`, rich `TourApproved` audit log, `IsPersistenceFailure` → `MSG127`, cancellation propagation) and Gate 1 `UnpublishTourCommandHandler` (see Section D). `RejectTourCommandHandler` and `PublishTourCommandHandler` were **not** modified.
5. `src/TripMate.Domain/Entities/Tour.cs` — Extended `Tour.Approve(long reviewerUserId, DateTimeOffset reviewedAtUtc, DateTimeOffset? publishedAtUtc = null)` while keeping backward compatibility for 2-argument callers (`PublishedAtUtc == null`), and added Gate 1 `Tour.CanUnpublish(DateTimeOffset asOfUtc)` + updated `Tour.Unpublish(DateTimeOffset unpublishedAtUtc)`.
6. `src/TripMate.Application/Features/Operator/Tours/Common/OperatorTourErrorCodes.cs` — Added `ReviewChecklistIncomplete = "operator_tour.review_checklist_incomplete"` and `ServiceUnavailable = "MSG127"`.
7. `src/TripMate.Api/Common/ApiControllerBase.cs` — Mapped `OperatorTourErrorCodes.ReviewChecklistIncomplete` → HTTP `400 BadRequest` (`MSG127` was already mapped globally to HTTP `503 ServiceUnavailable`).

### Endpoint 1: Approve Tour Post
- **Route:** `POST /api/v1/admin/tours/{tourId}/approve`
- **Authentication / Authorization:** Bearer JWT validated against DB session state (`UserSessionValidation.ValidateAsync`); **Staff role only** (`UserRole.Staff`). Inactive/locked Staff fail token validation (`401`); Administrator / Operator / Traveler receive `403 Forbidden` (`errorCode: "operator_tour.admin_access_required"`).
- **Request Body (`application/json`):**
```json
{
  "checklist": {
    "contentCompleteness": true,
    "imageAppropriateness": true,
    "pricePlausibility": true,
    "policyCompliance": true,
    "itineraryFeasibility": true
  },
  "note": "Optional internal review note"
}
```
- **Success Response (`200 OK`):**
```json
{
  "tourId": 1001,
  "status": "Approved",
  "changedAtUtc": "2026-11-18T10:00:00+00:00"
}
```
- **Error / Failure Semantics:**
  - `400 BadRequest` — Missing/incomplete checklist or invalid `TourId` (`MSG01` via FluentValidation; `operator_tour.review_checklist_incomplete` via defensive handler guard), or `note` > 1000 characters.
  - `401 Unauthorized` — Unauthenticated or inactive/invalidated Staff session.
  - `403 Forbidden` — Authenticated non-Staff user (`operator_tour.admin_access_required`).
  - `404 NotFound` — Tour does not exist (`operator_tour.tour_not_found`).
  - `409 Conflict` — Tour is not in `Pending` status (`operator_tour.invalid_state`, `"The selected package is no longer in the Pending Approval status."`).
  - `503 ServiceUnavailable` — Genuine database/persistence failure or exhausted deadlock retry (`MSG127`); transaction rolls back atomically.
  - `OperationCanceledException` when caller cancels request — propagated cleanly, not converted to `MSG127`.
  - Unexpected non-persistence programming exceptions — **not** disguised as `MSG127`.

### Endpoint 2: Unpublish Tour Post (Gate 1 Compatibility)
- **Route:** `POST /api/v1/admin/tours/{tourId}/unpublish`
- **Authentication / Authorization:** Bearer JWT; **Staff role only** (`UserRole.Staff`).
- **Request Body:** None required.
- **Allowed Transitions:**
  - `Published` → `Inactive`
  - `Approved` with `PublishedAtUtc != null && PublishedAtUtc <= nowUtc` → `Inactive`
- **Rejected States (`409 Conflict`, `operator_tour.invalid_state`):** `Draft`, `Pending`, `Rejected`, `Inactive`, `Approved` with `PublishedAtUtc == null`, `Approved` with future `PublishedAtUtc > nowUtc`.
- **Success Response (`200 OK`):**
```json
{
  "tourId": 1001,
  "status": "Inactive",
  "changedAtUtc": "2026-11-18T11:00:00+00:00"
}
```
- **Error / Failure Semantics:** `401` (unauthenticated/inactive), `403` (`operator_tour.admin_access_required`), `404` (`operator_tour.tour_not_found`), `409` (`operator_tour.invalid_state`), `503` (`MSG127` with atomic rollback on genuine persistence failure).

---

## D. Gate 1 — Unpublish Compatibility (Owner Approved)

Because Owner-approved immediate publication keeps an approved tour in `TourStatus.Approved` with `PublishedAtUtc` populated, the legacy `Tour.Unpublish()` guard (`Status != TourStatus.Published`) prevented Staff from unpublishing newly approved live tours. The Project Owner explicitly approved Gate 1 to resolve this within TM-106:

- **Domain (`src/TripMate.Domain/Entities/Tour.cs`):**
  - Added `Tour.CanUnpublish(DateTimeOffset asOfUtc)` and updated `Tour.Unpublish(DateTimeOffset unpublishedAtUtc)`.
  - Permits `Published` → `Inactive` and publicly active `Approved` (`PublishedAtUtc.HasValue && PublishedAtUtc.Value <= normalizedUnpublishedAtUtc`) → `Inactive`.
  - Rejects `Draft`, `Pending`, `Rejected`, `Inactive`, `Approved` without `PublishedAtUtc`, and `Approved` with future `PublishedAtUtc`.
  - Preserves `ReviewedBy`, `ReviewedAtUtc`, `PublishedAtUtc`, `VersionNo`, `VersionRootTourId`, existing schedules, and existing bookings. No new Tour status or schema migration.
- **Handler (`src/TripMate.Application/Features/Admin/Tours/Review/TourReviewCommandHandlers.cs`):**
  - `UnpublishTourCommandHandler` executes inside `ExecuteInSerializableTransactionAsync` with bounded deadlock retry (`maxConcurrencyAttempts = 3`), `ClearTrackedEntities()`, cancellation propagation, and `IsPersistenceFailure` → `MSG127`.
  - `TourUnpublished` audit records the accurate `beforeData.Status` (`"Approved"` or `"Published"`) rather than hardcoding `"Published"`.
- **Verified Post-Unpublish Downstream Behavior:**
  - Public Search and Public Detail no longer expose the `Inactive` tour (`404` on Detail).
  - New traveler Quote and Create booking requests return `404` for the `Inactive` tour.
  - Existing bookings remain intact with reserved capacity preserved.
- **Focused Tests Added:**
  - `tests/TripMate.Application.UnitTests/Features/Admin/Tours/Review/UnpublishTourCommandHandlerTests.cs` (26 tests)
  - `tests/TripMate.Api.IntegrationTests/Admin/UnpublishTourEndpointTests.cs` (14 tests)
  - `tests/TripMate.Api.IntegrationTests/Admin/ApproveTourSqlServerTests.cs` (4 Gate 1 real SQL Server tests)

---

## E. Gate 2 — TM-228 Test Fixture Adjustment (Owner Approved)

- **File Modified:** `tests/TripMate.Api.IntegrationTests/Admin/UserAccountBoundaryTests.cs` (`Operations_StaffCanReachHandlerAndAdministratorIsDenied` — `/approve` case only).
- **Reason:** Pre-TM-106 boundary test sent `null` as the POST body to `/api/v1/admin/tours/9999/approve`, which violated TM-106's mandatory 5-item checklist contract (`400 BadRequest` before reaching the handler's `404 NotFound`).
- **Authorized Change:** Only when `path.EndsWith("/approve", StringComparison.Ordinal)`, the test now sends all five checklist criteria as `true` for both Staff and Administrator requests and explicitly asserts:
  - Staff with valid checklist on missing tour `9999` → HTTP `404 NotFound`.
  - Administrator → HTTP `403 Forbidden` with `errorCode == "operator_tour.admin_access_required"`.
- **Integrity Guarantee:** Missing/incomplete checklist rejection (`400 BadRequest`) remains thoroughly enforced by dedicated TM-106 validator, unit, and endpoint integration tests. **No TM-228 business code was modified.**

---

## F. SQL Server Transactions, Concurrency, and Audit Snapshots

- **Transaction Boundary:** Both `ApproveTourCommandHandler` and `UnpublishTourCommandHandler` execute read + guard + domain transition + `AuditLogs.Add(...)` + `SaveChangesAsync(ct)` inside `dbContext.ExecuteInSerializableTransactionAsync(...)`.
- **Deadlock Retry:** Up to `3` attempts (`maxConcurrencyAttempts = 3`). On `dbContext.IsDeadlock(exception)` (SQL Server Error 1205), tracked entities are cleared (`dbContext.ClearTrackedEntities()`), jittered backoff (`20–50 ms * attempt`) runs, and the next attempt reloads authoritative DB state (returning `409 Conflict` if another request already committed).
- **Atomic Rollback & `MSG127`:** On genuine persistence failure (`DbUpdateException`, `DbException`, `TimeoutException`, or exhausted deadlocks), the SQL transaction rolls back completely (verified via EF Core `SaveChanges` interceptor throwing mid-transaction on real SQL Server: Tour status unchanged, `AuditCount == 0`, HTTP `503` with `MSG127`).

### Synthetic Audit JSON Examples

**`TourApproved` Audit Entry:**
- `beforeData`:
```json
{"Status":"Pending"}
```
- `afterData`:
```json
{
  "Status": "Approved",
  "VersionNo": 1,
  "VersionRootTourId": 1001,
  "ReviewedBy": 42,
  "ReviewedAtUtc": "2026-11-18T10:00:00+00:00",
  "PublishedAtUtc": "2026-11-18T10:00:00+00:00",
  "Checklist": {
    "ContentCompleteness": true,
    "ImageAppropriateness": true,
    "PricePlausibility": true,
    "PolicyCompliance": true,
    "ItineraryFeasibility": true
  },
  "InternalNote": "Verified itinerary and pricing."
}
```

**`TourUnpublished` Audit Entry (for publicly active `Approved` Tour):**
- `beforeData`:
```json
{"Status":"Approved","PublishedAtUtc":"2026-11-18T10:00:00+00:00"}
```
- `afterData`:
```json
{
  "Status": "Inactive",
  "VersionNo": 1,
  "VersionRootTourId": 1001,
  "ModeratedBy": 43,
  "UnpublishedAtUtc": "2026-11-18T11:00:00+00:00",
  "PublishedAtUtc": "2026-11-18T10:00:00+00:00"
}
```
*(When unpublishing a legacy `Published` Tour, `beforeData.Status` is `"Published"`.)*

---

## G. Local Testing Evidence (Executed at Commit `591863e6`)

> **Important:** All counts below are **local execution results** on Windows with local SQL Server (`localhost\MSSQLSERVER01`). They are **not** GitHub Actions CI results. Excluded and skipped tests are reported explicitly and never counted as passes.

### 1. Required Behavior & Verified Fixes (Passing Locally)

| Suite | Exact Command | Commit | Provider | Result | Local Log Ref |
|---|---|---|---|---|---|
| TM-106 Approve + Unpublish Unit Tests | `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter "FullyQualifiedName~Admin.Tours"` | `591863e6` | InMemory Double | **52 PASS, 0 FAIL, 0 SKIP** (26 Approve + 26 Unpublish) | `task-1745.log` |
| TM-106 Approve + Unpublish + TM-228 Boundary API Tests | `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter "FullyQualifiedName~ApproveTourEndpointTests\|FullyQualifiedName~UnpublishTourEndpointTests\|FullyQualifiedName~UserAccountBoundaryTests"` | `591863e6` | InMemory WebFactory | **54 PASS, 0 FAIL, 2 SKIP** *(2 SQL tests in `UserAccountBoundaryTests` skipped when SQL env unset)* | `task-1749.log` |
| Real SQL Server TM-106 Acceptance-Only (`Classification!=DefectReproduction`) | `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~ApproveTourSqlServerTests&Classification!=DefectReproduction" --logger "console;verbosity=detailed"` | `591863e6` | Real SQL Server (`localhost\MSSQLSERVER01`) | **8 PASS, 0 FAIL, 0 SKIP** *(4 Approve + 4 Gate 1 Unpublish)* | `task-1772.log` |
| Filtered Application Unit Regression *(excludes 34 `Golden` tests)* | `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-build --filter "FullyQualifiedName!~Golden"` | `591863e6` | InMemory Double | **5,044 PASS, 0 FAIL, 0 SKIP** *(34 `Golden` tests excluded by filter)* | `task-1756.log` |
| Infrastructure Unit Tests | `dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj` | `591863e6` | Unit / InMemory | **1,316 PASS, 0 FAIL, 3 SKIP** *(3 live Cloudinary credential smoke tests)* | `task-1788.log` |
| Related Tour & Booking Endpoint Tests | `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~GetTourDetailEndpointTests\|FullyQualifiedName~SearchToursEndpointTests\|FullyQualifiedName~TravelerTourBookingsEndpointTests\|FullyQualifiedName~CreateTourPackageEndpointTests"` | `591863e6` | InMemory WebFactory | **34 PASS, 0 FAIL, 2 SKIP** *(2 SQL tests in `SearchToursEndpointTests` skipped when SQL env unset)* | `task-1782.log` |

### 2. Cross-Task SQL Server Diagnostic Tests (`Classification=DefectReproduction` — NOT Acceptance Passes)
- **Command:** `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-build --filter "FullyQualifiedName~ApproveTourSqlServerTests&Classification=DefectReproduction" --logger "console;verbosity=normal"` (`task-1777.log`, commit `591863e6`)
- **Observed Results:**
  1. `Diagnostic_BookingAfterOperatorLocked_ReproducesCrossTaskBookingDefect`: **DEFECT REPRODUCED** (Quote `200 OK`, Create `201 Created` after Operator locked).
  2. `Diagnostic_ConcurrentApproveAndReject_ReproducesCrossTaskRaceDefect`: **DEFECT REPRODUCED (alternate failure mode — xUnit FAIL)**: Approve returned `200 OK` (`TourApproved=1`), unmodified TM-107 Reject crashed with unhandled `500 InternalServerError` (`TourRejected=0`), while the test's assertion checks the `200/200` conflicting-commit mode.

---

## H. Mandatory Open Defects, CI Blockers, and Dependencies

### BLOCKER A — TM-107 Approve/Reject Concurrency Race (`Severity: HIGH`)
> **UPDATE 2026-11-18 — RESOLVED in this PR by Owner-directed integration (commit `796b49e5`, see Section J).** The text below is preserved as the historical record of the defect as handed over; `RejectTourCommandHandler` now runs under the same serializable transaction, bounded deadlock retry and `MSG127` semantics as Approve.

- **Owning Task:** `TM-107` (Reject Tour Post) — *(at handoff)* **not** modified in TM-106.
- **Affected File:** `src/TripMate.Application/Features/Admin/Tours/Review/TourReviewCommandHandlers.cs` (`RejectTourCommandHandler`).
- **Problem:** `RejectTourCommandHandler` does not execute inside `ExecuteInSerializableTransactionAsync` and has no deadlock retry or `MSG127` persistence-failure handling matching `ApproveTourCommandHandler`.
- **Observed SQL Server Failure Modes:**
  1. `Approve 200 / Reject 200` with conflicting decision audit records (`TourApproved=1` and `TourRejected=1`) when Reject commits first.
  2. `Approve 200 / Reject 500 InternalServerError` when Approve acquires the Serializable lock first and Reject hits a deadlock/unhandled SQL exception.
- **Required Integrity Invariant:** Only **one** moderation decision may be committed for a `Pending` Tour, with a consistent final status and audit trail. Coordinate with TM-107 ownership before modifying `RejectTourCommandHandler`.

### BLOCKER B — Booking After Operator Account Lock (`Severity: HIGH`)
> **UPDATE 2026-11-18 — RESOLVED in this PR (commit `23edeb01`, see Section J).** Quote and Create now reuse `TourVisibility.PublicTour` (the Search/Detail definition), so a tour hidden by a locked/unapproved operator returns 404 before any mutation. The text below is the historical record of the defect as handed over.

- **Owning Task:** Traveler Booking feature — **not** modified in TM-106.
- **Affected Handlers:** `QuoteTourBookingQueryHandler` and `CreateTourBookingCommandHandler`.
- **Observed SQL Server Behavior:** After locking a Tour Operator's account:
  - Public Search: tour hidden.
  - Public Detail: `404 NotFound`.
  - Direct Booking Quote (`POST /api/v1/traveler/tour-bookings/quote`): `200 OK`.
  - Direct Booking Create (`POST /api/v1/traveler/tour-bookings`): `201 Created` (booking persisted and `ReservedCapacity` incremented).
- **Required Coordination:** Coordinate with Traveler Booking owner to enforce `OperatorProfile.ApprovalStatus == Approved` and `OperatorProfile.User.Status == Active` on quote/create paths.

### BLOCKER C — SQL Server Diagnostic Test in GitHub Actions CI (`Severity: CI BLOCKER`)
> **UPDATE 2026-11-18 — RESOLVED.** Both diagnostics were converted into acceptance tests once their underlying defects were fixed: `ConcurrentApproveAndReject_OnlyOneDecisionCommits_AndLoserReceivesConflict` (Blocker A, `796b49e5`) and `BookingAfterOperatorLocked_QuoteAndCreateAreRejected_AndNothingIsReserved` (Blocker B, `23edeb01`). No `DefectReproduction` test remains in the blocking `Category=SqlServer` filter and `.github/workflows/backend-tests.yml` was not modified. Original text preserved below.

- **CI Workflow & Filter:** `.github/workflows/backend-tests.yml` runs:
  `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --no-restore --filter "Category=SqlServer"`
  and fails the job if `failed > 0 || skipped > 0 || notExecuted > 0`.
- **Affected Test:** `TripMate.Api.IntegrationTests.Admin.ApproveTourSqlServerTests.Diagnostic_ConcurrentApproveAndReject_ReproducesCrossTaskRaceDefect` (tagged `[Trait("Category", "SqlServer")]` and `[Trait("Classification", "DefectReproduction")]`).
- **Why CI May Fail:** Because `Category=SqlServer` includes `Classification=DefectReproduction`, this diagnostic test executes in CI. When Approve wins the Serializable lock first, the observed combination is `Approve=200 OK / Reject=500 InternalServerError`, which fails the diagnostic test's `bothCommitted == true` assertion (originally written to prove the `200/200` lost-update race).
- **Owning Task for Underlying Fix:** `TM-107` (`RejectTourCommandHandler` concurrency).
- **Why This PR Is Draft:** The diagnostic test was intentionally preserved unmodified (not deleted, skipped, or weakened, and `.github/workflows/backend-tests.yml` was not altered) so the defect remains transparent for the receiving member and Project Owner to disposition properly.

### DEPENDENCY D — Operator Notification (`UC-60 PC-03`)
> **UPDATE 2026-11-18 — PARTIALLY IMPLEMENTED (Owner-directed, option 1).** In-app only: `ApproveTourCommandHandler` adds one `Notification` (`Channel=InApp`, `Type=TourApproved`, related `Tour`) for the owning operator in the same serializable transaction as the decision. No schema change (`Notifications.type` is free text), no outbox, no worker. **Email is still not delivered**: the existing delivery worker only handles `AccountUnlocked`. Notice wording is provisional until the official SRS message text is available. Original deferral text preserved below.

- **Status:** `PC03_DEFERRED_BY_OWNER_SCOPE`.
- Notification integration (in-app inbox and/or email outbox worker) was explicitly excluded from TM-106 by the Project Owner. The SRS postcondition `PC-03` remains outstanding until scheduled separately.

### DEPENDENCY E — TM-38 Frontend Integration (`CR-05`, `MSG144`, Screens 13 & 14)
- **Status:** `CR-05 = TM-38 FRONTEND_DEPENDENCY` (`NOT_VERIFIED` under TM-106 Backend scope).
- The confirmation modal, Approval Center moderation UI, checklist UI controls, and `MSG144` success display must be integrated and accepted with TM-38.

### SOURCE NOTE — SRS Binary Reconciliation
- Official authoritative SRS SHA-256: `a4ceabc0389bcffe8263254927ebe417abef6923d898f92c3003171add018298`.
- Previously inspected local workspace copy SHA-256: `c7636775d4208d8284905a04af5f79eacd4d2d05cb72d1022b12e7e830390be5`.
- Status remains `SOURCE_VERSION_NOT_VERIFIED` until the receiving member reconciles against the exact official SRS file (this hash mismatch does not invalidate the local code/test results).

---

## I. Next Member's Exact Continuation Checklist

### Step 1 — Review the PR Baseline
- [ ] Check out `feature/tm-106-approve-tour-post` and read this handoff (`docs/handoffs/TM-106_HANDOFF.md`).
- [ ] Reconcile against the official SRS binary (SHA-256 `a4ceabc0389bcffe8263254927ebe417abef6923d898f92c3003171add018298`, UC-60 §3.9.7.1).
- [ ] Inspect the complete PR diff against `develop` and verify the Approve and Unpublish API contracts.

### Step 2 — Review GitHub Actions CI
- [ ] Inspect all triggered GitHub Actions checks on the Draft PR (`.github/workflows/backend-tests.yml`: formatting check, `Category=SqlServer`, `Category=Redis`, remaining tests).
- [ ] Open any failed job logs and classify each failure accurately (TM-106 implementation vs. Blocker C diagnostic race vs. formatting/environment). Preserve all logs and evidence.

### Step 3 — Coordinate Cross-Task Blockers (Do Not Silently Fold Without Approval)
- [ ] Coordinate **Blocker A** (`RejectTourCommandHandler` Serializable transaction + deadlock retry + `MSG127`) with `TM-107` ownership.
- [ ] Coordinate **Blocker B** (Locked Operator eligibility check in `QuoteTourBookingQueryHandler` & `CreateTourBookingCommandHandler`) with the Traveler Booking owner.
- [ ] Obtain explicit governance disposition from the Project Owner on **Blocker C** (how `Classification=DefectReproduction` diagnostic tests should be transitioned once TM-107 and Booking blockers are fixed or tracked separately).

### Step 4 — Verify End-to-End Integration
- [ ] Re-run focused Approve & Unpublish unit and endpoint tests.
- [ ] Re-run full real SQL Server concurrency & rollback tests (including Approve/Reject mutual exclusion after TM-107 fix).
- [ ] Verify public Search/Detail and Traveler Booking behavior across Approve, Unpublish, and Operator Lock.
- [ ] Confirm Approve request/response contract compatibility with TM-38 Frontend.
- [ ] Confirm `TourApproved` and `TourUnpublished` audit trail consistency.

### Step 5 — Review Readiness Gate (Draft → Ready for Review)
Move the Draft PR to **Ready for Review** only after:
- [ ] Required CI checks pass cleanly (without weakening tests or manipulating CI workflows).
- [ ] Known test failures / cross-task blockers are resolved or explicitly dispositioned under approved governance.
- [ ] No unapproved out-of-scope changes exist.
- [ ] Dependencies (`PC-03`, `TM-38`) and SRS reconciliation remain accurately documented.

### Step 6 — Final Project Acceptance
- Merge, deployment, full UC-60 SRS acceptance, and Jira `Done` transition are separate later governance decisions after review approval and cross-task/frontend integration.


---

## J. Continuation Log — 2026-11-18 (receiving member)

> Historical sections A–I are preserved unchanged except for the dated `UPDATE` notes on Blockers A and C. Evidence below is labelled **CI** (GitHub Actions) or **LOCAL** (disposable SQL Server database `TripMate_Test_<guid>`, Windows, UTC+7).

### J.1 Commits added on top of handoff HEAD `61ef10dd`

| Commit | Scope | Summary |
|---|---|---|
| `fca8eb13` | formatting only | Removed the final LF from 7 new `.cs` files (`.editorconfig`: `insert_final_newline = false` for `*.cs`). 7 lines changed, no logic. |
| `8fd0b2cd` | test fixture only | `SeedPendingTourAsync` built the departure from an Unspecified-kind `DateTime`, which converts to `DateTimeOffset` using the **host** timezone. On a UTC runner the 08:00 departure became 15:00 Asia/Ho_Chi_Minh, so the 08:00/11:00 itinerary stops fell outside the departure window and `BookedTourItineraryTimeline.Plan` returned `stop_outside_departure_window` → `422 tour_booking.itinerary_not_bookable`. Departure is now pinned to `+07:00`. |
| `796b49e5` | production + tests | **Owner-directed TM-107 integration.** `RejectTourCommandHandler` now uses `ExecuteInSerializableTransactionAsync`, bounded deadlock retry (3 attempts), `ClearTrackedEntities`, and `MSG127` on genuine persistence failures, mirroring Approve. Error codes, messages, validation and audit payloads unchanged. |
| `eb791922` | docs | Handoff continuation log (this section's first version). |
| `23edeb01` | production + tests | **Booking eligibility.** `TourVisibility.IsPublicAsync` reuses the Search/Detail `PublicTour` predicate; `QuoteTourBookingQueryHandler` and `CreateTourBookingCommandHandler` drop their private status-only `IsPubliclyBookableTour`. A tour hidden by a locked/inactive/unapproved operator now returns `ScheduleNotFound` (404) before any mutation. Booking SqlServer diagnostic converted to an acceptance test (404/404, 0 bookings, `ReservedCapacity = 0`). |
| *PC-03 commit* (see `git log`) | production + tests | **PC-03 in-app notice (Owner option 1).** `ApproveTourCommandHandler` writes one `InApp` `TourApproved` `Notification` for the owning operator inside the approval transaction. Also fixes `TravelerTourBookingsEndpointTests` seed (needs an approved, active operator under BR-56). No schema change. |

### J.2 CI history (all on PR #188)

| Run | Head SHA | Result | Notes |
|---|---|---|---|
| [44304659055](https://github.com/FPTUCapstone/Capstone_BE/actions/runs/44304659055) | `61ef10dd` | FAIL | 7× `FINALNEWLINE` in "Verify formatting of changed C# files". SQL/Redis/remaining tests `SKIPPED` (never ran). |
| [44308874900](https://github.com/FPTUCapstone/Capstone_BE/actions/runs/44308874900) | `fca8eb13` | FAIL | Formatting PASS. SQL: 723 pass / **3 fail** / 0 skip (726). Failures: `UnpublishApprovedTour_...`, `Diagnostic_BookingAfterOperatorLocked_...`, `ApprovedTour_PublicVisibilityAndBookingFlow_...` — all `422 itinerary_not_bookable` (fixture timezone defect above). The Approve/Reject diagnostic **passed** in this run. |
| [44318366637](https://github.com/FPTUCapstone/Capstone_BE/actions/runs/44318366637) | `8fd0b2cd` | **PASS** | Formatting PASS; SQL 726/0/0; Redis 28/0/0; remaining: CandidatePoolReplay 13, Infrastructure 1316 (3 skip), Application 5397, Api 1220 (5 skip), 0 failures. |
| *(see PR checks for `796b49e5` and later)* | `796b49e5`+ | PENDING at time of writing | Do not treat as passing until the run completes. |

### J.3 TM-107 integration evidence (LOCAL, `796b49e5`)

- **Jira:** TM-107 `[UC-61] Reject Tour Post` is **Done** (assignee Phan Quoc Khanh); its handler already existed on `develop`, so integration = bringing it under the shared invariant, not merging a branch.
- **Unit (Application, `FullyQualifiedName~Features.Admin.Tours.Review`):** 67 PASS (52 Approve/Unpublish + 15 new Reject). 6 of the 15 were red before the fix (transaction count, deadlock retry, retry exhaustion, MSG127, no-retry on storage failure, programming error not disguised).
- **Unit regression (Application, excl. `Golden`):** 5,059 PASS (5,044 baseline + 15).
- **API regression, non-SQL (`Admin` + `Tour`, excl. SqlServer/Redis):** 238 PASS, 17 SKIP, 0 FAIL.
- **Real SQL Server, `ApproveTourSqlServerTests`:** 10 PASS, 0 FAIL, 0 SKIP; no `TripMate_Test_*` databases left behind.
- **Red-green of the race test:** against the **old** Reject handler → `Approve 200 / Reject 500` (handoff "Mode B" reproduced, test FAIL). Against the fix → `Approve 200 / Reject 409`, `TourApproved=1`, `TourRejected=0`, final status `Approved`; 5/5 repeated runs identical.
- **Not exercised:** the "Reject wins" ordering (the test asserts the symmetric branch but the barrier produced Approve-wins in every observed run). Final CI result for `796b49e5` is the authoritative check.

### J.4 State of each handoff item

| Item | State |
|---|---|
| Format / CI blockers from handoff | **Resolved** (run 44318366637 green at `8fd0b2cd`). |
| Blocker A — TM-107 race | **Resolved** by `796b49e5` (pending CI confirmation on that SHA). |
| Blocker B — Booking after Operator Locked | **Resolved** by `23edeb01` (CI confirmation pending). Local red→green: Quote 200 / Create 201 before; 404 / 404, 0 bookings, `ReservedCapacity = 0` after. The Booking diagnostic was converted to an acceptance test, so **no `DefectReproduction` test remains in the blocking `Category=SqlServer` filter** and no CI policy change was needed. Unit: 8 new Quote/Create theory cases (red before the fix). Touches Booking handlers owned by another task by Owner direction; reviewers from that area should be added. |
| PC-03 notification | **Partial.** In-app `TourApproved` notice implemented (atomic with the approval; no schema change). **Email delivery not implemented** (worker only handles `AccountUnlocked`). Notice text provisional until the official SRS message is available. |
| CR-05 / TM-38 Frontend | `FRONTEND_DEPENDENCY` — not implemented or verified. |
| SRS source identity | **`SOURCE_NOT_AVAILABLE`** — the official binary (SHA-256 `a4ceabc0…8298`) was **not found**. Two files named `Report3_Software-Requirement-Specification.docx` exist on the receiving member's machine (`D:\Capstone_Project_Env\` and `D:\HoangTrinh05\`); they are byte-identical to each other (SHA-256 `424cc3ebac39328d1f93d4616694ad133f9731da1dc96bb9c9b29c5619fc7ada`) but match **neither** the official hash nor the previously inspected copy (`c7636775…`). Source identity is therefore **not** verified; the official file must be supplied by the Project Owner. Report 3 V2 was not used as a substitute. |
| Draft → Ready for Review | **Not performed.** Requires Owner approval. |
| Pre-existing `dotnet format TripMate.slnx --verify-no-changes` (whole solution) | Fails on 16 `WHITESPACE` files already on `develop`, none in this PR. CI only checks changed files; intentionally not touched. |
