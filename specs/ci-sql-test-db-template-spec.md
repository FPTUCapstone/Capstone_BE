# CI: build the SQL Server test schema once, clone it per test

Status: draft for approval, 2026-11-23.
Branch `chore/ci-sql-test-db-template` (worktree `_wt/Capstone_BE_ci_sql_template`), based on `origin/develop` at `08dcae45`.
Scope: test fixture only (`tests/TripMate.Api.IntegrationTests/Infrastructure`). No application, schema or migration change.

## 1. Problem

The CI job `Backend tests` spends most of its time in the SQL Server suite (about 40-47 minutes on recent PR runs).
The cause is in `SqlServerTestDatabase.CreateAsync()`: every call creates a new database and replays
`database/tripmate_schema_v7.sql` plus every file in `database/migrations` (88 `.sql` files, about 300 `GO` batches).
There are 544 `CreateAsync()` call sites (47 more use `CreateEmptyAsync()` and are not affected), so a CI run replays the full schema more than 540 times.
The CI database is not "old": the service container is created fresh for every run. Cost grows with every new migration because every test pays for it.

Measured on 2026-11-23, SQL Server 2022 (16.0.1000.6, Developer) on the developer machine:

| Step | Time |
|---|---|
| Full build (schema + 88 migrations), driven by `sqlcmd`, process launch included | 20-25 s |
| One real `CreateAsync()` through the fixture (`CreateAsync_BootstrapReusesTwoConnections...`) | 17 s |
| `BACKUP DATABASE` of the finished database (compressed, 1 MB file) | 0.23 s |
| `RESTORE DATABASE` to a new name, sequential | 0.65-0.81 s |
| 8 concurrent restores, wall time | 2.9 s |

A restored copy had the same 896 user objects and the same collation (`Vietnamese_100_CI_AS`) as the source.
Linux container timings are not measured (no Docker on the author's machine); the first CI run is the real check.

## 2. Goal and non-goals

Goal: build the schema once per test process, then give every `CreateAsync()` caller its own database by `RESTORE`.
Isolation, naming (`TripMate_Test_<guid32>`), collation and the final database state stay the same as today.

Non-goals (separate follow-ups, only if measurements still justify them): matrix sharding of the job, `DELAYED_DURABILITY` or recovery-model tuning, a single `dotnet build` shared by the three `dotnet test` steps, restore throttling, a template shared across processes.

## 3. Isolation from TM-300

This change is independent of TM-300 and must not carry any of its files.
TM-300 work is uncommitted in the main checkout `Capstone_BE` (on `develop`):
`database/seeds/README.md` (modified), `database/seeds/20261118_*.sql` (4 untracked), `specs/TM-300-spec.md`, `plans/TM-300-plan.md`, `tools/poi-seed/`.
Rules for this branch:
- Work only in the worktree above; never touch, stash or commit the main checkout.
- Before every commit, `git status --short` must list only paths named in the plan's file list.
- The PR description and commits do not mention TM-300. TM-300 gets its own branch and PR later.

## 4. Design

### 4.1 Flow

1. First `CreateAsync()` in a test process triggers one template build; every other caller awaits the same build.
2. Build: create `TripMate_Template_<guid32>` (`COLLATE Vietnamese_100_CI_AS`), apply the schema and migrations through one database connection (same code and same SQL as today), read the logical file names, `BACKUP DATABASE` it to a `.bak` file, then `DROP DATABASE` the template.
3. Each `CreateAsync()` then runs `RESTORE DATABASE [TripMate_Test_<guid32>] FROM DISK = <bak> WITH MOVE ...` on a master connection and returns the usual `SqlServerTestDatabase`.
   A clone is restored from the `.bak` file, not from the live template, so it has no dependency on the template database.
4. `CreateEmptyAsync()` and every `ExecuteScriptAsync` caller (pre-migration fixtures, idempotency tests) are unchanged.

### 4.2 Lock

- Scope is one template per test process. The template name is unique per process (`TripMate_Template_<guid32>`), so two test processes (for example two local runs on the same SQL Server) never share or collide on a template or a file. No cross-process lock is needed.
- A new process always rebuilds from the current scripts, so a template can never be stale after someone edits a migration.
- Inside a process, xunit v2 runs test classes in parallel, so the first callers race. The build is held in a `Lazy<Task<SqlServerTemplateHandle>>` created with `LazyThreadSafetyMode.ExecutionAndPublication`: exactly one caller starts the build and all others await the same task. No `SemaphoreSlim` and no global test serialization (CI-785 and CI-787 both rejected that).
- If the build fails, the faulted task is kept for the life of the process: every caller fails fast with the same exception instead of repeating a 20 s failure 544 times. Before the fault is published the partial template database is dropped (best effort) so nothing is left behind.
- Restores are not throttled. Concurrency is already bounded by xunit parallelism (one thread per core), and 8 concurrent restores were fine in the measurement. If CI shows restore deadlocks (error 1205), a bounded retry with cleanup of the partial database is a follow-up, with fault injection in the style of CI-785.

### 4.3 Where the `.bak` and data files live

- All files are created and read by the SQL Server process on the SQL Server host. The test process never opens them. In CI that host is the `mssql/server:2022-latest` service container, which has no volumes; the files live in the container's writable layer and disappear when the job ends. Locally it is the SQL Server instance's own disk.
- Directories come from the server, never from constants and never from `Path.Combine` on the test host (the host and the server can run different operating systems):
  `SERVERPROPERTY('InstanceDefaultDataPath')`, `SERVERPROPERTY('InstanceDefaultLogPath')`, `SERVERPROPERTY('InstanceDefaultBackupPath')`. A NULL value fails the build with a clear message.
- Joining: pick the separator from the directory itself (`\` if it contains one, otherwise `/`), trim one trailing separator, add the separator, append the file name. This matters: on the Windows instance used for measuring, data and log paths end with `\` but the backup path does not (`...\MSSQL\Backup`). Concatenating directly wrote to `...\MSSQL\BackupTM_...bak` and failed with "Access is denied".
- Expected values in the Linux container are the documented defaults (`/var/opt/mssql/data/` for data and log, backup folder `/var/opt/mssql/data`). They are not verified on the author's machine. The design does not depend on the literal values, and plan Task 3 adds a probe test that records the real ones on the first CI run.
- File names:

| File | Directory | Name |
|---|---|---|
| Backup | backup path | `TripMate_Template_<guid32>.bak` (unique per process) |
| Clone data | data path | `TripMate_Test_<guid32>.mdf` |
| Clone log | log path | `TripMate_Test_<guid32>_log.ldf` |

- `WITH MOVE` uses the logical names read from `sys.master_files` for the template, not assumed ones. The build fails if the template does not have exactly one `ROWS` and one `LOG` file.
- Single quotes in a server path are doubled when the path is placed in the `RESTORE` text. Database and file names are generated here (`TripMate_*` plus 32 hex characters), never taken from outside.
- The SQL Server service account must be able to write the default folders. It always can for the defaults, so no permission setup is needed.
- No `COMPRESSION` option: it is edition dependent, and the backup of a schema-only database is small anyway. Plan Task 3 records the real size.

### 4.4 Cleanup and `DROP DATABASE`

- Template database: dropped immediately after the `BACKUP` completes (and in the failure path). It is infrastructure, so `TRIPMATE_SQLSERVER_TEST_PRESERVE=1` does not keep it. There is no process-exit hook and no template database to drop at exit.
- Clone databases: `DisposeAsync` is unchanged in behavior: name guard, `SINGLE_USER WITH ROLLBACK IMMEDIATE`, `DROP DATABASE`, bounded retry for deadlock 1205 (CI-785), and `TRIPMATE_SQLSERVER_TEST_PRESERVE=1` still keeps clones. Dropping an online database also deletes its `.mdf` and `.ldf`.
- One addition to the shared drop batch: `ALTER DATABASE ... SET SINGLE_USER` runs only when the database state is `ONLINE`. A restore that failed halfway leaves the database in `RESTORING`, where `ALTER` fails but `DROP` works. The batch must still contain the text `DROP DATABASE [name]` because the CI-785 fault injection matches on it.
- A failed `RESTORE` drops its own partial database before the exception propagates.
- The name guard gets a second form for templates (`TripMate_Template_` + 32 hex characters). A template name can never pass the clone guard and a clone name can never pass the template guard.
- What is not cleaned: the `.bak`. T-SQL cannot delete a single file safely. `xp_delete_file` is undocumented and deletes by folder, extension and age, so on a developer machine it could remove other `.bak` files in the default backup folder; it is rejected. In CI the container is discarded, so nothing remains. On a developer machine one small file stays per test run; `database/README.md` gets a short note on deleting `TripMate_Template_*.bak` by hand. After a killed test process the template database is already gone once the build has finished, so only the `.bak` remains from it; a kill during the template build can also leave the `TripMate_Template_<guid>` database, and a kill mid-test can leave in-flight `TripMate_Test_<guid>` clone databases, which a developer can remove by hand with `DROP DATABASE`.
- Minor side effect: `BACKUP` and `RESTORE` write history rows to `msdb`. Not mitigated.

### 4.5 Behavior change that tests must absorb

- Rows produced by migrations at build time (for example anything using `NEWID()` or the current time) are now identical in every clone instead of differing per database. The full suite is the check.
- `CreateAsync_BootstrapReusesTwoConnectionsEvenWhenOpeningIsRetried` asserts that one `CreateAsync()` opens exactly two connections. After this change that contract belongs to the template build, not to every call. The test is moved to the template class, which is instantiable per test; a new test asserts that a clone from an existing template uses one connection and runs no schema script.

## 5. Alternatives considered

| Option | Why not |
|---|---|
| Persistent template named by script hash, reused across runs, `sp_getapplock` as cross-process lock | Skips even the 20 s build locally, but needs a cross-process lock, file-existence checks (`xp_fileexist` is undocumented) and cleanup of old hashes. More moving parts than the gain justifies; revisit if local run time matters. |
| Detach / copy `.mdf` / attach | Needs file access on the SQL Server host; not possible for a service container from the test process. |
| `CREATE DATABASE ... AS COPY OF`, database snapshots | Azure-only, or read-only. |
| Reuse one database and reset data (Respawn style) | Migration tests need empty or pre-migration schemas, and some tests change schema or database options. Weaker isolation. |
| Shard the job instead | Complementary, not a replacement; each shard would still replay the schema hundreds of times. |

## 6. Acceptance

- TDD: each behavior below has a failing test first (list and names in the plan).
- Clone equals a freshly built database: same ordered `schema.object:type` list and same total row count for user tables.
- Two clones are isolated and drop independently.
- Concurrent first use builds once (`BuildCount == 1`); a broken migration fails every caller with the same error and leaves no template database and no clone.
- Template database is gone after the build; clones restore afterwards.
- Existing fixture tests (CI-785 cleanup, CI-787 connection) pass unchanged except the one moved test.
- Full `Category=SqlServer` suite locally with the real SQL Server: 0 failed, 0 skipped, passed count equals the pre-change count plus the new tests.
- First PR CI run: the `Run SQL Server integration tests without skips` step is much shorter than on the latest `develop` run (target: at least halved; the unmeasured part is test execution itself), with 0 failed and 0 skipped. Record both durations in the PR.

## 7. Risks and open questions

1. Container timings are unmeasured. If restore on the Linux container is much slower than the 0.7 s measured here, gains shrink; the CI run decides.
2. Restore under concurrency could hit deadlock 1205 as `CREATE DATABASE` once did. Accepted for the first iteration (section 4.2); watch at least three CI runs.
3. A clone is as large as the template files (80 MB on the author's instance because `model` is large; much smaller on a fresh container). Peak disk is parallelism times that size.
4. `.bak` leftovers on developer machines (section 4.4). Accepted; documented.
