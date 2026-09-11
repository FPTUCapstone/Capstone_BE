# Atomic Implementation Plan: TM-64 [UC-18] Invite Group Members

**Feature**: Invite Group Members  
**Jira Ticket**: TM-64  
**Use Case**: UC-18  
**Branch**: `feature/khanhpq-invite-group-members`  
**Base Branch**: `develop`  
**Specification Reference**: `specs/TM-64-spec.md`  
**Status**: Ready for Execution  

---

## Plan Overview

This plan breaks down the approved specification (`specs/TM-64-spec.md`) into sequential, atomic tasks following Test-Driven Development (TDD: Red -> Green -> Refactor), Clean Architecture boundaries, and database-first persistence invariants.

---

### Task 1: Error Codes, Query, and Response DTO
**Objective**: Define the CQRS Query, Response DTO, and error codes in the Application layer.

- **Files to create/modify**:
  - `[MODIFY]` `src/TripMate.Application/Features/TravelGroups/Common/TravelGroupErrorCodes.cs`
  - `[NEW]` `src/TripMate.Application/Features/TravelGroups/GetInvitation/GetGroupInvitationResponse.cs`
  - `[NEW]` `src/TripMate.Application/Features/TravelGroups/GetInvitation/GetGroupInvitationQuery.cs`
- **Definition of Done**:
  - Error codes `GroupNotFound` and `HostPermissionRequired` declared.
  - Query contains `GroupId` (long) and `CurrentUserId` (long).
  - Response DTO exposes `groupId`, `groupName`, `inviteCode`, `qrData`, `expiresAtUtc`.

---

### Task 2: TDD - Unit Tests for GetGroupInvitationQueryHandler (Red Phase)
**Objective**: Write comprehensive, failing unit tests for `GetGroupInvitationQueryHandler` before writing implementation code.

- **Files to create**:
  - `[NEW]` `tests/TripMate.Application.UnitTests/Features/TravelGroups/GetInvitation/GetGroupInvitationQueryHandlerTests.cs`
- **Test cases**:
  1. `Handle_WhenGroupDoesNotExist_ReturnsGroupNotFound`: returns failure with 404 error code.
  2. `Handle_WhenCallerIsNotHost_ReturnsHostPermissionRequired`: returns failure with 403 error code.
  3. `Handle_WhenActiveInvitationExists_ReturnsExistingInvitationWithoutInsertingNewRow`: returns 200 OK with correct `inviteCode` and `qrData`.
  4. `Handle_WhenInvitationExpiredOrMissing_GeneratesAndPersistsNewValidInvitation`: creates new 8-char code with 30-day expiry and returns it.
- **Definition of Done**:
  - Tests fail initially because `GetGroupInvitationQueryHandler` does not exist yet (Red).

---

### Task 3: Application CQRS Query Handler Implementation (Green Phase)
**Objective**: Implement `GetGroupInvitationQueryHandler` to satisfy the unit tests.

- **Files to create**:
  - `[NEW]` `src/TripMate.Application/Features/TravelGroups/GetInvitation/GetGroupInvitationQueryHandler.cs`
- **Handler logic**:
  - Query group with `HostUserId` and `Name`.
  - Validate group existence.
  - Enforce `group.HostUserId == request.CurrentUserId`.
  - Retrieve latest unexpired invitation (`ExpiresAtUtc > now && UsedCount < MaxUses`).
  - If null, generate a new code using `TravelGroupConstants` and persist it.
  - Return `GetGroupInvitationResponse`.
- **Definition of Done**:
  - All unit tests pass (Green).

---

### Task 4: WebAPI Endpoint Integration
**Objective**: Expose `GET /api/v1/travel-groups/{groupId}/invitation` in `TravelGroupsController`.

- **Files to modify**:
  - `[MODIFY]` `src/TripMate.Api/Controllers/V1/TravelGroupsController.cs`
- **Implementation**:
  - Add `[HttpGet("{groupId:long}/invitation")]`.
  - Validate authenticated user: `currentUserService.UserId`.
  - Dispatch query via MediatR: `Sender.Send(new GetGroupInvitationQuery(groupId, currentUserService.UserId.Value))`.
  - Map response: `result.IsSuccess ? Ok(result.Value) : HandleFailure(result)`.
  - Update `ApiControllerBase.cs` status mapping for `TravelGroupErrorCodes.HostPermissionRequired` -> 403 Forbidden, `TravelGroupErrorCodes.GroupNotFound` -> 404 Not Found.
- **Definition of Done**:
  - Endpoint properly declared with Swagger documentation attributes (`200 OK`, `401 Unauthorized`, `403 Forbidden`, `404 Not Found`).

---

### Task 5: Final Solution Verification
**Objective**: Run complete solution build and all tests to ensure Zero Regression.

- **Definition of Done**:
  - 100% tests passing.
  - Zero warnings or errors.

