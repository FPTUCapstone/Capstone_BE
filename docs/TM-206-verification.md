# TM-206 verification evidence

Date: 2026-09-23 (Asia/Ho_Chi_Minh)
Scope: database-only Tour-owned media schema and upgrade-safe migration
Branch: `feature/datmnt-tour-media-schema`
Base and pre-commit HEAD: `25551b1c17f21cb83eb6908ef5c6a026d5b2ec7f`

## 1. Verified outcome

TM-206 adds `commerce.TourMedia` to the fresh SQL Server schema and adds the
idempotent `20260923_add_tour_media.sql` upgrade migration. Fresh installation
and upgrade from the recorded pre-TM-206 fixture produce the same TourMedia
inventory: columns, types, lengths, nullability, collation, defaults, identity,
PK, cascade FK, CHECK constraints, filtered unique indexes, and lookup index.

The database allows zero or one active primary image per Tour and rejects a
second active primary image. It rejects duplicate active `sort_order` values.
Soft-deleted rows remain physically present with `lifecycle_status = 'Deleted'`
and a non-null `deleted_at`. `ON DELETE CASCADE` applies only when the parent
Tour is physically deleted. `catalog.POIPhotos` remains isolated.

There is deliberately no SQL count constraint for the maximum 10 active
images. TM-207 owns that application-workflow rule. This change contains no
API, Entity Framework model/migration, Cloudinary SDK/configuration, UI, or
publication workflow implementation.

## 2. Environment and safe test target

- .NET SDK: `10.0.401`.
- SQL Server: `16.0.4265.3`, Developer Edition (64-bit).
- Connection identity: `localhost,14330`, database `master`; credentials were
  obtained at runtime from the local `tripmate-sqlserver` container and were
  neither printed nor stored in this document.
- The SQL harness created isolated `TripMate_Test_<GUID>` databases and removed
  them after each test. Final leftover database count: `0`.
- Migration order is deterministic: `20260923_add_tour_media.sql` follows the
  existing `20260918_add_audit_result_reason.sql`; `apply-schema.sh` applies the
  sorted `database/migrations/*.sql` set.

## 3. Commands and measured results

| Command | Exit | Result |
| --- | ---: | --- |
| `dotnet restore TripMate.slnx` | 0 | All seven projects restored. The first sandboxed attempt returned NU1301 because outbound NuGet access was denied; the approved network retry passed. |
| `dotnet format TripMate.slnx --verify-no-changes --no-restore` | 0 | No formatting changes required. |
| `dotnet build TripMate.slnx -c Release --no-restore` | 0 | 0 warnings, 0 errors. |
| `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --no-build --filter "FullyQualifiedName~TourMediaMigrationTests"` | 0 | 9 passed, 0 failed, 0 skipped. |
| `dotnet test TripMate.slnx -c Release --no-build --logger "console;verbosity=minimal" --logger "trx;LogFileName=tm206-task4-full.trx"` | 0 | 752 passed, 0 failed, 0 skipped. |
| `dotnet list TripMate.slnx package --vulnerable --include-transitive` | 0 | No vulnerable packages reported for all seven projects using the configured NuGet source. |
| `git diff --check` | 0 | No whitespace errors. |

Full regression breakdown:

| Assembly | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| `TripMate.Infrastructure.UnitTests` | 9 | 0 | 0 |
| `TripMate.Application.UnitTests` | 475 | 0 | 0 |
| `TripMate.Api.IntegrationTests` | 268 | 0 | 0 |
| **Total** | **752** | **0** | **0** |

TRX outputs are under each test project's local `TestResults` directory and
are not source artifacts intended for commit.

## 4. Focused SQL evidence

The nine `TourMediaMigrationTests` prove:

1. canonical fresh-schema inventory;
2. full fresh-versus-upgrade inventory parity and existing-data preservation;
3. second-run idempotency;
4. rollback for a same-named wrong-shape table;
5. rollback for a wrong active-primary index;
6. rollback for a wrong lifecycle CHECK;
7. zero-or-one active primary plus active order uniqueness;
8. lifecycle/deleted-at consistency and persisted soft deletion; and
9. physical parent cascade with POI-photo isolation.

The first Task 3 run exposed a test-inventory collation conflict between SQL
Server system metadata and a Vietnamese database collation. The root cause was
the snapshot query, not the migration. Every inventory branch is now collated
to `DATABASE_DEFAULT`; the complete focused and regression suites pass.

## 5. Two-pass review

### Pass 1 - approved specification and scope

- Fresh schema and migration define the same TourMedia objects.
- Cloudinary fields are metadata only; there is no binary column.
- Active primary semantics are zero-or-one, not exactly one.
- Maximum 10 active images is not enforced in SQL.
- Soft deletion and physical-parent cascade are separate and consistent.
- No Tour, TourSchedule, POI, POIPhoto, booking, or historical row is rewritten.
- No API, Cloudinary runtime, UI, publication workflow, or EF migration was
  introduced.

### Pass 2 - implementation quality and operational safety

- Migration uses `SET XACT_ABORT ON`, TRY/CATCH, transaction, rollback, and
  rethrow.
- Existing same-named objects are fully validated and incompatible shapes fail
  closed instead of being silently accepted or destructively repaired.
- Column/default/collation, PK/FK/CHECK, key order, filter predicate, included
  column, uniqueness, enabled/trusted state, and total inventory are checked.
- Tests run on identified SQL Server, use isolated databases, and leave zero
  test databases behind.
- No Critical or Minor review finding remains.

## 6. Checklist v2.1 mapping

Status values: `Đ` = đạt locally, `NA` = outside this database-only task,
`C` = requires later PR/CI/reviewer state.

| Checklist | Status | Evidence / reason |
| --- | --- | --- |
| C01-C04 | Đ | Jira TM-206, source decision, scope, acceptance criteria, approved spec and atomic plan are recorded with exact files and expected tests. |
| C05 | Đ | Branch is `feature/datmnt-tour-media-schema`, based on `origin/develop`; no PR has been created yet. |
| C06-C08 | Đ | Base/HEAD/status and baseline are recorded; AC-to-DB/test evidence is mapped here. Untracked task files are preserved. |
| C17 | Đ | SQL types, lengths, nullable/default/CHECK/FK/UNIQUE inventory matches between full schema and migration. EF is intentionally outside TM-206. |
| C18-C19 | Đ | Database-first full schema plus upgrade migration; empty/fresh, populated baseline, rerun, preflight, preservation, and rollback cases pass. |
| C20 | NA | No EF parent/child save workflow is added. Cascade behavior is verified directly on SQL Server. |
| C21 | Đ | Lifecycle timestamps use `DATETIME2` with UTC defaults; lifecycle/deleted-at consistency is enforced and tested. No date-range query is introduced. |
| C22 | Đ | Migration transaction and negative rollback cases pass; no application race path is introduced. |
| C23 | NA | No filter/count/projection/page application query is added. Index keys and deterministic gallery order are nevertheless validated. |
| C24 | Đ | Provider-specific constraints, filtered indexes, rollback, and cascade ran on SQL Server with 0 skips. |
| D05 | Đ | Fresh schema, populated upgrade, second run, complete inventory parity, before/after legacy state, and identified test database are covered. |
| C25-C27 | Đ | Correct worktree/branch, .NET SDK, Docker SQL Server identity/host port, and healthy provider were verified without modifying source to hide environment errors. |
| C31 | Đ | Ephemeral database owner/cleanup is explicit; leftover count is 0; credentials were not printed or committed. |
| C32 | Đ | Restore, full Release build including test projects, formatter, and all tests pass. No conflict-derived duplicate was found. |
| C33 | NA | No Web or Mobile code is changed. |
| C34-C35 | Đ | Environment, provider, commands, exits and exact pass/fail/skip counts are recorded; SQL-critical tests were not skipped. |
| C36 | C | No CI job exists for this uncommitted local head. CI evidence must be collected after commit/push/PR and must not be inferred from local results. |
| C37-C38 | Đ | Final diff check and two-pass self-review pass; NuGet advisory scan reports no vulnerable packages for the configured source. |
| C47 | Đ | The collation test finding has a recorded root cause, correction, and 9/9 plus 752/752 regression evidence. |
| C48-C51 | NA | No earlier reviewed head, PR description, reviewer decision, or review thread exists yet. These become mandatory after PR creation. |
| C52 | C | Intended target is `develop`, but protected-branch/merge checks can only be verified on the future PR. Migration ordering is documented. |
| C53 | NA | The worktree is retained; no cleanup or deletion is authorized. |
| C54 | Đ | Branch, worktree, commands, verified scope, limitations, and next PR/CI dependencies are handed off in this document. |

## 7. Rollout and recovery

The migration is additive and does not write existing business rows. Deploy it
before any TM-207+ consumer starts writing TourMedia. If rollout must be
recovered before any consumer write, take a backup and remove only the newly
created, verified-empty `commerce.TourMedia` table under data-owner control.
Once rows exist, do not automate a destructive rollback; preserve data and use
an owner-reviewed forward migration.

## 8. Remaining delivery gates

- Commit and push only after explicit developer authorization.
- Create a PR targeting `develop`, then collect CI evidence for C36/C52.
- Obtain independent reviewer approval and resolve all review threads before
  claiming merge readiness (C48-C51).
