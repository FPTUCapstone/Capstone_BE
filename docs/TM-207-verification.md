# TM-207 verification evidence

Status: **ready for independent review; live Cloudinary smoke passed locally**

This evidence records the current local verification on 2026-09-27. The SQL
and Cloudinary provider gates passed. Independent review and CI of the final
commit remain before merge. No credentials are recorded here.

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
- The configured credential pair authenticated successfully against Cloudinary
  and direct signed uploads succeeded. The SDK upload path returned an
  invalid-signature HTTP 401 because the code explicitly set `Unsigned=false`:
  the SDK included that field in its signature while Cloudinary omitted it.
  Leaving `Unsigned` unset retains the SDK's signed-upload default. A focused
  unit test checks that the field is absent and the non-overwriting,
  synthetic-filename request shape is preserved.
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
| Signed upload adapter test after provider fix | `dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --no-restore --filter FullyQualifiedName~CloudinarySdkClientUploadTests` | Exit 0; 1 passed, 0 failed, 0 skipped. |
| Post-fix Infrastructure unit regression | `dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --no-build --no-restore` | Exit 0; 133 passed, 0 failed, 1 skipped (opt-in smoke). A prior local whole-solution API integration run ran unusually long and was interrupted without a final result; CI verification of the pushed fix remains required. |
| Live Cloudinary smoke | Opt-in `CloudinarySmokeTest` against configured development account | Exit 0; 1 passed, 0 failed, 0 skipped. Generated image uploaded with server-side signature, HTTPS delivery URL verified, and the exact smoke asset destroyed. No credential or delivery URL was printed. |
| Vulnerability scan | `dotnet list TripMate.slnx package --vulnerable --include-transitive` | Exit 0; current NuGet sources reported no vulnerable packages. |
| `git diff --check` | `git diff --check` | Exit 0 after code changes. |

## Remaining gates

1. The focused TM-207 SQL suites now pass with zero skips on an isolated test
   SQL Server. A reviewer may independently rerun them on an authorized test
   instance using the repository-supported `TRIPMATE_SQLSERVER_TEST_CONNECTION`.
2. Obtain independent review. Resolve any review findings and rerun affected
   checks. Rerun CI after the Cloudinary upload fix is pushed to the PR.

## PR #28 review follow-up — 2026-09-27

Source: https://github.com/FPTUCapstone/Capstone_BE/pull/28#issuecomment-5857581603

- Reorder with omitted/null primaryMediaId now snapshots the existing active
  primary before temporary-order changes clear the flags. Explicit replacement
  still works; zero-primary Tours stay at zero. Added two unit regressions and
  a SQL regression that verifies the result from another DbContext.
- RED: focused management handler tests exited 1 with exactly the expected
  primary-preservation failure (1 failed, 10 passed, 0 skipped).
- Initial GREEN: all TourMedia application tests exited 0 (36 passed,
  0 failed, 0 skipped). The complete regression run below also covers the
  subsequent shared authorization refactor.
- Upload now delegates account/profile/ownership checks to the existing
  TourMediaAccessResolver, retaining its existing public failure messages.
- Approved-Tour alt-text-only edits are intentional under the approved minor
  accessibility-edit rule. Existing tests cover this and Pending rejection;
  the handler now documents the distinction from material caption changes.
- ITourMediaUploadLock now documents its active same-connection transaction
  requirement and commit/rollback lifetime.
- Cleanup batchSize is a per-run processing limit. Claims remain one-at-a-time
  to avoid lease expiration while preceding provider calls wait. This behavior
  is documented; no batched claim or throughput change was introduced.
- Deferred nonblocking finding: when provider upload succeeds but persistence
  and compensation both fail, reuse of the same public ID with overwrite=false
  can reject same-key retries until cleanup succeeds. Bounded cleanup can also
  exhaust, so eventual automatic recovery is not guaranteed. This patch does
  not add a terminal upload-operation status or change SQL lifecycle rules.
  A follow-up needs a defined terminal response/recovery contract plus
  concurrency and compensation tests before changing that behavior. Do not
  describe this retry finding as fixed.
- Shared applock/idempotency abstractions and repeated audit/error-code
  construction remain nonblocking technical debt; no broad abstraction was
  introduced in this correction.
- Docker daemon is unavailable in this session. New SQL regression execution
  remains gated on an isolated TRIPMATE_SQLSERVER_TEST_CONNECTION.
- Final regression command: `dotnet test TripMate.slnx -c Release --logger
  "console;verbosity=minimal" --blame-hang-timeout 2m`. Exit 0: Application
  632 passed / 0 skipped; Infrastructure 133 passed / 1 skipped; API integration
  272 passed / 105 skipped. Total 1,037 passed, 0 failed, 106 skipped. SQL tests
  and opt-in Cloudinary smoke were skipped, including the new SQL regression;
  no hang timeout was triggered.
- Scoped `dotnet format TripMate.slnx --verify-no-changes --no-restore --include
  <changed C# files>` exited 0 after formatting those files. `git diff --check`
  exited 0. Spec-compliance review confirms null preserves the pre-reorder
  primary and explicit selection still replaces it. Code-quality review
  confirms the snapshot precedes the two-phase update and Upload shares the
  existing access resolver without changing its error messages.
- The owner requested committing and pushing this correction to the existing
  PR #28 branch. No review resolution or reviewer message is included in that
  delivery; the upload-retry finding remains explicitly deferred above.
