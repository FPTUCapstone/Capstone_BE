# Implementation Plan — UC-57 Backend: Configure Algorithm Parameters

## Overview

This implementation plan outlines the Clean Architecture Backend implementation for **UC-57: Configure Algorithm Parameters** in `Capstone_BE` (Administrator Algorithm Parameters Configuration). Revised 2026-09-19 per the UC-68/69 cross-review lessons; approved for implementation by the developer.

**Branch dependency**: implemented on `feature/linhnv-configure-algorithm-parameters`, stacked on `feature/linhnv-view-audit-log-details` (PR #17) — it reuses `AuditLog.CreateRecordedOutcome` and `AuditFailureBehaviour` from that branch; PR #17 must merge first (merge order: #17 → this PR).

## Technical Directives

1. **Database Table Mapping**: Map `dbo.SystemConfigs` (`config_key` VARCHAR(100) PK, `config_value` NVARCHAR(500), `description`, `updated_by`, `updated_at` DATETIME2 UTC → `AsUtcDateTime2`). No new migration — the table and the four seeded algorithm rows already exist in `tripmate_schema_v7.sql`.
2. **BR-115 (Role Authorization)**: Only accounts with `UserRole.Administrator` role may view or update algorithm configurations. Returns HTTP 403 Forbidden with **locked** `MSG126` ("You do not have permission to access this function.").
3. **BR-130 / CR-14 (Audit Trail)**: On success, writes one `dbo.AuditLogs` row via `AuditLog.CreateRecordedOutcome(..., result: AuditOutcome.Success)` — action type `UpdateAlgorithmParameters` (new `AuditActionTypes` constant), affected entity `SystemConfig` (new `AuditEntityTypes` constant), `beforeData`/`afterData` JSON with only the four managed keys — same `SaveChangesAsync` as the config update. `UpdateAlgorithmParametersCommand` is added to the `AuditFailureBehaviour` opt-in list so confirmed DB failures record a `Failure` audit row.
4. **Validation Rules (`MSG118`)**: enforced **inside the update handler** (not FluentValidation — `ValidationBehaviour` would surface them as HTTP 400, the CONTRACT-01 defect). Failures return `Result.Failure(InvalidValue, locked MSG118 text)` → mapped to `422`. `WeatherAlertThresholdSeverity` accepts case-insensitive `Moderate|Severe|Extreme` and normalizes to canonical casing.
5. **Scope guard**: only `CSP.BufferTimeMinutes`, `CSP.DefaultTravelSpeedKmh`, `Rerouting.SearchRadiusKm`, `Weather.AlertThresholdSeverity` are read/upserted; other `SystemConfigs` rows are untouched.
6. **CR-07**: `updatedAtLocal` = latest `updated_at` of the managed rows, Vietnam Time `dd/MM/yyyy HH:mm:ss`; timestamps come from `IDateTimeProvider`.

## Proposed Component Changes

### Component 1: Domain & EF Core Mapping
- [NEW] [`SystemConfig.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Domain/Entities/SystemConfig.cs): entity with guarded constructor + `UpdateValue` (invariant: key/value required, column lengths enforced).
- [NEW] [`SystemConfigConfiguration.cs`](file:///d:/study/Project-Capstone/Capstone_BE/src/TripMate.Infrastructure/Persistence/Configurations/SystemConfigConfiguration.cs): column mappings.
- [MODIFY] `IApplicationDbContext.cs` / `ApplicationDbContext.cs` / `TestApiDbContext`: add `DbSet<SystemConfig> SystemConfigs`.
- [MODIFY] `AuditActionTypes.cs` / `AuditEntityTypes.cs`: add the two constants.

### Component 2: Application Layer
- [NEW] `Features/Admin/SystemConfigs/Common/AlgorithmConfigErrorCodes.cs`: `Forbidden` (→403), `InvalidValue` (→422).
- [NEW] `GetAlgorithmParameters/`: query + handler + `AlgorithmParametersDto` (defaults fallback 15/30/5/Severe when a row is missing).
- [NEW] `UpdateAlgorithmParameters/`: command + handler (handler-side range checks → 422; upsert; audit entry; returns updated DTO).

### Component 3: API Layer
- [NEW] `AdminSystemConfigsController.cs`: `GET/PUT api/v1/admin/system-configs/algorithm-parameters`, `[Authorize(Roles = "Administrator")]`, `Ok(result.Value)` / `HandleFailure`.
- [MODIFY] `ApiControllerBase.cs`: map the two new error codes (403 / 422).

### Component 4: Tests (TDD — written before implementation)
- [NEW] `GetAlgorithmParametersQueryHandlerTests.cs`: 403, seeded values, defaults fallback, latest updated_at.
- [NEW] `UpdateAlgorithmParametersCommandHandlerTests.cs`: 403; success (upsert + audit row with `Success` outcome + before/after + `updated_by`); 422 per each range/enum violation with no row change and no audit row; severity normalization; untouched foreign config rows.
- [NEW] `AuditConfigEndpointTests.cs` (integration): 401/403/200 GET/PUT/422 through the real pipeline.

## Verification Plan
- `dotnet test tests/TripMate.Application.UnitTests/... --filter FullyQualifiedName~SystemConfigs`
- Full `dotnet build -c Release` + `dotnet test` + `dotnet format --verify-no-changes`
- SQL-gated integration tests with `TRIPMATE_SQLSERVER_TEST_CONNECTION` set.
