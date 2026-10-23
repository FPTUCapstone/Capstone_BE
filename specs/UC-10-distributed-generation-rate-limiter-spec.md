# UC-10 Distributed Generation Rate Limiter Specification

## Status

Approved; implementation complete locally. Unit, handler, HTTP, configuration, and non-Redis regression suites are verified. Real-Redis integration is wired into CI and remains pending until the branch is pushed or a local Redis endpoint is available.

## Problem

UC-10 currently registers `InMemoryGenerateRateLimiter` as a singleton. The singleton is shared
only by requests handled by one API process, so a deployment with `N` API instances can grant
approximately `N` times the intended per-user quota. Its `_users` dictionary also retains one key
for every user that has ever attempted a fresh generation. Expired timestamps are removed only
when that same user calls again, and the user entry itself is never removed.

The current limiter therefore does not provide a cluster-wide abuse-control boundary and has an
unbounded process-memory lifecycle.

## Goals

1. Enforce the existing cooldown and rolling one-minute quota atomically across all API instances.
2. Preserve the existing 429 error codes, messages, `retryAfterSeconds`, and `Retry-After` behavior
   for cooldown and quota rejection.
3. Expire distributed user state after it can no longer affect a decision.
4. Make backing-store failure behavior explicit and protect generation resources by failing closed.
5. Retain an intentional single-instance mode with bounded storage and idle eviction for local or
   genuinely single-process deployments.
6. Make accepted, rejected, store-failure, latency, eviction, and active-entry cardinality visible
   without user IDs or other high-cardinality labels.
7. Prove that concurrent requests sent through two independently constructed application
   instances cannot exceed the shared quota.

## Non-goals

- No change to the numeric defaults: three accepted fresh generations per rolling minute and a
  15-second cooldown.
- No change to idempotent replay. A replay that does not perform a fresh generation is not charged.
- No rate limit for itinerary regeneration, itinerary adjustment, authentication, or unrelated
  endpoints.
- No IP-, device-, tenant-, or global generation quota.
- No dynamic per-user tier or administrator override.
- No local fallback when the distributed store is unavailable.
- No replacement of the generation reservation/lease workflow.
- No database schema or EF migration.

## Existing Public Contract

For a healthy limiter, the endpoint contract remains unchanged:

- `planning.generation_cooldown` maps to HTTP 429.
- `planning.generation_rate_limited` maps to HTTP 429.
- Both failures carry `retryAfterSeconds`; `ApiControllerBase` emits the corresponding
  `Retry-After` response header.
- The limiter runs only after the request has acquired generation ownership and immediately before
  fresh preparation/generation. A rejected request deletes the reservation row when that row was
  created by the current attempt; a pre-existing reservation is released back to `Pending`.
  This prevents callers from accumulating rows by rotating idempotency keys while preserving
  retry/replay state that existed before the limiter decision.

Store unavailability introduces one controlled technical failure:

- code: `planning.generation_rate_limiter_unavailable`;
- HTTP status: 503 Service Unavailable;
- message: `Itinerary generation is temporarily unavailable. Please try again.`;
- no 429 response and no claim that the user exhausted quota.

Caller cancellation must continue to propagate as `OperationCanceledException`; it must not be
translated into the 503 failure.

## Functional Semantics

### 1. Identity and charge point

The partition key is the authenticated traveler's internal numeric user ID. It must never be
included as a metric label or written to routine logs.

One permit is consumed only when the current request owns the generation reservation and is about
to perform fresh generation. Replay, payload mismatch, waiting on another owner, and failures that
occur before acquisition do not consume a permit. Once an atomic store operation accepts a permit,
the permit is not refunded when later ranking, routing, planning, cancellation, or persistence
fails. This preserves the current abuse-control semantics and avoids a refund race.

### 2. Decision order and time boundaries

For each user, accepted timestamps form an exact rolling 60-second window.

At acquisition time `now`:

1. Remove accepted timestamps `<= now - 60 seconds`.
2. If the most recent remaining acceptance is later than
   `now - CooldownSeconds`, reject with `planning.generation_cooldown`.
3. Otherwise, if the remaining count is at least `MaxFreshGenerationsPerMinute`, reject with
   `planning.generation_rate_limited`.
4. Otherwise, append one accepted timestamp and allow the request.

Cooldown takes precedence over the per-minute quota, matching the existing implementation.
`retryAfterSeconds` is the positive ceiling of the remaining interval and remains bounded to
1–60 seconds. A timestamp exactly 60 seconds old no longer counts.

### 3. Distributed mode

Production multi-instance deployments use Redis through one singleton connection multiplexer.
One user key stores accepted events as a sorted set. A single server-side Lua script performs time
lookup, stale-event removal, cooldown/quota checks, optional insertion, TTL refresh, active-key
registry maintenance, and result construction atomically.

Required properties:

- Redis server time is authoritative; API-host clock skew cannot create additional permits.
- Every acquisition has a unique opaque member value so same-millisecond requests remain distinct.
- All keys passed to the script use one Redis Cluster hash tag and therefore one slot.
- User-state TTL is refreshed only after an accepted permit and is at least the rolling-window
  duration plus the configured safety margin.
- Rejected requests do not extend the user-state lifetime.
- The script removes expired entries from an active-key registry and reports its cardinality for
  telemetry. A low-frequency sampler may refresh the gauge while request traffic is absent; it
  must not scan the Redis keyspace on the request path.
- The script source/version is owned by the repository and loaded/evaluated through
  `StackExchange.Redis`; no Redis module is required.

The Redis operation is the sole authority in distributed mode. A timeout, connection failure,
script failure, malformed result, or unavailable store returns the controlled 503 failure. The
implementation must not consult an in-process limiter as fallback because that would restore the
cluster quota bypass. If Redis committed an acceptance but its response was lost, the request
still fails closed and that permit may remain conservatively consumed until TTL; it must not run
expensive generation on an ambiguous result.

### 4. Single-instance mode

Single-instance mode preserves the same decision order and time boundaries using
`IDateTimeProvider`, but its state lifecycle must be bounded:

- idle user entries expire after a configured TTL that is no shorter than 60 seconds;
- cleanup runs periodically or after a bounded number of acquisitions, not only when the same user
  returns;
- `MaxTrackedUsers` is a hard cap;
- when a new user cannot be tracked after an expiry sweep, acquisition fails closed with the same
  503 technical error instead of bypassing the quota;
- accepted timestamps and a user entry are removed together when the entry expires;
- all decisions and cleanup remain thread-safe and concurrent calls cannot over-grant.

Single-instance mode must be selected explicitly. It is not an automatic recovery path for Redis.
Deployment documentation must state that scaling this mode beyond one API process invalidates the
cluster-wide quota guarantee.

## Application Port and Cancellation

The infrastructure operation is asynchronous. Replace the synchronous port with the equivalent of:

```csharp
ValueTask<GenerateRateLimitDecision> TryAcquireAsync(
    long userId,
    CancellationToken cancellationToken);
```

The Application layer remains unaware of Redis. `GenerateRateLimitDecision` continues to carry
`Allowed`, `ErrorCode`, and `RetryAfterSeconds`; the new store-unavailable decision uses the new
technical error code and a zero retry-after value.

The handler awaits the limiter outside every database transaction. On any denied decision,
including store unavailability, it performs owner-checked cleanup before returning: delete a row
created by the current attempt, otherwise release the existing reservation. If cleanup itself
fails, existing owner/lease recovery rules remain authoritative.

## Configuration and Validation

Extend `SchedulingRateLimit` with non-secret settings for:

- provider mode: `Redis` or `SingleInstance`;
- Redis connection-string name (default `Redis`), key prefix, command timeout, and TTL safety
  margin;
- single-instance idle TTL, cleanup cadence, and maximum tracked users;
- the existing quota and cooldown.

The Redis endpoint/credentials are supplied only through `ConnectionStrings:Redis` from deployment
secrets or environment variables. They must not be committed or logged.

Options validation fails at startup when:

- provider mode is missing/unknown;
- Redis mode has no Redis connection string;
- quota, cooldown, timeouts, cleanup cadence, or capacity are outside documented positive bounds;
- either configured idle TTL can expire state before the 60-second rolling window ends.

The repository's production/default configuration selects Redis. Development and Testing may
explicitly select `SingleInstance`. The Docker Compose development stack may select either mode,
but a two-instance verification profile must use one shared Redis service.

## Observability Contract

Use a dedicated `System.Diagnostics.Metrics` meter. Exact names may follow repository naming
conventions, but the following measurements are required:

| Measurement | Type | Allowed low-cardinality dimensions |
|---|---|---|
| acquisition outcomes | counter | `provider=redis|single_instance`, `outcome=accepted|cooldown|quota|store_failure` |
| store operation duration | histogram | `provider`, `outcome=success|failure` |
| active user entries | observable gauge | `provider` |
| evicted/expired entries | counter | `provider` |

No user ID, key, idempotency key, request payload, exception message, or Redis endpoint may be a
metric dimension. Store-failure logs are structured, omit secrets and user identity, and are
rate-limited or aggregated to avoid an outage log storm.

A deployment dashboard must expose accepted/rejected/store-failure rates, store latency, and
active-entry cardinality. Alert thresholds are environment-owned and must be recorded with the
deployment evidence rather than invented in unit tests.

## Acceptance Criteria

1. With two independently constructed API/service-provider instances sharing Redis, concurrent
   requests for one user produce no more than the configured total number of accepted permits per
   rolling minute across both instances.
2. Concurrent requests inside the cooldown grant exactly one permit, including when they arrive in
   the same millisecond.
3. Cooldown-versus-quota precedence, exact 60-second expiry boundary, error codes, and
   `retryAfterSeconds` match the current contract.
4. Redis inspection proves an idle user key expires without that user making another request, and
   the active-user registry no longer counts it after cleanup/sampling.
5. Redis timeout, connection failure, and script failure invoke no ranking, routing, or itinerary
   generation, clean up the owned reservation without retaining a newly created pending row, and
   return the controlled 503 error.
6. Distributed mode never falls back to process-local state.
7. Single-instance tests prove idle eviction, hard capacity, fail-closed capacity exhaustion, and
   bounded active-entry cardinality under many one-time users.
8. Eight or more concurrent calls against each provider never grant above cooldown/quota limits and
   contain no collection/concurrency exception.
9. Replay of an existing completed/failed request does not call the limiter. A fresh owner calls it
   exactly once even when the generation snapshot is retried.
10. Metric-listener tests observe one outcome per completed acquisition, store failures, latency,
    eviction, and cardinality with only the approved dimensions and no user identifiers.
11. A dashboard or reproducible dashboard definition displays accepted, cooldown-rejected,
    quota-rejected, store-failure, latency, and cardinality signals from a two-instance smoke test.
12. Existing Application, Infrastructure, API integration, OpenAPI, and scheduling tests remain
    green; no database migration is introduced.

## Operational Verification

Before production rollout:

1. Run a two-instance load test against one Redis deployment and record accepted/rejected counts.
2. Confirm Redis key TTL and active-entry cardinality after the idle period.
3. Block Redis connectivity and verify fail-closed 503 behavior, reservation release, metrics, and
   bounded logs.
4. Restore Redis and verify acquisition recovers without restarting the API.
5. Record p50/p95/p99 limiter latency and Redis CPU/memory under expected and burst traffic.
6. Roll out with the dashboard visible; do not infer safe capacity from unit-test timings.

## Alternatives Not Chosen

- **`ConcurrentDictionary` only:** improves the concurrency primitive but neither shares state nor
  supplies an eviction lifecycle.
- **ASP.NET Core in-memory rate limiting:** remains process-local and cannot enforce a per-user
  cluster quota.
- **Local fallback after Redis failure:** favors availability but silently multiplies quota during
  the exact period when the authoritative state is unavailable.
- **SQL rows for every accepted event:** could be made atomic but adds cleanup and write contention
  to the primary transactional database for an operational control already suited to expiring
  Redis state.
- **Fixed-window counter:** permits boundary bursts and changes the existing rolling-window
  behavior.

## Delivery Constraints

- Follow red-green-refactor for each approved work item.
- Do not place Redis types in Domain or Application.
- Do not add EF migrations or schema changes.
- Do not modify unrelated UC-10 ranking, routing, generation, or reservation semantics.
- Preserve all pre-existing working-tree changes.
- Do not commit, push, merge, or open a pull request without explicit developer instruction.
