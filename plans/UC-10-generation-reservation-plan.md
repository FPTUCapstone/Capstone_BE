# UC-10 Generation Reservation Implementation Plan

## Status

Completed — implemented, formatted, verified with unit and SQL Server integration tests, and reviewed.

## Goal

Implement the approved workflow in
`specs/UC-10-generation-reservation-spec.md`: durable leased ownership for one
generation, AI ranking/ORS/CPU work outside database transactions, short
reservation and persistence transactions, one stale-snapshot retry, crash
takeover, and unchanged synchronous HTTP behavior.

## Baseline and Workspace

- Branch: `feature/phuctv-uc10-poi-recommendations` (non-main).
- Baseline command executed on 2026-10-04:
  `dotnet test TripMate.slnx --no-restore --nologo`.
- Baseline result: 231 infrastructure unit tests passed, 1 skipped; 1,059
  application tests passed; 323 API/integration tests passed, 148 skipped.
- SQL Server cases were skipped because the current environment did not enable
  the SQL fixture. They remain required before final delivery when the fixture is
  available.
- The working tree was not clean at baseline: the developer already had modified
  development settings and untracked UC-10 reports. Preserve all those changes.

## Frozen Technical Decisions

- Reuse `planning.SchedulingRequests`; do not add a reservation table.
- Add `generation_owner_id`, `generation_lease_expires_at`, and
  `generation_attempt` with database-enforced state consistency.
- Keep the existing synchronous endpoint. A non-owner polls without a transaction
  until replay, cancellation, or lease takeover.
- Default lease is 60 seconds; default poll interval is 100 milliseconds.
- A second stale snapshot returns the new technical error
  `planning.generation_temporarily_unavailable`, mapped to the endpoint's existing
  `503 Service Unavailable` response category. It does not add a new response
  shape or status code.
- Preserve the existing behavior that routing-provider failure allows a same-key
  retry.
- No EF migration, no unrelated query optimization, and no ranking/scheduling
  semantic changes.

## Approved TDD Seams

1. Public `SchedulingRequest` domain behavior.
2. `CreateSchedulingRequestCommandHandler.Handle`.
3. `POST /api/v1/scheduling-requests` and the real SQL Server schema.

Each work item follows red â†’ green and ends with its targeted suite green. Tests
observe behavior through one of these seams; private helpers are not test targets.

## Work Items

### W01 â€” Domain reservation lifecycle

Files:

- Modify `src/TripMate.Domain/Entities/SchedulingRequest.cs`.
- Modify `tests/TripMate.Application.UnitTests/Domain/SchedulingRequestTests.cs`.

Red tests first:

- a pending request can be claimed and becomes `Processing` with owner, UTC lease,
  and attempt 1;
- only an expired processing lease can be taken over, incrementing the attempt;
- a stale owner cannot renew, release, complete, or fail the request;
- completion, deterministic failure, and transient release clear lease data;
- invalid owner IDs, deadlines, and state transitions are rejected.

Implementation:

- add read-only lease properties and domain transition methods;
- change `Complete` and `FailInfeasible` to require the current owner token;
- keep all setters private and all time values normalized to UTC.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter FullyQualifiedName~SchedulingRequestTests
```

Definition of done: the aggregate alone enforces every state transition and owner
fencing rule in the spec.

### W02 â€” Database-first schema and EF mapping

Files:

- Add `database/migrations/20261004_add_scheduling_generation_reservation.sql`.
- Modify
  `src/TripMate.Infrastructure/Persistence/Configurations/SchedulingRequestConfiguration.cs`.
- Modify `tests/TripMate.Infrastructure.UnitTests/Persistence/SchedulingGenerationPersistenceModelTests.cs`.
- Modify
  `tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj`.
- Modify
  `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestSqlServerTests.cs`.

Red tests first:

- EF model exposes all three columns, UTC conversion, defaults, lengths/types, and
  the new check constraint;
- the migration upgrades the supported legacy schema and is idempotent;
- migration is idempotent and preserves existing pending/completed/failed rows;
- malformed same-named columns or constraints are rejected instead of silently
  accepted.

Implementation:

- write one transactional, idempotent SQL migration following existing shape-audit
  conventions;
- map the entity fields manually; do not create EF migrations;
- copy the migration into integration-test output.

Verification:

```powershell
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --no-restore --filter FullyQualifiedName~SchedulingGenerationPersistenceModelTests
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~CreateSchedulingRequestSqlServerTests
```

Definition of done: migrated databases have the enforceable reservation contract and existing
rows remain readable. The canonical SQL export remains unchanged by explicit delivery decision.

### W03 â€” Workflow options and deterministic snapshot token

Files:

- Add
  `src/TripMate.Application/Features/Scheduling/Common/SchedulingReservationOptions.cs`.
- Add
  `src/TripMate.Application/Features/Scheduling/Common/SchedulingGenerationSnapshot.cs`.
- Add
  `tests/TripMate.Application.UnitTests/Features/Scheduling/SchedulingReservationOptionsTests.cs`.
- Add
  `tests/TripMate.Application.UnitTests/Features/Scheduling/SchedulingGenerationSnapshotTests.cs`.
- Modify `src/TripMate.Infrastructure/DependencyInjection.cs`.
- Modify `src/TripMate.Api/appsettings.json` only; do not modify the developer's
  existing `appsettings.Development.json` changes.

Red tests first:

- stable ordering yields the same SHA-256 token for equivalent POI/tag/hour input;
- every generation-relevant field named in the spec changes the token;
- defaults are 60 seconds and 100 milliseconds;
- invalid lease/poll values and a lease not longer than ranking timeout plus ORS
  timeout fail options validation.

Implementation:

- introduce immutable snapshot records built from the already-loaded graph;
- canonicalize scalars and child collections before hashing;
- add validated configuration and DI registration without secrets.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter "FullyQualifiedName~SchedulingReservationOptionsTests|FullyQualifiedName~SchedulingGenerationSnapshotTests"
```

Definition of done: snapshot comparison is deterministic and configuration fails
fast when a lease cannot cover configured provider timeouts.

### W04 â€” Reservation, wait, and external-work transaction boundary

Files:

- Modify
  `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs`.
- Modify
  `src/TripMate.Application/Features/Scheduling/Common/SchedulingErrorCodes.cs`.
- Modify
  `tests/TripMate.Application.UnitTests/Features/Scheduling/Create/CreateSchedulingRequestCommandHandlerTests.cs`.

Red tests first, one vertical case at a time:

- first request commits `Processing` reservation before ranking or ORS starts;
- ranking, ORS, and generator execution are observed while no transaction is
  active;
- a valid competing lease causes cancellable polling and no provider calls;
- same payload replays completed/failed outcomes; different payload conflicts;
- expired/legacy pending reservation is claimed and generated;
- generate rate-limit rejection releases the owner reservation;
- routing-provider failure releases the reservation and same-key retry succeeds;
- caller cancellation leaves recoverable processing state without corrupt writes.

Implementation:

- replace the current precheck/ranking/long-transaction flow with a reservation
  decision loop;
- save the initial reservation inside its own short serializable transaction;
- move rate-limit ownership, catalog preparation, ranking, matrix call, and planner
  execution outside transactions;
- perform wait delays with the caller cancellation token;
- attempt an owner-checked best-effort release after unexpected preparation
  failures while preserving the original exception;
- retain explanation attachment after authoritative persistence.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter FullyQualifiedName~CreateSchedulingRequestCommandHandlerTests
```

Definition of done: only an owner performs expensive work, and no external or CPU
generation work is inside the transaction callback.

### W05 â€” Owner-fenced persistence and stale-snapshot retry

Files:

- Modify
  `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs`.
- Modify
  `src/TripMate.Application/Features/Scheduling/Common/SchedulingErrorCodes.cs`.
- Modify `src/TripMate.Api/Common/ApiControllerBase.cs`.
- Modify
  `tests/TripMate.Application.UnitTests/Features/Scheduling/Create/CreateSchedulingRequestCommandHandlerTests.cs`.
- Modify
  `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestEndpointTests.cs`.

Red tests first:

- matching snapshot and current ownership persist one itinerary and clear lease;
- a POI/preference mutation before persistence discards the plan and generates
  exactly once more outside the transaction;
- a second mutation persists no itinerary, releases to `Pending`, and returns
  `planning.generation_temporarily_unavailable`;
- loss of lease fencing prevents the stale worker from writing and routes it to
  wait/replay;
- infeasible generation is persisted only after matching revalidation;
- the new technical error maps to the already-declared HTTP 503 category.

Implementation:

- add the short owner/attempt/lease validation transaction;
- recompute the authoritative snapshot token inside that transaction;
- commit without itinerary writes before the one allowed regeneration;
- reuse the first attempt's immutable ranking snapshot/provider pool during that
  regeneration, without a second AI call, personalization recomputation, or
  outside-pool backfill;
- recompute current behavior only for the retry consistency token so prepared and
  authoritative snapshot hashes use the same contract;
- add the controlled retryable error and standard `HandleFailure` mapping.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter FullyQualifiedName~CreateSchedulingRequestCommandHandlerTests
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~CreateSchedulingRequestEndpointTests
```

Definition of done: a stale plan or stale owner can never persist, and retries are
bounded exactly as specified.

### W06 â€” Real SQL concurrency and crash recovery

Files:

- Modify
  `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestSqlServerTests.cs`.
- Modify, only if needed for controllable provider synchronization,
  `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestRankingSqlServerTests.cs`.

Red SQL tests first:

- concurrent same-key/same-payload calls return the same result, create one request
  and itinerary, and call ranking/ORS at most once while the lease is valid;
- concurrent different payloads preserve the existing conflict behavior;
- an expired reservation is taken over and completed;
- the previous owner cannot write after takeover;
- failure between reservation and generation leaves a row that becomes recoverable
  after fake-clock lease expiry;
- forced itinerary persistence failure rolls back itinerary/items while retaining a
  recoverable reservation state consistent with the spec.

Verification:

```powershell
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-restore --filter "FullyQualifiedName~CreateSchedulingRequestSqlServerTests|FullyQualifiedName~CreateSchedulingRequestRankingSqlServerTests"
```

Definition of done: correctness, single authoritative persistence, and fencing are
proven against SQL Server rather than only EF InMemory.

### W07 â€” Regression, review, and delivery evidence

Files:

- Update `specs/UC-10-generation-reservation-spec.md` and this plan only for final
  status/evidence; do not rewrite the audit reports.

Actions:

1. Run formatter/analyzers already configured by the solution.
2. Run targeted tests after every fix.
3. Run the complete solution suite once at the end.
4. Run the `code-review` skill against the implementation start point, reviewing
   Standards and Spec in parallel as required by that skill.
5. Fix every critical finding and rerun affected/full suites.
6. Inspect `git diff` to confirm only planned files plus the approved spec/plan are
   changed and the developer's pre-existing files are untouched.
7. Do not commit, push, merge, or open a PR unless the developer explicitly asks;
   the repository rule overrides the generic implementation-skill commit default.

Verification:

```powershell
dotnet test TripMate.slnx --no-restore --nologo
```

Definition of done: all runnable tests pass, SQL test status is reported honestly,
critical review findings are resolved, and no unrelated user changes are altered.

## Dependency Order

```text
W01 â†’ W02 â†’ W03 â†’ W04 â†’ W05 â†’ W06 â†’ W07
```

Each item is an independently reviewable vertical increment. Do not begin the next
item while the current targeted verification is red or a critical review finding
is unresolved.

## Risk Controls

| Risk | Control | Evidence |
|---|---|---|
| External call accidentally remains in transaction | transaction-aware provider spies and explicit callback boundary | W04 tests |
| Duplicate expensive work | durable owner token plus polling | W04/W06 call-count tests |
| Stale worker persists after takeover | owner token + attempt fencing | W01/W05/W06 tests |
| Stale POI plan persists | deterministic snapshot hash and bounded reprepare | W03/W05 tests |
| Crash strands idempotency key | expiring lease and takeover | W06 test |
| Migration shape drifts | inventory and shape rejection tests | W02 SQL tests |
| Existing transient retry behavior regresses | owner-checked release to `Pending` | W04 regression test |
| User configuration/report changes are overwritten | path-level diff audit; never reset worktree | W07 |

## Definition of Done

- [x] Three reservation columns and trusted constraints exist in fresh and upgraded
  schemas.
- [x] Domain ownership and state transitions are fenced.
- [x] AI ranking, ORS, and planner CPU execute outside transactions.
- [x] Concurrent same-key requests perform at most one active generation and
  persist one authoritative itinerary.
- [x] Expired reservations are recoverable and stale owners cannot write.
- [x] POI/preference changes trigger at most one regeneration; a second change
  returns controlled 503 without stale persistence.
- [x] Replay, mismatch, infeasible, rate-limit, routing-failure, cancellation, and
  explanation behavior remain compatible.
- [x] Idempotent SQL migration added; canonical schema intentionally unchanged; no EF migration.
- [x] Targeted and full runnable suites green; SQL fixture results reported.
- [x] Standards and Spec review complete with no unresolved critical findings.
- [x] No unrelated developer changes modified.

## Verification Evidence

- Formatter: `dotnet format TripMate.slnx --verify-no-changes` returned 0 changes (clean).
- Full Test Suite Results (against SQL Server container `codex-uc10-sqltest`):
  - `TripMate.Infrastructure.UnitTests`: 231 passed, 1 skipped (0 failed).
  - `TripMate.Application.UnitTests`: 1,102 passed (0 failed).
  - `TripMate.Api.IntegrationTests`: 497 passed (0 failed).
  - Total: 1,830 passed, 1 skipped, 0 failed across the entire solution.
- SQL Server Integration Tests Highlights:
  - `CreateSchedulingRequestSqlServerTests`: 30 passed, 0 failed.
  - `CreateSchedulingRequestRankingSqlServerTests`: 9 passed, 0 failed.
