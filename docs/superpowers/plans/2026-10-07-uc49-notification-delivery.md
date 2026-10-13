# UC-49 Notification Delivery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebase UC-49 safely and make its persisted unlock notification durably deliverable by email without allowing delivery failure to roll back the account unlock.

**Architecture:** Keep the existing `Notifications` table as the outbox created in the unlock transaction. Add delivery-claim and retry metadata to make the email worker safe across API instances; the worker claims only UC-49 email rows, sends through a dedicated administrative-notification email port, and persists delivery state separately from the unlock request. Resolve the branch's three `develop` conflicts by preserving both UC-49 behavior and the incoming UC-10 infrastructure behavior.

**Tech Stack:** .NET 10, EF Core/SQL Server, hosted services, MailKit SMTP adapter, xUnit/FluentAssertions, database-first SQL migrations.

**Spec:** `docs/superpowers/specs/2026-10-07-uc49-notification-delivery-design.md`

## Global Constraints

- Do not alter the UC-49 Administrator-only/protected-administrator boundary.
- Unlock, token revocation, audit, notification creation, and idempotency remain one transaction.
- Email transport is asynchronous and cannot roll back a committed unlock.
- No push/SMS transport or notification-centre API/UI is added.
- Use explicit idempotent SQL migration and update `database/tripmate_schema_v7.sql` together.
- Do not commit, push, or modify remote state without the developer's explicit request.

## Review Focus

- A worker crash after claiming an email must make the row eligible for a later retry, not leave it permanently Processing.
- Two API instances must not concurrently send the same claimed notification.
- SMTP success/failure must update only the claimed notification and never modify account-lock state.
- An idempotent unlock replay must not create another notification.
- A user without email must not be passed to the SMTP sender or be reported as email-delivered.

---

### Task 1: Rebase `develop` and preserve UC-49 integrations

**Files:**
- Modify: `src/TripMate.Api/Common/ApiControllerBase.cs`
- Modify: `src/TripMate.Infrastructure/DependencyInjection.cs`
- Modify: `tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs`
- Modify: any mechanically affected files from rebase conflicts only

**Interfaces:**
- Consumes: existing local UC-49 audit/notification edits and incoming `origin/develop` Redis/rate-limit changes.
- Produces: a working-tree branch rebased on `origin/develop`, with both contracts retained.

- [ ] **Step 1: Save tracked and UC-49 untracked local work to a named temporary stash, excluding `.superpowers/` artifacts.**

- [ ] **Step 2: Rebase the feature branch onto `origin/develop`; resolve the three known conflicts by retaining both unrelated changes.**

- [ ] **Step 3: Restore the temporary UC-49 work and resolve any resulting conflicts without modifying unrelated `develop` behavior.**

- [ ] **Step 4: Run a Release build to prove conflict resolution compiles.**

Run: `dotnet build TripMate.slnx -c Release --no-restore`

Expected: exit code 0.

### Task 2: Persist channel choice and outbox delivery contract

**Files:**
- Modify: `specs/TM-95-spec.md`
- Modify: `src/TripMate.Domain/Enums/NotificationStatus.cs`
- Modify: `src/TripMate.Domain/Entities/Notification.cs`
- Modify: `src/TripMate.Infrastructure/Persistence/Configurations/NotificationConfiguration.cs`
- Modify: `database/tripmate_schema_v7.sql`
- Create: `database/migrations/20261007_add_notification_delivery_outbox.sql`
- Modify: `src/TripMate.Application/Features/Admin/Users/Unlock/UnlockUserAccountCommandHandler.cs`
- Test: `tests/TripMate.Application.UnitTests/Features/Admin/Users/Unlock/UnlockUserAccountCommandHandlerTests.cs`
- Test: `tests/TripMate.Api.IntegrationTests/Admin/Users/UnlockUserAccountSqlServerTests.cs`

**Interfaces:**
- Consumes: user email state and an unlock transaction.
- Produces: `Notification` delivery fields sufficient for atomic worker claim/retry, and a single `AccountUnlocked` notification with Email or Push channel.

- [ ] **Step 1: Write failing handler tests proving a target with email creates `Email/Pending` and a target without email creates `Push/Pending`; preserve the one-row replay invariant.**

- [ ] **Step 2: Run the focused handler tests and confirm the no-email expectation fails against the current hard-coded Email behavior.**

Run: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --filter "FullyQualifiedName~UnlockUserAccountCommandHandlerTests"`

Expected: failure caused by channel selection.

- [ ] **Step 3: Add canonical delivery state (`Processing`, lease expiration, attempt count and next-attempt time), database-first schema/migration parity, and minimal handler channel selection.**

- [ ] **Step 4: Run focused handler and SQL Server unlock tests; confirm replay/concurrent unlock creates exactly one notification.**

Run: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --filter "FullyQualifiedName~UnlockUserAccountCommandHandlerTests"` and `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~UnlockUserAccountSqlServerTests"`

Expected: both pass.

### Task 3: Implement the durable UC-49 email delivery worker

**Files:**
- Create: `src/TripMate.Application/Common/Interfaces/IUserAdministrationNotificationEmailSender.cs`
- Create: `src/TripMate.Infrastructure/Email/UserAdministrationNotificationEmailSender.cs`
- Create: `src/TripMate.Infrastructure/Email/UserAdministrationNotificationDeliveryService.cs`
- Modify: `src/TripMate.Infrastructure/DependencyInjection.cs`
- Modify: `src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs`
- Modify: `src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs`
- Test: `tests/TripMate.Infrastructure.UnitTests/Email/UserAdministrationNotificationDeliveryServiceTests.cs`
- Test: `tests/TripMate.Api.IntegrationTests/Admin/Users/UnlockUserAccountSqlServerTests.cs`

**Interfaces:**
- Consumes: persisted Email/Pending `AccountUnlocked` notifications and `IUserAdministrationNotificationEmailSender.SendAsync(destination, subject, body, cancellationToken)`.
- Produces: a hosted service that atomically claims one notification at a time and marks it `Sent`, `Failed`, or retryable `Pending`.

- [ ] **Step 1: Write failing worker tests for confirmed send, transient/unknown retry, definite failure, expired lease reclaim, and Push fallback never invoking SMTP.**

- [ ] **Step 2: Run the focused worker tests and confirm they fail because the worker/port does not exist.**

Run: `dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --filter "FullyQualifiedName~UserAdministrationNotificationDeliveryServiceTests"`

Expected: failure due to missing implementation.

- [ ] **Step 3: Add the dedicated SMTP adapter and hosted worker. Claim rows atomically through SQL/EF persistence, schedule unknown/transient outcomes for delayed retry, and ensure an expired lease is reclaimed after a process failure.**

- [ ] **Step 4: Run focused worker and SQL Server tests to prove status transitions and concurrent claim behavior.**

Run: `dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --filter "FullyQualifiedName~UserAdministrationNotificationDeliveryServiceTests"` and `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~UnlockUserAccountSqlServerTests"`

Expected: both pass.

### Task 4: Validate source-of-truth and the full branch

**Files:**
- Modify: `specs/TM-95-spec.md`
- Modify: `docs/superpowers/specs/2026-10-07-uc49-notification-delivery-design.md` only if implementation decisions require a documented correction

**Interfaces:**
- Consumes: all previous tasks.
- Produces: verified UC-49 branch with current notification behavior specified.

- [ ] **Step 1: Update `TM-95-spec.md` to replace its notification exclusion with the approved delivery contract.**

- [ ] **Step 2: Run schema migration tests twice against SQL Server and confirm the migration is repeatable.**

Run: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~UserUnlockMigrationSqlServerTests"`

Expected: all selected tests pass.

- [ ] **Step 3: Run formatting, Release build, full test suite, and diff hygiene.**

Run: `dotnet format TripMate.slnx --verify-no-changes --no-restore`; `dotnet build TripMate.slnx -c Release --no-restore`; `dotnet test TripMate.slnx -c Release --no-restore`; `git diff --check`

Expected: all commands exit 0.

- [ ] **Step 4: Review the complete branch against the spec, with special attention to the five Review Focus conditions.**
