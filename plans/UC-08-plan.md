# UC-08 Update Traveler Profile — Backend Implementation Plan

> **Specification Reference:** [`Capstone_BE/specs/UC-08-spec.md`](file:///d:/study/Project-Capstone/Capstone_BE/specs/UC-08-spec.md)
> **Authoritative SRS Section:** Report 3 §3.2.8 (Update Traveler Profile)
> **Branch:** `feature/linhnv-update-traveler-profile`

---

## Granular Atomic Task Breakdown

### Task 1: Database Migration for Traveler Date of Birth Field
- **Status:** [DONE — migration applied and schema checked on local SQL Server]
- **Target Files:**
  - `database/migrations/20261012_add_traveler_profile_date_of_birth.sql`
- **Implementation Details:**
  - Create an idempotent SQL script adding `date_of_birth DATE NULL` to `dbo.TravelerProfiles`.
  - Execute script against local SQL Server instance (`localhost,14330`).

---

### Task 2: Domain Entity & EF Core Configuration
- **Status:** [DONE]
- **Target Files:**
  - `src/TripMate.Domain/Entities/TravelerProfile.cs`
  - `src/TripMate.Infrastructure/Persistence/Configurations/TravelerProfileConfiguration.cs`
- **Implementation Details:**
  - Extend `TravelerProfile` entity with:
    - `public DateOnly? DateOfBirth { get; private set; }`
    - Static factory overload or update logic to ensure initial creation handles personal details.
  - Update `TravelerProfileConfiguration`:
    - Map `date_of_birth` column (`HasColumnType("date")`).

---

### Task 3: Contracts, DTOs, and Error Codes
- **Status:** [DONE]
- **Target Files:**
  - `src/TripMate.Application/Features/Traveler/Profile/Common/TravelerProfileDto.cs`
  - `src/TripMate.Application/Features/Traveler/Profile/Get/GetTravelerProfileQuery.cs`
  - `src/TripMate.Application/Features/Traveler/Profile/Update/UpdateTravelerProfileCommand.cs`
  - `src/TripMate.Application/Features/Traveler/Profile/Update/UpdateTravelerProfileResponse.cs`
  - `src/TripMate.Application/Features/Traveler/Profile/Common/TravelerProfileErrorCodes.cs`
- **Implementation Details:**
  - Define `TravelerProfileDto` with:
    - `long UserId`, `string FullName`, `string? Email`, `string? PhoneNumber`, `DateOnly? DateOfBirth`, `string? AvatarUrl`.
  - Define `UpdateTravelerProfileCommand`:
    - `string FullName`, `string? PhoneNumber`, `DateOnly? DateOfBirth`, `byte[]? AvatarBytes`, `string? AvatarContentType`, `bool RemoveAvatar`.
    - Reject the invalid combination `RemoveAvatar == true` and `AvatarBytes != null`; do not give either operation implicit precedence.
  - Standardize error codes matching spec:
    - `MSG01`, `MSG02`, `MSG04`, `MSG_PHONE_DUP`, `MSG19`, `MSG20`, `MSG127`.

---

### Task 4: FluentValidation for Profile Updates
- **Status:** [DONE]
- **Target Files:**
  - `src/TripMate.Application/Features/Traveler/Profile/Update/UpdateTravelerProfileCommandValidator.cs`
- **Implementation Details:**
  - Enforce `FullName`:
    - Not empty $\rightarrow$ `MSG01` ("This field is required.").
    - Max 100 characters $\rightarrow$ `MSG02`.
  - Enforce `PhoneNumber`:
    - Normalize whitespace-only input to `null`.
    - If a non-empty value is provided, it matches `^0\d{9}$` $\rightarrow$ `MSG04`.
  - Enforce `DateOfBirth` (BR-18):
    - If provided, must be in the past and age $\ge 16$ years relative to current date $\rightarrow$ `MSG02`.
  - Enforce Avatar image (BR-16):
    - If `AvatarBytes` is provided:
      - Length $\le 5 \times 1024 \times 1024$ bytes (5 MB) $\rightarrow$ `MSG19`.
      - Content type must be `image/jpeg`, `image/png`, or `image/webp` $\rightarrow$ `MSG19`.

---

### Task 5: Avatar Cloud Storage Service
- **Status:** [DONE]
- **Target Files:**
  - `src/TripMate.Application/Common/Media/ITravelerAvatarStorage.cs`
  - `src/TripMate.Application/Common/Media/ITravelerAvatarCleanupJournal.cs`
  - `src/TripMate.Infrastructure/Persistence/SqlTravelerAvatarCleanupJournal.cs`
  - `src/TripMate.Infrastructure/Media/Cloudinary/CloudinaryTravelerAvatarStorage.cs`
  - `src/TripMate.Infrastructure/DependencyInjection.cs`
  - `src/TripMate.Infrastructure/Services/TravelerAvatarCleanupBackgroundService.cs`
- **Implementation Details:**
  - Implement `ITravelerAvatarStorage`:
    - `UploadAsync(TravelerAvatarUpload upload, CancellationToken ct)`:
      - Validates file signature/magic bytes.
      - Uses the shared Cloudinary client with public IDs `tripmate/avatars/{userId}/{Guid:N}`.
      - Returns HTTPS delivery URL.
    - `DeleteAsync(string publicId, CancellationToken ct)`:
      - Deletes an avatar asset by stable Cloudinary public ID, never by parsing a delivery URL.
  - Reuse or extend the repository's durable media-cleanup outbox/reservation pattern:
    - register cleanup for a newly uploaded asset if profile persistence cannot commit;
    - enqueue deletion of the replaced or removed asset only after the profile transaction commits;
    - retry transient Cloudinary delete failures in a background processor.
  - Register `ITravelerAvatarStorage` in `DependencyInjection.cs`.

---

### Task 6: CQRS Query and Command Handlers
- **Status:** [DONE]
- **Target Files:**
  - `src/TripMate.Application/Features/Traveler/Profile/Get/GetTravelerProfileQueryHandler.cs`
  - `src/TripMate.Application/Features/Traveler/Profile/Update/UpdateTravelerProfileCommandHandler.cs`
- **Implementation Details:**
  - `GetTravelerProfileQueryHandler`:
    - Resolves caller `userId` via `ICurrentUserService`.
    - Queries `User` entity and optional `TravelerProfile` entity.
    - Fails with forbidden result when the user is absent, not a Traveler, or not in an active account status.
    - Returns mapped `TravelerProfileDto`.
  - `UpdateTravelerProfileCommandHandler`:
    - Resolves caller `userId` via `ICurrentUserService`.
    - Loads `User` and `TravelerProfile`.
    - Verifies the current user exists, has the Traveler role, and is active before processing files or changing values.
    - Rejects a phone number already owned by another user before uploading media. If another request claims it between the check and commit, resolves the unique-index failure to `MSG_PHONE_DUP` without persisting profile changes.
    - Handles Avatar update safely:
      - If `RemoveAvatar == true`: clears the persisted avatar reference in the profile transaction, then enqueues durable deletion of the old asset after commit.
      - If `AvatarBytes` provided: uploads the new asset, persists its stable public ID and delivery URL with the profile transaction, then enqueues durable deletion of the old asset after commit.
      - If persistence cannot commit after upload, creates durable cleanup work for the newly uploaded asset; it must not leave an untracked Cloudinary asset.
    - Updates `User.FullName`, `User.PhoneNumber`, `User.UpdatedAtUtc = now`.
    - Upserts `TravelerProfile`: sets `DateOfBirth` and `UpdatedAtUtc = now`.
    - Saves profile values and cleanup/outbox records atomically.
    - **CRITICAL INVARIANT:** `User.Role`, `User.Status`, and `User.Email` are never touched (BR-17, BR-23).
    - Returns `UpdateTravelerProfileResponse` with `MSG20` and updated DTO.

---

### Task 7: API Controller Endpoints
- **Status:** [DONE]
- **Target Files:**
  - `src/TripMate.Api/Controllers/V1/TravelerProfileController.cs`
  - `src/TripMate.Api/Controllers/V1/Requests/UpdateTravelerProfileRequest.cs`
- **Implementation Details:**
  - Add `TravelerProfileController`:
    - `[Authorize(Roles = "Traveler")]`; handlers enforce active account state as a second authorization boundary.
    - `[Route("api/v1/traveler/profile")]`
  - Implement `[HttpGet]`:
    - Dispatches `GetTravelerProfileQuery`.
    - Returns `200 OK` with `TravelerProfileDto`.
  - Implement `[HttpPut]`:
    - Accepts `[FromForm] UpdateTravelerProfileRequest` with `multipart/form-data` only, even when no avatar is selected.
    - Validates file constraints.
    - Constructs and sends `UpdateTravelerProfileCommand`.
    - Returns `200 OK` with `UpdateTravelerProfileResponse` or ProblemDetails on validation failure.
    - Returns `409 Conflict` with `MSG_PHONE_DUP` when another account owns the phone number.

---

### Task 8: Automated Tests & Verification
- **Status:** [PARTIAL — 1,875 Application unit tests and eight focused API/SQL Server integration tests pass; full API integration suite was attempted but did not finish and remains unverified]
- **Target Files:**
  - `tests/TripMate.Application.UnitTests/Features/Traveler/Profile/UpdateTravelerProfileCommandValidatorTests.cs`
  - `tests/TripMate.Application.UnitTests/Features/Traveler/Profile/UpdateTravelerProfileCommandHandlerTests.cs`
  - `tests/TripMate.Api.IntegrationTests/Traveler/TravelerProfileEndpointTests.cs`
  - `tests/TripMate.Api.IntegrationTests/Traveler/TravelerAvatarSqlServerTests.cs`
- **Implementation Details:**
  - Test validator against all validation boundary conditions (empty full name MSG01, invalid phone MSG04, age < 16 BR-18 MSG02, file > 5MB MSG19).
  - Test command handler ensuring `User.Role` remains `Traveler`, `User.Email` is unaltered, and locked/inactive users are forbidden before storage is called.
  - Test avatar replacement/removal ordering, database-failure cleanup reservation, retryable Cloudinary deletion failure, and a concurrent phone-ownership race against SQL Server's unique index.
  - Test integration endpoints with an active authenticated Traveler token, a non-Traveler token, and a valid token for a locked/inactive account.
  - Execute `dotnet test` and `dotnet build` ensuring 0 warnings, 0 errors.
