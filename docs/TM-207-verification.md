# TM-207 verification evidence

Status: **ready for independent review with explicitly pending DB/provider gates**

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
| Full solution tests | `dotnet test TripMate.slnx -c Release --no-build --no-restore --logger "console;verbosity=minimal"` | Exit 0; API integration 272 passed / 104 skipped, Infrastructure 132 passed / 1 skipped, Application 630 passed / 0 skipped. Total: 1,034 passed, 0 failed, 105 skipped. Skips include all SQL-gated tests and opt-in Cloudinary smoke; this is not zero-skip verification. |
| SQL cleanup integration tests | `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~TourMediaCleanupOutboxSqlServerTests --logger "console;verbosity=minimal"` | User-run attempt with connection configured failed 3/3 at SQL connection open with TCP timeout. A later Codex run without `TRIPMATE_SQLSERVER_TEST_CONNECTION` skipped all 3. Rerun against a confirmed ready, isolated test SQL Server. |
| Live Cloudinary smoke | Opt-in `CloudinarySmokeTest` against configured development account | **Failed**: provider returned HTTP 401 (sanitized code `TOUR_MEDIA_STORAGE_REJECTED_HTTP_401`). Cleanup fallback was attempted and reported verified for the generated asset hash. No upload success or delivery URL is claimed. The local Cloudinary credential pairing/configuration must be corrected and the smoke rerun. |
| Vulnerability scan | `dotnet list TripMate.slnx package --vulnerable --include-transitive` | Exit 0; current NuGet sources reported no vulnerable packages. |
| `git diff --check` | `git diff --check` | Exit 0 after code changes. |

## Remaining gates

1. A reviewer with the authorized local test DB should configure
   `TRIPMATE_SQLSERVER_TEST_CONNECTION`, run SQL-gated suites with zero skips,
   and record credential-free server/database identity. The harness creates
   and drops only `TripMate_Test_<GUID>` databases; the SQL account must be
   allowed to create/drop those databases.
2. After the exposed Cloudinary credential is rotated and development config
   corrected, rerun the opt-in smoke test and confirm HTTPS delivery metadata
   and provider deletion. The prior smoke returned HTTP 401 and is not a pass.
3. Obtain independent review. Skipped/unavailable checks must not be described
   as passed; resolve review findings and rerun checks.
