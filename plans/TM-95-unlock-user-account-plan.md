# TM-95 / UC-49 Unlock User Account Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Safely restore a locked non-administrative account to its recorded pre-lock state with replay-safe administrative auditing and forced re-authentication.

**Architecture:** The Domain owns valid lock/unlock state transitions and an idempotency operation entity. The Application vertical slice validates and serializes the command, while Infrastructure maps database-first additions and provides transactional persistence. The API only authorizes, binds the request/header, and maps `Result` failures.

**Tech Stack:** .NET 10, ASP.NET Core, MediatR, FluentValidation, EF Core SQL Server, xUnit, FluentAssertions, SQL Server Docker integration tests.

**Spec:** `specs/TM-95-spec.md`

## Global Constraints

- Database-first only: update `database/tripmate_schema_v7.sql` and add an idempotent SQL migration; never generate EF migrations.
- Preserve Clean Architecture and `Result` error flow; controller contains no business logic.
- Use the existing `Administrator` role only; reject an `Administrator` target until the formal role split exists.
- Restore only the recorded `StatusBeforeLock`; never infer `Active` for a legacy/incomplete lock.
- Revoke all active target refresh tokens in the same transaction; do not issue a replacement token.
- All UTC database timestamps use the project `AsUtcDateTime2()` mapping.
- No UI in this task; UC-47 consumes the API from its future User Detail screen.

## Review Focus

- Concurrent same-key calls must yield one state update/audit record and replay one response.
- A reused idempotency key with a different reason or target must never perform a second unlock.
- Missing or malformed historical lock metadata must not turn a locked account into `Active`.
- An audit or idempotency write failure must roll back user status and refresh-token revocation.
- Administrator/self targets must be rejected before a state-changing save.

---

### Task 1: Lock-state and persistence model

**Files:**
- Modify: `src/TripMate.Domain/Entities/User.cs`
- Create: `src/TripMate.Domain/Entities/UserUnlockOperation.cs`
- Create: `src/TripMate.Infrastructure/Persistence/Configurations/UserUnlockOperationConfiguration.cs`
- Modify: `src/TripMate.Infrastructure/Persistence/Configurations/UserConfiguration.cs`
- Modify: `src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs`
- Modify: `src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs`
- Modify: `src/TripMate.Domain/Common/AuditActionTypes.cs`
- Modify: `src/TripMate.Domain/Common/AuditEntityTypes.cs`
- Test: `tests/TripMate.Infrastructure.UnitTests/Persistence/UserUnlockOperationPersistenceModelTests.cs`
- Test: `tests/TripMate.Application.UnitTests/Features/Admin/Users/Unlock/UserUnlockDomainTests.cs`

**Interfaces:**
- Produces `User.RecordLock(...)`, `User.TryRestoreFromLock(...)`, and `UserUnlockOperation.Create(...)` for the command handler.
- Produces `DbSet<UserUnlockOperation> UserUnlockOperations` and a bulk revoke-active-tokens-by-user operation for the handler.

- [ ] **Step 1: Write failing domain and persistence-model tests**

Assert that a complete lock restores only its saved status and clears metadata; incomplete metadata or `StatusBeforeLock == Locked` is rejected. Assert EF maps nullable lock columns, `admin.UserUnlockOperations`, UTC columns, FKs, and unique `(administrator_user_id, idempotency_key)`.

- [ ] **Step 2: Run focused tests to verify RED**

Run: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --filter "FullyQualifiedName~UserUnlockDomainTests"`

Expected: FAIL because the lock transition and operation entity do not exist.

- [ ] **Step 3: Implement the minimal Domain and EF contract**

Add encapsulated lock metadata and restoration methods to `User`; introduce a focused operation entity carrying administrator ID, normalized key, request hash, target ID, response state, and UTC completion timestamp. Map them and expose only the DbContext members needed by the handler.

- [ ] **Step 4: Run focused model/domain tests to verify GREEN**

Run: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --filter "FullyQualifiedName~UserUnlockDomainTests"; dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --filter "FullyQualifiedName~UserUnlockOperationPersistenceModelTests"`

Expected: PASS.

- [ ] **Step 5: Commit persistence model work**

```powershell
git add src tests
git commit -m "feat(user): add unlock persistence model"
```

### Task 2: Unlock command vertical slice

**Files:**
- Create: `src/TripMate.Application/Features/Admin/Users/Unlock/UnlockUserAccountCommand.cs`
- Create: `src/TripMate.Application/Features/Admin/Users/Unlock/UnlockUserAccountCommandValidator.cs`
- Create: `src/TripMate.Application/Features/Admin/Users/Unlock/UnlockUserAccountCommandHandler.cs`
- Create: `src/TripMate.Application/Features/Admin/Users/Unlock/UnlockUserAccountResponse.cs`
- Create: `src/TripMate.Application/Features/Admin/Users/Common/UserAdministrationErrorCodes.cs`
- Test: `tests/TripMate.Application.UnitTests/Features/Admin/Users/Unlock/UnlockUserAccountCommandHandlerTests.cs`

**Interfaces:**
- Consumes Task 1 `User` lock restoration, `UserUnlockOperation`, DbSets, token revocation operation, audit constants, and transactions.
- Produces `IRequest<Result<UnlockUserAccountResponse>>` for the controller.

- [ ] **Step 1: Write failing handler tests**

Cover a valid unlock with audit/token revocation, protected/self administrator target, absent target, non-locked target, incomplete legacy metadata, same-key replay, and same-key payload mismatch.

- [ ] **Step 2: Run focused handler tests to verify RED**

Run: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --filter "FullyQualifiedName~UnlockUserAccountCommandHandlerTests"`

Expected: FAIL because the command handler does not exist.

- [ ] **Step 3: Implement the command, validator, errors, response, and handler**

Canonicalize trimmed reason and idempotency key before hashing. Within a serializable transaction, replay matching completed operation; otherwise validate the target, restore it, revoke active refresh tokens, record successful audit, persist operation, and save once. Return `Result` failures for all expected cases.

- [ ] **Step 4: Run focused handler tests to verify GREEN**

Run: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --filter "FullyQualifiedName~UnlockUserAccountCommandHandlerTests"`

Expected: PASS.

- [ ] **Step 5: Commit vertical slice**

```powershell
git add src tests
git commit -m "feat(user): unlock locked accounts safely"
```

### Task 3: HTTP API and failure mapping

**Files:**
- Create: `src/TripMate.Api/Controllers/V1/AdminUsersController.cs`
- Modify: `src/TripMate.Api/Common/ApiControllerBase.cs`
- Test: `tests/TripMate.Api.IntegrationTests/Admin/Users/UnlockUserAccountEndpointTests.cs`

**Interfaces:**
- Consumes Task 2 command and `UnlockUserAccountResponse`.
- Produces `POST /api/v1/admin/users/{userId}/unlock`.

- [ ] **Step 1: Write failing endpoint tests**

Assert anonymous and non-administrator callers are denied; a valid Administrator gets `200`; invalid/missing header/body gets `400`; and domain not-found/conflict/protected failures map to documented `ProblemDetails` status/error codes.

- [ ] **Step 2: Run focused endpoint tests to verify RED**

Run: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~UnlockUserAccountEndpointTests"`

Expected: FAIL because the route does not exist.

- [ ] **Step 3: Implement the thin controller and centralized error mapping**

Bind the required header/body, extract the authenticated administrator ID using existing controller conventions, dispatch MediatR, and use `HandleFailure` for expected errors. Do not access DbContext in the controller.

- [ ] **Step 4: Run focused endpoint tests to verify GREEN**

Run: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~UnlockUserAccountEndpointTests"`

Expected: PASS.

- [ ] **Step 5: Commit API contract**

```powershell
git add src tests
git commit -m "feat(api): expose account unlock endpoint"
```

### Task 4: Database-first migration and real SQL Server proof

**Files:**
- Modify: `database/tripmate_schema_v7.sql`
- Create: `database/migrations/20261007_add_user_unlock_operations.sql`
- Test: `tests/TripMate.Api.IntegrationTests/Admin/Users/UnlockUserAccountSqlServerTests.cs`
- Test: `tests/TripMate.Api.IntegrationTests/Admin/Users/UserUnlockMigrationSqlServerTests.cs`

**Interfaces:**
- Consumes Tasks 1–3 persistence and endpoint contracts.
- Produces an idempotent upgrade path and fresh-schema parity proof.

- [ ] **Step 1: Write failing SQL Server tests**

Create a pre-UC49 schema fixture, apply the migration twice, verify canonical column/default/check/FK/index inventory, then exercise endpoint replay, payload mismatch, concurrent same-key requests, token revocation, and audit/operation rollback.

- [ ] **Step 2: Run focused SQL Server tests to verify RED**

Run: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~(UnlockUserAccountSqlServerTests|UserUnlockMigrationSqlServerTests)"`

Expected: FAIL because the SQL migration and schema objects do not exist.

- [ ] **Step 3: Implement canonical schema and idempotent SQL migration**

Add nullable legacy-safe lock metadata to `dbo.Users`, explicit check/FK constraints, the `admin.UserUnlockOperations` table with request-hash and replay response fields, and the unique index. Make absent/correct/wrong-shape behavior intentional and transaction-safe.

- [ ] **Step 4: Run focused SQL Server tests to verify GREEN**

Run: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~(UnlockUserAccountSqlServerTests|UserUnlockMigrationSqlServerTests)"`

Expected: PASS with no skipped SQL tests.

- [ ] **Step 5: Commit database work**

```powershell
git add database tests
git commit -m "feat(db): persist safe account unlock operations"
```

### Task 5: Full verification and independent review

**Files:**
- Verify only; no planned production files.

- [ ] **Step 1: Run repository quality gates**

Run: `dotnet format TripMate.slnx --verify-no-changes --no-restore; dotnet build TripMate.slnx --no-restore -c Release; dotnet test TripMate.slnx --no-restore -c Release; git diff --check`

Expected: all commands pass; record any external-environment failure rather than hiding it.

- [ ] **Step 2: Conduct a separate whole-branch review**

Review the branch against `specs/TM-95-spec.md`, especially the five Review Focus cases. Fix Critical/Important findings with a new RED→GREEN regression test before final verification.

- [ ] **Step 3: Commit only review fixes, if any**

```powershell
git add src tests database
git commit -m "fix(user): address unlock account review findings"
```
