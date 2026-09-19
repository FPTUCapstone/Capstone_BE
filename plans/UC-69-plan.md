# Implementation Plan — UC-69 Backend: View Audit Log Details (Schema Aligned)

## Overview
This plan implements **UC-69: View Audit Log Details** against the current `dbo.AuditLogs` schema. SOURCE-02 is resolved per the Decision Record in `specs/UC-69-spec.md` (2026-09-18); the proposed MSG132 still awaits SRS 5.3 catalog approval.

---

## Technical Directives

1. **Database Schema Compliance**: Map strictly the actual columns of `dbo.AuditLogs`: `id`, `actionType`, `actorUserId`, `affectedEntity`, `affectedEntityId`, `beforeData`, `afterData`, `ipAddress`, `createdAtUtc`, `createdAtLocal` plus joined `actorEmail`, `actorFullName`, `actorRole`. No fake/mocked root DTO fields.
2. **BR-115 (Role Authorization)**: Only accounts with `UserRole.Administrator` role may read audit log entries. Returns HTTP 403 Forbidden with `MSG126` ("You do not have permission to access this function." — locked SRS 5.3 content).
3. **BR-130 (Immutability)**: Stored content is immutable; apply BR-03 masking only to the read response.
4. **Entry Not Found**: Returns HTTP 404 Not Found with `MSG132` (proposed — pending SRS 5.3 approval; locked `MSG129` is a success toast and must not be reused as a 404 label) when the requested ID does not exist.
5. **CR-07 (Timezone Standard)**: Formats `createdAtLocal` in Vietnam Time (`Asia/Ho_Chi_Minh`, UTC+7) as `dd/MM/yyyy HH:mm:ss`.

---

## Proposed Component Changes

### Component 1: Error Code & Controller Mapping
- [MODIFY] [`AuditLogErrorCodes.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/Common/AuditLogErrorCodes.cs): `NotFound = "admin.audit_log_not_found"`.
- [MODIFY] [`ApiControllerBase.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Api/Common/ApiControllerBase.cs): Map `AuditLogErrorCodes.NotFound` to `StatusCodes.Status404NotFound` (HTTP 404) with `MSG132` (proposed).

### Component 2: Application Layer (DTO, Query, Handler)
- [NEW] [`AuditLogDetailDto.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetDetail/AuditLogDetailDto.cs): DTO matching DB schema.
- [NEW] [`GetAuditLogDetailQuery.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetDetail/GetAuditLogDetailQuery.cs): MediatR record query.
- [NEW] [`GetAuditLogDetailQueryHandler.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/AuditLogs/GetDetail/GetAuditLogDetailQueryHandler.cs):
  - Authorization check (`BR-115` / `Administrator`).
  - DB lookup with `ActorUser` included. If null, return `Result.Failure` with `MSG132` (proposed — "System audit log entry not found.").
  - Format `createdAtLocal` (`CR-07` GMT+7 `dd/MM/yyyy HH:mm:ss`).
  - Project system actor null safety (`actorUserId === null` -> `actorFullName = "System"`).

### Component 3: API Controller Endpoint
- [MODIFY] [`AdminAuditLogsController.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Api/Controllers/V1/AdminAuditLogsController.cs):
  - Endpoint `[HttpGet("{id:long}")] GetAuditLogDetail` returning direct `Ok(result.Value)`.

### Component 4: Unit Tests
- [NEW] [`GetAuditLogDetailQueryHandlerTests.cs`](file:///d:/study/Project-Capstone/Capstone_BE/tests/TripMate.Application.UnitTests/Features/Admin/AuditLogs/GetDetail/GetAuditLogDetailQueryHandlerTests.cs):
  - Unit tests covering 200 OK, 403 Forbidden, 404 Not Found (`MSG132` proposed), and System actor.

---

## Verification Plan

### Automated Tests
- Run unit tests: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj`

## PR #17 remediation (authorized by the user's review-fix request)

1. Record the current baseline without discarding uncommitted fixes. Add failing domain/read-payload security tests, then close the empty constructor and implement response-only redaction in GetDetail. Verify focused application tests.
2. Add JWT HTTP tests for UC-69 401/403/200/404, System actor, masked response and unchanged storage. Use TripMateApiFactory and the production token service. Verify focused integration tests.
3. Remove the unrelated migration newline and UC-51 documents from this PR diff. Preserve shared operator error mappings used by UC-49/50 and document that dependency. Do not edit UC-68 search/pagination behavior.
4. Synchronize FE UC-69 message documentation, remove the fictitious Success badge requirement, and prepare a local PR description/re-review checklist. Keep SOURCE-02 and MSG132 approval pending. Do not publish, commit or push.
5. Run solution build/tests; obtain independent spec/security and code-quality review. Record passes, skips and remaining decisions honestly.

---

## Result/Reason implementation plan (approved 2026-09-18)

> The developer approved this plan and requested implementation on 2026-09-18 (conversation decision record). Merged into this plan from the former `UC-69-result-reason-plan.md`. Constraints: preserve the previous remediation; keep the unrelated UC-57 work out of this task; no commit, push or remote PR mutation until the developer asks. Status as of 2026-09-19: steps 1–4 implemented and verified (build/tests green in both repositories); step 5 partially open — the SQL migration still needs to be applied and verified on a real test database (D05/C19), and the PR description/re-review remains pending.

1. Domain/schema/read API: add enum, two nullable columns, repeatable migration, explicit production factory, map DTOs. Domain/HTTP tests first.
2. Failure recording: request-specific pipeline behavior after validation for implemented POI creation/operator approval only; isolated scoped persistence and structured fallback. Unit tests for every recording/exclusion decision and integration tests for pipeline/storage isolation.
3. Frontend: result column and detail, explicit reason field with null states, failure-context label. Component tests then lint/typecheck/build.
4. Logging separation: safe 401/403/400/422 request-status events excluding bodies/tokens/query strings; never insert these automatically in AuditLogs.
5. Update SRS amendment and feature documents. Build/test both repositories, inspect migration and rollback coverage, record environment limitations. SQL apply only against an explicitly identified safe target; do not guess a database from secrets.
