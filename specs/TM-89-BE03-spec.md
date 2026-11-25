# TM-89 / UC-43 BE-03 Durable Rejection Audit Specification

## Status and authorization

`APPROVED_WITH_CONDITIONS` at design level by the Task Owner. Redis Streams + AOF and BE-A05 Option 1 (fail closed) are approved. The accompanying atomic plan was explicitly approved for branch implementation; production enablement remains gated by isolated durability evidence.

This specification does not authorize a production migration, Railway/Redis mutation, deployment, merge, retention policy, SLA, or SRS edit.

## Requirements authority and version reconciliation

- Previously verified original Report 3 SHA-256: `a4ceabc0389bcffe8263254927ebe417abef6923d898f92c3003171add018298`.
- Current official Google Drive Report 3 local snapshot: `D:/CapStone/Report3_Software-Requirement-Specification.docx`.
- Current snapshot SHA-256 verified on 4 November 2026: `74d81c867c068cf5312187337afa6bd5a83f1f8e2a5ef0cc1e19415f4edc00c8`.
- The normalized UC-43 difference between those versions is the Interface line: the original names Flutter and Next.js, while the current Drive version says Flutter Mobile only.
- Task Owner decision, 5 November 2026: Flutter Mobile is prioritized for the current phase; Next.js Web is deferred, not permanently removed. This is an owner scope decision recorded here and is not an SRS edit.
- UC-43 section 3.8.4.4 PC-05 is unchanged: every rejected verification attempt is written to the audit log.
- UC-43 abnormal case 8.a1 and MSG127 are unchanged: a system/network recording failure leaves the e-ticket unused, displays MSG127, and permits retry.
- PC-01 through PC-04 and the successful check-in transaction remain unchanged.

BE-03 is Backend infrastructure shared by current Mobile and a future Web client. It must not encode a permanent Mobile-only restriction.

## Current defect

`CheckInTravelerCommandHandler.RecordAuditRejectionAsync` writes directly to primary SQL using the request DbContext, suppresses persistence exceptions, and retains only an application log. `AuditFailureRecorder` uses another DbContext but the same SQL Server. Primary SQL outage, Backend crash/redeploy, or an uncertain persistence result can therefore silently lose PC-05 evidence.

## Scope

Included:

- Redis Streams admission journal before any SQL-backed verification.
- A stable `ScanAttemptId` per handler invocation and a stable `EventId` per immutable event.
- Durable rejection outcome before returning a known business rejection.
- Background replay into SQL `AuditLogs` with database-enforced idempotency.
- Explicit handling of Redis timeout, uncertain acknowledgment, Redis unavailability/OOM, SQL outage, process crash, and worker redelivery.
- Database-first idempotent SQL script and regression coverage.
- Isolated AOF `/data` configuration validation and latency benchmark; no production configuration change.

Excluded:

- MongoDB, file fallback, a second provider, or new paid infrastructure.
- Production migration, Railway restart/redeploy, production Redis mutation, merge, or deployment.
- Retention/trimming values, availability SLA, projection SLA, or designation of the official audit record.
- D1, D2, Contact Name, localization CR-09, or permanent Web removal.
- Reclassifying infrastructure failure as `InvalidQr` or another business rejection.

## Event contract

Every event has an immutable versioned envelope:

- `EventId`: GUID unique to one event.
- `ScanAttemptId`: GUID shared by admission and outcome for one request.
- `EventType`: `Admission` or `Rejected`.
- `OccurredAtUtc`: original UTC event time.
- `ActorUserId`: authenticated claim user ID.
- `ScheduleId`; `TicketId` when known.
- `ReasonCode`: stable non-sensitive code only for a known outcome.
- `SchemaVersion` and a canonical payload digest.

Raw QR payloads, booking codes, credentials, connection strings, personal names, and free-form exception details are forbidden. A repeated `EventId` must carry the same canonical digest; a mismatch is a conflict that is not ACKed and is surfaced operationally.

## Admission boundary and BE-A05

The controller authorization remains the outer boundary. At handler entry, after confirming a valid Tour Operator claim and positive `UserId`, but before `OperatorBookingAccess` or any other SQL query, the handler creates `ScanAttemptId` and admission `EventId` and durably admits the event.

This boundary avoids the current defect where SQL can fail while resolving the operator before an attempt is recorded. Middleware authentication/model-binding failures remain outside this increment.

BE-A05 Option 1 is authoritative:

- If admission is rejected, times out, or cannot be confirmed durable, stop immediately.
- Do not decode/verify the QR, query or mutate ticket state, or create a check-in record.
- Return HTTP 503 with `errorCode = MSG127` and the locked temporary-unavailability message.
- Never return `checkin.invalid_qr` for an infrastructure fault.

The current Mobile repository already maps an unknown 5xx check-in response to `CheckInFailureKind.system`/not-confirmed, so this contract requires no Mobile code change.

## Redis admission and acknowledgment protocol

- Use a dedicated Redis connection and validated `CheckInAuditJournal` configuration, independent of the scheduling rate limiter.
- Use separate admission and rejection-outcome stream keys. This prevents admission-only evidence from entering the SQL projection consumer group, whose ACK contract requires a successful SQL commit.
- Use an atomic Lua operation over the selected stream plus an EventId receipt index: first write creates the stream entry and receipt; retry returns the existing entry only when its digest matches.
- After the atomic append, require Redis `WAITAOF 1 0 <bounded-timeout>` on Redis 8.2 to confirm local AOF fsync. The production proposal remains `appendonly yes`, `appendfsync always`, AOF stored on `/data`, subject to isolated validation and latency approval.
- An uncertain client result retries with the same `EventId`; it never creates a new event identity.
- If durability still cannot be confirmed after the bounded technical retry, apply BE-A05 fail closed even if the server may have accepted the event.
- Do not apply `MAXLEN`, `XTRIM`, `XDEL`, receipt expiry, or any other retention mechanism until retention is approved.

## Rejection outcome failure, crash, and uncertain ACK

Once verification establishes a business rejection, create a separate deterministic outcome `EventId` derived from the attempt/event contract and append `Rejected` before returning that rejection.

- Known outcome + durable outcome confirmed: return the original business rejection.
- Outcome append unavailable/full or durability cannot be confirmed after same-EventId retry: return MSG127/503 instead of the business rejection. Do not mutate ticket state.
- Crash after durable outcome but before response: replay projects the event; client retry creates a new attempt but does not duplicate the prior event.
- Crash after admission but before a durable outcome: retain an admission-only indeterminate attempt, expose its age/count, and alert. Do not fabricate a rejection or silently discard it.
- Existing optional `CheckInLog` recording for an `AlreadyUsed` rejection occurs only after the durable rejection outcome. Its SQL failure remains observable but cannot erase the Redis event.

Admission-only evidence closes silent loss but does not by itself establish that the attempt was rejected. Full PC-05 compliance is not claimed until the official-audit-record and indeterminate-reconciliation policies are approved and acceptance evidence passes.

## SQL projection and schema

- Add nullable `scan_attempt_id uniqueidentifier` and `journal_event_id uniqueidentifier` to `dbo.AuditLogs` with an idempotent database-first script.
- Add a filtered unique index on non-null `journal_event_id`.
- Map the fields as nullable `Guid?`; historical rows stay valid.
- Project only the rejection-outcome stream as the existing `CheckInScanRejected` / `Failure` audit record. Admission remains durable journal evidence and is not submitted to the projection consumer group, so it is never ACKed without a SQL commit.
- Commit one SQL projection with a fresh DbContext. Duplicate `journal_event_id` is success only after the stored semantic content is verified.
- ACK the consumer-group message only after SQL commit or verified identical duplicate.
- ACK does not delete the stream entry. Poison/conflicting events stay pending and alert.

## Worker recovery and observability

- Read bounded batches from the rejection-outcome stream and reclaim stale pending deliveries using consumer-group pending recovery.
- Retry transient SQL failures without ACK; use bounded backoff and cancellation-aware shutdown.
- Resume after Backend restart without resetting the group or losing pending entries.
- Report journal availability, append/WAITAOF latency, backlog depth/oldest age, pending count, projection retries, duplicates, conflicts, and admission-only age.
- Redis OOM/full or connection loss is a hard admission failure; no in-memory/file bypass is permitted.

## API and transaction compatibility

- Success DTO, successful check-in transaction, ticket/QR mutation, concurrency protection, and Mobile behavior remain unchanged.
- An infrastructure failure never becomes a QR/business validation failure.
- MSG127 is retryable and does not claim whether verification would have succeeded.
- A response lost after a committed successful check-in remains a committed success and must not create a rejection outcome.

## Acceptance criteria

- SQL unavailable + Redis durable: admission and any durably determined rejection survive and replay after SQL recovery.
- Journal unavailable/full/uncertain admission: MSG127/503, no verification, no ticket mutation.
- Outcome append failure/uncertain ACK: no business rejection is reported as confirmed; event is retried by EventId and admission remains observable.
- Worker retry/restart: at most one SQL row per `JournalEventId`.
- Backend and isolated Redis restart retain confirmed events within the measured AOF envelope.
- Existing success, rollback, QR refresh race, AlreadyUsed, manual-code, and transaction tests remain green.
- No production Redis/Railway change or retention behavior is included.

## Pending policy decisions (not implementation blockers for the approved technical foundation)

- Official audit record definition: PENDING.
- Retention and access-control duration: PENDING.
- Projection/availability SLA: PENDING.
- Business reconciliation of admission-only indeterminate attempts: PENDING; implementation may preserve and alert only.
