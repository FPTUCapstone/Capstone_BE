# Implementation Plan — UC-69 Backend: View Audit Log Details

## Overview
This implementation plan outlines the C# / .NET Core backend implementation for **UC-69: View Audit Log Details** in `Capstone_BE` (Screen #34 System Audit Log Details View).

The feature enables Administrators to view complete details of a specific system audit log entry (including before/after data JSON payloads) via `GET /api/v1/admin/audit-logs/{id}`.

---

## Technical Context & Decisions

1. **Architecture & Pattern**: Clean Architecture with MediatR CQRS pattern:
   - `GetAuditLogDetailQuery(long Id)`: Request query record.
   - `AuditLogDetailDto`: Data Transfer Object containing full log payload (`beforeData`, `afterData`, `createdAtLocal`, etc.).
   - `GetAuditLogDetailQueryHandler`: Query handler handling authorization (BR-115), database lookup, null safety for system actors, and CR-07 timezone formatting.
2. **Error Codes & Mapping Consistency**:
   - `AuditLogErrorCodes.NotFound` (`"admin.audit_log_not_found"`) added to `AuditLogErrorCodes.cs`.
   - `ApiControllerBase.cs`: Mapped `AuditLogErrorCodes.NotFound` -> `StatusCodes.Status404NotFound` (404) with `MSG129` ("System audit log entry not found.").
3. **Controller Envelope Consistency**:
   - `AdminAuditLogsController.cs`: Endpoint returns `result.IsSuccess ? Ok(result.Value) : HandleFailure(result);` directly returning `Ok(result.Value)` when successful (consistent with UC-68 `GetAuditLogs`).

---

## Proposed Component Changes

### Component 1: Error Codes & Common Mapping
- [MODIFY] [`AuditLogErrorCodes.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/Common/AuditLogErrorCodes.cs): Added `public const string NotFound = "admin.audit_log_not_found";`.
- [MODIFY] [`ApiControllerBase.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Api/Common/ApiControllerBase.cs): Mapped `AuditLogErrorCodes.NotFound` to `StatusCodes.Status404NotFound`.

### Component 2: Application Features
- [NEW] [`AuditLogDetailDto.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetDetail/AuditLogDetailDto.cs): DTO for full audit log detail view.
- [NEW] [`GetAuditLogDetailQuery.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetDetail/GetAuditLogDetailQuery.cs): MediatR query record.
- [NEW] [`GetAuditLogDetailQueryHandler.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetDetail/GetAuditLogDetailQueryHandler.cs): Query handler logic with authorization, 404 lookup, and null-safe projections.

### Component 3: API Controller Endpoint
- [MODIFY] [`AdminAuditLogsController.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Api/Controllers/V1/AdminAuditLogsController.cs): Add `[HttpGet("{id:long}")] GetAuditLogDetail(long id)` returning `result.IsSuccess ? Ok(result.Value) : HandleFailure(result);`.

### Component 4: Unit Tests
- [NEW] [`GetAuditLogDetailQueryHandlerTests.cs`](file:///d:/study/Project-Capstone/Capstone_BE/tests/TripMate.Application.UnitTests/Features/Admin/AuditLogs/GetDetail/GetAuditLogDetailQueryHandlerTests.cs): Unit tests covering Success (200), Forbidden (403), Not Found (404), and System Actor (null actor).

---

## Verification Plan

### Automated Tests
- Run unit test suite: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj`

### Manual Verification
- Test endpoint via PowerShell / curl: `GET http://localhost:5021/api/v1/admin/audit-logs/{id}` with Admin JWT token.
