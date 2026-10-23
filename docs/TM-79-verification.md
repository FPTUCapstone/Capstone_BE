# TM-79 verification ledger

## 2026-10-09 — Backend R9 remediation closure

Status: **R1–R7 COMPLETE AND COMMITTED LOCALLY; R9 COMPLETE IN THIS LOCAL
CHECKPOINT COMMIT; R8 REMAINS A TM-78 CONTRACT DEPENDENCY**. This entry is the current
Backend result and supersedes older checkpoint language below without erasing
its historical test evidence. Mobile and Web remain frozen.

The committed starting checkpoint for R9 was
`dea9beaf8a863ff4d5d1e297944fe953cd885615`, after the local R1–R7 commits.
R9 closes the frozen contract as follows:

- `ReviewableRecordRef` remains `(kind, id)` with distinct
  `commerceBooking` and `serviceBooking` namespaces. The service route resolves
  only authoritative `Services.poi_id`; standalone itinerary-only review is
  still deferred.
- Service GET/POST/PUT and commerce GET/POST/PUT share the typed application
  flow while preserving exact wire compatibility: service payloads expose
  `serviceBookingId` and omit `bookingId`; commerce payloads keep `bookingId`;
  both expose exact `{kind,id}` reviewable-record and subject values.
- Option A is implemented with typed nullable parent columns, one-parent and
  aligned-subject checks, separate filtered uniqueness, restrictive
  single-column foreign keys, typed media-operation identity and transactional
  wrong-shape rejection.
- `tm79-review-text-v1` is local and deterministic. `Accepted` alone publishes;
  `Rejected` and `Unavailable` fail closed without publishing or logging raw
  review text. The final focused corpus is **324 passed, 0 failed, 0 skipped**.
- Published Tour/POI aggregates use canonical plus safe non-overlapping legacy
  contributions. A same-booking overlap is canonical-wins, excludes the legacy
  contribution, preserves both rows for diagnosis and emits a mandatory
  internal warning. Equal-valued legitimate rows are not deduplicated.
- R7 preserves the supported commerce booking-linked itinerary compatibility
  path. It does not add standalone itinerary identity/route/aggregate behavior.
  No new public Tour rating consumer was invented; canonical Tour reads and POI
  Detail/Explore reads are covered directly.

Final verification on the isolated local SQL Server:

| Gate | Result |
| --- | --- |
| Focused moderation | 324 passed, 0 failed, 0 skipped |
| Focused typed migration + aggregate SQL coverage | 101 passed, 0 failed, 0 skipped |
| Application unit project | 1,295 passed, 0 failed, 0 skipped |
| Infrastructure unit project | 619 passed, 0 failed, 2 skipped |
| API integration project with SQL enabled | 754 passed, 0 failed, 1 skipped |
| Release build | 0 warnings, 0 errors |
| Whole-solution format verification | Exit 0, no changes required |
| Disposable SQL cleanup | 0 `TripMate_Test_*` databases remain |

The three skips are the existing opt-in real-Cloudinary provider tests: two in
Infrastructure and one end-to-end handoff smoke in API integration. They are
not SQL skips. The first full API run exposed one real compatibility defect:
the ordinary service-review phrase `Helpful staff and a clean room.` failed
closed because three common English words were missing from the language
vocabulary. The exact regression was added, the focused and isolated service
tests passed, and the complete 755-test API run then passed with only the one
expected provider skip.

Independent read-only review returned **PASS** with no remaining BLOCKER, HIGH
or MEDIUM finding across moderation, typed runtime shapes, schema/FKs and
canonical-wins aggregate logging. No external provider, credential, generated
artifact or test result was added to source control. The historical untracked
`docs/TM-79-implementation-prompt.md` remains untouched. No push, PR #30
mutation, Mobile/Web change or Jira change occurred.

```text
R1 COMPLETE / COMMITTED LOCALLY
R2 COMPLETE / COMMITTED LOCALLY
R3 COMPLETE / COMMITTED LOCALLY
R4 COMPLETE / COMMITTED LOCALLY
R5 COMPLETE / COMMITTED LOCALLY
R6 COMPLETE / COMMITTED LOCALLY
R7 COMPLETE / COMMITTED LOCALLY
R8 CONTRACT DEPENDENCY FROZEN / FULL UC-32 OWNED BY TM-78
R9 COMPLETE / COMMITTED LOCALLY IN THIS CHECKPOINT

Mobile FROZEN
Web FROZEN
PR #30 UNCHANGED
```

## R1 contract-freeze notice (2026-10-07)

The sections below this notice are historical evidence for the pre-remediation
commerce subset and intermediate checkpoints. Their commerce-only results,
null policy-version behavior and earlier G-POLICY deferral entries remain
truthful for the code that produced them, but they do not override the R9
closure above, current spec, plan or API contract. R1 itself changed
documentation only and ran no application or SQL tests.

Updated: 2026-10-01 (Asia/Ho_Chi_Minh)
Branch: `feature/datmnt-submit-trip-review`
Current-delivery decision (later 2026-09-28): **G-POLICY DEFERRED FOR CURRENT
DELIVERY / FUTURE ENHANCEMENT; Task 7 DEFERRED.** Prior policy/provider gate
entries below are dated historical evidence, superseded for current delivery
by the final entry in this ledger. G-SCOPE approved; G-VISITS/G-LEGACY open.

## Approval and approved scope

- Owner explicitly approved TM-79 spec/plan v3 in conversation on 2026-09-27.
- G-SCOPE approved: first delivery supports `commerce.Bookings` only.
  `commercial.ServiceBookings` and standalone no-booking CSP trips are
  deferred; this delivery must not be described as full UC-33 coverage.
- C4 remains separate optional route-pacing and CSP-quality evaluations.
- Backend work is authorized in the approved scope. The later decision defers
  G-POLICY from current delivery; G-VISITS and G-LEGACY remain evidence gates
  for dependent behavior. No missing source/business rule may be invented.
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

## Historical gate snapshot before the current-delivery deferral

- At that stage, G-POLICY was **PARTIALLY APPROVED / OPEN**, updated by explicit owner decision
  on 2026-09-28. Policy/corpus and External direction, provider-independent
  defaults and minimization are approved; concrete provider/model, corpus
  capability evidence, provider-specific data permission/handling, safe config/
  credentials, mapping and operational evidence remain OPEN. Task 7 NOT STARTED;
  no adapter or production-data transmission authorized. See latest decision
  record below; this gate snapshot is historical. Test doubles did not
  close that earlier gate. Current-delivery G-POLICY is DEFERRED, not a blocker.
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

## Task 2 — prerequisite inspection

Status: **IN PROGRESS; migration test implementation not started**.
HEAD inspected: `309edd26dde59c9e6028ba80b5076a564e213c45`.
Reviewed Task 2, the approved G-VISITS schema restriction, existing Tour
migration inventory tests and the isolated SQL database creation/drop helper.
No Task 3 schema, production code or new migration tests were written.

Read-only environment check: `docker inspect tripmate-tm70-sql --format
'{{.State.Running}} {{json .NetworkSettings.Ports}}'` and the existing
container SQL identity query both exited 1: Docker Desktop Linux Engine pipe
was absent. Service inspection showed `com.docker.service` stopped.
`Start-Process -FilePath 'C:\Program Files\Docker\Docker\Docker Desktop.exe'
-WindowStyle Hidden` exited 0, but the subsequent Docker verification was
aborted; SQL readiness is not yet confirmed. No test run/count/RED is claimed.
No shared/Azure database was contacted.

Task 2's full migration contract includes POI evidence and media lifecycle /
historical-delete restrictions. The plan explicitly forbids freezing evidence
storage before G-VISITS is resolved. The public contract freezes unavailable
capability semantics, not that storage source. Media lifecycle/status and
historical deletion matrix likewise need a scoped persistence decision before
their SQL constraints can be asserted; TourMedia business rules must not be
silently reused for review media. Independent parent/legacy constraints can
be implemented separately once SQL readiness is confirmed, but that subset
must not be reported as full Task 2 completion. Task 3 was not started.

## Approved staged Tasks 2a and 3a

Owner approved proceeding with independent parent/legacy migration tests (2a)
followed by matching parent-only schema (3a). POI evidence/children/media
remain deferred; original Tasks 2/3 are not complete in their full scope.

SQL target reverified: stopped container `88e5c351ed00` was started using
`docker start tripmate-tm70-sql` (exit 0); configured port is only
`127.0.0.1:14331`. Test connection is reconstructed from its password in memory
and cleared in finally. No credential was logged/stored, no shared/Azure DB
was accessed. Only disposable `TripMate_Test_*` databases are used.

Task 2a RED: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj
-c Release --no-restore --filter FullyQualifiedName~TripReviewMigrationTests
--logger 'console;verbosity=minimal'`. Initial 14 cases: exit 1, 0 passed,
14 failed, 0 skipped. Expanded 19 cases: exit 1, 0 passed, 19 failed, 0 skipped.
Final 21 cases: exit 1, 0 passed, 21 failed, 0 skipped. Failures are missing
parent table/migration, not compilation, connectivity or discovery. The
wrong-shape case initially reports the assertion for the missing migration
instead of the future SqlException; its cause is the same absent script.
No SQL implementation existed before RED. Independent review found weak
canonical-key assertions and lossy CHECK normalization; both were strengthened
before GREEN. Inventory preserves CHECK grouping and literal casing verbatim;
PK and booking uniqueness require enabled, exact single-column keys.

Task 2a files: new `Reviews/TripReviewMigrationTests.cs` (21 cases),
`Infrastructure/TripReviewSchemaInventory.cs` (full column/default/key/FK/check/
index inventory for parent and legacy), frozen complete
`Fixtures/Database/tm79_pre_migration_schema.sql` from HEAD `309edd26`, and
test project asset copying. The full baseline fixture avoids invented
dependency stubs; SQL fixtures include repeated author/subject across
bookings, ambiguous booking-linked and unlinked legacy Tour/POI/Operator rows.
These are isolated test data, not evidence that G-LEGACY is resolved.

Task 3a files: `database/migrations/20260928_add_trip_review_parent.sql`,
`database/tripmate_schema_v7.sql`, and `database/README.md`. Parent subject
is exactly one nullable Tour/Itinerary FK; booking/author and subject FKs use
NO ACTION. Immutable deadline is checked as original creation + seven days;
separate nullable pacing/CSP, normalized text bounds, rowversion and unique
booking are represented independently. Public-label/policy text use
NVARCHAR(MAX) to avoid inventing new product length limits. Cross-table legacy
duplicate protection remains the planned application write-lock protocol,
not a false claim from parent uniqueness alone.

Migration reruns compare canonical full inventory using a transaction-scoped
comparison table, then drop that table. Any wrong shape/constraint throws and
rolls back, without repairing existing parent or legacy data. This does not
implement moderation policy, edit workflow, route provenance or visit writer.
First GREEN command same as RED: exit 0, **21 passed, 0 failed, 0 skipped**,
duration 45s. Scoped `dotnet format TripMate.slnx --no-restore --include
tests/TripMate.Api.IntegrationTests/Reviews/TripReviewMigrationTests.cs
tests/TripMate.Api.IntegrationTests/Infrastructure/TripReviewSchemaInventory.cs`
exited 0.

Independent Task 3a review identified an omitted `sys.indexes.ignore_dup_key`
in canonical inventory validation. A new regression test first failed with
"Expected SqlException ... no exception was thrown": exit 1, 0 passed,
1 failed, 0 skipped. Command: same focused test command above with filter
`FullyQualifiedName~Rerun_WithIgnoreDuplicateKey`. Both SQL inventory branches
and the test inventory now include that flag. The regression verifies unchanged
row count, rowversion and inventory after rejection/rollback. Final focused
command above: exit 0, **22 passed, 0 failed, 0 skipped**, duration 45s.
Fresh independent two-stage re-review: spec-compliance PASS, code-quality PASS;
the finding is closed, with no remaining actionable findings.

Full regression command (SQL environment configured safely as above):
`dotnet test TripMate.slnx -c Release --no-restore
--logger 'console;verbosity=minimal' --blame-hang-timeout 3m`.
Before the review correction: exit 0, 1058 passed (596 Application + 73
Infrastructure + 389 API), 0 failed, 0 skipped. Final run after correction:
exit 0, **1059 passed (596 + 73 + 390), 0 failed, 0 skipped**; API duration
2m42s. Both runs finished with a final process result.

Scoped format verification initially exited 1 for final-newline formatting;
scoped `dotnet format` fixed it (exit 0), then the same command with
`--verify-no-changes` exited 0. `git diff --check` exited 0. Staged fixture
check initially caught inherited extra EOF blank lines; removed only those
blank lines, then `git diff --cached --check` exited 0.

Task 2a COMPLETE; Task 3a COMPLETE (approved parent-only stages).
Post-run read-only inventory query on the identified local container returned
only master/model/msdb/tempdb and `remaining_test_databases = 0` (exit 0).
Original Tasks 2/3 remain incomplete for deferred Tasks 2b/3b and evidence
gates. Task 4 was not started. Existing unrelated implementation prompt was
not changed or staged. Task 2a commit: `1480d4e`; no push performed.

## Task 4a — approved parent domain and EF persistence (2026-09-28)

Owner explicitly approved Task 4a plus commit/push to existing Draft PR #30.
Scope: parent TripReview entity, nullable RoutePacingFeedback, exact parent EF
mapping, DbSets and required test-double wiring. No POI children, review media,
operation journal, API, moderation provider or publication handler. Task 4b
and original full Task 4 remain deferred to their schema/evidence prerequisites.

Refreshed `origin/develop` and the PR branch: develop remains `4afd56c`, remote
feature head `309edd26`, local parent-schema head `28d4179`. No rebase/merge,
force push or shared-history rewrite. Existing unrelated untracked handoff
prompt remains untouched. Baseline before Task 4a was 1059/1059 PASS with SQL.

Files: new `src/TripMate.Domain/Entities/TripReview.cs`,
`src/TripMate.Domain/Enums/RoutePacingFeedback.cs`,
`src/TripMate.Infrastructure/Persistence/Configurations/TripReviewConfiguration.cs`,
`tests/TripMate.Application.UnitTests/Domain/TripReviewTests.cs`,
`tests/TripMate.Api.IntegrationTests/Reviews/TripReviewPersistenceTests.cs`;
existing `IApplicationDbContext`, `ApplicationDbContext`, Application test
context, API test context and password-reset concurrent test double only gain
the parent DbSet/member. Legacy Review entity/configuration remains unchanged.

Tests were written before implementation. RED commands:
`dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj
-c Release --no-restore --filter FullyQualifiedName~TripReviewTests
--logger 'console;verbosity=minimal'` and the corresponding API project command
with filter `FullyQualifiedName~TripReviewPersistenceTests`.
Both exited 1 because TripReview/RoutePacingFeedback did not yet exist (CS0246/
CS0103). These are compile-time RED results, not discovered failing test counts.
After implementation, one unexpected harness issue affected five cases:
xUnit could not convert int InlineData to nullable long; changed only literals
to long. That run: exit 1, 29 passed, 5 failed, 0 skipped. Re-run: exit 0,
34 passed, 0 failed, 0 skipped. No tests were weakened or skipped.

SQL focused GREEN: API command above, exit 0, 4 passed, 0 failed, 0 skipped
(8s). New-context round-trip verifies all parent fields, UTC offset/100ns
precision, real identity and 8-byte rowversion; nullable C4/Tour subject;
two-context optimistic concurrency retains winning state; rollback leaves
parent/legacy unchanged and no Tour mirror row is generated. SQL uses only
the identified isolated container/127.0.0.1:14331 and random disposable DBs.

The factory/edit methods accept already-normalized, policy-accepted text
without changing it. They do not certify authorization, completion, moderation
or route/CSP provenance; those are later application tasks. Edit failure at
the original exclusive deadline does not mutate state. Consent changes
regenerate the public snapshot; the unchanged preference preserves it.
Bookings is not mapped at this base, so BookingId maps as a scalar with the
real SQL FK still enforced; no invented Booking model or workflow was added.

Fresh independent Task 4a review: spec-compliance PASS and code-quality PASS,
no blocking findings. The optional coverage suggestion (unchanged true consent
after profile rename) was implemented by parameterizing the same-preference
test. Final focused domain result: exit 0, 35 passed, 0 failed, 0 skipped.
Scoped `dotnet format TripMate.slnx --no-restore --include` the five new C#
files exited 0; same command with `--verify-no-changes` exited 0 before this
coverage-only addition. The coverage-only patch introduced a final-newline
formatting error (verify exit 1); scoped formatter fixed it (exit 0) and final
verify-no-changes exited 0. Both `git diff --check` and
`git diff --cached --check` exited 0; unrelated prompt was excluded from staging.

Full solution command:
`dotnet test TripMate.slnx -c Release --no-restore
--logger 'console;verbosity=minimal' --blame-hang-timeout 3m` with the same
process-only isolated SQL environment, cleared in finally without logging
credentials. First full run: exit 0, 1097 passed (630 Application + 73
Infrastructure + 394 API), 0 failed, 0 skipped; API duration 2m46s. Final run
including both same-consent snapshot cases: exit 0, **1098 passed
(631 + 73 + 394), 0 failed, 0 skipped**, API duration 2m45s. Both runs returned
a final process result. No retries hid a failure; the second run validates
the additional test case.

Task 4a COMPLETE. Original full Task 4/Task 4b remain deferred; Task 5 was not
started. Post-run read-only database inventory returned only
master/model/msdb/tempdb, `remaining_test_databases = 0`, exit 0. Task 5 was not
started. User-authorized delivery is a Task 4a Conventional Commit and ordinary
fast-forward push of this branch to existing Draft PR #30, not a new PR,
merge, rebase, force push or claim that all TM-79 is ready to merge.

## Task 5a — one-pass verifiable owner read context (2026-09-28)

Owner requested the presently implementable context work in one pass before
member review. Approved spec gates remain in force: this is Task 5a, not a
waiver of historical route/Visited proof, media/child schema or G-LEGACY.
Baseline from Task 4a: 1098 passed, 0 failed, 0 skipped, exit 0; PR #30 head
`fed9ca9`. No unrelated prompt or production/shared/Azure database was changed.

New Application files: `Features/TripReviews/Common/ITripReviewContextReader.cs`,
`TripReviewContextDto.cs`, `TripReviewErrorCodes.cs`,
`GetContext/GetTripReviewContextQuery.cs`, `GetTripReviewContextQueryHandler.cs`.
New Infrastructure `Persistence/SqlServerTripReviewContextReader.cs` plus DI.
Domain changes expose the already-tested name snapshot algorithm for the same
owner preview (no new hidden name field) and add the existing BookedTour SQL
source constant. Tests: `Features/TripReviews/GetTripReviewContextTests.cs`
and `Reviews/TripReviewContextSqlServerTests.cs`. No endpoint or database change.

Task 5a authenticates via ICurrentUserService plus actual active Traveler DB
state before booking access. The parameterized reader independently scopes
commerce.Bookings to owner/active role, reads only required linkage evidence,
and uses AsNoTracking for parent/legacy data. Foreign legacy/parent content is
never fetched or returned; only conflict-presence metadata is considered.
Schedule-only uses derived Tour; itinerary-only verifies owner; both retain
Tour subject and reject contradictory BookedTour source links. Parent subject
mismatch disables editing without rewriting/removing the owner's stored review.
CSP capability requires owned CSPGenerated itinerary and owned scheduling
request, independently of route pacing/Visited. No payment/session/itinerary
status gate was added. GET context is advisory: writes must recheck in their
own final locked transaction, so no atomic write-eligibility promise is made.

Route pacing returns routeContextUnavailable; POI returns visitEvidenceUnavailable
at this base, with no inferred visits. Historical positive cases remain Task 5b.
New review is readable across the original deadline; editable fields are exactly
the four approved fields before the exclusive deadline, with real rowversion
encoded Base64. Policy version/private author fields are not on the read DTO.
Known foreign-author and parent/legacy overlap cases report conflict; no
automatic legacy reconciliation or new ambiguity policy was invented.

IMPORTANT: child/media arrays are empty only for this parent-only schema stage,
not evidence that those features are delivered. Legacy edits/ambiguous-row
rollout are NOT resolved by returning a context DTO here. Do not expose this
stage as completed public API/UI: Task 5b/Task 10/Task 12 must incorporate the
approved legacy disposition and real child/media reads before that claim.

Tests were written first. RED commands:
`dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj
-c Release --no-restore --filter FullyQualifiedName~GetTripReviewContextTests
--logger 'console;verbosity=minimal'` and matching API project with filter
`FullyQualifiedName~TripReviewContextSqlServerTests`, safely wrapped with the
process-only isolated SQL environment. Both exited 1 on missing namespace/
types (CS0234/CS0246), before implementation. These are compile-time RED;
no discovered fail/skip count is claimed for those build failures.

Initial GREEN: Application 27 passed/0 failed/0 skipped, exit 0; SQL 12
passed/0 failed/0 skipped, exit 0 (23s). SQL proves missing/foreign/negative
booking IDs, completion authority, schedule-only/both links, foreign itinerary,
owned/foreign CSP provenance, parent recovery after deadline/real 8-byte
rowversion, unchanged legacy rows/foreign content exclusion, inactive rejection
and no tracked entities or writes. No ServiceBookings-only fixture is claimed.

Two additional tests first failed at runtime: foreign-legacy-only had kind
legacy with review=null, and a parent with mismatched subject could remain
editable. Exact command: Application command above with filter
`FullyQualifiedName~ForeignLegacyOnly|FullyQualifiedName~ExistingParentWithDifferentSubject`;
exit 1, 0 passed, 2 failed, 0 skipped. Corrected DTO/discriminator consistency
and parent/current-subject check. Final Application filter: exit 0, 29 passed,
0 failed, 0 skipped. Independent review identified/confirmed those issues and
a misleading service-only test name (renamed to actual evidence). Final fresh
two-stage review: spec-compliance PASS, code-quality PASS; all findings closed.

Scoped `dotnet format TripMate.slnx --no-restore --include` the new feature,
reader, shared name helper and both tests exited 0. Final format verification
and full regression are recorded below. Task 6 was not started; no commit/push
in this stage without a delivery request.

First full command (same SQL wrapper as prior tasks):
`dotnet test TripMate.slnx -c Release --no-restore
--logger 'console;verbosity=minimal' --blame-hang-timeout 3m`.
Exit 1: Application 660 passed, Infrastructure 73 passed, API 403 passed/3
failed/0 skipped; total 1136 passed/3 failed/0 skipped. Unexpected failures:
two Development cases of `WebSignInIntegrationTests.Post_SecureCookieMatchesApprovedEnvironment`
and `CorsPolicyTests.DevelopmentCors_OnlyAllowsConfiguredOrigins`. Error:
entry point exited without building IHost. Cause: the InMemory test factory
removed concrete ApplicationDbContext but left the new SQL context-reader
registration, so Development-host DI validation could not construct it.
This is required test-double wiring for the new production dependency,
not a reason to change production auth/logging or weaken the tests.

Added `Reviews/TripReviewContextDependencyInjectionTests.cs` first. A mistakenly
overlapping build while the full suite was still running exited 1 with
MSB3027/MSB3021 (test DLL locked by that live testhost), before discovery;
recorded as an orchestration error, not a test result. Waited for the complete
full-suite process result before the next build. The isolated regression
command (`dotnet test` API project, Release/no-restore, filter
`FullyQualifiedName~TripReviewContextDependencyInjectionTests`) then exited 1:
0 passed/1 failed/0 skipped, reproducing the host startup failure.

Fixed only the InMemory TripMateApiFactory branch: replace the SQL reader with
UnsupportedInMemoryTripReviewContextReader, which throws NotSupportedException
instead of inventing booking evidence. The real-SQL factory and production DI
remain unchanged. The regression verifies /health=200 and the unsupported-read
guard; it follows the existing factory collection, without global parallelism
changes. Focused filter combining this new test, the secure-cookie cases and
Development CORS: exit 0, 5 passed/0 failed/0 skipped (11s). Independent wiring
re-review found no blocking issue. Scoped formatting and final
`--verify-no-changes` exited 0; tracked `git diff --check` exited 0 and the
new-file whitespace scan found no trailing whitespace (rg exit 1 = no matches).

Final full command above exited 0: **1140 passed (660 Application + 73
Infrastructure + 407 API), 0 failed, 0 skipped**, API duration 2m44s; complete
process result obtained. The final run includes all focused read-context,
factory regression and formerly failing auth/CORS cases. No test retries
masked a failure: the understood wiring defect was fixed before rerunning.
Post-run isolated SQL cleanup check: `remaining_test_databases = 0`, exit 0.

Task 5a COMPLETE, original full Task 5 remains partial/gated for Task 5b.
Task 6 was not started. No commit/push/PR mutation was performed for Task 5a;
member-review delivery still requires a commit/push request. Existing
`docs/TM-79-implementation-prompt.md` remains untouched/untracked.

## Task 6a — Structural validation and moderation port (2026-09-28)

Continued within the approved Task 6 boundary. No handler, HTTP endpoint,
production moderator, image decoder, provider call or database schema change.
Task 6a is an execution subdivision, not a waiver of the original Task 6
authorization/screening-order checks or G-POLICY.

Changed feature files:
- Application `Features/TripReviews/Common/ReviewText.cs` (immutable trimmed
  text reused by create/edit), `TripReviewInputRules.cs` (shared bounds and
  metadata/rowversion checks), `TripReviewInputEligibility.cs` (optional field
  checks against an already-authorized server context),
  `IReviewContentModerator.cs` (Accepted/Rejected/Unavailable and cancellation
  contract), and `TripReviewErrorCodes.cs` (frozen input/eligibility codes).
- Application `Features/TripReviews/Submit/SubmitTripReviewCommand.cs`,
  `SubmitTripReviewCommandValidator.cs`, `Edit/EditTripReviewCommand.cs`,
  `EditTripReviewCommandValidator.cs`.
- Application tests `Features/TripReviews/TripReviewValidationTests.cs` and
  `TripReviewModerationPolicyTests.cs`.
- This ledger and `plans/TM-79-plan.md` for accurate staged progress.

TDD command:
`dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj
-c Release --no-restore --filter
"FullyQualifiedName~TripReviewValidation|FullyQualifiedName~TripReviewModerationPolicy"
--logger "console;verbosity=minimal"`.
Initial RED exited 1 before discovery: missing Submit/Edit namespaces, command
and moderation types, as expected. It is compile-time RED, not discovered
runtime failures. Intermediate implementation compilation exited 1 for
ambiguous target-typed FluentValidation AddFailure/Validate overloads and
an int/long relational-pattern bound; corrected explicit types/comparison.
First discovered GREEN: exit 0, 62 passed/0 failed/0 skipped.
Server-context eligibility tests were added first: compile RED exit 1 for
missing TripReviewInputEligibility; after minimal implementation GREEN exit 0,
67 passed/0 failed/0 skipped. Final stable-code regression was added following
review; final verification is recorded below.

Coverage: create/edit rating 1/5, nullable/whitespace title/content, trimmed
UTF-16 limits (including surrogate-pair length), preserved interior whitespace
and combining Unicode, optional defined pacing/CSP, distinct positive POI IDs,
0/5/6 photo count, separate declared/raw 5,000,000-byte bounds, supported local
filename/MIME metadata (not delivery URLs), null entries, positive booking IDs,
canonical Base64 eight-byte edit version, consent/Unicode initials and a
restricted edit command surface. Optional-field eligibility has distinct
route/CSP/POI errors and uses only server-provided eligible IDs. Synthetic
capability contexts test that helper; they are not historical-visit evidence.

Moderation tests use an explicitly test-only RecordingModerator. They prove
the port accepts the exact same immutable normalized create/edit text and
has separate decisions, an identified accepted policy version, and propagated
cancellation. They do NOT prove production screening, corpus acceptance,
persisted text, or zero calls for unauthorized/invalid input. Those execution
checks require Tasks 9/10; no production orchestrator is introduced to simulate
them. No moderator registration exists; missing policy cannot silently approve.
The eligibility helper is not an auth/duplicate/Completed write guard.
Declared/raw metadata checks are not decoding proof; Task 8 still inspects
bounded actual bytes and content. Task 12 retains strict HTTP unknown-member,
wrong-type/null binding, flat field-error mapping and transport size checks.

Fresh independent two-stage review: no blocking spec-compliance or code-quality
findings in Task 6a. Suggested stable error-code coverage was added. Original
Task 6 remains partial for handler integration; G-POLICY still blocks Task 7.
No commit/push/PR mutation; all Task 5a changes and the unrelated untracked
implementation prompt are preserved.

Final verification:
- Focused TDD command above after the stable-code test: exit 0, **68 passed,
  0 failed, 0 skipped**. Both named classes were discovered and executed.
- Scoped formatting (all new Task 6a source/test paths) exited 0. Final exact
  check: `dotnet format TripMate.slnx --verify-no-changes --no-restore --include
  src/TripMate.Application/Features/TripReviews
  tests/TripMate.Application.UnitTests/Features/TripReviews`, exit 0.
- First full run: `dotnet test TripMate.slnx -c Release --no-restore --logger
  'console;verbosity=minimal' --blame-hang-timeout 3m`, with the prior safe SQL
  wrapper, exit 0, **1207 passed (727 Application + 73 Infrastructure + 407
  API), 0 failed, 0 skipped**; API 2m46s. This preceded the last added test.
- Final complete run: `dotnet test TripMate.slnx -c Release --no-build
  --no-restore --logger 'console;verbosity=minimal' --blame-hang-timeout 3m`,
  after the latest test build and using the same SQL wrapper, exit 0,
  **1208 passed (728 Application + 73 Infrastructure + 407 API), 0 failed,
  0 skipped**; API 2m44s. Full process completion confirmed; no lingering run.
- SQL target remained local `tripmate-tm70-sql`, `127.0.0.1:14331`, `master`
  only as the administrative connection for random `TripMate_Test_*`
  databases. Post-run read-only `sys.databases` count returned
  `remaining_test_databases = 0`, exit 0. No Azure/shared database used.
- `git -c safe.directory=D:/CapStone/Capstone_BE_tm79 diff --check`: exit 0.
  New source/test trailing-whitespace scan found no matches (rg exit 1).
- `git -c safe.directory=D:/CapStone/Capstone_BE_tm79 status --short --branch`:
  exit 0, branch `feature/datmnt-submit-trip-review` tracking its origin branch.
  Existing Task 5a tracked changes remain in this ledger/plan, Itinerary,
  TripReview, Infrastructure DI and TripMateApiFactory. New feature/reader/
  context-test files plus Task 6a files remain untracked; the unrelated
  implementation prompt remains untouched. No files were staged.

Task 6a COMPLETE. Original Task 6 PARTIAL: production handler suppression of
screening for unauthorized/invalid requests and persisted-text equality must
still be verified in Tasks 9/10. Task 7 NOT STARTED, blocked on approved
G-POLICY version/categories/languages/corpus/implementation choice. These are
missing decisions, not test failures. Latest local committed head remains
`fed9ca99f63dd4f73f56d4c963d8333ecced4f28`; Task 5a/6a have not been committed
or pushed, and PR #30 was not modified in this continuation.

## Continuation — G-POLICY decision preparation (2026-09-28)

Owner requested completion of Task 6. Re-read AGENTS.md and the Task 6/7
dependency; source search found only the provider-independent moderation port,
not an approved production policy or implementation. Original Task 6 remains
partial as recorded above. The approved Task 7 explicitly requires stopping
dependent publication work when policy is unspecified; continuation is not
approval of unchosen business rules.

Added `docs/TM-79-content-policy-proposal.md`, explicitly PROPOSED, with a
candidate version, Vietnamese/English scope, prohibited-category proposals,
legitimate-negative-review examples, decision corpus, unresolved boundary
examples and implementation/configuration choices. Linked it from Task 7 in
the plan. No spec approval or gate state was changed. No source/test edits,
DB/provider calls, commit/push or PR mutation in this continuation.
This is decision preparation, not Task 7 implementation or screening evidence.
The last executed full regression remains 1208 passed/0 failed/0 skipped,
exit 0 from Task 6a; docs-only changes do not constitute a new test run.

Fresh independent two-stage docs review: no blocking spec-compliance or
documentation-quality findings; unapproved policy/provider/corpus choices
remain explicit. `git -c safe.directory=D:/CapStone/Capstone_BE_tm79 diff
--check` exited 0. New proposal trailing-whitespace scan found no matches
(rg exit 1). `git status --short --branch` exited 0; same feature branch,
proposal untracked and prior changes preserved. Owner direction is still
required to resolve G-POLICY; Task 6 was not falsely marked complete.

## Documentation-only revision — owner-decision package (2026-09-28)

Scope: the owner's attached documentation-only request. No source/test,
runtime, database, provider, configuration, approval or remote changes. No
Task 2+ execution, no Task 7 implementation, and no commit/push. Prior code
and document changes were preserved.

Revised `docs/TM-79-content-policy-proposal.md`:
- Section 1 retains APPROVED D4 unchanged in meaning, explicitly outside this
  owner decision. Sections 2–6 separate PROPOSED version/languages/categories,
  untrusted-instruction interpretation, quotation/mixed-content context and
  Accepted/Rejected/Unavailable semantics.
- Section 7 retains A01–A08/R01–R07/U01 with clarified R07 rationale and adds
  A09–A12, R08–R11, U02–U04: reported threat, diacritic-free/slang criticism,
  legitimate instruction quotation, synthetic sexual-content/private-contact
  disclosure cases, mixed violation, evasive threat, Korean/ambiguous language
  and unreliable-classifier outcome. U04 explicitly models a response failure,
  not an unconditional verdict against otherwise legitimate English text.
- Sections 8–12 retain OPEN implementation choice, proposed external operating
  settings, data minimization/version binding and conditional owner checklists.
  No provider/model or local classifier was selected; all required applicable
  decisions still need explicit owner approval. G-POLICY stays OPEN / PROPOSED.
- Sections 13–14 clarify Task 1/6/7/9/10 relationships and document consistency.
  The supplied Task 1 snapshot differs from local plan/ledger (Backend contract
  complete, central Docs handoff open); no historical progress was reset.

Compared against spec D4, plan Tasks 1/6/7/9/10, API contract sections 5–7 and
the existing ledger: no approved D4 behavior conflict found. Unsupported
language remains unresolved in the approved API contract; Unavailable is
explicitly only a proposed owner decision here. No admin/manual workflow or
image-content moderation was added. No real private identifiers or secrets
were introduced; R09 uses intentionally fake values, and A08 retains only
public place context.

Verification (documentation checks, not application tests):
- `git -c safe.directory=D:/CapStone/Capstone_BE_tm79 diff --check`: exit 0.
- `rg -n '[ \t]+$' docs/TM-79-content-policy-proposal.md`: exit 1, no matches
  (the proposal is untracked, so this check supplements tracked diff checking).
- PowerShell regex `(?m)^\| ([ARU]\d{2}) \|` over the proposal: exit 0;
  27 unique cases, 12 Accepted / 11 Rejected / 4 Unavailable, no duplicate IDs;
  IDs exactly A01–A12, R01–R11 and U01–U04.
No dotnet tests were run for this documentation-only revision. The earlier
1208-pass result remains historical Task 6a evidence, not a new test result.

Fresh independent review: stage 1 spec/request compliance PASS; stage 2
clarity/internal consistency PASS, no blocking findings. All 16 requested
areas are represented in 14 proposal sections. Final `git diff --check`
exited 0; branch and pre-existing changes remained unchanged. Only this
ledger and the policy proposal were edited in this documentation-only run.
Final status: **G-POLICY: OPEN / PROPOSED**. No commit or push.

## Owner decision — External direction approved, provider pending (2026-09-28)

Approval date: **2026-09-28 (Asia/Ho_Chi_Minh)**. Evidence: explicit OWNER
DECISION in the attached user request headed “Continue TM-79 documentation/
decision work ONLY”, in this conversation. Scope is recording that decision
in documentation only, not adapter development or provider research/selection.
Earlier proposal/gate/test entries above remain historical evidence.

Newly approved subset:
- Policy version `tm79-review-text-v1`; Vietnamese, English and mixed support.
- Unsupported/unreliable language => Unavailable => HTTP 503
  `trip_review.policy_unavailable`; neither automatic approval nor rejection
  merely because the language is unsupported.
- Accepted/Rejected/Unavailable model; the six prohibited categories and
  contextual rules in the decision record, including legitimate negative/
  one-star reviews, complaints/refunds, non-keyword rejection, reporting/
  quotation, untrusted moderation instructions, spam-only manipulation and
  whole-submission rejection when a material prohibited violation is present.
- Complete current synthetic corpus A01–A12, R01–R11, U01–U04 (27 cases),
  including U04 as classifier failure-path fixture, not an inherent verdict
  against legitimate English text. Corpus approval is not execution evidence.
- **External selected; Local not selected for TM-79 v1.** No fallback to Local
  or provider selection inferred from this direction approval.
- Provider-independent defaults: 10 seconds per screening request; no
  automatic retry in v1; caller cancellation propagated; missing config,
  timeout, malformed response or unreliable/ambiguous verdict => Unavailable;
  no always-allow fallback/automatic downgrade; no manual/admin queue.
  Revisions need documented provider technical constraints and owner approval.
- Data-minimization boundary: only normalized title/content as external
  moderation user input unless a later owner-approved provider contract
  strictly requires another field. No names/account/contact/booking/traveler,
  location-history, payment or unrelated user/profile identifiers; no routine
  raw-text/secret logs. Direction approval does NOT authorize production
  transmission to an unapproved concrete provider.
- Accepted policy version binds the exact normalized screened pair. Changed
  normalized text requires screening again. Rejected/Unavailable attempts
  gain no accepted policy version; failed edits preserve the previous
  publication; no retroactive legacy moderation certification.

Still OPEN before G-POLICY closure: concrete external provider; exact model/
version or stable contract; capability evidence on the approved Việt/Anh/mixed
corpus; provider-specific permission/data handling for title/content; safe
credential/configuration source; provider-specific request/response and
malformed/ambiguous-result mapping; availability/operational evidence. Defaults
approval is not approval of a provider-specific mapping or capability.

Current gate matrix:
- G-SCOPE: APPROVED.
- G-POLICY: **PARTIALLY APPROVED / OPEN**.
- G-VISITS: OPEN, unchanged.
- G-LEGACY: OPEN, unchanged.

Changed documentation: the policy decision record (filename retained), this
ledger (current gate summary plus this append-only record), spec D4/approval
note, plan D4/gate/Task 7/progress wording, and API contract gate summary,
503 condition and section 6. The API contract now records approved
unsupported/unreliable-language => Unavailable / 503; no provider-specific
schema/fields added. Unrelated D1–D7 rules and historical task/test evidence
were preserved. Task 7 remains NOT STARTED and blocked. Provider research/
selection may proceed only as a subsequent decision step, not in this run.

No source/test/schema edits, provider calls, package installs, new env vars,
configuration/secrets, database access, commits, pushes or PR #30 mutation.
No application tests were run for this documentation-only decision record.

Decision-record verification (2026-09-28):
- Two-pass review performed: (1) compared the approval subset, explicit open
  provider items and prohibited scope against the owner's attached decision;
  (2) checked gate, Task 7 and API wording across the five affected documents.
  No blocking inconsistency found. A fresh independent reviewer was requested,
  but its run ended at a usage limit without a review result; this is not
  represented as an independent-review pass.
- The approved corpus remains byte-for-byte unchanged in its 27 table rows:
  A01-A12 (12), R01-R11 (11), U01-U04 (4), all IDs unique. U04 remains a
  classifier failure-path fixture. The protected source/test/database inventory
  (475 files) has the same SHA-256 aggregate as before this decision-record run.
- `git -c safe.directory=D:/CapStone/Capstone_BE_tm79 diff --check`: exit 0.
  `rg -n '[ \t]+$'` over all five affected Markdown files: exit 1, no trailing
  whitespace matches, including the untracked policy record.
- No application test execution was required for documentation-only changes;
  the prior 1208-pass run above remains historical evidence, not a new result.

## G-POLICY provider-selection research only (2026-09-28)

The owner requested decision preparation, **not** a provider selection or Task 7.
Added `docs/TM-79-moderation-provider-evaluation.md` using current official
OpenAI, Microsoft and AWS sources. It compares OpenAI Moderation API, an
illustrative pinned OpenAI general-LLM Responses configuration, and Azure AI
Content Safety against the approved six-category/context/language contract;
AWS Comprehend toxic-content detection is noted as English-only and unsuitable
as the sole v1 provider. The package records privacy/retention distinctions,
.NET integration possibilities, unproved capabilities, a future opt-in 27-case
synthetic evaluation protocol, and explicit unchecked owner choices. No model
or provider was selected; no corpus was sent. Research is not policy-capability
or production data-handling approval.

Current gates remain G-SCOPE APPROVED; G-POLICY PARTIALLY APPROVED / OPEN;
G-VISITS OPEN; G-LEGACY OPEN. Task 7 NOT STARTED. Previous tests above remain
historical; no new application tests were run for research-only Markdown.
No source/test/schema, config/env/secret, dependency or API contract edits;
no provider call, Azure/shared DB access, commit, push or PR #30 mutation.
Independent two-stage documentation review: scope/gate compliance PASS. The
source-quality pass initially found an overstatement of Azure Vietnamese
support and a missing API-version lifecycle caveat. Both were corrected in
the comparison row; the independent re-review marked source quality PASS,
with no remaining concrete findings. `git diff --check` (with the worktree's
safe-directory override) exited 0. Trailing-whitespace scan of this ledger
and the new untracked Markdown returned exit 1 (no matches).

## G-POLICY synthetic capability evaluation preparation (2026-09-28)

Owner nominated **OpenAI Responses API / `gpt-5.6-terra` only as an evaluation
candidate**, not as the production provider. Updated the provider decision
package with the official API/model capability checks, an evaluation-only
strict JSON schema, fixed policy instruction, proposed fail-closed mapping,
26-real-case + U04-injected-fixture design, sanitization and failure probes.
The current Application `IReviewContentModerator` and `ReviewText` were read
and left unchanged; no production DI or Task 7 implementation was started.

Official model documentation lists `gpt-5.6-terra` for Responses and
Structured Outputs; this does not prove test-account entitlement or a dated
immutable snapshot. A process-environment presence check returned
`OpenAiKeyConfigured = false` without printing a credential. No key was
requested in chat or written to source. Therefore account availability was
not verified and **no provider request** or corpus evaluation was run; no
replacement model was used. Results: 0 real calls, 0 synthetic cases
evaluated, 0 latency samples, 0 failure-path tests executed. No PASS/FAIL
capability claim is possible. The exact future evaluation requires 26 real
synthetic calls (A01-A12/R01-R11/U01-U03) and a separate injected U04
failure-path fixture, aiming for 27/27 adapter-boundary expectations.

Only synthetic title/content transmission is authorized in this stage;
**none was sent in this run**. Production transmission and production
provider approval remain unauthorized. G-SCOPE APPROVED; G-POLICY PARTIALLY
APPROVED / OPEN; G-VISITS OPEN; G-LEGACY OPEN. Task 7 NOT STARTED. No source,
test, schema, config, env, secret, package, API contract, database, commit,
push or PR #30 mutation occurred in this preparation.

Verification for this preparation: the documented JSON schema parsed locally
with `strict=true`, four required properties, six allowed violation categories
and `additionalProperties=false` (exit 0); parsing does **not** prove the
provider accepts or follows it. Independent two-stage read-only review found
no blocking issues. A minor ambiguity between evaluation and production
selection in the summary was corrected; the reviewer did not run the provider
or claim account access. `git diff --check` (with the worktree's safe-directory
override) exited 0. `rg -n '[ \t]+$'` on both affected Markdown files
returned exit 1/no matches. No applicable evaluation tests could be run
without secure test-account configuration; prior test results remain historical.

## Current-delivery moderation deferral — explicit owner decision (2026-09-28)

The owner explicitly deferred all automated/AI pre-publication review-content
screening for the current TM-79 Capstone delivery. This **supersedes prior
G-POLICY/External evaluation direction for current implementation only**;
it does not erase or invalidate the approved future policy, 27-case corpus or
OpenAI/Azure research above. Report 3 §3.7.2 / BR-94 originally required
pre-publication screening; the current delivery intentionally omits it and
therefore has a documented source-scope deviation, not full BR-94 compliance.
Structural validation is not being renamed “screening.”

Current POST: Active Traveler/owned supported Completed booking -> early
new+legacy duplicate check -> one-time title/content normalization ->
structural and optional-field eligibility checks -> image inspection and
media coordination where applicable -> final locked rechecks/transaction ->
persist/publish -> aggregates. Current PUT: strict binding -> owner/review/
deadline/rowversion checks -> normalization/structural validation -> final
transaction/concurrency recheck -> persist edited normalized values ->
aggregates. No AI call or AI failure state in either path; no AI key,
paid moderation usage or provider-specific config required. Task 7 is
**DEFERRED / FUTURE ENHANCEMENT**, no longer a current blocker for Tasks 8–17.
G-SCOPE APPROVED; G-POLICY DEFERRED FOR CURRENT DELIVERY / FUTURE ENHANCEMENT;
G-VISITS OPEN; G-LEGACY OPEN. No production provider/model selected and no
production review may be sent to an AI provider.

The prior Task 6a port and test-only moderator remain in source but are not
production-registered; current handlers have not yet been implemented and
must not depend on the port. Task 6a completion evidence remains valid for
structural validation and dormant future-port behavior, but it is **not**
current moderation execution coverage. Full Task 6 remains partial for
handler validation/ordering and normalized persistence checks in Tasks 9/10.
**Critical future implementation prerequisite:** current uncommitted
`TripReview.CreatePublished`/`TryEditPublished` and SQL parent migration/full
schema require nonempty accepted `policy_version` (`CK_TripReviews_Policy`).
This docs-only decision cannot make a no-AI review publishable; a later
TDD-scoped domain/SQL/test correction must reconcile that invariant without
a fabricated approval marker. No source/schema/test correction was made here.

Manual post-publication reporting/admin review/hide-or-remove is a separate
follow-up scope, **not implemented**. The inspected Report 3 admin sections
cover account and Tour-post moderation; no explicit review-report/moderation/
remove use case and no matching reviewed-report API/schema was identified.
MSG124's generic review-removal copy alone is insufficient to freeze that
workflow. Future design must decide logical state, report source, authority,
reason, actor/time/audit, aggregate exclusion and restoration/appeal, without
inventing endpoint/column/UC/Jira names now. See spec §6 backlog note.

The old `docs/TM-79-implementation-prompt.md` is a historical handoff prompt
containing pre-deferral screening steps; it is **not** authority for current
delivery and was intentionally not edited in this six-file reconciliation.
The six scoped documents were updated; prior task/test evidence retained.
No application tests were run for documentation-only reconciliation, and no
source/test/schema, provider, database, commit, push or PR #30 change occurred.

Documentation verification: `git diff --check` exit 0. Trailing-whitespace
scan (`rg -n '[\t ]+$'` over all six scoped Markdown files) exit 1/no
matches. Independent read-only review checked contract/scope alignment and
stale wording against the owner decision and current domain/SQL constraints;
it found no blocking inconsistency. Its minor finding about an older
“Open evidence gates” heading was corrected to label that snapshot historical.
No application regression run is claimed by this documentation-only check.

## Task 6b — Current-delivery publication invariant reconciliation (2026-09-29)

Owner explicitly authorized this one TDD subdivision. Status: COMPLETE.
Final SQL-enabled full solution result: 1232 passed, 0 failed, 0 skipped,
exit 0. Independent review completion was limited by reviewer quota as below.
Task 7 remains DEFERRED / FUTURE ENHANCEMENT; Tasks 8/9/10/12 were not started.

Git inspection: previous/current HEAD and Draft PR #30 head are
`fed9ca99f63dd4f73f56d4c963d8333ecced4f28`. `git fetch origin` first failed
inside the network sandbox (exit 1), then succeeded with approved access (0).
Fetched `origin/develop`: `e8013f98a49b9fa1fbe4fcc412b562f7db8c37eb`;
merge-base: `525489b61ee2a2791b3d0cf71313e80a4a0445e7`.
Commands: `git rev-parse HEAD origin/develop`, `git merge-base HEAD origin/develop`,
`git log --oneline HEAD..origin/develop`, `git diff --stat HEAD...origin/develop`,
and `gh pr view 30 --repo FPTUCapstone/Capstone_BE --json headRefName,headRefOid,baseRefName,isDraft,state`.
PR is OPEN/Draft, base develop, head feature/datmnt-submit-trip-review.
Develop delta includes TM-207 TourMedia and scheduling/preference/routing work;
it has no TM-79 parent change. Base/history were left intact, with no rebase,
reset, stash or remote mutation. Existing Task 5a/6a changes were preserved.

Old invariant: required domain string/accepted-policy argument, EF IsRequired,
SQL NVARCHAR(MAX) NOT NULL and CHECK LEN(policy_version)>0. It rejected null
but the SQL check accepted tabs and other whitespace. New invariant: nullable
domain/EF/SQL value; NULL means no automated policy approval for this publication.
A non-null version is reserved for actual acceptance of the exact text by a
future policy. Domain rejects blank or unnormalized non-null input; it never
trims/discards a real version or invents one. An unscreened edit sets NULL even
when earlier text had a policy version. SQL checks NULL OR nonempty after
trimming the full .NET whitespace set with binary character comparison.
SQL does not rewrite valid existing values. Actual policy provenance remains
the caller's responsibility; Published alone does not establish it.

Changed files in this step:
- `src/TripMate.Domain/Entities/TripReview.cs` and
  `src/TripMate.Infrastructure/Persistence/Configurations/TripReviewConfiguration.cs`.
- `database/tripmate_schema_v7.sql` and
  `database/migrations/20260928_add_trip_review_parent.sql`.
- `tests/TripMate.Application.UnitTests/Domain/TripReviewTests.cs`;
  `tests/TripMate.Api.IntegrationTests/Reviews/TripReviewPersistenceTests.cs`;
  `tests/TripMate.Api.IntegrationTests/Reviews/TripReviewMigrationTests.cs`.
- New frozen fixture `tests/TripMate.Api.IntegrationTests/Fixtures/Database/tm79_pre_nullable_policy_parent.sql`
  and its asset entry in `tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj`.
- This ledger and `plans/TM-79-plan.md`. Spec D4/API section 6 were narrowly
  corrected because their prior current-state paragraphs still asserted the
  mandatory-policy implementation gap; public routes/DTOs/errors are unchanged.

`TripReviewSchemaInventory` was inspected and needs no reader change: it already
compares nullability and complete CHECK definitions without lossy normalization.
The explicit expected nullable-column assertion and parity tests were updated.
Migration supports absent parent, exact revised shape, and the fully verified
pre-deferral parent fixture. Only that known old shape is upgraded after full
inventory comparison. Hybrid/wrong shape, disabled/wrong check or incompatible
existing whitespace data fail and roll back. Tests compare complete parent JSON,
identity, rowversion, legacy JSON and inventories; the validation table is removed.
No UPDATE/backfill of policy values, default marker, unrelated-table alteration,
new moderation state, EF migration, API endpoint or provider registration exists.
PolicyVersion is absent from public context DTOs. The future moderator port is
unchanged, unregistered and unused by the current publication entity.

SQL target verified: container tripmate-tm70-sql,
ID `88e5c351ed000b324d25e6573104211b2d8c39902945b93567828820459e9436`,
binding `127.0.0.1:14331 -> 1433`, SQL server `88e5c351ed00`, version
`16.0.4265.3`. Before tests it held only master/model/msdb/tempdb.
The existing container credential was read into memory without printing it;
a connection builder set TRIPMATE_SQLSERVER_TEST_CONNECTION for the test
process only, cleared in finally. Tests create/dispose random TripMate_Test_*
databases. No shared, Azure or production database was contacted.

Exact focused commands (run from this worktree; SQL command uses that wrapper):

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~TripReviewTests' --logger 'console;verbosity=minimal'
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --no-restore --filter 'FullyQualifiedName~TripReviewPersistenceTests|FullyQualifiedName~TripReviewMigrationTests' --logger 'console;verbosity=minimal' --blame-hang-timeout 3m
```

| Stage | Passed | Failed | Skipped | Exit |
| --- | ---: | ---: | ---: | ---: |
| Existing domain baseline | 35 | 0 | 0 | 0 |
| Existing migration/persistence baseline | 26 | 0 | 0 | 0 |
| Domain RED, tests changed before implementation | 21 | 28 | 0 | 1 |
| SQL RED, tests/fixture changed before implementation | 21 | 15 | 0 | 1 |
| Domain GREEN | 49 | 0 | 0 | 0 |
| Migration/persistence GREEN | 36 | 0 | 0 | 0 |

All three named classes were discovered/executed. Expected RED: domain rejects
NULL and accepts padded policy; SQL rejects NULL, reports old NOT NULL inventory,
accepts tab-only policy, and cannot meet revised upgrade/rerun expectations.
Nullable compiler warnings during RED describe the old non-null signatures;
there were no compilation/discovery/connection failures in these test runs.
GREEN covers original unrelated invariants using unscreened defaults, genuine
future versions, edit preservation/clearing, real new-context SQL round-trip,
all .NET whitespace-only values, fresh/upgrade parity, safe rerun and rollback.

Scoped formatting and verify-no-changes both exited 0:

```powershell
$taskFiles = @(
  'src/TripMate.Domain/Entities/TripReview.cs',
  'src/TripMate.Infrastructure/Persistence/Configurations/TripReviewConfiguration.cs',
  'tests/TripMate.Application.UnitTests/Domain/TripReviewTests.cs',
  'tests/TripMate.Api.IntegrationTests/Reviews/TripReviewPersistenceTests.cs',
  'tests/TripMate.Api.IntegrationTests/Reviews/TripReviewMigrationTests.cs'
)
dotnet format TripMate.slnx --no-restore --include $taskFiles
dotnet format TripMate.slnx --verify-no-changes --no-restore --include $taskFiles
dotnet build TripMate.slnx -c Release --no-restore
dotnet test TripMate.slnx -c Release --no-build --no-restore --logger 'console;verbosity=minimal' --blame-hang-timeout 3m
```

Release build exit 0, 0 warnings/0 errors. Full regression final process exit 0:
742 Application + 73 Infrastructure + 417 API = **1232 passed, 0 failed,
0 skipped**; API duration 2m49s. No pending test process result remains.
Independent two-stage review was requested. Reviewer reported no actionable
domain/schema/test findings in its initial pass, then hit a usage limit before
finishing docs/fixture review. This is partial independent evidence, not a
completed independent PASS. Equivalent two-pass self-review checked the owner
contract, NULL/real-version semantics, complete migration inventory/rollback,
whitespace constraint, DTO/DI exclusion and preservation of unrelated changes;
no actionable issue remained. G-SCOPE APPROVED, G-POLICY DEFERRED, G-VISITS OPEN,
G-LEGACY OPEN. No AI/provider integration, commit or push was performed.

Final verification and cleanup:
- Discovery-only API command: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --no-build --no-restore --list-tests --filter 'FullyQualifiedName~TripReviewPersistenceTests|FullyQualifiedName~TripReviewMigrationTests'`, exit 0; both intended classes and all named methods were listed.
- The pre-deferral fixture's CREATE TABLE/index block exactly matches the
  committed parent block at HEAD, compared ordinally after newline normalization.
- Local read-only SQL inventory returned only master/model/msdb/tempdb,
  `remaining_test_databases = 0`, exit 0. Temporary test log roots: 0;
  generated TestResults files: 0. No manual database or file deletion needed.
- `git -c safe.directory=D:/CapStone/Capstone_BE_tm79 diff --check` and
  `git -c safe.directory=D:/CapStone/Capstone_BE_tm79 diff --cached --check`
  passed, exit 0. Nothing is staged. Scoped Markdown/new-fixture trailing
  whitespace scan returned no matches (rg exit 1).
- SHA-256 file inventory comparison confirms this run changed only the listed
  Task 6b files; unrelated Task 5a/6a files, dormant moderator, provider research
  and corpus remain untouched. TripReview's pre-existing display-name helper
  changes remain preserved. No public policy metadata was added.
- `git status --short --branch` confirms feature/datmnt-submit-trip-review
  still tracks origin/feature/datmnt-submit-trip-review, with pre-existing
  dirty work plus this scoped change. HEAD remains fed9ca99f63dd4f73f56d4c963d8333ecced4f28.

Current-delivery reviews may persist with policyVersion = null; no fake policy
approval value is used, and a genuine future version remains supported.
Task 6b COMPLETE does not complete full Task 6 or the submission/edit workflow.
Task 7 remains DEFERRED / FUTURE ENHANCEMENT; G-VISITS/G-LEGACY remain OPEN.
Tasks 8/9/10/12 were not started. No commit, push or PR modification performed.

## Task 8a — Review image inspection (2026-09-29)

Status: **Task 8a COMPLETE; Task 8 overall PARTIAL**. Tasks 8b/8c/8d and
Task 9 NOT STARTED. Task 7/G-POLICY remain DEFERRED; G-VISITS/G-LEGACY OPEN.
This entry supersedes the historical "Task 8 not started" statement above.

### Dependency revalidation and scope

Worktree `D:\CapStone\Capstone_BE_tm79`; branch
`feature/datmnt-submit-trip-review`; previous and final HEAD
`d87c0ef24be799e0a536c54e8a60aa51f6c53d04` (four existing local commits ahead).
Fetched `origin/develop`: `e8013f98a49b9fa1fbe4fcc412b562f7db8c37eb`;
merge base `525489b61ee2a2791b3d0cf71313e80a4a0445e7`.
Read-only PR inspection confirmed #30 OPEN/DRAFT, base develop, remote head
`fed9ca99f63dd4f73f56d4c963d8333ecced4f28`. No remote state was changed.

Commands: `git status --short --branch`, `git fetch origin`,
`git rev-parse HEAD`, `git rev-parse origin/develop`,
`git merge-base HEAD origin/develop`, `git log --oneline HEAD..origin/develop`,
and `gh pr view 30 --json state,isDraft,baseRefName,headRefName,headRefOid`.
Git commands used `-c safe.directory=D:/CapStone/Capstone_BE_tm79`.
Network/sandbox attempts initially failed; authorized retries succeeded.
These failures are environment evidence, not TDD RED.

TM-207 is present in current develop. Inspected its
`SkiaSharpTourMediaImageInspector.cs`, `TourMediaImageContracts.cs`,
`TourMediaImageInspectorTests.cs`, Infrastructure project and DI.
It uses SkiaSharp 4.152.1, 24,000,000 pixels, positive dimensions and RGBA8888;
these match the approved resource contract. Its encoded-byte policy is 10 MiB
and its abstractions are TourMedia-specific, not a generic decoder port.
This branch does not contain that implementation. Adapt the low-level decoder
approach in a review-owned adapter; do not merge/cherry-pick TourMedia business
code or invent a generic extraction in this task. The private pixel guard is
annotated with the revalidated source SHA. Reuse existing
`TripReviewInputRules.MaximumPhotoBytes` (5,000,000), not the Tour limit.
No existing shared decoder/TourMedia code was modified; a separate TM-207
regression filter is therefore not applicable on this branch.

### Files and behavior

- Application: `Features/TripReviews/Media/IReviewImageInspector.cs` — port,
  input/result records and deterministic rejection categories; no SDK dependency.
- Infrastructure: `Reviews/Media/ReviewImageInspector.cs` — actual bounded
  stream read, at most limit+1 bytes even for an endless/non-seekable stream;
  ignores declared length and never reads Stream.Length. Caller owns input.
- Infrastructure project — SkiaSharp and Linux native assets 4.152.1, matching
  inspected develop; no Cloudinary package/configuration.
- Infrastructure DI — singleton stateless inspector registration.
- Infrastructure tests: `Reviews/ReviewImageInspectorTests.cs` — deterministic
  generated fixtures (no network, no large checked-in binaries or image files).
- This ledger and `plans/TM-79-plan.md` — execution evidence only.

Allowed extension/MIME/content must agree (case-insensitive extension/MIME;
MIME whitespace trimmed). URL/path/public-ID inputs are not accepted as upload
filenames. Reject empty, oversized, unsupported GIF/SVG, mismatched, corrupt or
incomplete fixtures, animated WebP and area above 24m pixels. Decode requires
`SKCodecResult.Success` into RGBA8888 after dimension/area checks; conceptual
pixel buffer <=96m bytes excludes codec overhead. No invented edge limit.
Animation fixture independently proves two decoded frames before rejection.

JPEG traversal validates first EOI at exact end through length-delimited
segments/entropy markers; PNG traversal validates first IEND at exact end.
WebP checks RIFF total length, codec success and animation chunks/flags/frame
count. This is not exhaustive validation of every ancillary WebP chunk, nor
universal polyglot detection. Tests prove appended script and repeated JPEG/
PNG terminator rejection, not a general content-security guarantee.
Output is re-encoded from decoded pixels (metadata stripped); its bytes/size
can differ from input. The 5m limit here is the actual input encoded-file bound.
Synchronous native decoding/encoding cannot be interrupted mid-call;
cancellation propagates before/during async reads and at native-call boundaries.
Photo-count/request bounds remain Application/HTTP responsibilities. No upload,
storage, journal, cleanup worker, handlers, endpoints or AI integration added.

### TDD and verification commands

All commands below ran in the worktree above.

```powershell
dotnet restore TripMate.slnx
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --no-restore --filter FullyQualifiedName~ReviewImageInspectorTests --logger 'console;verbosity=minimal'
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --no-restore --filter FullyQualifiedName~InfrastructureRegistersInspector --logger 'console;verbosity=minimal'
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --no-restore --filter FullyQualifiedName~TrailingPayloadWithRepeatedTerminator --logger 'console;verbosity=minimal'
dotnet format TripMate.slnx --no-restore --include src/TripMate.Application/Features/TripReviews/Media/IReviewImageInspector.cs src/TripMate.Infrastructure/Reviews/Media/ReviewImageInspector.cs src/TripMate.Infrastructure/DependencyInjection.cs tests/TripMate.Infrastructure.UnitTests/Reviews/ReviewImageInspectorTests.cs
dotnet build TripMate.slnx -c Release --no-restore
dotnet format TripMate.slnx --no-restore --verify-no-changes --include src/TripMate.Application/Features/TripReviews/Media/IReviewImageInspector.cs src/TripMate.Infrastructure/Reviews/Media/ReviewImageInspector.cs src/TripMate.Infrastructure/DependencyInjection.cs tests/TripMate.Infrastructure.UnitTests/Reviews/ReviewImageInspectorTests.cs
dotnet test TripMate.slnx -c Release --no-build --no-restore --logger 'console;verbosity=minimal' --blame-hang-timeout 3m
git -c safe.directory=D:/CapStone/Capstone_BE_tm79 diff --check
git -c safe.directory=D:/CapStone/Capstone_BE_tm79 status --short --branch
```

Restore exit 0. Initial restricted restore was canceled after network failures
(exit 1); not counted as RED. Tests were written before the behavior, then a
compilable scaffold threw `NotImplementedException("Task 8a RED scaffold")`:
31 discovered / 0 passed / 31 failed / 0 skipped, exit 1. Initial implementation
GREEN: 31 passed, exit 0. DI test independently RED: 1 discovered/failed,
missing registration, exit 1. Reviewer-derived repeated-terminator tests RED:
2 discovered/failed (incorrect acceptance), exit 1. Fixes followed those REDs.
Final focused GREEN: **34 passed, 0 failed, 0 skipped, exit 0**.
Scoped format and verification exit 0; build exit 0, 0 warnings, 0 errors.

Full SQL-enabled suite finished with final process exit **0**:

| Project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Application | 742 | 0 | 0 |
| Infrastructure | 107 | 0 | 0 |
| API integration | 417 | 0 | 0 |
| Total | **1266** | **0** | **0** |

API duration 2m49s. No retry loop, skipped tests or global parallelism change.
Approved SQL identity: existing container `tripmate-tm70-sql`, full ID
`88e5c351ed000b324d25e6573104211b2d8c39902945b93567828820459e9436`,
server `88e5c351ed00`, `127.0.0.1:14331`, administrative catalog master.
Docker Desktop and this stopped container were started. No other container was
explicitly started/stopped by task commands. Full ID and port binding were
asserted before testing. The existing container credential was read into memory
from docker inspect, never printed/written; a connection-string builder set
`TRIPMATE_SQLSERVER_TEST_CONNECTION` only for the test process and restored the
previous environment value in finally. Tests create/drop random TripMate_Test_*
databases via the existing repository helper, never a shared/Azure database.

### Review and cleanup

Fresh independent reviewer performed spec-compliance then code-quality passes.
Initial pass found missing DI wiring and reterminated JPEG/PNG trailing payload
acceptance. Both were reproduced, fixed and verified as above. Reviewer briefly
hit a usage limit, then resumed and completed both passes on the final code:
no further confirmed blocking finding. Limitations requested by review are
documented above; this is not merely the earlier partial review result.

Read-only SQL inventory before tests showed only master/tempdb/model/msdb;
after final suite, query for TripMate_Test_* returned 0 rows, exit 0.
No commit, stage, push, PR change, merge, rebase or reset. Historical untracked
`docs/TM-79-implementation-prompt.md` is preserved and excluded from this work.
Final cleanup: removed only the three empty generated `tests/*/TestResults`
directories after resolving/validating each path inside this worktree and
confirming zero files. No data-bearing artifact was deleted. Test log root
contains 0 entries; no image fixture files were created. Both `git diff --check`
and `git diff --cached --check` exit 0. Final status contains exactly the seven
Task 8a files listed above plus the unchanged historical untracked prompt;
nothing staged, branch still ahead 4 at the same HEAD. Final scoped format
verification exited 0 before the documentation-only update.

## Task 8b — approved media-only SQL persistence (2026-09-29)

Owner approved M1–M5 and continuous 8b.1–8b.4 execution. This section records
only SQL durability, domain/EF and the internal media journal, not provider
upload/delete, cleanup worker, HTTP handlers or POI/visit behavior.

Starting/final HEAD: `0a29671d9ccccf91f7b1488a3766c38d7400caa7`.
Inspected origin/develop: `e8013f98a49b9fa1fbe4fcc412b562f7db8c37eb`;
merge-base: `525489b61ee2a2791b3d0cf71313e80a4a0445e7`.
No merge/rebase/reset/stage/commit/push/PR mutation. Historical untracked
`docs/TM-79-implementation-prompt.md` is preserved.

### TDD and focused evidence

| Slice/run | Passed | Failed | Skipped | Exit |
| --- | ---: | ---: | ---: | ---: |
| 8b.1 valid migration RED: missing media migration | 0 | 22 | 0 | 1 |
| 8b.2 initial migration GREEN | 22 | 0 | 0 | 0 |
| 8b.3 domain RED: unimplemented transitions | 0 | 17 | 0 | 1 |
| 8b.3 domain GREEN | 17 | 0 | 0 | 0 |
| 8b.3 EF SQL RED: unimplemented domain | 0 | 2 | 0 | 1 |
| 8b.3 EF SQL GREEN | 2 | 0 | 0 | 0 |
| 8b.4 journal RED: unimplemented journal | 0 | 9 | 0 | 1 |
| 8b.4 initial journal GREEN | 9 | 0 | 0 | 0 |
| DI registration RED | 0 | 1 | 0 | 1 |
| Expanded migration GREEN | 27 | 0 | 0 | 0 |
| Final focused application tests | 17 | 0 | 0 | 0 |
| Final focused API tests (migration 27, EF 2, journal 24) | 53 | 0 | 0 | 0 |

Final focused command:
`dotnet test TripMate.slnx -c Release --no-build --no-restore --filter "FullyQualifiedName~TripReviewMedia" --logger "console;verbosity=minimal"`.
Discovery: same solution/configuration/filter with `--list-tests`, exit 0;
all four classes discovered. Infrastructure.UnitTests has no matching media
tests (not a skipped test). Focused total: **70 passed, 0 failed, 0 skipped**.
Earlier slice commands used the same filter mechanism on the applicable
project, without `--no-build`, and `--no-restore`, Release, minimal logger.

Non-acceptance diagnostic failures were corrected, not counted as valid RED:
initial fixture used the wrong parent column name; initial restrictive-delete
test triggered EF relationship validation before SaveChanges; a collation
mutation needed to remove/re-add its dependent CHECKs before ALTER COLUMN.
One overlapping build failed MSB3027/MSB3021 because the running testhost held
the test DLL; subsequent builds/tests were serialized. At continuation Docker
was stopped: the guarded command exited 1 before tests. Docker Desktop and
only the identified SQL container were started; no SQL tests were skipped.

### Review findings closed

Independent two-pass review covered M1–M5 then schema/concurrency/code quality.
It found verification gaps rather than a confirmed production defect:

- foreign reservation initially failed the input guard; matching foreign-owner
  metadata now reaches the database booking ownership check;
- cleanup-winning race initially bypassed the journal; both orderings now call
  real journal methods on independent contexts with command barriers;
- trigger-based SQL injection could fail with SQL 334 rather than the intended
  error; the replacement asserts SQL 51000 after inspecting actual parent,
  two links and two Adopted operations within the transaction, then proves
  complete rollback from a new context;
- added atomic reserve collision, Reserved stale upload, other-parent replay,
  partial adoption, extra cleanup member and Cleaned rejection checks.

Reviewer reported final two-pass inspection with no remaining confirmed
production blocker; its turn subsequently hit a usage limit after delivering
that review message. Main agent owns final execution evidence.

Migration compares complete media table column/type/nullability/collation,
keys/indexes, FK actions, CHECK definitions/trust and identity shape against
canonical expected objects in a guarded transaction. Tests compare fresh and
upgraded inventories, reject altered shapes without repair, and preserve
nonempty parent/legacy fixture data and existing operation rowversion.
Input limit remains 5,000,000 bytes; stored output may exceed it.

SQL target remains the isolated container/identity documented in Task 8a,
127.0.0.1:14331, master only for test database administration. Credential is
read into memory from the verified container; no credential or .env change.
TRIPMATE_SQLSERVER_TEST_CONNECTION is process-scoped and restored in finally.
Only random TripMate_Test_* databases are created/dropped by the test helper.

Full regression exposed Development-host service validation failures: the new
SQL journal required concrete ApplicationDbContext, which the InMemory factory
removed. A focused Development CORS run reproduced the AggregateException
(0 passed, 1 failed, exit 1), identifying this dependency rather than a log lock.
An attempted context alias failed compilation (CS0029/CS1662) because the test
context is a separate type. The final correction removes the SQL-only journal
registration only in InMemory factories; they cannot pretend to provide SQL
durability. Real-SQL factories retain the actual journal and scoped context.
Production DI and authentication/CORS behavior are unchanged.

Changed files (Task 8b): database README, canonical schema and media migration;
spec/plan/this ledger; media operation/link entities; IReviewMediaJournal;
SqlServerReviewMediaJournal; both EF configurations; infrastructure DI and
ApplicationDbContext; integration project fixture-copy entries; nonempty
pre-media fixture; TripReviewSchemaInventory; TripMateApiFactory;
TripReviewMediaMigrationTests, TripReviewMediaSqlServerTests,
TripReviewMediaJournalTests and TripReviewMediaOperationTests.

### Final acceptance

First full SQL run finished exit 1: Application 759 passed, Infrastructure 107
passed, API 466 passed/4 failed, all 0 skipped (1332 passed/4 failed total).
All four failures were the concrete-context DI issue above. Focused corrected
Development/CORS/cookie/context checks: 5 passed, 0 failed, 0 skipped, exit 0.

Final command:
`dotnet test TripMate.slnx -c Release --no-build --no-restore --logger "console;verbosity=minimal" --blame-hang-timeout 3m`
with the same guarded SQL environment. Final process exit **0**:

| Project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Application.UnitTests | 759 | 0 | 0 |
| Infrastructure.UnitTests | 107 | 0 | 0 |
| Api.IntegrationTests | 470 | 0 | 0 |
| Total | **1336** | **0** | **0** |

API duration 2m55s. No retry-based masking, skip or global parallelism change.
Fresh final independent reviewer inspected both compliance and implementation,
including the InMemory-only journal removal, and reported no blocker. Its
stale plan wording finding is reconciled here and in the plan.

Formatting: scoped `dotnet format TripMate.slnx --no-restore --include <changed-cs-files>`
then the same command with `--verify-no-changes`, exit 0. Initial imports/final
newline findings were fixed. `dotnet build TripMate.slnx -c Release --no-restore`
exit 0, 0 warnings/errors. A final newline-only formatting change after build
does not alter the tested code. `git diff --check` and `git diff --cached --check`
exit 0. Nothing staged; branch remains ahead 5, HEAD unchanged.

SQL cleanup query returned server 88e5c351ed00 and 0 TripMate_Test_* databases,
exit 0. Test log root has 0 entries. Removed only three empty generated
tests/*/TestResults trees after validating absolute paths and absence of files.
No temporary media fixtures were created, no provider configuration/calls,
no credentials written, no .env changes. Task 8a image files unchanged.

Task 8a COMPLETE; Task 8b COMPLETE; 2b-media/3b-media/4b-media COMPLETE.
Task 8 overall PARTIAL. Original full Tasks 2/3/4 remain partial.
Task 8c NOT STARTED; Task 8d NOT STARTED; Task 9 NOT STARTED.
Task 7 DEFERRED; G-VISITS OPEN; G-LEGACY OPEN; G-POLICY DEFERRED.
No Cloudinary/provider implementation, no AI integration, no commit, no push.

## Task 8c/8d — provider recovery and real Cloudinary smoke (2026-09-30)

Status: **Task 8c COMPLETE; Task 8d COMPLETE; Task 8 COMPLETE**. Task 9 was not
started. Task 7/G-POLICY remain DEFERRED; G-VISITS and G-LEGACY remain OPEN.
The working base stayed at local HEAD
`3a0ee5831200efe58f3ab4d2b7db265b2a02d41b`; no commit, push or PR mutation was
performed. The historical untracked `docs/TM-79-implementation-prompt.md` was
preserved untouched.

### Implemented contract

- Added guarded additive SQL recovery metadata and fresh/upgrade inventory,
  parity, idempotency, wrong-shape rollback and existing-row preservation tests.
- Added the review-owned Cloudinary adapter under the immutable
  `tripmate/reviews` schema-version namespace. SQL stores only the operation's
  opaque provider suffix; no arbitrary URL/public ID is accepted.
- Added fenced upload and cleanup claims, two-minute leases, five-minute
  no-activity abandonment grace, bounded exponential retry, eight-attempt
  exhaustion/manual-recovery evidence, and an exact-operation cleanup worker.
- Provider I/O stays outside SQL transactions. SQL transitions require the
  current fence. `Adopted` assets are excluded from reconciliation and cleanup.
- The approved R7 amendment is enforced: lease expiry, cancellation, crash,
  stale fence, elapsed grace or ordinary absence cannot turn an unknown upload
  into terminal certainty. Unknown outcomes stay `CleanupPending`; repeated
  absence exhausts to manual recovery without becoming `Cleaned`. A late
  success is never adopted and is cleaned only through the current cleanup
  fence with positive resolution.
- Upload dispatch refreshes operation activity, so a delayed claim receives the
  full five-minute grace from its latest durable activity.
- Ordinary API test factories remove the real recovery hosted service for both
  in-memory and SQL modes, preventing timers from racing fixtures or calling a
  provider. Production DI still registers the worker.
- The real-provider smoke is explicit opt-in only
  (`TRIPMATE_CLOUDINARY_SMOKE=1`), uses a dedicated `/smoke` namespace, synthetic
  in-memory image bytes and a fresh bounded final-cleanup token.

### TDD and focused verification

Representative RED evidence was observed before each correction:

- SQL-backed `TripMateApiFactory` still registered the production recovery
  worker: 0 passed / 1 failed, exit 1.
- A reservation claimed four minutes after creation was incorrectly abandoned
  three minutes after that claim: 0 passed / 1 failed, exit 1.
- An expired stale upload completion persisted provider success; a complete
  pre-existing upload tuple was backfilled as unknown; cleanup selection could
  be starved by an earlier blocked batch; and exact accepted completion replay
  was not idempotent. Each focused test first failed for its expected contract
  reason and then passed after the minimal correction.
- The changed Cloudinary review root was initially not rejected. Host-start
  validation now proves the stable root starts and a changed root throws
  `OptionsValidationException`.

Final focused commands (all `-c Release`, `--no-build/--no-restore` where the
preceding build applied):

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter "FullyQualifiedName~TripReviewMediaRecoveryTests|FullyQualifiedName~ReviewMediaOrchestrationTests"
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --filter "FullyQualifiedName~ReviewMediaRegistrationTests|FullyQualifiedName~CloudinaryReviewMediaStorageTests"
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter "FullyQualifiedName~TripReviewMediaJournalTests|FullyQualifiedName~TripReviewMediaRecoveryMigrationTests|FullyQualifiedName~TripReviewMediaRecoverySqlTests"
```

| Focused gate | Passed | Failed | Skipped | Exit |
| --- | ---: | ---: | ---: | ---: |
| Application recovery/orchestration | 21 | 0 | 0 | 0 |
| Infrastructure adapter/registration | 34 | 0 | 0 | 0 |
| SQL journal/migration/recovery | 50 | 0 | 0 | 0 |
| SQL factory worker-isolation reproducer | 1 | 0 | 0 | 0 |

The SQL connection was process-scoped and obtained without printing credentials
from the already approved isolated local container:
`tripmate-tm70-sql`, full container ID
`88e5c351ed000b324d25e6573104211b2d8c39902945b93567828820459e9436`,
bound only as `127.0.0.1:14331 -> 1433`. Test helpers used `master` only to
create/drop random `TripMate_Test_*` databases. No Azure/shared database was
read or modified.

### Real provider smoke

Development Cloudinary values were read process-only from the existing ignored
TM-207 `.env`; names, values, URLs and public IDs were not printed or written.
With `TRIPMATE_CLOUDINARY_SMOKE=1` and the stable review root, the smoke executed
upload -> HTTPS delivery validation -> probe -> exact destroy -> repeated
absence confirmation. Result: **1 passed, 0 failed, 0 skipped, exit 0**, 4s.
The same smoke also ran inside the final full suite. Its `finally` path used a
separate 30-second cleanup budget, and a green result required confirmed zero
operation-owned provider residue.

### Final quality gates

Scoped format covered every changed/untracked Task 8c C# file using Windows
relative paths and then `--verify-no-changes`: exit 0. Release build:

```powershell
dotnet build TripMate.slnx -c Release --no-restore --verbosity minimal
```

Exit 0, 0 warnings, 0 errors. Whole-solution format verification remains exit 1
only for 12 pre-existing, unchanged `FINALNEWLINE` findings outside the Task 8c
change set; no unrelated file was mass-formatted.

Final SQL- and provider-enabled command:

```powershell
dotnet test TripMate.slnx -c Release --no-build --no-restore --blame-hang-timeout 3m --logger "console;verbosity=minimal"
```

| Project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Application.UnitTests | 780 | 0 | 0 |
| Infrastructure.UnitTests | 142 | 0 | 0 |
| Api.IntegrationTests | 497 | 0 | 0 |
| **Total** | **1419** | **0** | **0** |

Final process exit **0**; API duration 2m54s. The run used no retries to hide
failures and no global parallelism reduction. An earlier full run exposed four
Development/InMemory host failures because newly added SQL recovery services
survived the test context replacement. A five-case minimal reproducer isolated
the issue; the factory now removes the SQL-only services in memory and removes
the timer worker for every ordinary test host. The corrected full result above
is the final acceptance evidence.

### Review and cleanup

Fresh independent spec/contract and code/concurrency/security reviews both
approved the final tree with no remaining blocker. Findings fixed before those
approvals included stale upload-success evidence, migration backfill of known
uploads, immutable provider-root validation, cleanup starvation, delayed-claim
grace, ordinary test-host worker isolation, bounded smoke cleanup and stale plan
wording.

The final SQL query returned zero `TripMate_Test_*` databases. The integration
test log root contained zero entries. Three generated `tests/*/TestResults`
trees contained directories only and zero files; their absolute paths were
validated inside this worktree before removal. Provider residue was zero by the
real smoke's required absence check. No temporary media file was written; image
bytes were generated in memory. No credentials or `.env` values were written.

Task matrix after this entry: Task 8a COMPLETE; Task 8b COMPLETE; Task 8c
COMPLETE; Task 8d COMPLETE; Task 8 overall COMPLETE. Task 9 COMPLETE. Tasks
10–17 remain unstarted. Task 7 remains DEFERRED / FUTURE ENHANCEMENT;
G-VISITS/G-LEGACY remain OPEN; G-POLICY remains DEFERRED.

## Task 9 — Submit transaction and duplicate recovery (9a + 9b + 9c)

Status: **COMPLETE** (Uncommitted in working tree for user review).

### Scope and Implementation

1. **Task 9a — Application Handler and Domain Orchestration**:
   - `ITripReviewWriteLock`: Transaction-scoped booking lock interface.
   - `IReviewMediaCoordinator`: Interface extracted from `ReviewMediaCoordinator` for clean architecture testing and decoupling.
   - `SubmitTripReviewCommandHandler`: Complete implementation of submit pipeline:
     - Authentication and active Traveler verification.
     - Early context reading and duplicate check (new parent or legacy `social.Reviews`).
     - Booking completion and provenance consistency check.
     - Input structural validation and contextual eligibility validation (`TripReviewInputEligibility`).
     - Image preparation outside lock/transaction via `IReviewMediaCoordinator`.
     - Transaction-scoped write lock acquisition (`ITripReviewWriteLock.AcquireAsync`).
     - Locked context recheck for concurrent duplicates or legacy conflicts. The final read holds compatible shared `HOLDLOCK` evidence locks on booking, account and derived-subject rows through commit, while the booking applock serializes audited parent/legacy writers.
     - Unscreened parent review persistence (`PolicyVersion = null`).
     - Atomic media adoption (`IReviewMediaJournal.AdoptAsync`) or parent-only save.
     - Out-of-transaction loser media cleanup uses a bounded independent token and is best-effort, so cleanup failure cannot replace an already determined business result; durable Reserved/Uploaded rows remain scanner-recoverable.
     - Image rejection maps to public `trip_review.invalid_input`; journal/provider failure maps to `trip_review.storage_unavailable`. Adoption failure is sanitized and never exposes internal `review_media.*` codes.
     - Strict duplicate mapping is delegated to `SqlServerTripReviewPersistenceErrorClassifier`: only a real `Microsoft.Data.SqlClient.SqlException`, number 2601/2627, and exact quoted `'UX_TripReviews_Booking'` identity map to Duplicate; arbitrary lookalikes propagate.
   - `SubmitTripReviewTests`: 33 executed cases covering authentication, role/status, frozen precedence, early/final legacy conflicts, normalization/null policy, zero/multi-photo flows, media error taxonomy, sanitized adoption failure, bounded cleanup, route/POI/CSP eligibility, final account recheck and strict duplicate mapping.

2. **Task 9b & 9c — Real SQL Write Lock, Concurrency Race, and Duplicate Recovery**:
   - `SqlServerTripReviewWriteLock`: Real SQL Server exclusive transaction-scoped lock using `sp_getapplock` on resource `TripMate:TripReview:Booking:{bookingId}` with 15-second timeout.
   - `SubmitTripReviewCommandHandler`: aborts the caller-owned transaction when adoption fails after parent insertion; the shared transaction primitive remains unchanged.
   - `TripMateApiFactory`: Provided `UnsupportedInMemoryReviewMediaJournal`, `UnsupportedInMemoryReviewMediaCoordinator`, and `NoOpTripReviewWriteLock` for InMemory test fixtures to maintain clean startup validation while preventing fake durability claims.
   - `SubmitTripReviewSqlServerTests`: 11 real-SQL Server integration tests, plus the existing inactive-reader regression in the final 12-case focused SQL filter, covering:
     - `Submit_PersistsNormalizedUnscreenedParentWithoutLegacyMirror`: verifies exact normalized text, null policy version, and zero mirror rows in legacy `Reviews`.
     - `FailureAfterParentInsert_RollsBackParentChildrenAndAdoption`: verifies transactional rollback when media adoption fails, leaving 0 parent rows and 0 media links in a fresh DbContext.
     - `SameBookingRace_PublishesOne_AdoptsWinnerAndMarksLoserCleanupPending`: simulates concurrent submissions on the same booking using barriers; write lock serializes them so exactly 1 wins (Published + Adopted media) and 1 loses (Duplicate error + loser media marked `CleanupPending`).
     - `DifferentBookingLocks_CanBeHeldConcurrently`: verifies that submissions on different bookings do not block each other.
     - `WriteLock_RejectsUseWithoutCallerOwnedTransaction`: verifies the lock cannot bypass its caller-owned transaction.
     - `SameTravelerDifferentBookings_CanSubmitConcurrently`: verifies two complete submissions for different bookings can both publish without global serialization.
     - `LockedEvidenceRows_BlockConcurrentAuthorizationMutationUntilCommit`: proves with a barrier that a concurrent account-status mutation cannot pass before submit commits.
     - `AmbiguousAndMismatchedLegacyRows_AreConflictsAndRemainUnchanged`: verifies conservative conflict classification and preservation.
     - `LegacyAppearingAfterEarlyRead_IsRejectedByLockedRecheck`: verifies that a concurrent legacy review inserted during media preparation is detected by the locked recheck and rejected with Duplicate.
     - `AmbiguousLegacyAppearingAfterMediaPreparation_IsConflictAndCleansPreparedBatch`: verifies late `legacy_conflict`, preserved legacy rows and exact prepared-batch cleanup.
     - `CommittedButLostResponse_IsRecoveredByGet_AndLaterPostIsDuplicate`: simulates lost response after commit; subsequent submit returns Duplicate 409, while GET context query returns the original published review.

### TDD evidence

- Application RED: the focused `FullyQualifiedName~SubmitTripReview` command
  exited 1 while the new tests referenced the not-yet-created media coordinator
  abstraction/write-lock contract (`CS0246`/`CS0535`).
- Application GREEN after implementation and review corrections: Submit-only
  executed 33 cases; the combined context/media/submit slice exited 0 with 76
  passed, 0 failed, 0 skipped. The infrastructure classifier slice exited 0
  with 3 passed, 0 failed, 0 skipped.
- SQL RED: the SQL-enabled focused command discovered six initial tests and
  exited 1 with 5 passed, 1 failed, 0 skipped. The expected failure showed the
  parent row survived a simulated adoption failure after insertion.
- SQL GREEN after transaction-abort correction and review-driven race coverage:
  the final current-binary filter exited 0 with 12 passed, 0 failed, 0 skipped
  (11 submit tests plus the ordinary inactive-reader regression).

### Quality Gates

- Scoped `dotnet format`: exit 0 on all modified/new files.
- Whole-solution whitespace verification remains exit 1 only for 7 historical
  `FINALNEWLINE` findings outside the Task 9 changed-file set; no unrelated
  mass-formatting was applied.
- Release build: `dotnet build TripMate.slnx -c Release --no-restore`: exit 0 (0 warnings, 0 errors).
- Full solution regression (SQL Server enabled):

```powershell
$env:TRIPMATE_SQLSERVER_TEST_CONNECTION="<isolated-local-sql-connection>"
dotnet test TripMate.slnx -c Release --no-build --logger "console;verbosity=minimal"
```

| Project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Application.UnitTests | 813 | 0 | 0 |
| Infrastructure.UnitTests | 144 | 0 | 1 |
| Api.IntegrationTests | 508 | 0 | 0 |
| **Total** | **1,465** | **0** | **1** |

*(Note: The 1 skipped test is the opt-in Cloudinary provider smoke test requiring external network credentials).*

### Database & Residue Cleanup

- SQL Server test databases query: returned 0 `TripMate_Test_*` databases.
- Test logs & residue: 0 files created or leftover.
- Environment: worktree `.env` absent and still ignored. No secret was staged,
  logged or persisted; the owner reported the previously exposed credential rotated.
- Git ownership compliance: `git -c safe.directory=D:/CapStone/Capstone_BE_tm79` used per-command; global Git config untouched.
- Working tree state: Task 9 files remain UNCOMMITTED for user review. No commit, push, or PR mutation performed.

### Legacy writer audit and protocol boundary

Repository source contains no production writer that creates booking-linked
legacy `social.Reviews`; the only booking-linked legacy inserts found are SQL
fixtures/tests. Task 9 serializes every currently supported new TripReview
writer through the booking-scoped application lock and rechecks both new and
legacy rows while holding it. An external/raw SQL writer that ignores this
protocol is outside the serialization claim; a visible row is still rejected
by the final locked recheck. No legacy row is rewritten or auto-migrated.

### Independent post-fix review

Two independent read-only reviews first found actionable issues in media-error
taxonomy, public error sanitization, cleanup bounds, frozen precedence, legacy
conflict coverage, exact SQL duplicate provenance and final evidence stability.
All findings were corrected through RED/GREEN tests. The final spec/contract
review and the final security/concurrency review both approved the current tree
with no remaining code blocker. All final test counts above were produced from
binaries rebuilt after the reviewed source and test changes.

## Task 10 — Canonical new TripReview edit transaction (2026-09-30)

Status: **COMPLETE for canonical new `TripReview` edit**. Task 11 and Task 12
were not started. Task 7/G-POLICY remain DEFERRED; G-VISITS and G-LEGACY remain
OPEN. Legacy `social.Reviews` rows remain unchanged and intentionally cannot be
edited through the canonical command while the legacy adapter/disposition gate
is unresolved.

Starting and final committed HEAD:
`e8ca568619c0581d7b2e4c528c82f88fa49f1e99`. Baseline status was branch
`feature/datmnt-submit-trip-review` ahead of its remote by eight commits with
only historical untracked `docs/TM-79-implementation-prompt.md`. That prompt
remains untouched/untracked. No commit, push, rebase, merge, reset or Draft PR
#30 mutation occurred.

### Implementation and behavior

- Added `EditTripReviewCommandHandler`: authenticate Active Traveler, perform
  advisory owned-context/new-parent gate and structural validation, then use
  the existing short transaction and booking-scoped `sp_getapplock` protocol.
  The locked read rechecks account, owner, supported Completed booking,
  canonical parent, subject consistency and legacy conflict before mutation.
- The persisted original deadline is exclusive (`now < deadline`). Equal and
  later instants return `trip_review.edit_expired`; `CreatedAtUtc` and
  `EditDeadlineUtc` never change.
- The frozen canonical Base64 eight-byte command version is decoded only after
  validation. The current database rowversion is compared after the booking
  lock; EF concurrency conflicts are also mapped narrowly to
  `trip_review.stale_version`. No arbitrary SQL exception is reclassified.
- Only overall rating, normalized title/content and display-name preference
  reach `TryEditPublished`. A current unscreened edit always stores
  `PolicyVersion = null`, including clearing a genuine earlier version.
- A preference change regenerates the snapshot using the current locked
  profile name. Keeping the preference preserves the prior snapshot across an
  account rename. Blank current name uses the approved `Traveler` fallback.
- Subject, owner, original timestamps/deadline, route pacing, CSP score and
  existing media remain unchanged. Edit has no image inspector, coordinator,
  journal, Cloudinary or moderator dependency/call. The current schema has no
  linked POI-child contract; tests prove edit creates no `social.Reviews` row
  rather than claiming positive linked-child support.
- Business failures abort the transaction through an internal exception so
  the shared transaction helper cannot commit a failed Result after mutation.
  Persistence exceptions roll back normally. Verification reads important
  success/failure state through a new DbContext.

Changed Task 10 paths:

- `src/TripMate.Application/Features/TripReviews/Common/TripReviewErrorCodes.cs`
- `src/TripMate.Application/Features/TripReviews/Edit/EditTripReviewCommandHandler.cs`
- `tests/TripMate.Application.UnitTests/Features/TripReviews/EditTripReviewTests.cs`
- `tests/TripMate.Api.IntegrationTests/Reviews/EditTripReviewSqlServerTests.cs`
- `plans/TM-79-plan.md`
- `docs/TM-79-verification.md`

### TDD and focused evidence

Application RED command:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --no-restore --filter "FullyQualifiedName~EditTripReviewTests" --logger "console;verbosity=minimal"
```

After correcting one test-only setter compilation issue (not counted as RED),
the compilable handler scaffold threw its explicit not-implemented behavior:
**0 passed, 27 failed, 0 skipped, exit 1**. The same command after minimal
implementation and a fixture-context correction finished **27 passed,
0 failed, 0 skipped, exit 0**. Coverage includes unauthenticated/inactive/
foreign/missing/legacy paths, structure/version validation, before/equal/after
deadline, normalization, policy clearing, all D5 snapshot directions, final
locked account/business changes and immutable C4/subject/timestamps.

SQL tests were then added and discovered under
`FullyQualifiedName~EditTripReviewSqlServerTests`. The first executable SQL
contract run occurred after Application GREEN and was already **13 passed,
0 failed, 0 skipped, exit 0**. Therefore no separate valid SQL RED was observed;
none is fabricated or relabeled. This is a recorded sequencing deviation, not
a skipped SQL gate. The suite uses genuine rowversions and independent
DbContexts to prove just-before/equal/after deadline, stale preservation,
normalized round-trip, policy clearing, media/C4/subject/timestamp immutability,
D5 rename behavior, foreign/legacy isolation, injected SaveChanges rollback,
same-version exactly-one-winner/stale-loser and different-booking concurrency.
Both races use deterministic barriers; a reviewer-requested 15-second token
now bounds the same-booking test without sleeps or retries. Final affected SQL
rerun: **13 passed, 0 failed, 0 skipped, exit 0**.

Shared focused regressions:

- Application domain/validation/context/submit/edit filter: **199 passed,
  0 failed, 0 skipped, exit 0**.
- SQL persistence/context/submit/edit filter: **42 passed, 0 failed, 0 skipped,
  exit 0**.
- Scoped format and final scoped `--verify-no-changes` over all four Task 10
  C# paths: exit 0.
- `dotnet build TripMate.slnx -c Release --no-restore --verbosity minimal`:
  exit 0, 0 warnings, 0 errors.

Final SQL-enabled whole-solution command:

```powershell
dotnet test TripMate.slnx -c Release --no-build --no-restore --logger "console;verbosity=minimal" --blame-hang-timeout 3m
```

| Project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Application.UnitTests | 840 | 0 | 0 |
| Infrastructure.UnitTests | 144 | 0 | 1 |
| Api.IntegrationTests | 521 | 0 | 0 |
| **Total** | **1,505** | **0** | **1** |

Final process exit 0; API duration 2m58s. The sole skip is the explicit
opt-in real Cloudinary smoke already completed in Task 8d. Task 10 makes no
provider change/call; every executable Task 10 SQL test ran with zero skip.

One preceding whole-solution attempt exited 1 because the unrelated
`AuditLogOutcomeTests.Queries_ExposeRecordedOutcomeAndBusinessReason(Failure)`
case observed `[REDACTED]` once. The affected theory then passed 2/2 in
isolation, the combined audit-log slice passed 46/46, and the complete
Application suite passed 840/840 in five consecutive runs without a source
change. No retry or production change was added to hide the observation. The
final full SQL-enabled run shown above subsequently completed with exit 0.

### Independent review and cleanup

Fresh independent spec/contract and transaction/concurrency/security reviews
both approved the implementation with no blocking finding. They confirmed the
exclusive deadline, original timestamp/deadline, final active-owner recheck,
rowversion race, normalization, null policy, D5 snapshots, rollback, immutable
media/C4 and absence of HTTP/aggregate/AI/provider scope. Both called out the
same explicit residual: positive linked POI-child preservation cannot be
claimed because that schema remains gated by G-VISITS. The security reviewer
also noted an unbounded race-test wait; a 15-second token was added, the SQL
suite returned 13/13, and both reviewers re-approved the final state.

The approved isolated target remained container `tripmate-tm70-sql`, bound to
`127.0.0.1:14331`; credentials were read process-only and never printed or
persisted. Final inventory: zero `TripMate_Test_*` databases, zero data-bearing
TestResults files, zero generated media files and no new provider call/asset.
One ignored historical `uc04-s11.log` last written 2026-09-27 was removed from
the Release output; it was generated test output, not user/business data, and
can be regenerated by tests. Final test log file count is zero. Worktree `.env`
is absent and ignored. A scoped secret-pattern scan found no credential path.

Task matrix at the Task 10 checkpoint: Task 8 COMPLETE; Task 9 COMPLETE and
committed; Task 10 COMPLETE and committed; Task 11 NOT STARTED;
Task 12 NOT STARTED; Task 7 DEFERRED; G-POLICY DEFERRED; G-VISITS OPEN;
G-LEGACY OPEN. Current-delivery Task 6 structural ordering and exact normalized
persistence evidence is complete across Tasks 9 and 10; dormant moderation
remains future-only.

## Task 11 — Published aggregates and legacy compatibility (2026-10-01)

Status: **PARTIAL**. Tour aggregation and the executable legacy POI slice are
implemented. Linked POI-child aggregation remains blocked by G-VISITS because
the authoritative schema/model has no parent linkage from `social.Reviews` to
`social.TripReviews`; no schema or relationship was invented.

- Empty Tour aggregate is `{ average: null, count: 0 }`.
- Published Tour parents and legitimate legacy Tour rows contribute without
  blanket `DISTINCT` or mirror creation.
- Itinerary-only and unrelated targets are excluded.
- Same-booking canonical/legacy overlap returns the existing
  `trip_review.legacy_conflict` and leaves source rows unchanged.
- POI Detail and Explore share the legacy POI source filter, preserve one-digit
  `MidpointRounding.AwayFromZero`, and retain their query budgets.

Focused verification: Release build PASS (0 warnings/errors); POI Application
119/119 PASS; aggregate SQL 19/19 PASS; POI SQL 14/14 PASS. SQL used the identified
local server at `127.0.0.1:14331` and disposable `TripMate_Test_*` databases.
An earlier attempt hit connection-refused while the container was stopped; after
starting it and confirming the port, both focused SQL suites passed. Gated
linked-child cases remain explicitly unexecuted rather than fabricated.

TDD evidence/deviation: the aggregate production skeleton was introduced before
the new SQL contract file, so the required SQL-RED-first sequence was not met
and no behavioral SQL RED is claimed. The first SQL attempt was connection
refused while the container was stopped (infrastructure only). A genuine POI
regression RED then exposed 19 EF InMemory translation failures from projecting
the grouped DTO; the shared source was narrowed to a queryable Review filter,
after which POI Application tests passed 119/119. Final aggregate SQL discovery
and execution passed 19/19.

Fresh review passes found and drove fixes for: target-restricted Tour overlap,
asymmetric overlap when the legacy contributor identified the requested Tour,
missing equal-valued UNION ALL proof, a non-midpoint rounding fixture, DTO names
inconsistent with the frozen `{ average, count }` contract, and missing POI
same-booking canonical/legacy conflict detection. The final POI fix projects a
shared conflict subquery inside the existing Detail/Explore SQL commands, returns
the Application `trip_review.legacy_conflict`, and is covered for both handlers
without adding N+1/query-count overhead. Public 409/ProblemDetails/OpenAPI mapping
is explicitly Task 12 work and was not invented here. Both code/query reviewers
approved the corrected scoped implementation; the final contract review's only
remaining notes were the ledger corrections made in this entry.

Final SQL-enabled regression after all Task 11 fixes: Application 840 passed;
Infrastructure 144 passed plus 1 expected opt-in Cloudinary smoke skip; API/SQL
540 passed. Total: **1524 passed, 0 failed, 1 expected skip**, exit code 0.

Cleanup found two disposable `TripMate_Test_*` databases left by full-suite
runs that were deliberately interrupted to address review findings. Their exact
test-only identities were verified, both were dropped, and the final temporary
database count is zero. Final repository residue checks found zero data-bearing
TestResults files and zero `.trx`/dump/temp/log files. No provider call or asset
was created. `.env` remains ignored and untracked; the Task 11 credential-pattern
scan returned zero matches.

Final matrix: Task 8 COMPLETE; Task 9 COMPLETE; Task 10 COMPLETE; Task 11
PARTIAL (Tour aggregate COMPLETE, legacy POI compatibility COMPLETE, linked new
POI-child aggregate BLOCKED BY G-VISITS/deferred child schema); Task 12 NOT
STARTED; Task 13 NOT STARTED; Task 7 DEFERRED; G-POLICY DEFERRED; G-VISITS OPEN;
G-LEGACY OPEN. No Task 11 commit or push was made and PR #30 was not modified.

## Task 12 — HTTP endpoints and OpenAPI (2026-10-01)

Status: **COMPLETE for the currently supported TM-79 Backend delivery**. Starting
and final committed HEAD remained
`23ee868f95e042e5ec1c8eb20a3fdcaefda4129d`; Task 11's existing uncommitted
aggregate changes and the historical untracked
`docs/TM-79-implementation-prompt.md` were preserved. No commit, push, merge,
rebase, reset, stash or PR #30 mutation occurred.

Task 12 changed paths:

- `plans/TM-79-plan.md`
- `docs/TM-79-verification.md`
- `src/TripMate.Api/Authorization/ActiveTravelerAuthorization.cs`
- `src/TripMate.Api/Authorization/AuthorizationProblemDetailsAttribute.cs`
- `src/TripMate.Api/Authorization/ProblemDetailsAuthorizationMiddlewareResultHandler.cs`
- `src/TripMate.Api/Common/ApiControllerBase.cs`
- `src/TripMate.Api/Common/DisableFormValueModelBindingAttribute.cs`
- `src/TripMate.Api/Common/ErrorCodeProblemDetails.cs`
- `src/TripMate.Api/Common/TripReviewValidationExceptionFilterAttribute.cs`
- `src/TripMate.Api/Controllers/V1/Requests/TripReviewRequests.cs`
- `src/TripMate.Api/Controllers/V1/TripReviewsController.cs`
- `src/TripMate.Api/OpenApi/ProblemDetailsContractSchemaFilter.cs`
- `src/TripMate.Api/OpenApi/TripReviewOpenApiFilter.cs`
- `src/TripMate.Api/Program.cs`
- `src/TripMate.Application/Features/TripReviews/Common/TripReviewContextDto.cs`
- `src/TripMate.Application/Features/TripReviews/Common/TripReviewErrorCodes.cs`
- `tests/TripMate.Api.IntegrationTests/Infrastructure/TestJwtTokenFactory.cs`
- `tests/TripMate.Api.IntegrationTests/Reviews/TripReviewEndpointTests.cs`
- `tests/TripMate.Api.IntegrationTests/Reviews/TripReviewOpenApiTests.cs`

HTTP behavior and safety:

- `GET /api/v1/bookings/{bookingId}/review` exposes the existing owner context
  with `none`/`new`/`legacy` discriminators, current nullable C4/media fields,
  deadline/version and public-safe identity. POI capability remains unavailable
  with `visitEvidenceUnavailable`; no linked POI child or positive visit evidence
  is fabricated while G-VISITS is open.
- New and legacy review timestamps use the frozen canonical ISO 8601 UTC wire
  spelling with trailing `Z`; the converter is property-scoped to TM-79 DTOs and
  does not change unrelated serialization.
- `POST` accepts exactly one bounded JSON `metadata` form field and zero to five
  `files` parts. It enforces 64,000 metadata UTF-8 bytes, 5,000,000 actual bytes
  per file and 26,000,000 bytes for the complete multipart body before image/
  provider work. It invokes the Task 9 transaction and returns direct public DTO
  data without internal provider identifiers.
- `PUT` accepts JSON only and requires exactly `overallRating`, `title`,
  `content`, `publishDisplayName` and `version`. Unknown and create-only members
  return `trip_review.invalid_edit_payload` even when null, empty or unchanged.
  Wrong value types/nullability return `trip_review.invalid_input`. The strict
  reader is endpoint-scoped; unrelated JSON model binding remains unchanged.
- Authorization middleware executes before MVC resource binding and business
  access. Missing/invalid auth maps to 401 `trip_review.unauthorized`; inactive
  or wrong-role callers map to 403 `trip_review.forbidden` without disclosing
  booking/review state. Inactive-Traveler tests cover malformed, unsupported and
  oversized request bodies; a signed valid Traveler JWT covers the real bearer
  authentication and database-backed Active-account policy path.
- Feature mappings are: invalid input/edit payload and the frozen C4/POI
  eligibility codes -> 400; unauthorized -> 401; forbidden -> 403; missing or
  foreign booking -> 404; duplicate/stale/edit-expired/not-completed/
  inconsistent-context/legacy-conflict -> 409; body too large -> 413;
  unsupported media type -> 415; storage unavailable -> 503. Unexpected failures
  remain sanitized 500 ProblemDetails. Tests assert provider/SQL/exception data
  is not leaked. Task 11 `trip_review.legacy_conflict` now has the shared safe
  409 mapping; canonical POST conflict is proven through real HTTP.
- OpenAPI documents GET response, POST multipart metadata/files and limits, PUT
  JSON-only closed five-member request, rating/text/POI/version constraints,
  required/nullable fields, lower-camel C4 enum values, and only
  `application/problem+json` for declared error responses with required
  `status`/`errorCode` (plus `errors` for validation). Runtime and documented
  request shapes agree.

TDD evidence and sequencing deviations:

- Initial route/OpenAPI RED: 0 passed / 4 failed because the review route was
  absent. Expanded HTTP RED: 0 passed / 21 failed for the same missing route.
- A later focused malformed-multipart RED was 0 passed / 2 failed because MVC
  parsed form input before feature mapping; the endpoint-scoped resource filter
  fixed that ordering without changing global binding.
- Detailed OpenAPI schema assertions were added after the operation-filter
  implementation. The original route/OpenAPI assertion was RED first, but a
  separate detailed-schema RED was not captured; this sequencing deviation is
  recorded rather than reconstructed.
- The first combined SQL endpoint/OpenAPI run passed 30/34. Four failures were
  test-host DI leakage to the real Cloudinary adapter (`Cloud name must be
  specified`). The SQL factory was corrected to inject deterministic test media
  services; production provider behavior was not changed.
- Independent review then identified five contract-hardening gaps: inactive
  account authorization occurred after body parsing, FluentValidation lacked
  the feature `errorCode`, form binding did not authoritatively preserve raw
  metadata bytes/charset, total-body epilogue counting needed explicit evidence,
  and OpenAPI constraint/problem schemas were incomplete. Expanded RED was
  **41 passed / 9 failed** for those expected reasons. The fixes add the
  endpoint policy, endpoint-scoped validation filter, bounded raw
  `MultipartReader` with strict UTF-8 and request draining, and exact OpenAPI
  schema/media constraints. The initial synthetic exact-total fixture produced
  400 because its appended bytes made invalid multipart framing; it was replaced
  with a valid maximum payload (64,000-byte metadata plus five 5,000,000-byte
  files), while the chunked post-closing-boundary 26,000,001-byte case continues
  to prove 413 enforcement.
- The independent re-review found three additional blockers. Standards-valid
  quoted `charset="utf-8"` was rejected, zero-offset timestamps serialized as
  `+00:00` instead of the frozen trailing `Z`, and the new request schema filter
  used raw wire-property strings contrary to `AGENTS.md` section 4.1. Two focused
  RED tests failed for the first two exact reasons. The reader now removes
  charset quotes, TM-79 timestamp properties use a scoped canonical UTC
  converter, OpenAPI exposes new/legacy response discrimination plus `Z`
  constraints, and all schema property lookup derives from `nameof(...)`
  through the camel-case naming convention.
- The next re-review confirmed those three fixes and found one OpenAPI-only
  gap introduced by the new polymorphic schema: the `kind` discriminator lacked
  explicit runtime-value mapping. It now maps `new` and `legacy` to their exact
  component schemas and constrains each derived `kind` enum; focused OpenAPI
  tests assert both mapping and values.

Verification:

- Final focused HTTP/OpenAPI SQL run: **59 passed, 0 failed, 0 skipped**, exit 0.
- Application TripReview regression: **244 passed, 0 failed, 0 skipped**, exit 0.
- Context/submit/edit/aggregate/endpoint/OpenAPI SQL regression: **116 passed,
  0 failed, 0 skipped**, exit 0.
- Scoped `dotnet format` and scoped `dotnet format --verify-no-changes` over all
  17 Task 12 C# paths: exit 0.
- `dotnet build TripMate.slnx -c Release --no-restore`: exit 0, **0 warnings,
  0 errors**.
- Final full SQL-enabled regression after all review fixes: Application **840
  passed**; Infrastructure **144 passed / 1 skipped**; API/SQL **599 passed**.
  Total: **1,583 passed, 0 failed, 1 expected skip**, final exit 0. API/SQL
  duration was 14m52s. The
  sole skip is the explicit opt-in real Cloudinary smoke already proven during
  Task 8d; there were no unexpected skips.
- An earlier full-suite attempt was cancelled after Docker Desktop stopped and
  the SQL container exited, producing widespread connection-refused failures.
  Docker/container/port health was restored and the complete suite was rerun
  from the beginning; only the final exit-0 run is acceptance evidence.

Review and cleanup:

- `.agents/skills/open-code-review-delegate/SKILL.md` was absent. `ocr delegate
  preview --format json --from origin/develop --to HEAD` exited 0 and identified
  the committed review range; `ocr delegate rule --format json` over all Task 12
  paths exited 0 and resolved the default correctness/security/performance/
  maintainability/test-coverage rule. Because Task 12 is uncommitted, the full
  working-tree files were also reviewed directly against those rules.
- Independent final re-review is **APPROVED** with no remaining blocker after
  closing authorization-ordering, validation ProblemDetails, raw multipart,
  canonical UTC, schema-name, and explicit `new`/`legacy` discriminator findings.
- Scoped credential-pattern inspection found only existing configuration access
  and the test database connection-property reference, not a literal credential.
  Worktree `.env` is absent and ignored; no secret value was printed or persisted.
- Final SQL inventory contains zero `TripMate_Test_*` databases. TestResults has
  zero files; repository search found zero `.trx`, dump, log or temp residue.
  No provider smoke was run and no provider asset was created by Task 12.

Final matrix: Task 8 COMPLETE; Task 9 COMPLETE; Task 10 COMPLETE; Task 11
PARTIAL (Tour aggregate COMPLETE, legacy POI compatibility COMPLETE, linked new
POI-child aggregate BLOCKED BY G-VISITS/deferred child schema); Task 12
COMPLETE; Task 13 NOT STARTED; Task 7 DEFERRED; G-POLICY DEFERRED; G-VISITS
OPEN; G-LEGACY OPEN. This is not a claim of full UC-33 or BR-94 compliance.

## Task 13 — Backend regression and reproducible handoff (2026-10-03)

Status: **COMPLETE for the currently supported `commerce.Bookings` Backend
delivery**. Final verdict: **BACKEND READY FOR REVIEW with the gates below**.
This is not full TM-79, Report 3 UC-33 or BR-94 completion: Mobile, Web and
cross-repository UAT remain Tasks 14-17, automated screening remains deferred,
and the positive linked POI-child and deployed-legacy gates remain open.

### Repository and drift evidence

- Starting and final committed HEAD:
  `91b31a25712ed229bb2da94f981c1263b936da57`.
- Read-only fetched `origin/develop`:
  `d1981071452426b050ec1c424fe4b843327f6669`; merge-base:
  `525489b61ee2a2791b3d0cf71313e80a4a0445e7`.
- Draft PR #30 remained OPEN, base `develop`, head
  `feature/datmnt-submit-trip-review`, remote head
  `fed9ca99f63dd4f73f56d4c963d8333ecced4f28`, and GitHub reported the remote
  state MERGEABLE. No PR mutation occurred.
- The baseline worktree contained only the untracked historical
  `docs/TM-79-implementation-prompt.md`. It remained untouched and excluded.
- A read-only synthetic merge-tree against current `origin/develop` reports
  conflicts in `src/TripMate.Api/Common/ApiControllerBase.cs`,
  `src/TripMate.Infrastructure/DependencyInjection.cs`, and
  `tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs`.
  Reconciliation is a required pre-merge follow-up; Task 13 did not merge,
  rebase, cherry-pick, reset, stash or alter the frozen verified branch.

Task 13 changed, uncommitted paths are:

- `plans/TM-79-plan.md`
- `docs/TM-79-verification.md`
- `docs/TM-79-smoke-test.md`
- `src/TripMate.Application/Features/TripReviews/GetContext/GetTripReviewContextQueryHandler.cs`
- `tests/TripMate.Api.IntegrationTests/Reviews/TripReviewEndpointTests.cs`
- `tests/TripMate.Api.IntegrationTests/Reviews/Task13CloudinaryHandoffSmokeTests.cs`

### Cross-layer handoff evidence

The canonical handoff test uses a real signed Traveler JWT and HTTP endpoints,
an owned Completed Tour booking, isolated SQL, new hosts/request scopes and the
deterministic media adapter. It proves:

1. initial GET returns `none`;
2. multipart POST persists one canonical parent, one adopted media link and
   normalized trimmed text;
3. a second POST returns safe 409 duplicate without a second parent or media
   adoption, and owner GET recovers the single committed review;
4. a fresh GET returns version, canonical UTC timestamps, public-safe initials,
   normalized values and the persisted media ID/HTTPS URL;
5. a valid five-member PUT edits rating/title/content/name consent using the
   returned rowversion;
6. a fresh final GET shows a new version and the edited values while
   `createdAtUtc`, `editDeadlineUtc`, C4 fields and media ID/URL remain unchanged;
   publishing the name changes the public display to the approved owner name;
7. a new aggregate context reports average 5/count 1 after create and average
   3/count 1 after edit, so the edit changes average without adding a
   contributor; and
8. final SQL contains exactly one parent, media row and media operation.

The handoff RED first failed because GET mapped adopted media to an empty list
(`media` length 0 instead of 1). The scoped production correction queries the
owner review's media ordered by `sort_order` and maps only media ID plus public
delivery URL. It does not expose provider public IDs. A separate signed-JWT C4
test proves an owned `CSPGenerated` itinerary/request makes CSP rating available
and persists a score, while route pacing and POI remain independently
unavailable. Existing focused HTTP/SQL coverage supplies compact representative
stale-version, exact/expired-deadline, unknown/create-only edit-member,
legacy-read/block/conflict, empty/Tour/legacy/itinerary aggregate and POI
Detail/Explore consistency cases.

G-VISITS remains OPEN: GET reports POI capability unavailable with
`visitEvidenceUnavailable`, omitted ratings allow an otherwise valid review,
and nonempty ineligible input is rejected. Positive linked POI-child UAT is
**NOT AVAILABLE / BLOCKED BY G-VISITS**; no schema or visit evidence was
invented. G-LEGACY remains OPEN: isolated fixtures prove conservative legacy
read, POST blocking, canonical edit isolation, fail-safe ambiguity/overlap and
safe conflict surfacing. They do not prove deployed legacy-data shape or rollout
readiness and no legacy migration/linking was performed.

The representative media failure path proves from a new SQL context that a
storage failure creates no parent or adopted media link and leaves exactly one
cleanup-recoverable `CleanupPending` operation. Existing Task 8/9 focused
coverage continues to prove winner/adopted assets are not cleaned as losers.

### Provider, focused regression and full regression

The opt-in Task 13 real-provider test executed with process-only development
configuration and isolated SQL. It passed **1/1, 0 failed, 0 skipped**, exit 0,
in 10 seconds. The test performs signed-JWT HTTP POST -> SQL (exactly one
parent/media/operation, operation Adopted) -> fresh-host GET -> HTTPS delivery,
then verifies nonempty bytes decode as an image. Exact destroy plus bounded,
repeated absence probes run in `finally`; zero provider residue was confirmed.
Public IDs and credentials were never printed or returned by HTTP. Delivery is
bounded to 30 seconds and cleanup to 45 seconds. This is development-provider
handoff evidence, not production/deployment evidence.

Focused final evidence:

- Application `Features.TripReviews`: **171 passed, 0 failed, 0 skipped**, exit 0.
- Infrastructure review-media slice: **71 passed, 0 failed, 1 expected opt-in
  provider skip**, exit 0.
- API/SQL `TripMate.Api.IntegrationTests.Reviews`: **233 passed, 0 failed,
  1 expected Task 13 provider-handoff skip**, exit 0, 1m20s.
- Final signed-JWT handoff plus C4 cases after review corrections: **2 passed,
  0 failed, 0 skipped**, exit 0, 9s.
- Real Task 13 Cloudinary handoff was then run separately with its opt-in flag:
  **1 passed, 0 failed, 0 skipped**, exit 0, 10s.

Final formatting/build commands and results:

- Scoped `dotnet format ... --verify-no-changes` over the three changed C#
  paths: exit 0.
- `git diff --check`: exit 0.
- `dotnet build TripMate.slnx -c Release --no-restore`: exit 0, **0 warnings,
  0 errors**, 2.09s.

Final SQL-enabled full regression used only container `tripmate-tm70-sql` at
`127.0.0.1:14331` and disposable `TripMate_Test_*` databases:

- Application: **840 passed, 0 failed, 0 skipped**, 4s.
- Infrastructure: **144 passed, 0 failed, 1 skipped**, 6s.
- API/SQL: **601 passed, 0 failed, 1 skipped**, 3m38s.
- Total: **1,585 passed, 0 failed, 2 expected skips**, exit 0.

Both skips are intentional opt-in real-provider tests: the Task 8d adapter-only
smoke and the Task 13 HTTP/SQL/provider handoff smoke. Each was separately
executed successfully with development credentials; there are zero unexpected
skips. A stopped Docker Desktop attempt and an initially invalid generated PNG
fixture were environment/test-authoring failures before provider success, not
behavioral RED/PASS evidence. The fixture now generates a valid in-memory 2x2
PNG with Skia and the final provider handoff passes.

### Consistency, review, cleanup and limits

Runtime was compared with the approved spec, plan and API contract for routes,
multipart/JSON shapes, byte/file limits, status/error codes, `none`/`new`/
`legacy` discrimination, UTC `Z` timestamps, exact edit members, rowversion,
deadline, nullable C4/media, duplicate recovery and aggregation. The only
genuine handoff mismatch was missing media in owner GET and it was corrected
with the RED above. The final runtime/docs contract is consistent for the
supported delivery.

Task 7/G-POLICY remains DEFERRED. The verified runtime has no required OpenAI,
Azure Content Safety, moderation-provider/key or policy-service dependency.
The current delivery intentionally deviates from Report 3 BR-94 and does not
claim automated-screening compliance.

`.agents/skills/open-code-review-delegate/SKILL.md` was absent. Read-only OCR
delegate preview/rule discovery completed, while external source export was not
used. Two independent direct reviews covered contract/handoff completeness and
security/SQL/provider/reproducibility. Review findings drove: decoded image
delivery validation, deliberate normalization inputs, final name/C4/media
immutability assertions, exact operation count, bounded CDN reads, accurate two
skip documentation and this ledger. Final security/reliability review is
APPROVED. Contract/handoff re-review is also APPROVED after this ledger closed
its sole remaining documentation blocker.

Final cleanup found **0** `TripMate_Test_*` databases, TestResults files,
`.trx`/dump/temp/log residue and lingering `testhost` processes. Generated media
was in memory only; exact Cloudinary cleanup confirmed zero smoke asset residue.
Credential scanning found no secret literal: the only pattern matches are the
documented process-local extraction/construction variable names. `.env` is not
present or tracked in this worktree; no credential value was persisted.

Final matrix: Task 8 COMPLETE; Task 9 COMPLETE; Task 10 COMPLETE; Task 11
PARTIAL (Tour aggregate COMPLETE, legacy POI compatibility COMPLETE, linked new
POI-child BLOCKED BY G-VISITS); Task 12 COMPLETE; Task 13 COMPLETE for the
currently supported Backend delivery; Task 7 DEFERRED; G-POLICY DEFERRED;
G-VISITS OPEN; G-LEGACY OPEN; Tasks 14-17 NOT STARTED. No Task 13 commit or push
was made and PR #30 was unchanged.

## 2026-10-04 — Delivery reconciliation with latest `origin/develop`

The local feature branch at `b5193cdfde1abb6636096c615d13d93280f103fd`
was reconciled with fetched `origin/develop`
`d1981071452426b050ec1c424fe4b843327f6669`; the merge base was
`525489b61ee2a2791b3d0cf71313e80a4a0445e7`. The develop delta contained 257
paths (30,962 insertions and 204 deletions), including the newer tour-media,
itinerary, AI-ranking and shared-host infrastructure. It did not introduce a
new TripReview production contract.

Three conflicts were resolved semantically:

- `ApiControllerBase.cs` retains the current develop error mappings,
  retry-header behavior and the complete safe TM-79 ProblemDetails mappings.
- `DependencyInjection.cs` retains all current develop registrations and all
  required TM-79 review context, lock, image inspection, storage, journal,
  coordinator and recovery registrations with their intended lifetimes. No
  review content moderator or AI moderation provider was registered.
- `TripMateApiFactory.cs` retains current develop test-host behavior together
  with TM-79 per-factory log isolation and disposal, deterministic media
  substitutes, SQL/InMemory separation, authentication support and recovery
  worker isolation.

The reconciled shared Cloudinary example/configuration exposes one placeholder
credential block plus distinct stable tour-media and review-media folder roots.
The review-media registration test supplies the now-required shared tour-media
options while continuing to validate the immutable review namespace. No real
credential was added.

Post-merge focused evidence:

- Application TripReview/domain slice: **244 passed, 0 failed, 0 skipped**.
- Infrastructure media/Cloudinary/DI slice: **108 passed, 0 failed, 2 expected
  opt-in provider skips**.
- API/SQL review, shared host, auth/CORS, media and itinerary slice: **264
  passed, 0 failed, 1 expected opt-in provider skip**.
- Compact Task 13 signed-JWT handoff plus C4 regression: **2 passed, 0 failed,
  0 skipped**.
- Post-review API-factory media-worker isolation guard: **2 passed, 0 failed,
  0 skipped**.
- Release build: **0 warnings, 0 errors**.

The final SQL-enabled full solution regression used only the isolated local SQL
target at `127.0.0.1:14331` and disposable `TripMate_Test_*` databases:

- Application: **1,242 passed, 0 failed, 0 skipped**.
- Infrastructure: **293 passed, 0 failed, 2 skipped**.
- API/SQL: **695 passed, 0 failed, 1 skipped**.
- Total: **2,230 passed, 0 failed, 3 expected opt-in provider skips**, exit 0.

An intermediary post-review full run exposed one unrelated audit-reason
redaction failure while the three test projects were running concurrently: a
short ordinary Vietnamese reason hit the redactor's fail-closed regex timeout.
The exact theory passed **20/20 cases across 10 repeated invocations**, the
complete Application project then passed **1,242/1,242**, and no audit code was
changed outside this merge's scope. The complete SQL-enabled solution was run
again without altered parallelism or retries inside tests and produced the
final 2,230-pass result above.

The three skips are the existing TM-207 Cloudinary adapter smoke, TM-79 review
adapter smoke and Task 13 HTTP/SQL/provider handoff smoke. No unexpected skip
was introduced. A real provider smoke was not repeated because neither provider
adapter nor lifecycle semantics changed; the previously recorded Task 8d and
Task 13 provider evidence remains applicable.

Two independent final review passes covered merge/spec compatibility and
shared infrastructure/security/test lifecycle. The compatibility pass approved
without findings. The security pass found that the ordinary API test factory
removed the TM-79 recovery worker but still allowed develop's tour-media cleanup
worker to run at host startup. The factory now removes both ambient media
workers; an explicit DI test guards that isolation, while worker-specific tests
continue to drive processors directly. Review after that correction found no
TM-79 contract regression, develop feature loss, duplicate registration,
moderator registration, hidden provider/database access, conflict marker or
test weakening. The six Task 13 handoff paths are byte-unchanged from the
accepted pre-merge checkpoint.

Final filesystem/process cleanup found zero TestResults, `.trx`, dump, temp or
log residue and zero lingering `testhost` processes. `.env` remains absent and
ignored, and the historical `docs/TM-79-implementation-prompt.md` remains the
sole untracked file and was not touched or staged. Task 13 remains COMPLETE and
the Backend remains READY FOR REVIEW for the supported `commerce.Bookings`
delivery. Task 11 remains PARTIAL; G-POLICY remains DEFERRED; G-VISITS and
G-LEGACY remain OPEN; Tasks 14-17 remain NOT STARTED.

## 2026-10-07 — R2 deterministic BR-94 local moderation

Status: **R2 COMPLETE LOCALLY; UNCOMMITTED FOR OWNER REVIEW**. The starting
HEAD was `3e2684f7a54c44a9e98debe1fcda34c28476015f`, the committed R1
checkpoint. This entry records implementation evidence only; it does not
change the R1-frozen policy or begin R3.

Application continues to own `IReviewContentModerator`, the result model and
the six stable `ReviewPolicyCategory` values. The contract now exposes
`ActivePolicyVersion`, backed by the single Application-owned constant
`ReviewContentPolicy.ActiveVersion = "tm79-review-text-v1"`. Infrastructure
implements the deterministic local evaluator and a fail-closed adapter with a
centralized 10-second watchdog. The stateless immutable implementation is
registered as a singleton. No provider, network call, credential, mutable
policy configuration or environment variable was added.

Only normalized title/content is evaluated. Classification uses Unicode Form D
folding, combining-mark removal, intentional Vietnamese `đ`/ASCII-evasion
handling, Unicode tokenization, supported-language evidence and
sentence-scoped contextual rules. Reporting exemptions require an adjacent
reported-speech/quotation introducer; ambiguous multiple quotations return
`Unavailable`. Clear negation and protective inclusion remain allowed.
Clear compositional targeted harm/exclusion rejects, while suspicious unknown
targeted intent fails closed as `Unavailable`.

Result and logging boundaries are:

- `Accepted`: exact active policy version and no categories.
- `Rejected`: one or more stable, sorted, distinct categories; no policy
  approval version and no matched text.
- `Unavailable`: no policy approval version/categories; used for unsupported
  or unreliable language, invalid internal results, exceptions and watchdog
  timeout.
- Caller-requested cancellation propagates as cancellation.
- Logs contain only policy version, decision, category identifiers and a safe
  failure class. Title, content, normalized text, matched phrases/tokens and
  exception messages are absent.

RED was observed before production implementation: the focused Application
contract tests failed to compile because category/result support did not yet
exist, and the focused Infrastructure tests failed to compile because the
local moderation implementation did not yet exist. Final focused GREEN:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --filter "FullyQualifiedName~TripReviewModerationPolicyTests"
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --filter "FullyQualifiedName~LocalReviewContentModeratorTests"
```

Application moderation contract: **10 passed, 0 failed, 0 skipped**.
Infrastructure moderation: **76 passed, 0 failed, 0 skipped**. This includes
the approved A01–A12/R01–R11 production corpus, U01–U03 language outcomes and
the U04 adapter-failure fixture, all six categories, VI/EN/mixed input,
normalization, false-positive boundaries, exact version, timeout, synchronous
stall, exceptions, invalid results, caller cancellation, safe logging and DI.

Definitive project regression:

| Project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Application unit tests | 1,279 | 0 | 0 |
| Infrastructure unit tests | 370 | 0 | 2 |

The two Infrastructure skips are the existing opt-in real-provider smoke tests.
The full solution command
`dotnet test TripMate.slnx -c Release --no-build --no-restore --logger
"console;verbosity=minimal"` exited 0:

| Project | Passed | Failed | Skipped |
| --- | ---: | ---: | ---: |
| Application unit tests | 1,279 | 0 | 0 |
| Infrastructure unit tests | 370 | 0 | 2 |
| API integration tests | 360 | 0 | 267 |
| Total | 2,009 | 0 | 269 |

The API skips are the repository's environment-gated SQL/provider integration
tests; no Azure/shared database was contacted and no local database state was
altered. Release build
`dotnet build TripMate.slnx -c Release --no-restore` passed with **0 warnings
and 0 errors**. Changed-file formatting and
`dotnet format ... --verify-no-changes` passed; `git diff --check` passed.

Independent read-only P0/P1 review initially found quote-association,
unsupported-language, contextual co-occurrence, policy-version ownership,
watchdog, compositional-coverage and negation/protective-context defects. Each
confirmed finding was corrected with a regression test. The final independent
review found **no remaining P0/P1 blockers** and confirmed version/categories/
outcomes, VI/EN/mixed behavior, fail-closed/cancellation/privacy/DI behavior,
no external dependency and the R2 scope boundary.

Submit/Edit handlers, controllers, API/OpenAPI behavior, persistence/schema,
aggregates and clients were not changed or wired. R5 remains responsible for
publication orchestration and policy-version persistence. Mobile and Web
remain frozen. The historical untracked
`docs/TM-79-implementation-prompt.md` remains untouched and untracked. No
commit, push, merge, rebase or PR #30 mutation was performed.

```text
R1 COMPLETE
R1 COMMITTED LOCALLY

R2 COMPLETE
R2 UNCOMMITTED / READY FOR OWNER REVIEW

R3 NOT STARTED
R4 NOT STARTED
R5 NOT STARTED
R6 NOT STARTED
R7 NOT STARTED
R8 CONTRACT DEPENDENCY FROZEN
R9 NOT STARTED

Mobile FROZEN
Web FROZEN
PR #30 UNCHANGED
```
