# TM-89 / UC-43 BE-03 Final Acceptance Report

**Date:** 2026-11-11 (Asia/Saigon)
**Verdict:** `DEV_TEST_ACCEPTED_PENDING_HUMAN_REVIEW`

## 1. Provenance and safety

- Repository/worktree: `D:\CapStone\.codex-worktrees\tm89-be03-durable-audit`
- Branch: `feature/tm89-redis-durable-audit`
- Source baseline HEAD: `7bdadaa59733d59c641aa410c31e0018f6bda606`; remediation is an uncommitted worktree diff on that exact baseline.
- Runtime: .NET SDK `10.0.401`, .NET runtime `10.0.12`, Docker Engine `29.5.2`.
- Isolated Redis: `redis:8.2-alpine`, runtime `8.2.11`, port `16382`, named volume mounted at `/data`, `appendonly=yes`, `appendfsync=always`.
- Isolated SQL Server: local container `tm89-be03-sql`, port `11433`; credentials remained process-local and are not reproduced here.
- The pre-existing modification to `docs/operations/tm89-be03-production-readiness.md` was preserved. Test-generated scheduling snapshots were restored to their pre-test state. No source, test expectation, threshold, production service, SRS, branch, commit, or PR was changed.

### Requirements source and version reconciliation

- Current official Google Drive Report 3 local snapshot: `D:\CapStone\Report3_Software-Requirement-Specification.docx`.
- Verified SHA-256: `74d81c867c068cf5312187337afa6bd5a83f1f8e2a5ef0cc1e19415f4edc00c8`.
- The historical original SHA-256 `a4ceabc0389bcffe8263254927ebe417abef6923d898f92c3003171add018298` is not present in the accessible roots, so a byte/content comparison could not be repeated.
- This is not an unrecorded authority selection: `specs/TM-89-BE03-spec.md` records the current SHA as the official Google Drive Report 3 snapshot, records the old SHA, and records the Task Owner decision of 5 November 2026: Mobile is prioritized; Next.js Web is deferred, not permanently removed. No V2 document was used.
- The recorded normalized version discrepancy is the UC-43 Interface line: the historical version named Flutter and Next.js; the current SHA says Flutter mobile only. The current document change history also records a 3 November 2026 UC-43 restoration. BE-03 remains shared Backend infrastructure and does not encode permanent Web removal.

Current SHA `74d81c...`, UC-43 §3.8.4.4 exact traceability:

- paragraphs 4499-4509: trigger, ownership, booking/ticket verification, departure window, atomic successful check-in effects;
- BR-83/84/85/86/108 and BR-82 at paragraphs 4524-4529;
- validation/MSG mappings at paragraphs 4531-4540, including MSG127 when check-in cannot be recorded;
- abnormal cases at paragraphs 4557-4566, especially 8.a1: system/network recording failure displays MSG127, leaves the ticket unused, and permits retry;
- PC-01..PC-05 at paragraphs 4568-4572; PC-05 requires every rejected verification attempt to be written to the audit log;
- MSG127 catalog text at paragraphs 7532-7536 and CR-12 at paragraph 6887.

## 2. Commands executed

Representative commands (connection values omitted):

```text
dotnet test tests/TripMate.Application.UnitTests/... --filter CheckInTravelerCommandHandlerTests|CheckInAuditContractTests|BookingCheckInTests
dotnet test tests/TripMate.Infrastructure.UnitTests/... --filter RedisCheckInAuditJournalEvidenceTests|CheckInAuditProjectorTests|CheckInAuditProjectionWorkerTests|CheckInAuditJournalOptionsTests|AuditOutcomePersistenceTests
dotnet test tests/TripMate.Api.IntegrationTests/... --filter CheckInAuditRedisJournalTests|CheckInAuditProjectionRedisTests
dotnet test tests/TripMate.Api.IntegrationTests/... --filter AuditMigrationSqlServerTests.CheckInJournal
dotnet test tests/TripMate.Api.IntegrationTests/... --filter CheckInAuditProjectionSqlServerTests
dotnet test tests/TripMate.Api.IntegrationTests/... --filter <individual CheckInTravelerRegressionSqlServerTests scenarios>
dotnet test tests/TripMate.Api.IntegrationTests/... --filter CheckInAuditAofRestartTests.ConfirmedEvent... (write, restart, verify)
dotnet test tests/TripMate.Api.IntegrationTests/... --filter CheckInAuditAofRestartTests.AppendAndWaitAof...
dotnet test TripMate.slnx -c Release --no-restore -m:1 --filter "Category!=SqlServer&Category!=Redis"
dotnet format TripMate.slnx --verify-no-changes --no-restore --include <32 PR C# files>
git diff --check
dotnet list src/TripMate.Infrastructure/TripMate.Infrastructure.csproj package --vulnerable --include-transitive --no-restore
dotnet test ... --filter "FullyQualifiedName~InvalidCredential_LeavesTicketUnused&DisplayName~<case>" --logger trx
dotnet test ... --filter "FullyQualifiedName~RecordingFailure_RollsBackTicketQrAndLog_AndAllowsRetry&DisplayName~<case>" --logger trx
dotnet test ... --filter "FullyQualifiedName~TicketQrIssuerConcurrencyTests" --logger trx
dotnet test ... --filter "FullyQualifiedName~TemporaryCheckInHttpAcceptanceTests" --logger trx
```

## 3. Results

### Focused BE-03 evidence

| Suite / evidence | PASS | FAIL | SKIP / incomplete | Result |
|---|---:|---:|---:|---|
| Application handler and audit contract | 37 | 0 | 0 | PASS |
| Infrastructure options/projector/worker/persistence | 20 | 0 | 0 | PASS |
| Real Redis journal and projection | 5 | 0 | 0 | PASS |
| BE-03 SQL migration (legacy/idempotent/incompatible drift) | 2 | 0 | 0 | PASS |
| SQL outage/recovery and duplicate/conflict projection | 2 | 0 | 0 | PASS |
| AOF write + actual restart + verify; latency probe | 3 executions | 0 | 0 | PASS |
| Individually isolated QR/transaction/race scenarios | 7 | 0 | 0 | PASS |
| Multi-row SQL theories (`InvalidCredential`, `RecordingFailure`) | 8 | 0 | 0 | PASS: each row ran serially with an isolated database and its own TRX |
| Complete `TicketQrIssuerConcurrencyTests` class | 7 | 0 | 0 | PASS in one isolated TRX |
| Actual HTTP acceptance scenarios after remediation | 10 | 0 | 0 | PASS; an additional non-SQL negative control also passed |

Invalid setup attempts (missing/empty SQL connection, and subsequently aborted contended runs) are not counted as product failures or passes.

### Gap-closure TRX evidence

All paths below are under the executing user's temporary directory; hashes permit evidence identity verification without exposing connection data.

| Evidence | Result | SHA-256 |
|---|---|---|
| `tm89-invalid-missing.trx` | 1 PASS | `3aaaf2408c162ec96ae6644914f0dbcee033f20e86c263fd9e9800765e06895b` |
| `tm89-invalid-consumed.trx` | 1 PASS | `78086e3e3c260f534f6f8040c3ffe0903e7ca530b824cb549e5b1fb16388f6a1` |
| `tm89-invalid-timestamp.trx` | 1 PASS | `ade5f31fc4219e2a7f943eeff2b23b72b59412eb9388aa68acea047f0d7fde8f` |
| `tm89-invalid-association.trx` | 1 PASS | `1fbc324a94538636d30553fda0c1690ad7b5818033c1128833c2fafe9c13796a` |
| `tm89-invalid-signature.trx` | 1 PASS | `532c292f1dd66223d290945bf6eae7e7af8aaedbfd8954697b85b43504322b22` |
| `tm89-record-aggregation.trx` | 1 PASS | `b4c1faab2be28314e470634888c29fa2c04d1489e912adf6d24d0a94ed6cf62d` |
| `tm89-record-save.trx` | 1 PASS | `878f5307ae2203e5e280b47699c3ec5a1c6354e186c1a372f247aa9510af5689` |
| `tm89-record-commit.trx` | 1 PASS | `cc989a0f9be1edaf57afb6967e4dafe125787c0e912546558a942d8d21a96b44` |
| `tm89-qr-issuer-concurrency.trx` | 7 PASS | `ce416d925fee76c2df10fa108cbb8d58de2fab740d512b67faa2628406c3437e` |
| `tm89-http-acceptance-final.trx` | 9 PASS, 1 FAIL | `762c63723722620e20cd4ed39671d596bc47cfa44f72b4196839cd3796d3830f` |

### Blocking-defect remediation evidence

The historical failing TRX above is retained as RED evidence. The remediation was tested on a new isolated SQL Server 2022 container at loopback port `11434` and the existing isolated Redis 8.2 AOF fixture at loopback port `16382`; no production or shared service was accessed.

| Evidence | Result | SHA-256 |
|---|---|---|
| `tm89-remediation-red3.trx` | RED: expected SQL-outage scenario failed; non-SQL control passed | `9d043608f1ee5bbdbed5d8d6a4c7d2e228cc1d90cbdf705b6994524531ea8819` |
| `tm89-remediation-http-acceptance-final.trx` | 10/10 HTTP acceptance PASS + 1 non-SQL negative control PASS | `796e147e06b8bdd78b48b26ef8aa55cb7beb84093763f4c2c596eced46f35b1b` |
| `tm89-remediation-application-focused.trx` | 23 PASS, 0 FAIL/SKIP | `ad20d9592b1c84f8139f74746c69c0a7a5484bb523d4910a5f35e748b59e5325` |
| `tm89-remediation-sql-regression.trx` | 15 PASS, 0 FAIL/SKIP; includes all 8 SQL theory rows | `b85acda6e589baa11f78e9b5c896e4a27f598219f1286b3e878b655142677168` |
| `tm89-remediation-concurrency.trx` | 7 PASS, 0 FAIL/SKIP | `4bc5f98bc0165eff9e2abd262ac6c73c138db94bda4aee14f7072bb3ed0fbc8b` |
| `tm89-remediation-redis-projection.trx` | 9 PASS, 0 FAIL/SKIP | `0b53444f665b9aafea6aa70a4f12ed65285533e19a6026e04a8cbc9fe6cc066e` |
| `tm89-remediation-infrastructure-focused.trx` | 20 PASS, 0 FAIL/SKIP | `1588939b352645e5fcb3d2c48d64bf05f792dd83eb6a07e237097b76d3f08fe1` |
| `tm89-remediation-application-full-serial.trx` | 4,446 PASS, 0 FAIL/SKIP | `c06c6dde9e1bdf87d183b4510c38fe82e73380a149a0ce8e9c11ca8fff99173a` |

The first broad parallel run produced the previously documented contention-sensitive audit-redaction failure (4,445 PASS / 1 FAIL). Its two rows immediately passed in isolation, then the complete Application suite passed 4,446/4,446 with one xUnit worker. No timeout, test expectation, or production logic was weakened.

The temporary HTTP harness was removed after the final TRX was captured. It changed no production code or existing test expectation.

### Fresh regression

| Project | PASS | FAIL | SKIP / NOT_EXECUTED |
|---|---:|---:|---:|
| Application Unit Tests (Release, dedicated TRX) | 4,445 | 0 | 0 |
| Infrastructure Unit Tests (Release) | 999 | 0 | 3 intentional real-provider smoke skips |
| CandidatePoolReplay Tests (Release) | 13 | 0 | 0 |
| API remaining-suite step (Release, CI category filter) | 760 | 0 | 157 gated/not executed |
| **Executed total** | **6,217** | **0** | **160** |

An earlier Debug run reproduced the known contention-sensitive `AuditLogOutcomeTests...Failure` redaction timeout; the same two-case test passed `2/2` immediately in isolation, and the fresh Release Application suite passed `4,445/4,445`. No production logic or timeout was changed.

Formatting verification and `git diff --check` passed. Build output retains pre-existing nullable/member-hiding warnings. Dependency scan found transitive `System.IO.Packaging 8.0.0` (two High advisories) and `System.Security.Cryptography.Pkcs 10.0.0` (one High, one Moderate); no advisory was remediated under this test-only authorization.

## 4. SQL, Redis, restart, and performance evidence

- Atomic append, idempotent same `EventId`/digest, conflicting projection retention, separate admission/rejection streams, and poison-backlog fairness passed against real Redis.
- SQL migration tests verified legacy upgrade/re-run/event uniqueness and incompatible-column rollback. SQL projection verified backlog growth during outage, recovery projection, identical duplicate ACK, and conflict retention.
- The two-stage AOF probe returned confirmed write only after WAITAOF, Redis container `tm89-be03-redis-aof` was actually restarted, AOF remained `yes/always` on `/data`, the event survived, and repeated delivery remained idempotent.
- Append + WAITAOF, 50 samples: **p50 3.04 ms, p95 4.37 ms, p99 72.95 ms**. No SLA is inferred.
- Backlog evidence: the outage/recovery test retained 25 events without trim/expiry and replayed them after SQL recovery.
- Shared-workload latency beyond this isolated Redis process is `NOT_VERIFIED`; no production/shared service was load-tested.

## 5. Functional acceptance matrix

| Requirement / scenario | Status | Evidence |
|---|---|---|
| Durable admission precedes first SQL query | COMPLIANT against approved colocated BE-03 spec and reconciled current SRS authority | `CheckInTravelerCommandHandlerTests`, `CheckInAuditContractTests` |
| Journal unavailable fails closed with MSG127 semantics; no ticket mutation | COMPLIANT against approved spec | handler tests; isolated SQL `AdmissionFailure_ReturnsMsg127_AndLeavesTicketAndQrUnchanged` PASS |
| Durable rejection outcome before returning a known business rejection | COMPLIANT against approved spec | handler/contract tests and real Redis stream tests |
| Stable `ScanAttemptId`; deterministic per-event `EventId` | COMPLIANT against approved spec | contract tests and duplicate/conflict integration tests |
| SQL projection is idempotent; SQL commit precedes ACK; ACK=0 observed | COMPLIANT against approved spec | projector/worker unit tests and Redis/SQL projection tests |
| Poison/conflict retained; bounded reclaim; healthy events progress | COMPLIANT against approved spec | worker tests and `ManyConflictingPendingEvents_DoNotStarveNewHealthyEvent` |
| AOF restart durability within tested single-node/`appendfsync always` boundary | COMPLIANT in isolated environment | actual write → restart → verify PASS |
| Valid QR, repeated QR, manual code, concurrent scans, refresh race | COMPLIANT at handler + real SQL level | seven isolated regression executions PASS |
| Invalid signature/all invalid credential variants and all transaction fault stages | COMPLIANT | 8/8 serial SQL theory rows PASS, 0 FAIL/SKIP |
| Seven QR issuer concurrency cases | COMPLIANT | 7/7 PASS, 0 FAIL/SKIP |
| HTTP valid check-in | COMPLIANT | 200; ticket consumed once; durable admission present |
| HTTP reused QR | COMPLIANT | 409/AlreadyUsed; ticket remains used; durable rejection projected |
| HTTP invalid signature | COMPLIANT | 400/InvalidQr; ticket unused; durable rejection projected |
| HTTP wrong Tour Operator | COMPLIANT | 403/ScheduleForbidden; ticket unused; durable rejection projected |
| HTTP outside departure window | COMPLIANT | 409/DepartureNotToday; ticket unused; durable rejection projected |
| HTTP admission unavailable | COMPLIANT | 503/MSG127; no SQL verification or ticket mutation |
| HTTP rejection-outcome append unavailable | COMPLIANT | 503/MSG127 instead of misleading business rejection; ticket unchanged |
| HTTP concurrent scans | COMPLIANT | exactly one 200 and one 409; one ticket consumption; rejection audited |
| HTTP SQL outage after durable admission and recovery | COMPLIANT after remediation | actual HTTP 503/MSG127; admission retained; no rejection fabricated; ticket/QR unchanged for the verified pre-read failure; recovery retry succeeded once |
| HTTP-originated rejection duplicate replay | COMPLIANT | repeated projection classified Duplicate; exactly one SQL audit row |
| Current official Report 3 UC-43/PC-05/MSG127 traceability | RESOLVED WITH RECORDED VERSION RECONCILIATION | SHA `74d81c...`; historical SHA unavailable; recorded Owner decision governs Mobile-first/Web-deferred scope |

## 6. Review and remaining risks

- **Specification review:** current official SHA traceability is reconciled through the recorded Owner decision. The remediated SQL-outage HTTP behavior now matches UC-43 abnormal case 8.a1, MSG127, and CR-12 in the verified pre-read failure scenario.
- **Security/concurrency/durability review:** no secrets, raw QR payload, customer data, or connection strings are included in this report. Real Redis and SQL tests support ordering, idempotency, crash persistence, replay, poison retention, and concurrency claims only within the isolated topology. Single-node Redis/AOF remains a failure-domain limitation. Unbounded stream growth remains pending retention/capacity policy.
- **Evidence review:** SQL theory and QR concurrency gaps remain closed. Actual HTTP execution first reproduced the blocker and the tracked regression suite now proves the corrected contract plus all nine companion acceptance scenarios.

### Resolved blocking defect

**RESOLVED — SQL infrastructure failure after durable admission now returns HTTP 503/MSG127.**

- Reproduction: `Http_SqlReadFailureAfterAdmission_PreservesAdmissionAndReturnsMsg127` in `tm89-http-acceptance-final.trx`.
- Observed: HTTP 500 with the generic unexpected-error path.
- Before: HTTP 500 with the generic unexpected-error payload.
- After: HTTP 503 with `errorCode=MSG127` and the locked temporary-unavailability message.
- Safety evidence: durable admission existed before the failing SQL read; the ticket and QR were unchanged; no rejection outcome was fabricated; after SQL recovery, an HTTP retry succeeded and consumed the ticket once.
- Root cause: after durable admission, provider exceptions from SQL verification escaped the handler and reached the generic 500 middleware path.
- Fix boundary: `CheckInTravelerCommandHandler.cs:58-376` encloses only the post-admission execution path; request cancellation is rethrown at `:363-366`; only provider failures accepted by `ICheckInPersistenceErrorClassifier` are mapped at `:367-376`. `SqlServerCheckInPersistenceErrorClassifier.cs:9-31` uses real `SqlException` identity plus transient/known infrastructure numbers. Unexpected non-SQL failures remain generic 500. The handler logs only attempt/schedule/operator identifiers, never QR or booking content.
- Transaction safety: existing deadlock retries remain inside the protected execution path. A transient failure after an uncertain commit is reported as temporary unavailability without claiming rollback; retry observes persisted state through existing single-use rules. No Rejected event is fabricated for the indeterminate infrastructure attempt and no Redis evidence is ACKed/deleted/trimmed/expired by this fix.

## 7. Gate status

- Code acceptance: **development test accepted; human review pending**.
- Development/testing completion: **closed for this scoped defect**; 10/10 HTTP acceptance and all mandatory focused gates passed.
- Human GitHub approval: **PENDING**.
- Merge readiness: **BLOCKED** by human approval and deployment-safety/owner gates.
- Production activation: **NOT APPROVED**; SQL migration, Redis AOF/capacity/noeviction/backup, controlled deploy, monitoring, and rollback evidence remain separate gates.
- Official audit-record definition, retention/access policy, projection/availability SLA, and admission-only reconciliation: **PENDING OWNER/PO DECISION**.
- Full PC-05 production acceptance: **NOT CLAIMED**.

## 8. Final verdict

`DEV_TEST_ACCEPTED_PENDING_HUMAN_REVIEW`

Required next action: Owner reviews the uncommitted remediation diff and test evidence, then separately authorizes commit/push if acceptable. Human approval, merge, Railway deployment, production Redis durability, production SQL migration, and audit-policy approvals remain independent gates.
