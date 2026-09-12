# Implementation Plan — UC-51: Reject Tour Operator Application

## Overview
This implementation plan covers the backend implementation for **UC-51: Reject Tour Operator Application** in `TripMate.Application` and `TripMate.Api` following Clean Architecture, Database-First conventions, and the `Result<T>` pattern.

The feature allows an Administrator to reject a pending Tour Operator application by providing a mandatory rejection reason, updating statuses in a single database transaction, creating an audit log entry, and queueing an email notification.

---

## Technical Context & Decisions

1. **Database-First Mapping**: Follows `database/tripmate_schema_v7.sql`. `Users.status` transitions to `AccountStatus.Rejected` (`5`) and `OperatorProfiles.approval_status` transitions to `OperatorApprovalStatus.Rejected` (`3`).
2. **Single Transaction Persistence**: In `RejectOperatorApplicationCommandHandler`, updates to `Users`, `OperatorProfiles`, `OperatorDocuments`, and inserts into `AuditLogs` & `Notifications` MUST be committed in a **single `SaveChangesAsync` call**.
3. **Concurrency Protection**: `.IsConcurrencyToken()` on `ApprovalStatus` and `Status` ensures concurrent reviews return `409 Conflict` (`NotPending`).
4. **Input Validation**: `RejectOperatorApplicationCommandValidator` validates that the rejection reason is non-empty, non-whitespace, and maximum 1000 characters.
5. **Message Catalog Resolution**: Success message `MSG116` ("Application rejected. Notification sent to operator.") is dynamically queried from `dbo.Messages` with fallback to default catalog constants.

---

## Proposed Component Changes

### Component 1: Application Layer
- [`RejectOperatorApplicationCommand.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/TourOperatorApplications/Reject/RejectOperatorApplicationCommand.cs): `UserId`, `Reason`.
- [`RejectOperatorApplicationCommandValidator.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/TourOperatorApplications/Reject/RejectOperatorApplicationCommandValidator.cs): FluentValidation rules.
- [`RejectOperatorApplicationCommandHandler.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/TourOperatorApplications/Reject/RejectOperatorApplicationCommandHandler.cs): Core rejection handler logic.
- [`RejectOperatorApplicationResponseDto.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Application/Features/Admin/TourOperatorApplications/Reject/RejectOperatorApplicationResponseDto.cs): Response payload.

### Component 2: Presentation Layer (API Controllers)
- [`AdminTourOperatorApplicationsController.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Api/Controllers/V1/AdminTourOperatorApplicationsController.cs): `POST /api/v1/admin/tour-operator-applications/{userId}/reject` with null-safe request handling.

### Component 3: Test Suite
- [`RejectOperatorApplicationCommandHandlerTests.cs`](file:///d:/study/Project-Capstone/Capstone_BE/tests/TripMate.Application.UnitTests/Features/Admin/TourOperatorApplications/Reject/RejectOperatorApplicationCommandHandlerTests.cs): Unit tests for rejection workflow, validation, concurrency, and DB message catalog resolution.

---

## Verification Plan

### Automated Tests
- Run unit tests: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter "FullyQualifiedName~Reject"`
- Full test suite: `dotnet test`
