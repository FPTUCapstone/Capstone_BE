# UC-34 Update Tour Operator Profile — Backend Implementation Plan

> Execution gate: **APPROVED by the developer on 2026-11-26; all tasks T1-T10 executed.** The approved contract is
> `specs/UC-34-update-operator-profile-spec.md`. Workspace: worktree `_wt/Capstone_BE_uc34`,
> branch `feature/uc34-update-operator-profile` from `develop` `745bbd2f`.

## Objective

Let an approved, active Tour Operator read and edit company name, description, address, contact phone,
contact email, website and logo through `GET`/`PUT /api/v1/operator/profile`, keeping tax code, business
licence number, account email, approval status, commission and bank account read-only.

## Ground rules for every task

- TDD: failing test first (Red), minimal code (Green), refactor. Code written before its test is deleted.
- Clean Architecture order: Domain ← Application ← Infrastructure ← Api. No EF migrations; SQL script only.
- `Result<T>` + `HandleFailure`; no magic strings (error codes and field keys are constants).
- After each task: run its verification command, then a review pass against spec + plan. Critical findings
  block the next task.
- No commit, push or PR until the developer asks (AGENTS §5.1). Work stays uncommitted on the branch;
  I will ask before committing.
- `.cs` files end without a final newline only where `.editorconfig` says so (CI checks formatting);
  run `dotnet format TripMate.slnx --verify-no-changes` before finishing.
- SQL-backed tests need
  `TRIPMATE_SQLSERVER_TEST_CONNECTION="Server=localhost;Database=master;Integrated Security=True;Encrypt=False;TrustServerCertificate=True"`;
  without it they silently skip, so a run without it is not evidence for T1/T9.

## Resolved details (decided here from the spec; flag if you disagree)

- Migration file is dated **`20261126_uc34_operator_profile_fields.sql`** (today); the spec's example date
  was a placeholder.
- Command property → FE key: validator uses `.OverridePropertyName` so failures surface as `businessName`,
  `businessDescription`, `businessAddress`, `contactPhone`, `contactEmail`, `website`, `logo` after
  `ValidationErrorKeyNormalizer` camelCases them. `removeLogo` conflicts report under `logo`.
- Logo bytes: controller reads at most 5 MB; a larger file is not buffered and is passed as empty bytes so the
  validator reports `MSG19` (same trick as `OperatorApplicationController.ReadDocumentAsync`). Accepted types
  `image/jpeg|png|webp`, with the same magic-byte check as the Traveler avatar validator.
- Whitespace-only optional text is stored as NULL. `contactEmail` is stored trimmed and **not** lower-cased
  (the lower-casing rule belongs to account email, which this spec does not touch).
- `ValidationBehaviour` also validates in the MediatR pipeline; controller pre-validation exists only to build
  the §4.1 body, exactly like `Resubmit`.
- Logo public ids: `tripmate/operator-logos/{userId}/{guid:N}`; both upload and delete reject ids outside that root.

## Tasks

### T1 — Schema migration and fresh-schema parity

Files:
- Create `database/migrations/20261126_uc34_operator_profile_fields.sql`
- Modify `database/tripmate_schema_v7.sql` (`dbo.OperatorProfiles`: add the five nullable columns)
- Create `tests/TripMate.Api.IntegrationTests/OperatorProfileTests/OperatorProfileFieldsMigrationSqlServerTests.cs`

Steps (Red → Green): test that a fresh schema has `description`, `contact_email`, `website_url`, `logo_url`,
`logo_public_id` with the spec's types, all nullable; test that on a schema without them the script adds them,
keeps existing rows with NULLs, and can be run twice. Pattern: `OperatorLicenseMigrationSqlServerTests`.
Verify: `dotnet test tests/TripMate.Api.IntegrationTests --filter "FullyQualifiedName~OperatorProfileFieldsMigration"`
with the SQL env var. DoD: both tests green on local SQL Server; schema + migration identical in effect.

### T2 — Domain entity and EF mapping

Files:
- Modify `src/TripMate.Domain/Entities/OperatorProfile.cs`
- Modify `src/TripMate.Infrastructure/Persistence/Configurations/OperatorProfileConfiguration.cs`
- Create `tests/TripMate.Application.UnitTests/Features/Operator/Profile/OperatorProfileBusinessProfileTests.cs`

Steps: add `Description`, `ContactEmail`, `WebsiteUrl`, `LogoUrl`, `LogoPublicId` plus max-length constants
(2000/200/300/500/300). Add `UpdateBusinessProfile(name, description, address, phone, email, website, nowUtc)`
(trims, blank→null, name required, length guards, never touches tax/licence/status/commission/bank),
`SetLogo(url, publicId, nowUtc)` and `ClearLogo(nowUtc)` (clears both columns). Map columns with `HasColumnName`.
Tests: guards, trimming, blank→null, read-only fields untouched, `ClearLogo` idempotent when already empty.
Verify: `dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~OperatorProfileBusinessProfile"`.
DoD: tests green; `TestDbContext` model still builds.

### T3 — Contracts, error codes and GET query

Files (folder `src/TripMate.Application/Features/Operator/Profile/`):
- `Common/OperatorProfileErrorCodes.cs` — `Forbidden` reuses `OperatorTourErrorCodes.AccessRequired`;
  new `NotFound = "operator_profile.not_found"`, `RequestInvalid = "operator_profile.request_invalid"`,
  `Unavailable = "operator_profile.unavailable"`; field-key constants; `InvalidLogo = AuthErrorCodes.Msg19`-style
  constant `"MSG19"` (add `Msg19` constant if absent, comment citing UC-34 §3.8.1).
- `Common/OperatorProfileDto.cs` (FE shape, §4) and `UpdateOperatorProfileResponse.cs`.
- `Get/GetOperatorProfileQuery.cs` + `GetOperatorProfileQueryHandler.cs` (access via
  `OperatorBookingAccess.ResolveOperatorIdAsync`, `AsNoTracking`, projects `Users.Email` as `accountEmail`).
- Tests `tests/TripMate.Application.UnitTests/Features/Operator/Profile/GetOperatorProfileQueryHandlerTests.cs`.

Tests: Traveler/anonymous/Pending/Rejected/inactive → access error; approved → full DTO, null optional fields
omitted per §4; another operator's data never returned.
Verify: `--filter "FullyQualifiedName~GetOperatorProfileQueryHandler"`. DoD: green.

### T4 — Update command and validator

Files: `Update/UpdateOperatorProfileCommand.cs`, `Update/UpdateOperatorProfileCommandValidator.cs`;
tests `UpdateOperatorProfileCommandValidatorTests.cs`.

Rules exactly as spec §4 table and §4.1 codes: required name → `MSG01`; email → `MSG02`; phone `^0\d{9}$` →
`MSG04`; lengths and website scheme → `operator_profile.request_invalid`; logo size/type/signature → `MSG19`;
`removeLogo` with a logo is allowed (logo wins) and is **not** an error.
Verify: `--filter "FullyQualifiedName~UpdateOperatorProfileCommandValidator"`. DoD: every rule has a failing-then-passing test.

### T5 — Update handler, text fields only

Files: `Update/UpdateOperatorProfileCommandHandler.cs`; tests `UpdateOperatorProfileCommandHandlerTests.cs`.

Steps: access check; tracked load (no `AsNoTracking`); call `UpdateBusinessProfile`; `SaveChangesAsync` inside
`ExecuteInTransactionAsync`; return the §4 profile DTO. 404 if the profile row vanished. Logo storage is not
touched in this task (fake storage asserts zero calls).
Tests: happy path updates only editable fields; tax/licence/status/commission/bank unchanged; trimming; access
failures; blank optional → NULL; `UpdatedAtUtc` set from `IDateTimeProvider`.
Verify: `--filter "FullyQualifiedName~UpdateOperatorProfileCommandHandler"`. DoD: green, no logo code yet.

### T6 — Logo storage port and logo paths in the handler

Files:
- `src/TripMate.Application/Common/Media/IOperatorLogoStorage.cs` (`AllocatePublicId(long)`, `UploadAsync`, `DeleteAsync`;
  result type like `TravelerAvatarUploadResult`)
- Modify `UpdateOperatorProfileCommandHandler.cs`
- Extend `UpdateOperatorProfileCommandHandlerTests.cs` with a fake `IOperatorLogoStorage`.

Behavior per spec §4.2 and §6: new logo → upload, commit, best-effort delete old; DB failure → delete the new
asset; upload failure → `operator_profile.unavailable`, nothing persisted; `removeLogo` with no logo → no-op 200,
zero storage calls, idempotent twice; `removeLogo` with legacy `logo_url` only → clear both columns, no delete;
`removeLogo` + new logo → new wins; failed old-asset delete only logs.
Verify: `--filter "FullyQualifiedName~UpdateOperatorProfileCommandHandler"`. DoD: every §4.2 bullet has a test.

### T7 — Cloudinary logo storage

Files: `src/TripMate.Infrastructure/Media/Cloudinary/CloudinaryOperatorLogoStorage.cs`; register
`IOperatorLogoStorage` in `src/TripMate.Infrastructure/DependencyInjection.cs` (singleton, next to the avatar
storage); tests in `tests/TripMate.Infrastructure.UnitTests/Media/` mirroring any existing avatar-storage test.

Behavior: root `tripmate/operator-logos`; public https delivery URL; reject foreign roots and bad content types;
`Overwrite:false`; failures return a safe code, never the provider message.
Verify: `dotnet test tests/TripMate.Infrastructure.UnitTests --filter "FullyQualifiedName~OperatorLogo"`. DoD: green.

### T8 — API controller, error mapping, OpenAPI

Files:
- `src/TripMate.Api/Controllers/V1/OperatorProfileController.cs` — `[Authorize(Roles = "TourOperator")]`,
  route `api/v1/operator/profile`, `GET` (no-store) and multipart `PUT` (`RequestSizeLimit` 6 MB), validation and
  §4.1 400 body built like `OperatorApplicationController.Resubmit`.
- `src/TripMate.Api/Controllers/V1/Requests/UpdateOperatorProfileRequest.cs` — only the seven editable form
  fields + `removeLogo`; no binder property for tax code, licence, email or status.
- Modify `src/TripMate.Api/Common/ApiControllerBase.cs` — map `NotFound` 404, `RequestInvalid` 400,
  `Unavailable` 503.
- Tests `tests/TripMate.Api.IntegrationTests/OperatorProfileTests/OperatorProfileEndpointTests.cs` (factory + fake storage
  pattern from `ResubmitOperatorApplicationEndpointTests`).

Tests: 401 anonymous, 403 Traveler / Pending / Rejected operator; 200 GET shape; 200 PUT; 400 body asserts
`errorCode`, camelCase `errors` as `string[]`, only failing fields, correct MSG codes, `logo` over 5 MB →
`MSG19`; extra form fields `taxCode`/`businessLicenceNumber`/`accountEmail`/`approvalStatus` are ignored and the
DB values stay; `removeLogo` no-op returns 200; 503 when upload fails.
Verify: `dotnet test tests/TripMate.Api.IntegrationTests --filter "FullyQualifiedName~OperatorProfileEndpoint"`
(SQL env var set). DoD: green; Swagger generation test (existing OpenAPI tests) still green.

### T9 — SQL-backed round trip

Files: `tests/TripMate.Api.IntegrationTests/OperatorProfileTests/OperatorProfileSqlServerTests.cs` (pattern:
`ResubmitOperatorApplicationSqlServerTests`).

Tests against real SQL Server: update persists each column and survives a re-read; read-only columns byte-identical
before/after; logo replace and remove persist `logo_url`/`logo_public_id`; two concurrent saves leave one coherent
row (last write wins).
Verify: `--filter "FullyQualifiedName~OperatorProfileSqlServer"` with the SQL env var. DoD: green.

### T10 — Docs, full verification and handoff

Files:
- `docs/UC-34-api-contract.md` (routes, bodies, §4.1 examples, error table, FE integration notes: key names,
  phone rule, 413 mapping).
- Handoff note `UC-34-update-operator-profile-be-handoff.md`, delivered outside the repo in `D:/Capstone_Project_Production/Handoff` and not committed (what UC-34 is, what is done, what remains: FE wiring, Mobile, Azure
  migration before merge, tour-detail display follow-up; commands to rerun).

Verification (all must pass, results pasted into the handoff):
1. `dotnet build TripMate.slnx -c Release` — 0 warnings, 0 errors.
2. `dotnet format TripMate.slnx --verify-no-changes`.
3. `dotnet test tests/TripMate.Application.UnitTests` and `tests/TripMate.Infrastructure.UnitTests` — no regressions
   against the recorded baseline.
4. Operator-related integration filters with the SQL env var: `OperatorProfile`, `ResubmitOperatorApplication`,
   `RegisterOperator`, `OperatorLicenseMigration`.
5. Full API integration suite is left to CI (~45 min); noted as not run locally.

DoD: handoff written, all of the above green or honestly reported. Then ask the developer whether to commit,
push, open a PR, keep, or discard.

## Out of scope (from the spec)

Re-verification flow, audit log, Staff notification, durable logo cleanup journal, Traveler-facing display of
the new fields, FE/Mobile changes.

## Risks and open items

- Azure SQL must receive the migration **before** the PR merges (develop auto-deploys).
- Orphaned Cloudinary logos are possible after a crash between commit and old-asset delete (accepted, spec §6).
- FE currently has no phone validation and no 413 handling; documented in the API contract for the FE task.
