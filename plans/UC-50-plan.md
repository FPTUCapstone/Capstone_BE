# Implementation Plan — UC-50 Approve Tour Operator Application

## Overview

This implementation plan covers the backend implementation for **UC-50: Approve Tour Operator Application**. It defines the domain entities, EF Core configurations, CQRS query/commands, API controller endpoints, and unit tests following Clean Architecture, Database-First policies, and the `Result<T>` pattern.

---

## Technical Context & Decisions

1. **Database-First Mapping**: No EF migrations (`dotnet ef migrations add` is strictly prohibited). All 4 new entity mappings (`OperatorProfile`, `OperatorDocument`, `AuditLog`, `Notification`) mirror `database/tripmate_schema_v7.sql` schema directly.
2. **Single Transaction Persistence**: In `ApproveOperatorApplicationCommandHandler`, updates to `Users`, `OperatorProfiles`, `OperatorDocuments`, and inserts into `AuditLogs` & `Notifications` MUST be committed in a **single `SaveChangesAsync` call**.
3. **Tracking Discipline**:
   - `GetOperatorApplicationDetailQueryHandler` uses `.AsNoTracking()` for read-only queries.
   - `ApproveOperatorApplicationCommandHandler` keeps change tracking enabled for modified entities.
4. **Navigation Property Assignment**: When creating new entities (`AuditLog`, `Notification`), assign navigation properties (`User`, `ActorUser`) if parent entities are tracked/created in the same context graph.
5. **Mandatory Document Verification**: `BusinessLicense` and `TaxCode` documents are mandatory. The `OperatorDocumentType` enum uses `TaxCode` value.
6. **Audit Logging & Notifications**:
   - `AuditLog`: `ActionType = 'ApproveOperatorApplication'`, `AffectedEntity = 'OperatorProfile'`, `BeforeData` / `AfterData` JSON snapshots.
   - `Notification`: `Channel = Email`, `Status = Pending`. Email delivery transport is handled out-of-process.
7. **Reject Action Scope**: UC-51 owns the full rejection logic. The `Reject` handler in UC-50 acts as a UI-dependency stub returning a `501 Not Implemented` failure without altering state.

---

## Proposed Component Changes

### Component 1: Domain Layer (Entities & Enums)
- [NEW] [`OperatorApprovalStatus.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Domain/Enums/OperatorApprovalStatus.cs): `PendingApproval = 1`, `Approved = 2`, `Rejected = 3`
- [NEW] [`OperatorDocumentType.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Domain/Enums/OperatorDocumentType.cs): `BusinessLicense = 1`, `TaxCode = 2`, `Other = 3`
- [NEW] [`DocumentStatus.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Domain/Enums/DocumentStatus.cs): `Submitted = 1`, `Approved = 2`, `Rejected = 3`
- [NEW] [`NotificationChannel.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Domain/Enums/NotificationChannel.cs): `Push = 1`, `Email = 2`, `SMS = 3`
- [NEW] [`NotificationStatus.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Domain/Enums/NotificationStatus.cs): `Pending = 1`, `Sent = 2`, `Failed = 3`, `Read = 4`
- [NEW] [`OperatorProfile.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Domain/Entities/OperatorProfile.cs): `dbo.OperatorProfiles` mapping with PK `UserId`.
- [NEW] [`OperatorDocument.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Domain/Entities/OperatorDocument.cs): `dbo.OperatorDocuments` mapping with PK `Id` (`document_id`).
- [NEW] [`AuditLog.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Domain/Entities/AuditLog.cs): `dbo.AuditLogs` mapping with PK `Id` (`audit_log_id`).
- [NEW] [`Notification.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Domain/Entities/Notification.cs): `dbo.Notifications` mapping with PK `Id` (`notification_id`).

### Component 2: Infrastructure Layer (EF Configurations & DbContext)
- [NEW] [`OperatorProfileConfiguration.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Infrastructure/Persistence/Configurations/OperatorProfileConfiguration.cs)
- [NEW] [`OperatorDocumentConfiguration.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Infrastructure/Persistence/Configurations/OperatorDocumentConfiguration.cs)
- [NEW] [`AuditLogConfiguration.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Infrastructure/Persistence/Configurations/AuditLogConfiguration.cs)
- [NEW] [`NotificationConfiguration.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Infrastructure/Persistence/Configurations/NotificationConfiguration.cs)
- [MODIFY] [`IApplicationDbContext.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs): Expose `DbSet<OperatorProfile>`, `DbSet<OperatorDocument>`, `DbSet<AuditLog>`, `DbSet<Notification>`.
- [MODIFY] [`ApplicationDbContext.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs): Expose `Set<T>` implementations.

### Component 3: Application Layer (CQRS Features & DTOs)
- [NEW] [`TourOperatorApplicationErrorCodes.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/TourOperatorApplications/Common/TourOperatorApplicationErrorCodes.cs)
- [NEW] [`TourOperatorApplicationDetailDto.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/TourOperatorApplications/GetDetail/TourOperatorApplicationDetailDto.cs)
- [NEW] [`GetOperatorApplicationDetailQuery.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/TourOperatorApplications/GetDetail/GetOperatorApplicationDetailQuery.cs)
- [NEW] [`GetOperatorApplicationDetailQueryHandler.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/TourOperatorApplications/GetDetail/GetOperatorApplicationDetailQueryHandler.cs)
- [NEW] [`ApproveOperatorApplicationCommand.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/TourOperatorApplications/Approve/ApproveOperatorApplicationCommand.cs)
- [NEW] [`ApproveOperatorApplicationCommandValidator.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/TourOperatorApplications/Approve/ApproveOperatorApplicationCommandValidator.cs)
- [NEW] [`ApproveOperatorApplicationCommandHandler.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/TourOperatorApplications/Approve/ApproveOperatorApplicationCommandHandler.cs)
- [NEW] [`RejectOperatorApplicationCommand.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/TourOperatorApplications/Reject/RejectOperatorApplicationCommand.cs)
- [NEW] [`RejectOperatorApplicationCommandHandler.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/TourOperatorApplications/Reject/RejectOperatorApplicationCommandHandler.cs)

### Component 4: WebAPI Presentation Layer
- [NEW] [`AdminTourOperatorApplicationsController.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Api/Controllers/V1/AdminTourOperatorApplicationsController.cs): `[Authorize(Roles = "Administrator")]`, routes for `GET /{userId}`, `POST /{userId}/approve`, `POST /{userId}/reject`.

### Component 5: Test Suite (TDD Verification)
- [NEW] [`GetOperatorApplicationDetailQueryHandlerTests.cs`](file:///d:/study/Project-Capstone/Capstone_BE/tests/TripMate.Application.Tests/Features/Admin/TourOperatorApplications/GetDetail/GetOperatorApplicationDetailQueryHandlerTests.cs)
- [NEW] [`ApproveOperatorApplicationCommandHandlerTests.cs`](file:///d:/study/Project-Capstone/Capstone_BE/tests/TripMate.Application.Tests/Features/Admin/TourOperatorApplications/Approve/ApproveOperatorApplicationCommandHandlerTests.cs)

---

## Sequential Execution Tasks (Atomic TDD Steps)

### Task 1: Domain Entities & Infrastructure Mappings
- **Files**: Create enums, `OperatorProfile.cs`, `OperatorDocument.cs`, `AuditLog.cs`, `Notification.cs`, EF Configurations, update `IApplicationDbContext` & `ApplicationDbContext`.
- **Verification Command**: `dotnet build`
- **Definition of Done**: Solution compiles without errors or EF migration files.

### Task 2: Get Operator Application Detail Query (Red -> Green)
- **Files**: `GetOperatorApplicationDetailQuery.cs`, DTOs, `GetOperatorApplicationDetailQueryHandler.cs`, `GetOperatorApplicationDetailQueryHandlerTests.cs`.
- **Verification Command**: `dotnet test --filter GetOperatorApplicationDetailQueryHandlerTests`
- **Definition of Done**: Detail query returns application info & documents without leaking passwords or sensitive auth data (AC 1, AC 2).

### Task 3: Approve Operator Application Command - Core Approval Logic (Red -> Green)
- **Files**: `ApproveOperatorApplicationCommand.cs`, `Validator`, `ApproveOperatorApplicationCommandHandler.cs`, `ApproveOperatorApplicationCommandHandlerTests.cs`.
- **Verification Command**: `dotnet test --filter ApproveOperatorApplicationCommandHandlerTests`
- **Definition of Done**: Approving transitions `Users.status` to `Active`, `OperatorProfiles.approval_status` to `Approved`, keeps `TourOperator` role, updates document statuses to `Approved`, records reviewer ID & UTC timestamp (AC 3, AC 4, AC 5, AC 6).

### Task 4: Approve Command - Validation & Single-Transaction Audit/Notification (Red -> Green)
- **Files**: Update `ApproveOperatorApplicationCommandHandler.cs` and test suite.
- **Verification Command**: `dotnet test --filter ApproveOperatorApplicationCommandHandlerTests`
- **Definition of Done**: Validates mandatory documents (`BusinessLicense`, `TaxCode`), fails non-pending states, writes 1 `AuditLog` row with JSON snapshots, inserts 1 `Notification` row (`Email`, `Pending`), all persisted in 1 `SaveChangesAsync` call (AC 7, AC 8, AC 9, AC 10, AC 11).

### Task 5: Reject Action UI Dependency Stub & API Controller Layer
- **Files**: `RejectOperatorApplicationCommand.cs`, `RejectOperatorApplicationCommandHandler.cs`, `AdminTourOperatorApplicationsController.cs`.
- **Verification Command**: `dotnet test`
- **Definition of Done**: Controller maps 3 endpoints with `[Authorize(Roles = "Administrator")]`; Reject endpoint returns 501 stub.

### Task 6: Full Solution Verification & Zero-Regression Check
- **Verification Command**: `dotnet test`
- **Definition of Done**: 100% green pass rate across all new and existing test suites (AC 12).

---

## Verification Plan

### Automated Tests
- Command: `dotnet test`

### Definition of Done
1. `dotnet test` passes 100% green across all unit test suites.
2. All 12 Acceptance Criteria validated.
3. Clean Architecture & Database-First rules fully respected.
