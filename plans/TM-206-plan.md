# TM-206 — Atomic implementation plan

Status: **IMPLEMENTED AND LOCALLY VERIFIED — 2026-09-23**
Spec: `specs/TM-206-spec.md` (approved 2026-09-23)
Branch: `feature/datmnt-tour-media-schema`

## Task record and baseline

- Jira: TM-206.
- Repository: `FPTUCapstone/Capstone_BE`.
- Base: `origin/develop` at `25551b1`.
- Worktree: `D:\CapStone\Capstone_BE_tm206`.
- Source decisions: `Capstone_Docs/requirements/tour-catalogue-enrichment.md`
  at `7bf28d8`, plus the approved D4 soft-delete/cascade amendment.
- Process sources reviewed before implementation:
  `Dev_and_CrossReview_Checklist v2.1.pdf`,
  `TEAM_ENGINEERING_RULES v2.1.docx`, repository `AGENTS.md`, and
  repository `CLAUDE.md`.
- Record `git status --short --branch`, `git worktree list`, `dotnet --info`,
  Docker/SQL Server availability, exact command, exit code, and
  pass/fail/skip counts before implementation. A zero exit code without test
  discovery/count evidence is not sufficient.
- This task changes only database schema, migration scripts, and SQL Server
  integration tests. It does not add an Entity Framework migration, API,
  Cloudinary SDK/configuration, or UI.

**Baseline commands**

```powershell
dotnet restore TripMate.slnx
dotnet format TripMate.slnx --verify-no-changes --no-restore
dotnet build TripMate.slnx -c Release --no-restore
dotnet test TripMate.slnx -c Release --no-build --logger trx
```

If SQL Server tests are skipped because the provider is unavailable, record
them as skipped and keep TM-206 unverified until they run against an identified
SQL Server test database.

**Baseline evidence — 2026-09-23 (Asia/Ho_Chi_Minh)**

- `HEAD` and `origin/develop`: `25551b1c17f21cb83eb6908ef5c6a026d5b2ec7f`.
- .NET SDK: `10.0.401`; runtime: `10.0.12`.
- Restore: pass.
- Format verification: pass, exit code 0.
- Release build: pass, 0 warnings and 0 errors.
- Tests: pass with 0 failed, 686 passed, 57 skipped, 743 total
  (`Infrastructure.UnitTests`: 9/9; `Application.UnitTests`: 475/475;
  `Api.IntegrationTests`: 202 passed, 57 skipped, 259 total).
- All 57 skips require `TRIPMATE_SQLSERVER_TEST_CONNECTION`. This baseline is
  acceptable for planning, but TM-206 cannot be marked verified or complete
  until its SQL-critical tests run against an identified SQL Server database.

## Task 1 — Define migration test contract (Red)

**Files**

- Add `tests/TripMate.Api.IntegrationTests/Tours/TourMediaMigrationTests.cs`.
- Add a scoped fixture at
  `tests/TripMate.Api.IntegrationTests/Fixtures/Database/tm206_pre_migration_schema.sql`
  containing the required pre-TM-206 parent/reference objects and representative
  Tour, TourSchedule, POI, and POIPhoto rows.

**Work**

1. Add failing SQL Server integration tests for fresh-schema TourMedia inventory,
   baseline-upgrade parity, and a second migration execution.
2. Add negative tests for a same-named wrong-shape table/index/check constraint;
   verify the failed transaction leaves no partial TM-206 objects.
3. Add constraint tests: zero active primary images are allowed; one active
   primary image is allowed; duplicate active primary images or active
   `sort_order` values are rejected; deleted rows do not violate the active
   filtered indexes.
4. Add lifecycle tests: a normal soft-deleted media row remains physically
   present with `lifecycle_status = 'Deleted'` and non-null `deleted_at`; a
   physical parent Tour delete cascades its media rows; `catalog.POIPhotos` is
   untouched.
5. Record before/after row counts and identity values for existing fixture data
   so migration success proves preservation instead of only proving schema shape.

**Verification**

```powershell
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~TourMediaMigrationTests"
```

**Definition of done**

The tests compile and fail only because `commerce.TourMedia` and the TM-206
migration do not exist.

## Task 2 — Add canonical fresh-schema inventory (Green, part 1)

**File**

- Modify `database/tripmate_schema_v7.sql`.

**Work**

1. Add `commerce.TourMedia` after `commerce.Tours` in the commerce catalogue
   section.
2. Add exactly the table columns, defaults, PK/FK, lifecycle/deleted-at and
   positive-order checks approved in `TM-206-spec.md`.
3. Add the active-only filtered unique primary/order indexes and gallery lookup
   index from the approved spec.
4. Do not add a SQL CHECK/count mechanism for the maximum 10 active images;
   TM-207 owns that application-workflow limit.

**Verification**

```powershell
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~TourMediaMigrationTests"
```

**Definition of done**

Fresh-schema tests pass; upgrade and migration-specific tests remain red.

## Task 3 — Implement upgrade-safe SQL migration (Green, part 2)

**Files**

- Add `database/migrations/<next-date>_add_tour_media.sql`.
- Update the migration test's explicit file-name list/path only if necessary.

**Work**

1. Use `SET XACT_ABORT ON`, `BEGIN TRY`/transaction/rollback, matching TM-70
   migration discipline.
2. Create an absent `commerce.TourMedia` table and all required constraints and
   indexes.
3. For existing same-named objects, validate complete shape instead of silently
   accepting an incompatible table, column, FK, check, or filtered index.
4. Use a cascade FK solely for physical parent deletion. Do not write any
   delete/update workflow or infer whether a Tour may be hard deleted.
5. Make a second execution a no-op after shape validation.
6. Document migration impact as additive. Recovery before consumer rollout is
   removal of the newly created empty/known-owned table after backup; never run
   destructive rollback against a user database as part of automated tests.

**Verification**

```powershell
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~TourMediaMigrationTests"
```

**Definition of done**

All TM-206 migration tests pass, including fresh-versus-upgraded full inventory
and negative rollback tests.

## Task 4 — Review and regression verification

**Files**

- Review all files touched by Tasks 1–3.
- Add `docs/TM-206-verification.md` with reproducible evidence for the final
  branch head.

**Work**

1. Compare SQL table inventory, migration shape validation, and integration
   inventory serialization for every affected object.
2. Confirm normal media removal remains represented only as persisted soft
   lifecycle state; there is no API, Cloudinary, UI, or publication workflow
   behaviour in this change.
3. Confirm no migration writes to existing Tour, POI, booking, or historical
   data.
4. Map applicable checklist items C01-C08, C17-C24, D05, C25-C27, C31-C38,
   and C47-C54 to evidence. Mark irrelevant API/UI/idempotency cases `NA` with
   a reason rather than treating them as pass.
5. Record the identified SQL Server database/schema, migration files applied,
   final head/base SHA, SDK, exact commands, exit codes, and pass/fail/skip
   counts. SQL-critical tests must actually run.

**Verification**

```powershell
dotnet format TripMate.slnx --verify-no-changes --no-restore
dotnet build TripMate.slnx -c Release --no-restore
dotnet test TripMate.slnx -c Release --no-build --logger trx
git diff --check
```

**Definition of done**

All tests pass; a two-pass spec-compliance and code-quality review has no
critical finding; unrequested scope is absent.

**Measured result — 2026-09-23**

- Two-pass review completed with no Critical or Minor finding remaining.
- Focused SQL Server suite: 9 passed, 0 failed, 0 skipped.
- Full solution suite: 752 passed, 0 failed, 0 skipped.
- Restore, formatter, Release build, NuGet vulnerability scan, and
  `git diff --check` passed. See `docs/TM-206-verification.md`.

## Completion boundary

After Task 4, present the diff and verification outcome. Do not commit, push,
or open a PR until explicitly requested by the developer.
