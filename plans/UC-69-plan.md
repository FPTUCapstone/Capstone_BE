# Implementation Plan — UC-69 Backend: View Audit Log Details (SRS Aligned)

## Overview
This implementation plan outlines the Clean Architecture Backend implementation for **UC-69: View Audit Log Details** in `Capstone_BE` (Screen #34 System Audit Log Entry Detail View), aligned 100% with SRS specifications.

The feature provides a GET endpoint returning complete audit payload including header metadata (`result`), actor info (`sourceAddress`, `clientPlatform`), target info (`affectedModule`, `affectedEntity`, `affectedEntityId`), field change payloads (`beforeData`, `afterData`), and context info (`reason`).

---

## Business Rules & Validation Requirements

1. **BR-115 (Role Authorization)**: Only accounts with `UserRole.Administrator` role may read audit log entries. Returns HTTP 403 Forbidden with `MSG126` ("Access denied. Administrator role required.").
2. **BR-130 (Immutability)**: Audit log entry content is immutable and presented exactly as recorded.
3. **BR-03 (Sensitive Field Masking)**: Sensitive credentials (passwords, tokens, payment cards) must be masked.
4. **BR-119 (Supplied Reason Context)**: Included in the response payload when a reason was supplied for the action.
5. **MSG150 (Entry Not Found)**: Returns HTTP 404 Not Found with `MSG150` ("The selected audit log entry does not exist.") when the requested ID does not exist.
6. **CR-07 (Timezone Standard)**: Formats `createdAtLocal` in Vietnam Time (`Asia/Ho_Chi_Minh`, UTC+7) as `dd/MM/yyyy HH:mm:ss`.

---

## Proposed Component Changes

### Component 1: Error Code & Controller Mapping
- [MODIFY] [`AuditLogErrorCodes.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/Common/AuditLogErrorCodes.cs): Ensure `NotFound = "admin.audit_log_not_found"` is present.
- [MODIFY] [`ApiControllerBase.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Api/Common/ApiControllerBase.cs): Map `AuditLogErrorCodes.NotFound` to `StatusCodes.Status404NotFound` (HTTP 404) with `MSG150`.

### Component 2: Application Layer (DTO, Query, Handler)
- [MODIFY] [`AuditLogDetailDto.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetDetail/AuditLogDetailDto.cs): Add fields: `result`, `sourceAddress`, `clientPlatform`, `affectedModule`, `reason`.
- [MODIFY] [`GetAuditLogDetailQueryHandler.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetDetail/GetAuditLogDetailQueryHandler.cs):
  - Authorization check (`BR-115` / `Administrator`).
  - DB lookup with `ActorUser` included. If null, return `Result.Failure` with `MSG150` ("The selected audit log entry does not exist.").
  - Format `createdAtLocal` (`CR-07` GMT+7 `dd/MM/yyyy HH:mm:ss`).
  - Project system actor null safety (`actorUserId === null` -> `actorFullName = "System"`).

### Component 3: API Controller Endpoint
- [MODIFY] [`AdminAuditLogsController.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Api/Controllers/V1/AdminAuditLogsController.cs):
  - Endpoint `[HttpGet("{id:long}")] GetAuditLogDetail` returning direct `Ok(result.Value)` envelope consistency.

### Component 4: Unit Tests
- [MODIFY] [`GetAuditLogDetailQueryHandlerTests.cs`](file:///d:/study/Project-Capstone/Capstone_BE/tests/TripMate.Application.UnitTests/Features/Admin/AuditLogs/GetDetail/GetAuditLogDetailQueryHandlerTests.cs):
  - Update unit tests covering 200 OK, 403 Forbidden, 404 Not Found (`MSG150`), and System actor.

---

## Verification Plan

### Automated Tests
- Run unit tests: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj`
