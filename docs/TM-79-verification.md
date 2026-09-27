# TM-79 verification ledger

Updated: 2026-09-28 (Asia/Ho_Chi_Minh)
Branch: `feature/datmnt-submit-trip-review`

## Approval and approved scope

- Owner explicitly approved TM-79 spec/plan v3 in conversation on 2026-09-27.
- G-SCOPE approved: first delivery supports `commerce.Bookings` only.
  `commercial.ServiceBookings` and standalone no-booking CSP trips are
  deferred; this delivery must not be described as full UC-33 coverage.
- C4 remains separate optional route-pacing and CSP-quality evaluations.
- Backend work is authorized in the approved scope. G-POLICY, G-VISITS and
  G-LEGACY remain evidence gates for dependent behavior; no missing source or
  business rule may be invented to bypass them.
- No commit, push, PR, merge, production DB access, or shared Azure DB writes
  are authorized by this approval.

## Task 0 — baseline and environment

Status: **COMPLETE**. The log-file lock and test-factory teardown are fixed.
The final integration project and two consecutive full solution baselines
passed with SQL integration enabled and no skipped tests. Temporary log roots
and disposable test databases were cleaned. Tasks 1-17 have not started.

### Git and workspace

- Worktree: `D:\CapStone\Capstone_BE_tm79`.
- Branch: `feature/datmnt-submit-trip-review`.
- Before synchronization, local HEAD was `d7269132dab28c36db913a4c6b018a0a4a299156`,
  equal to its merge base with fetched develop and zero commits ahead.
- Fetched develop commit: `525489b61ee2a2791b3d0cf71313e80a4a0445e7`.
- Fast-forwarded safely to that commit; no rebase/reset/stash was used.
- Existing spec, plan, and implementation prompt were preserved. Their current
  status is visible in `git status --short --branch`; no source implementation
  has started.

### SQL Server safety gate

- Initial attempt on 2026-09-27: the test connection was unset. The local TM70
  `.env` named host port `14330`; TCP check to
  `127.0.0.1:14330` failed. The previously observed dedicated TM70 port `14331`
  also failed from this execution environment, including an elevated retry.
- At that initial attempt no SQL connection was attempted and no database was
  created, modified, or dropped.
- Verified on 2026-09-28: container `tripmate-tm70-sql` binds only
  `127.0.0.1:14331 -> 1433`. SQL identity is `88e5c351ed00`, product version
  `16.0.4265.3`, bootstrap database `master`. Before and after the full run,
  only `master`, `model`, `msdb`, and `tempdb` were present.
- Inspected the repository helper: it creates random `TripMate_Test_<GUID>`
  databases and disposes only its generated databases. Real SQL integration
  tests ran with zero skips and the post-run inventory confirmed cleanup.
- The user's terminal environment is not inherited by the agent's shell.
  The verified container password was read into memory and a connection-string
  builder configured `TRIPMATE_SQLSERVER_TEST_CONNECTION` only in the test
  shell. A finally block cleared it and the temporary credential references.
  Credentials were not printed, persisted, or committed.

### Commands and results

| Command | Result |
| --- | --- |
| `dotnet restore TripMate.slnx` (sandboxed attempt) | Failed: NuGet network access denied (`NU1301`); no code/test result. |
| `dotnet restore TripMate.slnx` (approved elevated retry) | Exit 0; all solution projects restored. |
| `dotnet build TripMate.slnx -c Release --no-restore` | Exit 0; 0 warnings, 0 errors. |
| `dotnet test TripMate.slnx -c Release --no-build --no-restore --logger "console;verbosity=minimal"` | Interrupted after prolonged integration-test execution; no solution-wide final summary/exit code. Application unit tests: 596 passed, 0 failed, 0 skipped. Infrastructure unit tests: 73 passed, 0 failed, 0 skipped. SQL Server-dependent integration tests shown in output were skipped because the test connection is unset. Integration-test project final count is unknown. |
| `git diff --check` | No whitespace diagnostics for tracked files; Git printed warnings that user global ignore config could not be read. |

The first interrupted test run is retained as historical evidence above.

### Completed SQL-enabled baseline — 2026-09-28

- Command: `dotnet test TripMate.slnx -c Release --no-build --no-restore
  --logger "console;verbosity=minimal" --blame-hang-timeout 3m`.
- Exit 1. Application: 596 passed, 0 failed, 0 skipped. Infrastructure:
  73 passed, 0 failed, 0 skipped. API integration: 361 passed, 3 failed,
  0 skipped. Total: **1,030 passed, 3 failed, 0 skipped**.
- The three failures are `SignOutIntegrationTests.
  Post_WebLogout_SaveChangesThrows_Returns500PreservesCookieAndDoesNotLogSecrets`,
  `LogSanitizationTests.AuthTraffic_NeverLogsPasswordsOrTokens`, and
  `LogSanitizationTests.SuccessfulSignOut_NeverLogsAuthenticationSecrets`.
  Each fails at `File.Delete` because `logs/uc04-s11.log` is in use. No TM-79
  implementation code or tests have been introduced.
- Fresh-process diagnostic command: `dotnet test
  tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj
  -c Release --no-build --no-restore --filter
  "FullyQualifiedName~LogSanitizationTests|FullyQualifiedName~Post_WebLogout_SaveChangesThrows_Returns500PreservesCookieAndDoesNotLogSecrets"
  --logger "console;verbosity=minimal" --blame-hang-timeout 2m`.
  Exit 0: 3 passed, 0 failed, 0 skipped. This establishes a suite-interaction
  failure, not a passing whole-solution baseline. The exact log-handle owner
  has not been established; no unrelated auth/logging code was modified.
- Source re-audit found Review aggregate readers in POI detail/explore, no
  Review insertion writer in application features, no mapped Booking DbSet,
  and no mapped historical Visited status in ItineraryItem. G-VISITS remains
  unresolved. The isolated container has no real legacy business sample,
  so G-LEGACY remains unverified rather than assuming no legacy reviews exist.

## Open evidence gates

- G-POLICY: select and verify the production content-screening policy before
  implementing Task 7. Test doubles alone do not satisfy this gate.
- G-VISITS: no authoritative, booking-attributable historical POI visit source
  has yet been confirmed. POI evidence schema/acceptance must not be frozen
  around a guessed source.
- G-LEGACY: inspect authorized non-production legacy-review samples to resolve
  within-seven-day edit behavior and ambiguous duplicate handling. No
  production scan or automatic data repair is permitted.

## Task 0 — test logging isolation correction, 2026-09-28

### Root cause and deterministic reproduction

The three failures named in the completed baseline all called `File.Delete`
on the same `logs/uc04-s11.log` before starting their own factory. That file is
configured by `src/TripMate.Api/appsettings.Testing.json` for every Testing host.
`TripMateApiFactory` had no log-path override and did not isolate the Serilog
provider from the process-wide `Log.Logger` used by Program's host registration.

Most HTTP tests share `[Collection(nameof(TripMateApiFactory))]`, but
`SearchSelectablePoisSqlServerTests` has no such collection and its endpoint
test holds a real factory open. Thus collection-level serialization did not
prevent overlap with the auth test collection. Serilog's `shared: true` allows
writers to share the file but does not grant permission to delete an open file
on Windows. The failure does not require a stale process or an undisposed
factory: holding a correctly scoped factory alive while invoking the original
auth-sanitization test reproduces it in a fresh test process, without SQL or
scheduler timing.

The default Serilog host registration also controls disposal through the
static logger. A later live host can replace that global reference; explicit
host-owned loggers remove that cross-host ownership ambiguity. This behavior
was checked against the installed Serilog.Extensions.Hosting 10.0.0 XML docs
and its [service-registration source](https://github.com/serilog/serilog-extensions-hosting/blob/v10.0.0/src/Serilog.Extensions.Hosting/SerilogServiceCollectionExtensions.cs).

### Files changed and retained boundaries

- `tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs`:
  each Testing host gets an absolute path under a generated per-factory temp
  directory and a generated per-host subdirectory, including derived hosts.
  Test-only `AddSerilog(... preserveStaticLogger: true)` binds the logger's
  lifetime to that host's DI container. Sync/async factory disposal closes
  hosts and derived factories before deleting its own generated temp tree.
- `tests/TripMate.Api.IntegrationTests/Infrastructure/FactoryLoggingIsolationTests.cs`:
  deterministic overlap reproducer, independent log-content assertions,
  sync/async disposal and cleanup checks, and parent/derived-host isolation.
- `tests/TripMate.Api.IntegrationTests/Authentication/LogSanitizationTests.cs`
  and `Authentication/SignOutIntegrationTests.cs`: read the originating
  factory's log file; removed shared-file reset/delete. All existing request,
  status, cookie/session, non-vacuous logging and secret-exclusion assertions
  remain intact. Existing bounded flush-read loops are unchanged.
- Documentation updated only in `docs/TM-79-verification.md`. The existing
  untracked spec, plan and implementation prompt were preserved.

No production logging/configuration, business logic, SQL schema, collection
membership, assembly-wide parallelism, test skips or retry-on-failure policy
was changed. No commit, push or PR action was performed. Task 1 was not started.

### Commands and intermediate results

All test commands run from `D:\CapStone\Capstone_BE_tm79`. Permission to use
local TestServer sockets was granted to the test processes. SQL configuration
for broad checks is read from the verified dedicated local container into the
test shell only, and removed in a finally block.

| Check | Exact command | Exit / counts |
| --- | --- | --- |
| Deterministic RED, before infrastructure fix | `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --no-restore --filter FullyQualifiedName~FactoryLoggingIsolationTests --logger 'console;verbosity=minimal'` | Exit 1; 0 passed, 3 failed, 0 skipped. One failure is the exact original IOException at auth `ResetLogFile`; two show that independently live hosts receive the same log path. |
| First implementation build | Same focused command above | Exit 1; compile error CS1929 from the missing test-only `Microsoft.Extensions.Hosting` import. No tests executed; import corrected. |
| First compiled implementation | Same focused command above | Exit 1; 1 passed, 2 failed, 0 skipped. The overlap/old lock reproducer passed. The two new verification cases initially used `File.ReadAllText` while their own sink was live; corrected their inspection reader to `FileShare.ReadWrite`, matching the existing auth tests. No failure was ignored. |
| Focused GREEN before adding derived-host coverage | Same focused command above | Exit 0; 3 passed, 0 failed, 0 skipped. |
| Final focused logging regressions | Same focused command above | Exit 0; 4 passed, 0 failed, 0 skipped. Includes the added derived-host case. |
| Minimal reproducer repeated three independent runs | `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~LiveFactory_DoesNotInterfereWithAuthLogSanitization --logger 'console;verbosity=minimal'` | Each exit 0; each 1 passed, 0 failed, 0 skipped. The loop stops on any failure; these are reproducibility checks, not retries. |
| Three original affected tests together | Exact command in the code block below. | Exit 0; 3 passed, 0 failed, 0 skipped. |
| Final Release build | `dotnet build TripMate.slnx -c Release --no-restore` | Exit 0; 0 warnings, 0 errors. |

Format commands used the exact four changed test C# paths listed above via
`$loggingFiles`: `dotnet format TripMate.slnx --no-restore --include $loggingFiles`,
then `dotnet format TripMate.slnx --verify-no-changes --no-restore --include $loggingFiles`.
Both exited 0. A read-only independent reviewer completed spec-compliance and
code-quality passes with no actionable findings; broad SQL-enabled verification
is still required below.

Exact affected-test command:

```powershell
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --no-build --no-restore --filter 'FullyQualifiedName~LogSanitizationTests|FullyQualifiedName~Post_WebLogout_SaveChangesThrows_Returns500PreservesCookieAndDoesNotLogSecrets' --logger 'console;verbosity=minimal'
```

Exact scoped formatting commands:

```powershell
$loggingFiles = @(
  'tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs',
  'tests/TripMate.Api.IntegrationTests/Infrastructure/FactoryLoggingIsolationTests.cs',
  'tests/TripMate.Api.IntegrationTests/Authentication/LogSanitizationTests.cs',
  'tests/TripMate.Api.IntegrationTests/Authentication/SignOutIntegrationTests.cs'
)
dotnet format TripMate.slnx --no-restore --include $loggingFiles
dotnet format TripMate.slnx --verify-no-changes --no-restore --include $loggingFiles
```

### Broad baseline acceptance

Integration-project command: `dotnet test
tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release
--no-build --no-restore --logger 'console;verbosity=minimal' --blame-hang-timeout 3m`.
Final exit 0: **368 passed, 0 failed, 0 skipped**, duration 2m48s.

Initial two consecutive full solution runs each finished with exit 0:
**1037 passed, 0 failed, 0 skipped** (596 application, 73 infrastructure,
368 API integration). Final teardown then identified two temporary log roots
per broad run, from `ConcurrentConfirm_WithIndependentScopes_AllowsExactlyOneSuccess`
and `ForcedPasswordHashFailure_RollsBackPasswordAndTokenMutation` in
`tests/TripMate.Api.IntegrationTests/Authentication/PasswordResetSqlServerTests.cs`.
They disposed the derived `WithWebHostBuilder` factory but not its original
parent owning the log root. Both now use explicit parent and derived `using`
scopes, closing the host before deleting its logs. Assertions are unchanged.
This is the fifth changed test C# file. Six identified generated log directories
from these earlier runs were removed; no user or database data was removed.

Additional commands: scoped `dotnet format TripMate.slnx --no-restore --include
tests/TripMate.Api.IntegrationTests/Authentication/PasswordResetSqlServerTests.cs`
and the same command with `--verify-no-changes` both exit 0. Rebuilt solution:
exit 0, 0 warnings, 0 errors. Password-reset SQL filter:
`dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj
-c Release --no-build --no-restore --filter FullyQualifiedName~PasswordResetSqlServerTests
--logger 'console;verbosity=minimal'`: exit 0; **3 passed, 0 failed, 0 skipped**.
Independent read-only lifecycle review passed without findings.

Final integration-project check: exit 0; **368 passed, 0 failed, 0 skipped**,
duration 2m49s. Final full solution run 1: exit 0; **1037 passed, 0 failed,
0 skipped**, API duration 2m43s. Final full solution run 2: exit 0;
**1037 passed, 0 failed, 0 skipped**, API duration 2m45s. The encompassing
verification shell finished with exit 0; every command had a final result.
These were independent consecutive checks, not retries after a failure.
Full command: `dotnet test TripMate.slnx -c Release --no-build --no-restore
--logger 'console;verbosity=minimal' --blame-hang-timeout 3m`.

Final teardown: generated test log directory count **0**. Read-only SQL inventory
contains only `master`, `model`, `msdb`, `tempdb`; no `TripMate_Test_*` database
remains. The isolated local SQL target was `127.0.0.1,14331`, container
`tripmate-tm70-sql`, server identity `88e5c351ed00`. No Azure/shared DB was used.

`git -c safe.directory=D:/CapStone/Capstone_BE_tm79 diff --check`: exit 0,
no whitespace errors. `git -c safe.directory=D:/CapStone/Capstone_BE_tm79
status --short --branch`: exit 0. Final status:

```text
## feature/datmnt-submit-trip-review...origin/develop
 M tests/TripMate.Api.IntegrationTests/Authentication/LogSanitizationTests.cs
 M tests/TripMate.Api.IntegrationTests/Authentication/PasswordResetSqlServerTests.cs
 M tests/TripMate.Api.IntegrationTests/Authentication/SignOutIntegrationTests.cs
 M tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs
?? docs/TM-79-implementation-prompt.md
?? docs/TM-79-verification.md
?? plans/TM-79-plan.md
?? specs/TM-79-spec.md
?? tests/TripMate.Api.IntegrationTests/Infrastructure/FactoryLoggingIsolationTests.cs
```

The pre-existing spec, plan and implementation prompt remain unchanged.
Only the verification ledger was updated among documents. Task 0: COMPLETE.
Task 1 was not started. No commit or push was performed.

## Task 1 — freeze scope, DTO and acceptance matrix (2026-09-28)

Status: **COMPLETE for the approved commerce.Bookings Backend contract**.
Task 0 remains COMPLETE; Tasks 2–17 have not started. This is a
documentation-only contract step, not evidence that endpoints/schema exist.

Previous HEAD: `5c035f7e4f03df5bc9c4a050fb910b4248ea0702`.
Fetched `origin/develop`: `4afd56cabcda7dbbd83062086121d32e77848885`;
merge base: `525489b61ee2a2791b3d0cf71313e80a4a0445e7`.
No rebase/merge/reset/force push was used. The develop delta adds TM-207 Tour
media. Its safe image inspector has a 24,000,000-pixel area limit and RGBA8888
decode; Task 1 records that resource ceiling but keeps the distinct 5,000,000
encoded-byte review limit. This feature branch does not yet contain TM-207
code, so Task 8 must reconcile the actual implementation base.

Changed this Task: `docs/TM-79-api-contract.md` (new),
`plans/TM-79-plan.md` (stale progress footer only), and this ledger.
Pre-existing untracked `docs/TM-79-implementation-prompt.md` is untouched.
No production source, SQL, tests, or shared/Azure DB changed. The central Docs
repo is outside this Backend worktree; the exact dedicated-branch handoff
target/delta is recorded in the contract, not written under a fake path here.

Frozen: three route methods; commerce booking identity; subject/owner and
new/legacy/none discriminators; direct DTO fields/nullability and reasons;
multipart create and closed JSON edit; exact pacing wire values; text
normalization; independent C4/POI eligibility; image/transport/resource limits;
consent/snapshot/fallback; new+legacy duplicate recovery; Base64 SQL rowversion;
exclusive original seven-day deadline; feature ProblemDetails codes; Tour/POI
aggregate formulas; OpenAPI target; requirement-to-test matrix for Tasks 2–13
and client handoff. No Task 2 schema was created or guessed.

Open gates: G-SCOPE approved (commerce.Bookings subset); G-POLICY open before
Task 7, including unsupported-language disposition; G-VISITS open for real
booking-attributable historical Visited evidence, with POI capability
unavailable meanwhile; G-LEGACY open for authorized samples and within-window
edit disposition. Route pacing remains unavailable for schedule-only bookings
without authoritative booking-route provenance. Central Docs publication is a
cross-repo dependency.

Verification: manually compared each D1–D7 and plan Task 1 requirement with
the contract; checked the current Booking/Review/Itinerary SQL/domain shapes,
TM-207 media inspector, and existing direct-DTO/ProblemDetails conventions.
Checked PUT's exact five-member set including null/empty/unchanged immutable
payloads, UTF-16 post-trim lengths, independent route/CSP/POI capabilities,
and separate unresolved gates. Independent spec review found a reason
precedence ambiguity when multiple GET ineligibility conditions coexist;
the contract now defines deterministic precedence and distinguishes ordinary
legacy duplicate from ambiguous legacy conflict. No application tests are
claimed or fabricated for this docs-only Task 1.

Independent two-stage review: spec compliance PASS and document quality PASS,
no remaining Critical or Minor finding. The reviewer also required explicit
booking-status wire values; all five SQL values are now enumerated. Exact
checks: `git -c safe.directory=D:/CapStone/Capstone_BE_tm79 diff --check`
(exit 0), plus a PowerShell `Select-String -Pattern '[\t ]+$'` scan of the
new contract, plan and ledger (exit 0, no trailing whitespace). No test
command was run for this documentation-only change; Task 0's full SQL-enabled
baseline remains recorded above. Task 2 was not started.
