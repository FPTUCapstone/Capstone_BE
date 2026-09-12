# Implementation Plan — UC-68: View Audit Logs

## Overview
This implementation plan outlines the backend implementation for **UC-68: View Audit Logs** in `TripMate.Application` and `TripMate.Api` following Clean Architecture, Database-First conventions, SRS §3.9.12.1 (Screen #33 System Audit Logs List), and the `Result<T>` pattern.

UC-68 is strictly focused on searching, filtering, paginating, and listing system audit records for Administrators.

---

## Technical Context & Scope Adjustments

1. **UC-68 Scope Strictness:**
   - Focuses strictly on Screen #33: System Audit Logs List (`GET /api/v1/admin/audit-logs`).
   - Detail inspection (`GET /{id}`) is owned by **UC-69** (Screen #34).
   - CSV / Report export is owned by **UC-67**.
2. **Database-First Mapping & Null-Safe Left Join:**
   - Uses existing `dbo.AuditLogs` entity (`AuditLog.cs`) and `AuditLogConfiguration.cs`.
   - `.Include(a => a.ActorUser)` performs a `LEFT JOIN` with `dbo.Users`.
   - Handles `actor_user_id = null` (system-triggered background tasks) safely by populating `ActorFullName` as `"System"`.
3. **Timezone Formatting (CR-07):**
   - Operates in UTC (`DateTimeOffset`) for queries.
   - DTO outputs `createdAtUtc` and `createdAtLocal` (`dd/MM/yyyy HH:mm:ss` in `Asia/Ho_Chi_Minh` UTC+7).
4. **Read-Only Tracking Discipline:**
   - Queries use `.AsNoTracking()`.
5. **Ordering & Pagination:**
   - Strict ordering: `OrderByDescending(a => a.CreatedAtUtc)`.
   - Uses reusable `PaginatedList<T>` model.
6. **Numeric Keyword Search (`AffectedEntityId`):**
   - In `GetAuditLogsQueryHandler`, use `long.TryParse(keyword, out long entityId)` before building the `Where` clause to ensure EF Core translates string/number comparisons cleanly across all DB providers and InMemory test contexts.
7. **Controller Error Mapping & Message Alignment:**
   - `AuditLogErrorCodes.Forbidden` maps to `403 Forbidden` (`MSG126`: `"Access denied. Administrator role required."`).
   - `AuditLogErrorCodes.InvalidDateRange` maps to `422 Unprocessable Entity` (`MSG29`: `"The submitted Event Date range is logically invalid."`).

---

## Proposed Component Changes

### Component 1: Application Common Models
- [NEW] [`PaginatedList.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Common/Models/PaginatedList.cs): Generic pagination container (`Items`, `PageNumber`, `PageSize`, `TotalCount`, `TotalPages`, `HasPreviousPage`, `HasNextPage`).

### Component 2: Application Layer (CQRS Features)
- [NEW] [`AuditLogErrorCodes.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/Common/AuditLogErrorCodes.cs): `Forbidden`, `InvalidDateRange`.
- [NEW] [`GetAuditLogsQuery.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetList/GetAuditLogsQuery.cs): Search parameters.
- [NEW] [`GetAuditLogsQueryValidator.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetList/GetAuditLogsQueryValidator.cs): Validates page parameters & date range (`MSG29`).
- [NEW] [`GetAuditLogsQueryHandler.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetList/GetAuditLogsQueryHandler.cs): Core query logic with `LEFT JOIN` and null actor safety.
- [NEW] [`AuditLogSummaryDto.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetList/AuditLogSummaryDto.cs): Output record payload with UTC and CR-07 local timestamps.

### Component 3: Presentation Layer (API Controllers)
- [MODIFY] [`ApiControllerBase.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Api/Common/ApiControllerBase.cs): Map `AuditLogErrorCodes`.
- [NEW] [`AdminAuditLogsController.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Api/Controllers/V1/AdminAuditLogsController.cs): `GET /api/v1/admin/audit-logs`.

### Component 4: Test Suite
- [NEW] [`GetAuditLogsQueryHandlerTests.cs`](file:///d:/study/Project-Capstone/Capstone_BE/tests/TripMate.Application.UnitTests/Features/Admin/AuditLogs/GetList/GetAuditLogsQueryHandlerTests.cs): Unit tests for list filtering, pagination, ordering, system actor null safety, and date range validation.

---

## Sequential Execution Tasks

### Task 1: Common Models & Error Codes
- Create `PaginatedList.cs` and `AuditLogErrorCodes.cs`.

### Task 2: CQRS List Feature (Query, DTO, Validator, Handler)
- Create `GetAuditLogsQuery.cs`, `AuditLogSummaryDto.cs`, `GetAuditLogsQueryValidator.cs`, `GetAuditLogsQueryHandler.cs`.

### Task 3: Controller & Failure Mapping
- Update `ApiControllerBase.cs` and create `AdminAuditLogsController.cs`.

### Task 4: Unit Test Suite & Solution Verification
- Create `GetAuditLogsQueryHandlerTests.cs` and run `dotnet test`.

---

## Verification Plan

### Automated Tests
- `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter "FullyQualifiedName~AuditLogs"`
- Full test suite: `dotnet test`
