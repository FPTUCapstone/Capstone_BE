# TM-207 verification evidence

Status: **ready for independent review; Cloudinary provider gate remains open**

This evidence records the current local verification on 2026-09-27. It does
not claim SQL/provider gates are passed or that TM-207 is ready to merge. No
credentials are recorded here.

## Revision

- Branch: `feature/datmnt-tour-media-management-api`
- HEAD before Task 9 verification changes: `6b4f5feff7d16d4ace7236d39e5998cf52f81f19`
- `origin/develop` observed at: `525489b61ee2a2791b3d0cf71313e80a4a0445e7`
- Task 8 commit: `6b4f5fe feat(api): process deferred tour media cleanup`

## Changes and review notes

- Cleanup processing now claims one outbox item immediately before provider
  I/O and processes the configured batch sequentially. This prevents a scoped
  EF `DbContext` from being used concurrently and avoids holding leases while
  other claimed items wait for provider calls.
- Added a barrier-based unit test for sequential claim timing and a barrier to
  the SQL concurrency integration test (no sleep-based synchronization).
- Cloudinary upload failures expose only a sanitized HTTP status code in the
  internal safe error code; provider response bodies and credentials remain
  excluded.
- Added an opt-in smoke test using an in-memory generated image. The smoke test
  prints only cloud/folder identity and a public-ID hash, and attempts cleanup
  even when upload verification fails.

## Verification results

| Check | Command / method | Result |
|---|---|---|
| Focused cleanup processor tests | `dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --no-restore --filter FullyQualifiedName~TourMediaCleanupProcessorTests` | Exit 0; 7 passed, 0 failed, 0 skipped |
| Infrastructure unit tests | `dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --no-build` | Exit 0; 132 passed, 0 failed, 1 skipped (opt-in provider smoke) |
| Application unit tests | `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --no-build` | Exit 0; 630 passed, 0 failed, 0 skipped |
| Restore | `dotnet restore TripMate.slnx` | Exit 0 after approved NuGet network access |
| Format verification | `dotnet format TripMate.slnx --verify-no-changes --no-restore` | Exit 0 after running `dotnet format TripMate.slnx --no-restore` |
| Release solution build | `dotnet build TripMate.slnx -c Release --no-restore` | Exit 0; 0 warnings, 0 errors |
| Full solution tests (pre-final SQL fixes) | `dotnet test TripMate.slnx -c Release --no-build --no-restore --logger "console;verbosity=minimal"` | Exit 0; API integration 272 passed / 104 skipped, Infrastructure 132 passed / 1 skipped, Application 630 passed / 0 skipped. Total: 1,034 passed, 0 failed, 105 skipped. SQL-gated tests and opt-in Cloudinary smoke were skipped. Final code was subsequently revalidated with all application/infrastructure unit tests and all focused TM-207 SQL tests below. |
| Focused TM-207 SQL integration suites | Sequential filters: `TourMediaMigrationTests`, `TourMediaManagementMigrationTests`, `TourMediaManagementSqlServerTests`, `UploadTourMediaSqlServerTests`, `TourMediaCleanupOutboxSqlServerTests` on `tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj` | Exit 0 across all 5 filters; 26 passed, 0 failed, 0 skipped (9 + 11 + 1 + 2 + 3). Ran on disposable `mcr.microsoft.com/mssql/server:2022-latest`, container `codex-tm207-sqltest`, host port 14332. The test harness created/dropped its `TripMate_Test_<GUID>` databases. Container had no mounted volume and was stopped/removed afterwards; generated SA password and test connection environment variables were cleared. |
| Final unit regression after SQL fixes | `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --no-build --no-restore`; `dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --no-build --no-restore` | Application: 630 passed, 0 failed, 0 skipped. Infrastructure: 132 passed, 0 failed, 1 skipped (opt-in Cloudinary smoke). |
| Live Cloudinary smoke | Opt-in `CloudinarySmokeTest` against configured development account | **Failed**: provider returned HTTP 401 (sanitized code `TOUR_MEDIA_STORAGE_REJECTED_HTTP_401`). Cleanup fallback was attempted and reported verified for the generated asset hash. No upload success or delivery URL is claimed. The Cloudinary account owner must verify the development API key/secret configuration and the smoke must be rerun. |
| Vulnerability scan | `dotnet list TripMate.slnx package --vulnerable --include-transitive` | Exit 0; current NuGet sources reported no vulnerable packages. |
| `git diff --check` | `git diff --check` | Exit 0 after code changes. |

## Remaining gates

1. The focused TM-207 SQL suites now pass with zero skips on an isolated test
   SQL Server. A reviewer may independently rerun them on an authorized test
   instance using the repository-supported `TRIPMATE_SQLSERVER_TEST_CONNECTION`.
2. The live Cloudinary smoke remains blocked by HTTP 401. Have the Cloudinary
   account owner verify the development API key/secret in local configuration,
   then rerun the opt-in smoke to confirm HTTPS delivery metadata and provider
   deletion.
3. Obtain independent review. Resolve any review findings and rerun affected
   checks. The PR is not merge-ready until the provider gate passes or an
   approved exception is documented.
