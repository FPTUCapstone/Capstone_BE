# Atomic Implementation Plan: TM-63 [UC-17] Create Travel Group

**Feature**: Create Travel Group  
**Jira Ticket**: TM-63  
**Use Case**: UC-17  
**Branch**: `feature/khanhpq-create-travel-group`  
**Base Branch**: `develop`  
**Specification Reference**: `specs/TM-63-spec.md`  
**Status**: Ready for Execution  

---

## Plan Overview

This plan breaks down the approved specification (`specs/TM-63-spec.md`) into small, sequential, atomic tasks following Test-Driven Development (TDD: Red -> Green -> Refactor), Clean Architecture boundaries, and database-first persistence invariants.

---

### Task 1: Domain Entities & Enums
**Objective**: Create pure Domain layer models corresponding to the approved schema without any framework dependency.

- **Files to create/modify**:
  - `[NEW]` `src/TripMate.Domain/Enums/GroupMemberStatus.cs`
  - `[NEW]` `src/TripMate.Domain/Entities/TravelGroup.cs`
  - `[NEW]` `src/TripMate.Domain/Entities/GroupMember.cs`
  - `[NEW]` `src/TripMate.Domain/Entities/TravelGroupCreationRequest.cs`
  - `[NEW]` `src/TripMate.Domain/Entities/Itinerary.cs` (Minimal stub for FK linkage)
  - `[NEW]` `src/TripMate.Domain/Common/Errors/TravelGroupErrors.cs`
- **Verification Command**:
  ```bash
  dotnet build src/TripMate.Domain/TripMate.Domain.csproj
  ```
- **Definition of Done**:
  - Pure Domain layer compiles with zero warnings.
  - Zero dependencies on EF Core or ASP.NET Core in `TripMate.Domain`.
  - Balanced English comments with Input/Output and business rationale on public types.

---

### Task 2: Infrastructure EF Core Configuration & DbContext Registration
**Objective**: Map the Domain entities to the existing SQL Server database tables (`social.TravelGroups`, `social.GroupMembers`, `social.TravelGroupCreationRequests`, `planning.Itineraries`) using Fluent API.

- **Files to create/modify**:
  - `[MODIFY]` `src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs`
  - `[NEW]` `src/TripMate.Infrastructure/Persistence/Configurations/TravelGroupConfiguration.cs`
  - `[NEW]` `src/TripMate.Infrastructure/Persistence/Configurations/GroupMemberConfiguration.cs`
  - `[NEW]` `src/TripMate.Infrastructure/Persistence/Configurations/TravelGroupCreationRequestConfiguration.cs`
  - `[NEW]` `src/TripMate.Infrastructure/Persistence/Configurations/ItineraryConfiguration.cs`
  - `[MODIFY]` `src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs`
- **Verification Command**:
  ```bash
  dotnet build src/TripMate.Infrastructure/TripMate.Infrastructure.csproj
  ```
- **Definition of Done**:
  - `IApplicationDbContext` and `ApplicationDbContext` expose the new `DbSet` properties.
  - Fluent configurations match table names, schemas (`social`, `planning`), primary/foreign keys, and UTC datetime conversions.

---

### Task 3: TDD - Unit Tests for Command Validator & Handler (Red Phase)
**Objective**: Write comprehensive, failing unit tests for `CreateTravelGroupCommandValidator` and `CreateTravelGroupCommandHandler` before writing implementation code.

- **Files to create/modify**:
  - `[NEW]` `tests/TripMate.Application.UnitTests/Features/TravelGroups/CreateTravelGroup/CreateTravelGroupCommandValidatorTests.cs`
  - `[NEW]` `tests/TripMate.Application.UnitTests/Features/TravelGroups/CreateTravelGroup/CreateTravelGroupCommandHandlerTests.cs`
- **Verification Command**:
  ```bash
  dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~TravelGroups"
  ```
- **Definition of Done**:
  - Tests verify: empty group name, name > 150 chars, itineraryId <= 0, valid input.
  - Tests verify: itinerary not found (returns Failure with ItineraryNotFound), successful creation (returns Result.Success with correct group and Host member), repeated idempotency key, and transaction rollback.
  - Tests fail initially as implementation does not exist (Red).

---

### Task 4: Application CQRS Implementation (Green Phase)
**Objective**: Implement the Command, Validator, Handler, and Response DTO to satisfy the unit tests.

- **Files to create/modify**:
  - `[NEW]` `src/TripMate.Application/Features/TravelGroups/CreateTravelGroup/CreateTravelGroupCommand.cs`
  - `[NEW]` `src/TripMate.Application/Features/TravelGroups/CreateTravelGroup/CreateTravelGroupCommandValidator.cs`
  - `[NEW]` `src/TripMate.Application/Features/TravelGroups/CreateTravelGroup/CreateTravelGroupResponse.cs`
  - `[NEW]` `src/TripMate.Application/Features/TravelGroups/CreateTravelGroup/CreateTravelGroupCommandHandler.cs`
- **Verification Command**:
  ```bash
  dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~TravelGroups"
  ```
- **Definition of Done**:
  - All unit tests pass (Green).
  - Navigation property rule applied for same-transaction insert (`hostMember.TravelGroup = travelGroup`).
  - The same `Idempotency-Key` returns the original group and never creates a duplicate.
  - Balanced English comments with Input/Output format.

---

### Task 5: WebAPI Controller & Endpoint Integration
**Objective**: Expose `POST /api/v1/travel-groups` in Presentation layer following REST standards without synthetic wrappers.

- **Files to create/modify**:
  - `[NEW]` `src/TripMate.Api/Controllers/V1/TravelGroupsController.cs`
  - `[NEW]` `src/TripMate.Api/Controllers/V1/Requests/CreateTravelGroupRequest.cs`
- **Verification Command**:
  ```bash
  dotnet test TripMate.slnx
  dotnet build src/TripMate.Api/TripMate.Api.csproj
  ```
- **Definition of Done**:
  - Endpoint returns `201 Created` with `CreatedAtAction` or standard URL.
  - Passes full solution build and tests.

---

### Task 6: Final Solution Verification & Code Review
**Objective**: Run complete solution verification command, check git status, and prepare review checklist.

- **Verification Command**:
  ```bash
  dotnet test TripMate.slnx
  ```
- **Definition of Done**:
  - 100% test suite passing with Zero Regression.
  - No secrets, no extraneous files, clean working tree.
