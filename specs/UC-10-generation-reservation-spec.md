# UC-10 Generation Reservation and Short-Transaction Workflow

## Status

Completed — implemented and verified on 2026-10-04.

## Problem

`CreateSchedulingRequestCommandHandler` currently calls POI ranking before the
authoritative idempotency lock and calls the route-duration provider plus the
itinerary generator inside a `SERIALIZABLE` transaction. The transaction and its
database connection therefore remain open across external network I/O and CPU
work. Concurrent requests with the same idempotency key can also duplicate the
ranking call before one request wins persistence.

## Scope

This change shall:

- introduce a durable, leased generation reservation on `SchedulingRequest`;
- keep the existing synchronous `POST /api/v1/scheduling-requests` contract;
- move AI ranking, ORS matrix retrieval, and itinerary generation outside all
  database transactions;
- use short `SERIALIZABLE` transactions to reserve/take over generation and to
  validate/persist its result;
- preserve idempotent replay and payload-mismatch behavior;
- detect a stale POI/preference snapshot before persistence and prepare/generate
  again at most once;
- allow an expired reservation to be recovered after a worker crash;
- ensure one authoritative itinerary is persisted for a traveler/idempotency-key
  pair.

## Out of Scope

- changing request or response DTOs or routes; the new technical retry failure uses the
  endpoint's existing `503 Service Unavailable` category;
- returning a new asynchronous `202 Accepted` processing resource;
- optimizing the catalog query/projection described as audit item 3;
- changing ranking, candidate-pool, routing, budget, rest, or itinerary semantics;
- adding distributed rate limiting or the full telemetry package from audit item
  23;
- changing itinerary regeneration or adjustment flows;
- attaching friendly explanations inside the generation reservation. Explanation
  generation remains best-effort after the authoritative itinerary is persisted.

## Existing API Contract

No public API shape changes are allowed.

- Route: `POST /api/v1/scheduling-requests`
- Request DTO: unchanged.
- Success DTO: `SchedulingResponseDto`, unchanged.
- Same key and same canonical payload: return the originally persisted result.
- Same key and different canonical payload: return
  `planning.idempotency_key_payload_mismatch` through the existing failure mapping.
- Infeasible constraints and routing-provider failures retain their current error
  codes and messages.
- Caller cancellation remains observable as `OperationCanceledException` through
  the current ASP.NET cancellation behavior.

## Persistence Contract

`planning.SchedulingRequests` remains the reservation record. Add these nullable
or defaulted columns through an idempotent SQL migration:

| Column | SQL type | Meaning |
|---|---|---|
| `generation_owner_id` | `UNIQUEIDENTIFIER NULL` | Opaque token for the worker that owns the active lease. |
| `generation_lease_expires_at` | `DATETIME2 NULL` | UTC deadline after which another request may take over. |
| `generation_attempt` | `INT NOT NULL DEFAULT 0` | Monotonic reservation version, incremented on every initial claim or takeover. |

Constraints:

- `generation_attempt >= 0`;
- `Pending`, `Completed`, and `Failed` rows have no active owner or lease;
- `Processing` rows have a non-null owner, a non-null lease deadline, and an
  attempt greater than zero;
- the existing unique index on `(traveler_user_id, idempotency_key)` remains the
  final duplicate-persistence guard;
- all new UTC timestamps use the repository's `AsUtcDateTime2()` mapping.

The canonical `database/tripmate_schema_v7.sql` export is intentionally not modified in this
delivery. Environments created from that export must apply the migration before running the
updated application.

The migration must preserve existing rows. Existing `Pending` rows have no lease
and are immediately claimable. Existing `Completed` and `Failed` rows remain
replayable. A legacy `Processing` row created before lease columns existed has no
provable owner, so the migration normalizes it to ownerless `Pending` for safe
takeover.

## Domain State Transitions

`SchedulingRequest` owns the reservation invariants through domain methods; the
handler must not set lease fields directly.

```text
new/Pending --claim--> Processing(owner, lease, attempt + 1)
Processing --expired takeover--> Processing(new owner, new lease, attempt + 1)
Processing(owner) --complete--> Completed(no owner/lease)
Processing(owner) --infeasible--> Failed(no owner/lease)
Processing(owner) --transient release--> Pending(no owner/lease)
```

Only the current owner token may complete, fail, or release a reservation. A stale
owner that lost its lease must not persist an itinerary.

## Workflow

### Phase 1: reserve or replay

Run a short `SERIALIZABLE` transaction:

1. Acquire the existing transaction-owned scheduling application lock for the
   traveler and idempotency key.
2. Read the `SchedulingRequest` by its unique key.
3. If no row exists, create it, claim a lease, save, and commit.
4. If its request hash differs, commit without mutation and return the existing
   payload-mismatch failure.
5. If it is `Completed` or `Failed`, commit and replay the stored outcome outside
   the transaction.
6. If it is `Processing` with an unexpired lease owned by another worker, commit
   and enter the wait path.
7. If it is legacy `Pending`, explicitly released `Pending`, or has an expired
   lease, claim/take over it, increment `generation_attempt`, save, and commit.

Only the request that receives ownership may consume the generate rate-limit
permit or invoke ranking/ORS. A rate-limit rejection releases the reservation in
a short owner-checked transaction so the same idempotency key is not stranded.

### Wait path

A non-owner waits outside any transaction and does not hold a database connection.
It polls the reservation using no-tracking reads at a configurable short interval:

- `Completed` or `Failed`: replay the stored outcome;
- request hash mismatch: return the existing conflict;
- lease still valid: continue waiting until completion, expiry, or caller
  cancellation;
- lease expired or status is `Pending`: return to Phase 1 and attempt takeover.

The API remains synchronous. There is no separate processing response. Polling is
bounded by the caller's cancellation token rather than by a new business timeout.

### Phase 2: prepare and generate

The owner performs these steps with no ambient database transaction:

1. Load the traveler preference and the authoritative active POI graph used by
   eligibility, ranking, explanation metadata, and generation.
2. Build a deterministic SHA-256 snapshot hash from every loaded value that can
   affect the generated result. At minimum this includes traveler interest tags;
   POI identity/status/name/category; coordinates; visit duration; planning-ready
   fields; cost; scenic/photo/shelter values; opening hours; and POI tags. Records
   and child collections are ordered by stable keys before hashing.
3. Build the ranking snapshot.
4. Select matrix candidates, call ORS, and generate the plan.

The request hash remains the identity of the user request. The snapshot hash is an
internal consistency token and is not persisted as request identity.

### Phase 3: revalidate and persist

Run a second short `SERIALIZABLE` transaction:

1. Acquire the same scheduling application lock.
2. Re-read the reservation and verify request hash, `Processing` status, owner
   token, attempt, and lease validity.
3. Re-read the generation-relevant POI/preference values and recompute the
   deterministic snapshot hash.
4. If the hash matches, persist the itinerary/items and mark the request
   `Completed`, or persist the deterministic infeasible outcome as `Failed`; clear
   owner and lease; save and commit.
5. If the hash differs on the first preparation attempt, renew the same owner's
   lease, commit without itinerary writes, and repeat the data load, ORS call, and
   generation exactly once. Reuse the first attempt's immutable ranking snapshot
   and provider pool: do not call the ranking provider again, recompute
   personalization, or backfill from outside that pool. The retry reads the current
   behavior aggregation for its consistency token only, so its prepared and
   authoritative hashes use the same current-data contract without changing the
   frozen ranking.
6. If the hash differs again, release the reservation to `Pending`, commit, and
   return a controlled retryable failure. Do not persist a plan from either stale
   snapshot.
7. If ownership was lost, commit without writes and use the wait/replay path. A
   stale worker must never overwrite the current owner's result.

The Phase 3 revalidation query may remain the current graph query in this change;
reducing its row volume belongs to audit item 3. External provider calls and the
generator itself are forbidden inside Phase 1 or Phase 3.

## Lease and Retry Configuration

Add validated application options with these defaults:

- lease duration: 60 seconds;
- wait poll interval: 100 milliseconds;
- maximum stale-snapshot regeneration count: 1 (fixed by this specification).

The lease duration must be configurable without code changes and must validate as
longer than the configured ranking timeout plus the 20-second ORS HTTP timeout.
Tests use shorter deterministic values through injected options and a fake clock;
production code must not use `Task.Delay` without the request cancellation token.

## Failure and Recovery Rules

- Deterministic infeasibility is persisted as the existing `Failed` outcome and is
  replayed for the same key.
- A controlled routing-provider failure releases the current reservation to
  `Pending` in a short owner-checked transaction that is not cancelled with the
  caller request, preserving the existing behavior that the same key can retry
  successfully. Caller cancellation remains observable after cleanup.
- Caller cancellation while the owner is preparing or finalizing generation
  attempts an owner-checked best-effort release with an independent cleanup token
  before propagating `OperationCanceledException`.
- Unexpected preparation or finalization failures attempt the same owner-checked
  best-effort release without replacing the original exception. If cleanup fails,
  or the process terminates before cleanup, the active lease remains available for
  expiry-based recovery. Failures must not delete completed/failed data or permit
  a stale owner to write.
- EF execution-strategy retries may repeat database-only reservation/persistence
  callbacks, but can no longer repeat AI or ORS side effects.

## Acceptance Criteria

1. No ranking provider, route-duration provider, or itinerary generation call is
   made while `ExecuteInSerializableTransactionAsync` is active.
2. Two concurrent same-key/same-payload requests persist exactly one scheduling
   request and one itinerary, return the same authoritative result, and invoke AI
   ranking and ORS at most once while the first lease remains valid.
3. Two concurrent same-key/different-payload requests persist at most one result;
   the loser receives the existing payload-mismatch failure.
4. A request encountering an expired `Processing` reservation can take ownership
   and complete it; the prior owner cannot subsequently persist.
5. A worker failure after Phase 1 does not leave the key permanently blocked.
6. A generation-relevant POI or traveler-interest change between Phase 2 and Phase
   3 is detected. The handler prepares again at most once and never persists the
   stale plan.
7. A second stale snapshot releases the reservation and returns a controlled
   retryable failure.
8. Routing-provider failure leaves no itinerary/items and permits a later same-key
   retry.
9. Existing completed and failed records remain replayable after migration.
10. Transaction-scope instrumentation demonstrates that network/provider waits are
    excluded from transaction duration. Load-test measurements of p95 duration,
    deadlocks, pool wait, and ORS calls/request are recorded separately and are not
    fabricated as unit-test guarantees.
11. All existing unit, infrastructure, integration, OpenAPI, and endpoint tests
    remain green.

## Approved TDD Seams

Implementation will use vertical red-green slices at these externally observable
seams:

1. `SchedulingRequest` public domain methods for lease ownership and state
   transitions.
2. `CreateSchedulingRequestCommandHandler.Handle` for transaction boundaries,
   stale-snapshot retry, provider call counts, cancellation, and replay behavior.
3. `POST /api/v1/scheduling-requests` plus the real SQL Server schema for
   concurrent same-key behavior, migration convergence, expired-lease takeover,
   and single authoritative persistence.

Tests must not target private helpers or assert implementation-only call order
unless the order is required to prove that external I/O occurs outside a
transaction.

## Delivery Constraints

- Add an idempotent migration under `database/migrations/`; do not modify the canonical SQL
  export and do not create EF migrations.
- Preserve the user's existing changes to development settings and audit reports.
- Work on the current non-main feature branch.
- Do not push, merge, or open a pull request without an explicit request.
