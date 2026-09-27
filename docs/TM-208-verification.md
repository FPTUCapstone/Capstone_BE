# TM-208 verification evidence — 2026-09-27

Status: **implementation and scoped verification complete; independent review and delivery pending**.

The BE branch is `feature/datmnt-tour-search-thumbnail`, based on TM-207
commit `ca9ec3a` while PR #28 is open. The central API contract is maintained
on the separate `feature/datmnt-tour-search-thumbnail-contract` Docs branch.

## Environment

- Isolated SQL Server 2022 test container `codex-tm208-sqltest` listened only
  on `127.0.0.1:14332` and had no mounted volume. The repository harness
  created/dropped its own `TripMate_Test_<GUID>` databases. Shared local and
  Azure databases were not used. After testing, only SQL Server system
  databases remained; the exact temporary container was stopped and removed.
- The first sandboxed `dotnet restore TripMate.slnx` failed (exit 1) because
  outbound NuGet access was denied. Retrying with permitted network access
  passed (exit 0). This was an environment restriction, not a package error.
- No connection string, password, Cloudinary credential, or delivery URL from
  a real account was printed or committed.

## Commands and results

| Gate | Result |
| --- | --- |
| `dotnet restore TripMate.slnx` | Exit 0 on network-enabled retry. |
| Baseline `dotnet build TripMate.slnx -c Release --no-restore` | Exit 0; 0 warnings, 0 errors. |
| Baseline Application unit tests | Exit 0; 630 passed, 0 failed, 0 skipped. |
| Baseline Infrastructure unit tests | Exit 0; 133 passed, 0 failed, 1 skipped (opt-in Cloudinary smoke). |
| Baseline `FullyQualifiedName~SearchToursSqlServerTests` | Exit 0; 5 passed, 0 failed, 0 skipped. |
| TM-208 RED: `SearchToursSqlServerTests`, `SearchToursEndpointTests`, `ToursOpenApiTests` filters | Exit 1; 22 passed, 4 expected failures, 0 skipped. All four failures were missing `thumbnailUrl` in DTO JSON or OpenAPI. No SQL setup failure. |
| TM-208 GREEN: same filters after implementation and null-response assertion | Exit 0; 26 passed, 0 failed, 0 skipped. |
| `dotnet format TripMate.slnx --verify-no-changes --no-restore` | Exit 0 after formatting changed C# files. |
| Final `dotnet build TripMate.slnx -c Release --no-restore` | Exit 0; 0 warnings, 0 errors. |
| Full `dotnet test TripMate.slnx -c Release --no-build --no-restore` with isolated SQL | Exit 1: Application 630 passed; Infrastructure 133 passed/1 intentional skip; API 390 passed/3 failed due Windows file lock on `logs/uc04-s11.log` in unrelated UC-04 authentication tests. |
| Rerun of the three UC-04 failures alone | Exit 0; 3 passed, 0 failed, 0 skipped. |
| API suite excluding only those three UC-04 tests, with isolated SQL | Exit 0; 390 passed, 0 failed, 0 skipped. |
| `git diff --check` | Exit 0 for tracked changes; new Markdown files were separately checked for trailing whitespace. |

The split reruns cover all 393 API tests successfully, but **one full-suite
invocation did not pass**. Do not present it as a clean full-suite pass. The
three UC-04 failures are file-contention errors in test log reset, and they
passed individually; no TM-208 code touches that log. CI on a delivered branch
must still run the complete suite and independent review must assess the diff.

## TM-208 behavioral evidence

- OpenAPI and runtime JSON expose an always-present nullable `thumbnailUrl`.
- Active primary media wins even when its `sort_order` is after another active
  image. Deleted primary and non-primary-only media yield null.
- Draft/unpublished Tours remain excluded by the existing public predicate;
  POI photos cannot become Tour thumbnails.
- The page query retains its original count/order/availability behavior. Media
  lookup is one query for only selected page Tour IDs, and none for an empty
  page; it projects only Tour ID and delivery URL.
- The endpoint stays anonymous, direct-DTO and `no-store`.

## Remaining gates

1. Review the BE and Docs diffs independently.
2. After TM-207 PR #28 merges, rebase or otherwise reconcile TM-208 with the
   merged `develop`, then rerun affected tests.
3. Deliver BE as a stacked PR based on TM-207 while PR #28 is open, and Docs
   as a separate PR based on `develop`. The owner authorized PR delivery after
   implementation on 2026-09-27.
