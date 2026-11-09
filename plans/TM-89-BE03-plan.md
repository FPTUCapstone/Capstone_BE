# TM-89 / UC-43 BE-03 Atomic Implementation Plan

## Gate and baseline

- Branch: `feature/tm89-redis-durable-audit`
- Worktree: `D:/CapStone/.codex-worktrees/tm89-be03-durable-audit`
- Base: `origin/develop` at `448c8ec14c22f1421a27edb77f0bba0e68b09559`
- Clean baseline command: `dotnet test TripMate.slnx`
- Baseline result: PASS — CandidatePoolReplay 13/13; Infrastructure 980 passed, 3 skipped; API Integration 761 passed, 556 environment-gated skipped; Application 4436/4436.
- Existing NuGet vulnerability and compiler warnings are baseline observations, not introduced by TM-89.

Approved by the Task Owner for branch implementation. Execute each task TDD Red -> Green -> Refactor, then perform spec-compliance review followed by code-quality review before advancing.

## Task 1 — Freeze journal contract and fail-closed handler expectations

Tests first:

- Extend `tests/TripMate.Application.UnitTests/Features/Operator/CheckIn/CheckInTravelerCommandHandlerTests.cs`.
- Add `tests/TripMate.Application.UnitTests/Features/Operator/CheckIn/CheckInAuditContractTests.cs`.
- Prove admission occurs before the first DbContext access; unavailable/uncertain admission returns MSG127 and performs no QR verification, SQL query, ticket mutation, or check-in log write.
- Prove outcome is durably appended before each known rejection; failed/uncertain outcome returns MSG127 rather than `InvalidQr` or another business result.

Implementation files:

- Add `src/TripMate.Application/Common/Interfaces/ICheckInAuditJournal.cs`.
- Add `src/TripMate.Application/Features/Operator/CheckIn/Audit/CheckInAuditEvent.cs`.
- Update `src/TripMate.Application/Features/Operator/CheckIn/Common/CheckInErrorCodes.cs` with locked MSG127/unavailable semantics.
- Update `src/TripMate.Application/Features/Operator/CheckIn/Scan/CheckInTravelerCommandHandler.cs` to use claim identity at admission, preserve one `ScanAttemptId`, and replace suppressed SQL audit writes with durable outcome calls.

Verification: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter "FullyQualifiedName~CheckIn"`

Definition of Done: all handler rejection paths satisfy ordering; success transaction/DTO is unchanged; no Redis dependency leaks into Application.

## Task 2 — Implement idempotent Redis Streams admission

Tests first:

- Add `tests/TripMate.Infrastructure.UnitTests/Services/CheckInAuditJournalOptionsTests.cs` for required connection name, stream/group names, timeout and positive batch/retry bounds.
- Add `tests/TripMate.Api.IntegrationTests/Tickets/CheckInAuditRedisJournalTests.cs` using `RedisTestFixture`: first append, same EventId replay, conflicting digest, concurrent duplicate, connection loss, OOM/full response classification where the isolated test service permits it, and `WAITAOF` confirmation.

Implementation files:

- Add `src/TripMate.Infrastructure/Services/Audit/CheckInAuditJournalOptions.cs` and validator.
- Add `src/TripMate.Infrastructure/Services/Audit/CheckInAuditRedisConnection.cs`.
- Add `src/TripMate.Infrastructure/Services/Audit/RedisCheckInAuditJournal.cs`.
- Add `src/TripMate.Infrastructure/Services/Audit/CheckInAuditAppendLuaScript.cs` for atomic append + EventId receipt/digest validation on the configured admission or rejection-outcome stream.
- Update `src/TripMate.Infrastructure/DependencyInjection.cs` with the dedicated connection and application port registration.
- Add non-secret `CheckInAuditJournal` keys to `src/TripMate.Api/appsettings.json` and test overrides to `src/TripMate.Api/appsettings.Testing.json`; connection values remain environment-provided.

Verification: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter "FullyQualifiedName~CheckInAuditRedisJournal"` with `TRIPMATE_REDIS_TEST_CONNECTION` pointed only at an isolated Redis.

Definition of Done: retries reuse EventId; admission and outcome use separate stream keys; digest conflict fails safely; no in-memory/file fallback; no trim/expiry/retention setting.

## Task 3 — Add database-first event identity

Tests first:

- Extend `tests/TripMate.Api.IntegrationTests/AuditLogs/AuditMigrationSqlServerTests.cs` for fresh/legacy schema, rerun idempotency, null historical values, filtered unique `journal_event_id`, and rollback on incompatible existing schema.
- Extend `tests/TripMate.Infrastructure.UnitTests/Persistence/AuditOutcomePersistenceTests.cs` for Guid round-trip and entity invariants.

Implementation files:

- Add `database/migrations/20261105_add_checkin_audit_journal_identity.sql`.
- Update `src/TripMate.Domain/Entities/AuditLog.cs` with nullable `ScanAttemptId` and `JournalEventId` plus a check-in journal projection factory.
- Update `src/TripMate.Infrastructure/Persistence/Configurations/AuditLogConfiguration.cs`.

Verification: `dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --filter "FullyQualifiedName~Audit"` and the SQL migration filter with `TRIPMATE_SQLSERVER_TEST_CONNECTION`.

Definition of Done: database rejects duplicate non-null EventId; legacy rows remain valid; no EF migration is created.

## Task 4 — Implement replay worker and ACK discipline

Tests first:

- Add `tests/TripMate.Infrastructure.UnitTests/Services/CheckInAuditProjectionWorkerTests.cs` for cancellation, bounded batches, transient retry, poison/conflict handling, and ACK ordering.
- Add `tests/TripMate.Api.IntegrationTests/Tickets/CheckInAuditProjectionSqlServerTests.cs` for SQL outage then recovery, duplicate redelivery, worker restart/pending reclaim, identical existing projection, and conflicting existing projection.

Implementation files:

- Add `src/TripMate.Infrastructure/Services/Audit/CheckInAuditProjectionBackgroundService.cs`.
- Add `src/TripMate.Infrastructure/Services/Audit/CheckInAuditProjector.cs`.
- Add `src/TripMate.Infrastructure/Services/Audit/CheckInAuditJournalMetrics.cs`.
- Update `src/TripMate.Infrastructure/DependencyInjection.cs` to register the hosted service only when validated journal configuration is enabled.

Verification: focused Infrastructure and API integration filters for `CheckInAuditProjection`.

Definition of Done: the worker consumes only the rejection-outcome stream; SQL commit precedes ACK; admission events are never ACKed without SQL commit; transient SQL failure leaves outcomes pending; duplicate has one SQL effect; malformed/conflicting data is never silently ACKed.

## Task 5 — Complete endpoint and transaction regression coverage

Tests first:

- Extend `tests/TripMate.Api.IntegrationTests/Tickets/CheckInTravelerRegressionSqlServerTests.cs` for MSG127/503, no InvalidQr substitution, no mutation on admission failure, and outcome-failure ordering.
- Extend `tests/TripMate.Api.IntegrationTests/Tickets/TicketQrIssuerConcurrencyTests.cs` to retain QR refresh/check-in race guarantees with journal enabled.
- Add the 503 response declaration to `src/TripMate.Api/Controllers/V1/OperatorCheckInController.cs` and explicit MSG127 mapping in `src/TripMate.Api/Common/ApiControllerBase.cs` only if the frozen error-code test requires it.

Verification: focused `CheckInTraveler`, `TicketQrIssuerConcurrency`, and endpoint tests, then `dotnet test TripMate.slnx`.

Definition of Done: successful check-in behavior and response are byte-contract compatible; Mobile treats 503 as system/not-confirmed; all non-environment baseline tests pass.

## Task 6 — Prove AOF restart envelope and benchmark isolated configuration

Tests/config first:

- Add `tests/TripMate.Api.IntegrationTests/Fixtures/Redis/tm89-aof-always.conf` with `dir /data`, `appendonly yes`, and `appendfsync always` for isolated use only.
- Add `tests/TripMate.Api.IntegrationTests/Tickets/CheckInAuditAofRestartTests.cs`, environment-gated by `TRIPMATE_REDIS_AOF_TEST_CONNECTION`, to append confirmed events, restart the isolated Redis through the test owner’s approved harness, and verify replay without loss.
- Measure p50/p95/p99 append + WAITAOF latency and rate-limiter regression under representative concurrent load. Record evidence in the PR description/test artifact, not as an invented SLA.

Verification: isolated AOF restart suite and benchmark command documented by the test harness.

Definition of Done: restart evidence is reproducible; results and residual durability window are reported; no Railway production setting is changed.

## Final branch gate

1. Run formatting/analyzers required by the repository and `dotnet test TripMate.slnx`.
2. Run Redis and SQL environment-gated suites on isolated services and report every skip separately.
3. Two-pass review: specification compliance, then code quality/security/data minimization.
4. Confirm `git diff --check`, no secret/config value, no EF migration, no production Railway change, and no retention setting.
5. Present results and await explicit choice before commit, push, or Draft PR creation.

## Pending items that must remain explicit

- Official audit-record definition, retention/access policy, projection/availability SLA, and business resolution of admission-only attempts remain PENDING.
- These do not block building the approved preservation/replay foundation, but they block any claim that PC-05 is fully accepted in production.
