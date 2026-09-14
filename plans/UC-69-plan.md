# Implementation Plan — UC-69 Backend: View Audit Log Details (Schema Aligned)

## Overview
This implementation plan outlines the Clean Architecture Backend implementation for **UC-69: View Audit Log Details** in `Capstone_BE` (Screen #34 System Audit Log Entry Detail View), aligned 100% with the actual `dbo.AuditLogs` database schema (`tripmate_schema_v7.sql`).

---

## Technical Directives

1. **Database Schema Compliance**: Map strictly the actual columns of `dbo.AuditLogs`: `id`, `actionType`, `actorUserId`, `affectedEntity`, `affectedEntityId`, `beforeData`, `afterData`, `ipAddress`, `createdAtUtc`, `createdAtLocal` plus joined `actorEmail`, `actorFullName`, `actorRole`. No fake/mocked root DTO fields.
2. **BR-115 (Role Authorization)**: Only accounts with `UserRole.Administrator` role may read audit log entries. Returns HTTP 403 Forbidden with `MSG126` ("Access denied. Administrator role required.").
3. **BR-130 (Immutability)**: Audit log entry content is immutable and presented exactly as recorded.
4. **MSG129 (Entry Not Found)**: Returns HTTP 404 Not Found with `MSG129` ("System audit log entry not found.") when the requested ID does not exist.
5. **CR-07 (Timezone Standard)**: Formats `createdAtLocal` in Vietnam Time (`Asia/Ho_Chi_Minh`, UTC+7) as `dd/MM/yyyy HH:mm:ss`.

---

## Proposed Component Changes

### Component 1: Error Code & Controller Mapping
- [MODIFY] [`AuditLogErrorCodes.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/Common/AuditLogErrorCodes.cs): `NotFound = "admin.audit_log_not_found"`.
- [MODIFY] [`ApiControllerBase.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Api/Common/ApiControllerBase.cs): Map `AuditLogErrorCodes.NotFound` to `StatusCodes.Status404NotFound` (HTTP 404) with `MSG129`.

### Component 2: Application Layer (DTO, Query, Handler)
- [NEW] [`AuditLogDetailDto.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetDetail/AuditLogDetailDto.cs): DTO matching DB schema.
- [NEW] [`GetAuditLogDetailQuery.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetDetail/GetAuditLogDetailQuery.cs): MediatR record query.
- [NEW] [`GetAuditLogDetailQueryHandler.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetDetail/GetAuditLogDetailQueryHandler.cs):
  - Authorization check (`BR-115` / `Administrator`).
  - DB lookup with `ActorUser` included. If null, return `Result.Failure` with `MSG129` ("System audit log entry not found.").
  - Format `createdAtLocal` (`CR-07` GMT+7 `dd/MM/yyyy HH:mm:ss`).
  - Project system actor null safety (`actorUserId === null` -> `actorFullName = "System"`).

### Component 3: API Controller Endpoint
- [MODIFY] [`AdminAuditLogsController.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Api/Controllers/V1/AdminAuditLogsController.cs):
  - Endpoint `[HttpGet("{id:long}")] GetAuditLogDetail` returning direct `Ok(result.Value)`.

### Component 4: Unit Tests
- [NEW] [`GetAuditLogDetailQueryHandlerTests.cs`](file:///d:/study/Project-Capstone/Capstone_BE/tests/TripMate.Application.UnitTests/Features/Admin/AuditLogs/GetDetail/GetAuditLogDetailQueryHandlerTests.cs):
  - Unit tests covering 200 OK, 403 Forbidden, 404 Not Found (`MSG129`), and System actor.

---

## Verification Plan

### Automated Tests
- Run unit tests: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj`
