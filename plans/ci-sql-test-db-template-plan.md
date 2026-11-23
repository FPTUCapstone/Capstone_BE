# CI SQL Test Database Template Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the SQL Server test schema once per test process and give every `SqlServerTestDatabase.CreateAsync()` caller its own database by `RESTORE`, cutting the SQL suite's bootstrap cost from about 20 s per test to under 1 s.

**Architecture:** A per-process `SqlServerTemplateDatabase` builds a throwaway `TripMate_Template_<guid32>` database (schema + migrations, one connection), backs it up to a `.bak` on the SQL Server host, drops it, and restores clones from the `.bak`. A `Lazy<Task>` with `ExecutionAndPublication` makes the build single-flight. Paths come from `SERVERPROPERTY` and are joined with the server's separator.

**Tech Stack:** .NET 10, xunit 2.9.3, FluentAssertions 7.2.0, Microsoft.Data.SqlClient, SQL Server 2022.

**Spec:** `specs/ci-sql-test-db-template-spec.md`

## Global Constraints

- Test-fixture change only. No change under `src/`, `database/schema` or `database/migrations`, and no change to `.github/workflows`.
- Work only in worktree `D:\Capstone_Project_Production\_wt\Capstone_BE_ci_sql_template`, branch `chore/ci-sql-test-db-template`. Never touch the main checkout `Capstone_BE`. Before every commit, `git status --short` must list only paths from this plan; no TM-300 path (`database/seeds/*`, `specs/TM-300-spec.md`, `plans/TM-300-plan.md`, `tools/poi-seed/`). No push, no PR until asked.
- C# files: file-scoped namespaces, 4-space indent, **no final newline** (`.editorconfig` `insert_final_newline = false` for `*.cs`; CI fails with FINALNEWLINE otherwise). Before each commit run `dotnet format TripMate.slnx --verify-no-changes --no-restore --include <changed .cs files>`.
- Database names stay `TripMate_Test_` + 32 hex for clones; templates use `TripMate_Template_` + 32 hex. Collation `Vietnamese_100_CI_AS`. Pooling stays disabled on every fixture connection.
- Keep CI-785 and CI-787 behavior: guarded drop with `SINGLE_USER WITH ROLLBACK IMMEDIATE`, deadlock-1205 retry (3 attempts, 200/500 ms), `TRIPMATE_SQLSERVER_TEST_PRESERVE=1` keeps clones, connection-open retry only before a command runs. The drop batch keeps the literal text `DROP DATABASE [name]`.
- SQL tests carry `[SqlServerFact]` / `[SqlServerTheory]` plus `[Trait("Category", "SqlServer")]`. Pure unit tests carry neither category.
- Local SQL: `$env:TRIPMATE_SQLSERVER_TEST_CONNECTION = "Server=localhost;Database=master;Integrated Security=True;Encrypt=False;TrustServerCertificate=True"`. Without it SQL tests skip silently; a green run without it proves nothing.
- Commit message ends with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- When concatenating text from `sys.*` columns in test SQL, add `COLLATE DATABASE_DEFAULT` (a server with a different default collation raises error 451).

## Review Focus

1. Backup folder returned without a trailing separator (Windows) or with `/` separators (Linux): the `.bak` must land in the backup folder. Pinned in Task 1.
2. Many test classes calling `CreateAsync()` at once on a cold process: exactly one build. Pinned in Task 3.
3. A broken migration: every waiting caller fails fast with the same error, and no template database or clone remains. Pinned in Task 3.
4. A restore that stops halfway leaves a `RESTORING` database: cleanup must still drop it. Pinned in Task 4.
5. A clone silently differing from a freshly built database (objects or seeded rows). Pinned in Task 4.

## File Structure

- Create `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerServerPath.cs`: joins a server directory and a file name with the server's separator.
- Create `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerManagedNames.cs`: generates and validates the two database-name forms.
- Create `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerTemplateDatabase.cs`: template build, single-flight lock, clone restore.
- Modify `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerTestDatabase.cs`: use the helpers, extract schema apply and drop, switch `CreateAsync()` to clones.
- Modify `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerTestDatabaseConnectionTests.cs`: retarget one test.
- Create tests: `Infrastructure/SqlServerServerPathTests.cs`, `Infrastructure/SqlServerManagedNamesTests.cs`, `Infrastructure/SqlServerTemplateDatabaseTests.cs`, `Infrastructure/SqlServerTemplateCloneTests.cs`.
- Modify `database/README.md`: short note on test templates and `.bak` leftovers (Vietnamese, matching the file).

---

### Task 1: Server path join and managed database names

**Files:**
- Create: `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerServerPath.cs`, `SqlServerManagedNames.cs`
- Test: `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerServerPathTests.cs`, `SqlServerManagedNamesTests.cs`

**Interfaces:**
- Produces:
  - `internal static class SqlServerServerPath { internal static string Combine(string directory, string fileName); }`
  - `internal static class SqlServerManagedNames { internal const string TestPrefix = "TripMate_Test_"; internal const string TemplatePrefix = "TripMate_Template_"; internal static string NewTestName(); internal static string NewTemplateName(); internal static bool IsTestName(string name); internal static bool IsTemplateName(string name); }`

- [ ] **Step 1: Write the failing tests** (no `Category` trait)

```csharp
public sealed class SqlServerServerPathTests
{
    [Theory]
    [InlineData(@"C:\SQL\Data\", "a.mdf", @"C:\SQL\Data\a.mdf")]
    [InlineData(@"C:\SQL\Backup", "a.bak", @"C:\SQL\Backup\a.bak")]
    [InlineData("/var/opt/mssql/data/", "a.mdf", "/var/opt/mssql/data/a.mdf")]
    [InlineData("/var/opt/mssql/data", "a.bak", "/var/opt/mssql/data/a.bak")]
    [InlineData(@"C:\", "a.bak", @"C:\a.bak")]
    [InlineData("/", "a.bak", "/a.bak")]
    public void Combine_UsesTheSeparatorOfTheServerPath(string directory, string file, string expected) =>
        SqlServerServerPath.Combine(directory, file).Should().Be(expected);
}

public sealed class SqlServerManagedNamesTests
{
    [Fact]
    public void NewNames_PassOnlyTheirOwnGuard()
    {
        var test = SqlServerManagedNames.NewTestName();
        var template = SqlServerManagedNames.NewTemplateName();
        SqlServerManagedNames.IsTestName(test).Should().BeTrue();
        SqlServerManagedNames.IsTemplateName(test).Should().BeFalse();
        SqlServerManagedNames.IsTemplateName(template).Should().BeTrue();
        SqlServerManagedNames.IsTestName(template).Should().BeFalse();
    }

    [Theory]
    [InlineData("TripMate_Test_")]                                  // prefix only
    [InlineData("TripMate_Test_0123456789abcdef0123456789abcde")]   // 31 chars
    [InlineData("TripMate_Test_0123456789abcdef0123456789abcdef0")] // 33 chars
    [InlineData("TripMate_Test_0123456789abcdef0123456789abcde]")]  // bracket
    [InlineData("TripMateDb")] [InlineData("master")] [InlineData("")]
    public void IsTestName_RejectsAnythingButPrefixPlus32SafeCharacters(string name) =>
        SqlServerManagedNames.IsTestName(name).Should().BeFalse();
    [Fact]
    public void IsTemplateName_RejectsATestName() =>
        SqlServerManagedNames.IsTemplateName("TripMate_Test_0123456789abcdef0123456789abcdef").Should().BeFalse();
}
```

- [ ] **Step 2: Run to verify failure**
Run: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SqlServerServerPathTests|FullyQualifiedName~SqlServerManagedNamesTests"`
Expected: build error, the two types do not exist.

- [ ] **Step 3: Implement both classes.** `Combine`: separator is `\` when `directory` contains one, else `/`; trim one trailing `\` or `/`; return `directory + separator + fileName`. `IsTestName` keeps the exact rule of today's `EnsureSafeDatabaseName` (prefix, total length prefix+32, only ASCII letters, digits, `_`); `IsTemplateName` is the same rule with `TemplatePrefix`. `New*Name` is prefix + `Guid.NewGuid().ToString("N")`.

- [ ] **Step 4: Run to verify pass.** Same command. Expected: all pass.

- [ ] **Step 5: Format check and commit**
```bash
dotnet format TripMate.slnx --verify-no-changes --no-restore --include <the four new .cs files>
git add tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerServerPath*.cs tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerManagedNames*.cs
git commit -m "test(ci): add server path join and managed database name guards"
```

### Task 2: Extract shared fixture helpers (no behavior change)

**Files:**
- Modify: `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerTestDatabase.cs` (`InitializeAsync` lines 249-284, `DisposeAsync` 207-247, `EnsureSafeDatabaseName` 343-353, `FindRepositoryRoot` 355-369)

**Interfaces:**
- Consumes: `SqlServerManagedNames` from Task 1.
- Produces, all on `SqlServerTestDatabase`:
  - `internal static Task ApplyCurrentSchemaAsync(string databaseConnectionString, string schemaPath, string migrationsDirectory)`: opens one connection, runs the schema file, then every `*.sql` in the directory ordered by ordinal file name; same SQL and same `QUOTED_IDENTIFIER ON` handling as today.
  - `internal static Task DropDatabaseAsync(string masterConnectionString, string databaseName)`: the existing guarded drop batch and 1205 retry loop, unchanged; accepts a name only if `IsTestName` or `IsTemplateName`, otherwise throws `InvalidOperationException`.
  - `internal static string DefaultSchemaPath()` (`AppContext.BaseDirectory/Database/tripmate_schema_v7.sql`) and `internal static string DefaultMigrationsDirectory()` (`FindRepositoryRoot()/database/migrations`).

- [ ] **Step 1: Baseline.** Run the existing fixture tests with the local env var set: `dotnet test ... --filter "FullyQualifiedName~SqlServerTestDatabase"`. Expected: 11 passed, 0 failed, 0 skipped (already observed on 2026-11-23).

- [ ] **Step 2: Refactor.** Move the schema/migration loop and the drop loop into the two static methods, have `InitializeAsync` and `DisposeAsync` call them, switch the guard to `SqlServerManagedNames`. `DisposeAsync` still honors `TRIPMATE_SQLSERVER_TEST_PRESERVE` and `_databaseCreated`. Do not change any SQL text.

- [ ] **Step 3: Verify nothing moved.** Re-run the 11 tests plus consumers: `--filter "FullyQualifiedName~SqlServerTestDatabase|FullyQualifiedName~AuditMigrationSqlServerTests|FullyQualifiedName~PasswordResetSqlServerTests|FullyQualifiedName~TourCategoryMigrationTests"`. Expected: all pass, 0 skipped.

- [ ] **Step 4: Format check and commit** (`git add` only `SqlServerTestDatabase.cs`; message `refactor(test): extract schema apply and guarded drop from SqlServerTestDatabase`).

### Task 3: Template build with single-flight lock

**Files:**
- Create: `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerTemplateDatabase.cs`
- Test: `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerTemplateDatabaseTests.cs`

**Interfaces:**
- Consumes: Tasks 1-2.
- Produces:
  - `internal sealed record SqlServerTemplateHandle(string BackupPath, string DataLogicalName, string LogLogicalName, string DataDirectory, string LogDirectory);`
  - `internal sealed class SqlServerTemplateDatabase` with `internal SqlServerTemplateDatabase(string configuredConnectionString, string schemaPath, string migrationsDirectory)`, `internal static SqlServerTemplateDatabase Shared { get; }` (created lazily from `TRIPMATE_SQLSERVER_TEST_CONNECTION`; throws the existing "was removed after test discovery" `InvalidOperationException` when unset), `internal int BuildCount { get; }`, `internal string? TemplateName { get; }`, `internal Task<SqlServerTemplateHandle> GetHandleAsync()`.
  - `internal static (string Data, string Log) PickLogicalNames(IReadOnlyList<(string Name, string TypeDesc)> files)`: throws `InvalidOperationException` unless there is exactly one `ROWS` and one `LOG` entry.

- [ ] **Step 1: Write the failing tests** in `SqlServerTemplateDatabaseTests` (each SQL test builds its own instance with `DefaultSchemaPath()` / `DefaultMigrationsDirectory()` unless noted; the class takes `ITestOutputHelper`):
  - `GetHandleAsync_ConcurrentCallers_BuildTheTemplateOnce`: 8 parallel `GetHandleAsync()` calls; `BuildCount.Should().Be(1)`; all 8 results equal.
  - `GetHandleAsync_AfterBuild_LeavesNoTemplateDatabase`: after the call, `DB_ID(TemplateName)` is NULL on master; handle fields are non-empty.
  - `GetHandleAsync_BrokenMigration_FailsEveryCallerAndLeavesNothingBehind`: migrations directory is a temp folder with one file `0001_broken.sql` containing `THROW 51001, 'broken', 1;`; 4 parallel callers each fail with `SqlException` number 51001; `BuildCount == 1`; no database named `TemplateName` exists; a later call fails with the same error without a new build.
  - `GetHandleAsync_ReportsServerPaths`: writes `BackupPath`, `DataDirectory`, `LogDirectory` to `ITestOutputHelper`; asserts `BackupPath` ends with `.bak` and starts with the backup directory the server reports (a green CI run proves the paths work; to read the actual Linux container values, run this test in a CI debug re-run or locally against a Linux SQL Server with `--logger "console;verbosity=detailed"`).
  - `BuildAsync_UsesTwoConnectionsEvenWhenOpeningIsRetried`: the body of the old `CreateAsync_BootstrapReusesTwoConnectionsEvenWhenOpeningIsRetried` (probe with `openFailures: 1`) pointed at a fresh instance's `GetHandleAsync()`; expects exactly 2 distinct connections and more than 2 open attempts. Copy the `ConnectionProbe` class into a shared internal file if reuse needs it, or move it out of the connection-tests class.
  - Unit (no category): `PickLogicalNames_OneDataAndOneLog_ReturnsThem`; `PickLogicalNames_TwoDataFiles_Throws`; `PickLogicalNames_NoLogFile_Throws`.

- [ ] **Step 2: Run to verify failure** (`--filter "FullyQualifiedName~SqlServerTemplateDatabaseTests"`). Expected: build error, type missing.

- [ ] **Step 3: Implement `SqlServerTemplateDatabase`.** Build order, all on non-pooled connections with the existing open-retry helper: read the three `SERVERPROPERTY` paths (fail with a clear message on NULL) → `CREATE DATABASE [TemplateName] COLLATE Vietnamese_100_CI_AS` → `ApplyCurrentSchemaAsync` → read logical names from `sys.master_files` through `PickLogicalNames` → `BACKUP DATABASE ... TO DISK = N'<SqlServerServerPath.Combine(backupDir, TemplateName + ".bak")>' WITH INIT` → `DropDatabaseAsync(template)`. A failure after the `CREATE` drops the template (best effort) before rethrowing. The handle task is a `Lazy<Task<SqlServerTemplateHandle>>(LazyThreadSafetyMode.ExecutionAndPublication)`; `BuildCount` increments once inside the factory; a faulted task stays cached. Escape `'` as `''` in any server path placed in SQL text.

- [ ] **Step 4: Run to verify pass**, then record the `.bak` size reported by the probe test output for Task 5.

- [ ] **Step 5: Format check and commit** (`SqlServerTemplateDatabase.cs`, its test file, and the connection-test file only if `ConnectionProbe` was moved; message `test(ci): build the SQL test schema once into a backup template`).

### Task 4: Restore clones and switch `CreateAsync()`

**Files:**
- Modify: `SqlServerTemplateDatabase.cs` (add clone restore), `SqlServerTestDatabase.cs` (`CreateAsync()` at lines 65-68, `InitializeAsync`), `SqlServerTestDatabaseConnectionTests.cs` (remove the old first test, lines 11-25)
- Test: `tests/TripMate.Api.IntegrationTests/Infrastructure/SqlServerTemplateCloneTests.cs`

**Interfaces:**
- Consumes: Task 3 (`GetHandleAsync`, `SqlServerTemplateHandle`).
- Produces:
  - `internal Task SqlServerTemplateDatabase.RestoreCloneAsync(string cloneName)`: on one master connection, `RESTORE DATABASE [cloneName] FROM DISK = N'<BackupPath>' WITH MOVE N'<DataLogicalName>' TO N'<Combine(DataDirectory, cloneName + ".mdf")>', MOVE N'<LogLogicalName>' TO N'<Combine(LogDirectory, cloneName + "_log.ldf")>'`; no retry.
  - `internal static Task<SqlServerTestDatabase> SqlServerTestDatabase.CreateAsync(SqlServerTemplateDatabase template)`; the existing public `CreateAsync()` becomes `CreateAsync(SqlServerTemplateDatabase.Shared)`. `_databaseCreated` is set to true before the `RESTORE` is issued so a failed restore is dropped by `DisposeAsync`. `CreateEmptyAsync()` is untouched.
  - The shared drop batch runs `ALTER DATABASE ... SET SINGLE_USER WITH ROLLBACK IMMEDIATE` only when `sys.databases.state = 0` (ONLINE), still followed by `DROP DATABASE [name]`.

- [ ] **Step 1: Write the failing tests** in `SqlServerTemplateCloneTests` (SQL category; one class-level built template shared through `IClassFixture` to avoid extra `.bak` files):
  - `CreateAsync_Clone_MatchesAFreshlyBuiltDatabase`: clone from the fixture template vs a database made with `CreateEmptyAsync()` + `ApplyCurrentSchemaAsync`; compare the ordered list of `s.name + '.' + o.name + ':' + o.type` for `is_ms_shipped = 0` objects and the sum of `row_count` over user-table partitions (`index_id IN (0,1)`); both equal.
  - `CreateAsync_Clones_AreIsolatedAndDropIndependently`: insert a row in clone A, clone B has none; dispose A; B still answers `SELECT 1`; A's database no longer exists.
  - `CreateAsync_FromBuiltTemplate_UsesOneConnectionAndRunsNoScript`: after `GetHandleAsync()` completes, attach `ConnectionProbe`, call `CreateAsync(template)`; `DistinctConnections == 1` and `template.BuildCount == 1`.
  - `DropDatabaseAsync_DatabaseLeftInRestoringState_StillDropsIt`: using the fixture handle, issue a `RESTORE ... WITH MOVE ..., NORECOVERY` for a new `TripMate_Test_` name by hand, confirm `state_desc = 'RESTORING'`, call `SqlServerTestDatabase.DropDatabaseAsync`; the database is gone.

- [ ] **Step 2: Run to verify failure.** Expected: the overload and `RestoreCloneAsync` do not exist; the RESTORING test would also fail on the current `ALTER`.

- [ ] **Step 3: Implement** the three items above. Delete `CreateAsync_BootstrapReusesTwoConnectionsEvenWhenOpeningIsRetried` from the connection tests (its contract now lives in Task 3's `BuildAsync_UsesTwoConnectionsEvenWhenOpeningIsRetried`).

- [ ] **Step 4: Run to verify pass:** the new class, then `--filter "FullyQualifiedName~SqlServerTestDatabase|FullyQualifiedName~SqlServerTemplate"`. Expected: all pass; the CI-785 cleanup tests and the remaining CI-787 tests pass unchanged.

- [ ] **Step 5: Format check and commit** (message `perf(test): restore SQL test databases from a per-process template`).

### Task 5: Docs, full verification, timing record

**Files:**
- Modify: `database/README.md` (new short section, Vietnamese)

- [ ] **Step 1: README note.** Explain in a few lines: SQL tests build a `TripMate_Template_*` database once per run, back it up and drop it; the backup file `TripMate_Template_<guid>.bak` stays in the SQL Server's default backup folder (`SERVERPROPERTY('InstanceDefaultBackupPath')`) on developer machines and can be deleted by hand; in CI it vanishes with the container. State the size measured in Task 3.

- [ ] **Step 2: Full local suite.** With the env var set: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "Category=SqlServer"`. Expected: Failed 0, Skipped 0, Passed equal to the pre-change count plus the new SQL tests. Obtain the pre-change count from the latest green `develop` run (`gh run view <id> --log`, line `SQL tests: N passed`). Also run `dotnet test TripMate.slnx -c Release --no-restore --filter "Category!=SqlServer&Category!=Redis"` and expect 0 failed.

- [ ] **Step 3: Record before/after.** Before: `gh run list --workflow "Backend tests" --branch develop --limit 5 --json databaseId,conclusion,createdAt`, then step times with `gh run view <id> --json jobs`. After: the same for the PR run once opened. Put both `Run SQL Server integration tests without skips` durations and the Linux container paths from the probe output into the PR description.

- [ ] **Step 4: Commit** the README (`docs(test): note SQL test template backup files`), then stop: ask the user whether to push and open a PR, merge, keep or discard (CLAUDE.md "Finishing the Branch").
