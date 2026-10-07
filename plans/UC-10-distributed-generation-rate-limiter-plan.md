# UC-10 Distributed Generation Rate Limiter Implementation Plan

## Status

Implementation complete through W06. W07 local verification is complete except for the real-Redis suite, which is now mandatory in GitHub Actions and remains pending until the branch is pushed or a local Redis endpoint is available.

## Goal

Replace the process-local-only UC-10 generation limiter with an atomic Redis-backed limiter for
multi-instance deployments, retain a bounded/evicting single-instance provider, fail closed when
authoritative state is unavailable, and expose operational metrics without changing healthy-store
429 behavior.

## Source Specification

`specs/UC-10-distributed-generation-rate-limiter-spec.md`

## Baseline and Workspace

- Current implementation branch: `fix/uc10-distributed-generation-rate-limiter`.
- The working tree already contains extensive developer changes for UC-10. Every task must preserve
  them and inspect path-level diffs before completion.
- Before implementation, record the result of:

```powershell
dotnet test TripMate.slnx --no-restore --nologo
```

- If Redis packages are not already restored, package restore requires the developer's normal
  network/package workflow. Do not claim Redis integration coverage when no Redis fixture ran.

## Frozen Technical Decisions

- Distributed provider: Redis via `StackExchange.Redis`.
- Algorithm: exact rolling 60-second accepted-event sorted set plus cooldown, executed by one Lua
  script using Redis server time.
- Failure policy: fail closed with `planning.generation_rate_limiter_unavailable` / HTTP 503.
- Distributed mode has no process-local fallback.
- Single-instance mode uses bounded state, idle TTL, and proactive sweep.
- Existing quota/cooldown defaults and healthy-store 429 contract remain unchanged.
- Application port becomes cancellation-aware and asynchronous.
- No database schema or EF migration.

## Approved TDD Seams

Tests observe behavior through:

1. `IGenerateRateLimiter.TryAcquireAsync` for provider semantics and concurrency;
2. `CreateSchedulingRequestCommandHandler.Handle` for charge point, reservation release, provider
   call count, and store-failure behavior;
3. `POST /api/v1/scheduling-requests` for HTTP 429/503 mapping and headers;
4. two independently constructed provider/application instances connected to a real Redis server
   for cross-instance atomicity and TTL.

Do not test private helpers or mock the Redis client in place of the required real-store atomicity
tests.

## Work Items

### W01 — Async port, options, and unchanged handler semantics

Files:

- Modify `src/TripMate.Application/Common/Interfaces/IGenerateRateLimiter.cs`.
- Move the existing rate-limit configuration model out of Application into
  `src/TripMate.Infrastructure/Services/SchedulingRateLimitOptions.cs`; Application retains only
  the limiter port and decision contract.
- Modify
  `src/TripMate.Application/Features/Scheduling/Common/SchedulingErrorCodes.cs`.
- Modify
  `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs`.
- Modify `src/TripMate.Infrastructure/Services/SchedulingRateLimitOptionsValidator.cs`.
- Modify affected fake limiters in Application and API integration tests.
- Add/modify focused options-validator tests under
  `tests/TripMate.Infrastructure.UnitTests/Services/`.

Red tests first:

- handler awaits one acquisition only after fresh-generation ownership;
- replay/wait/payload mismatch do not acquire;
- one allowed permit remains one charge across the existing snapshot retry;
- denied cooldown/quota releases the reservation and preserves the existing result metadata;
- caller cancellation propagates rather than becoming store unavailable;
- every invalid provider/TTL/timeout/capacity combination fails validation.

Implementation:

- replace `TryAcquire` with cancellation-aware `TryAcquireAsync`;
- add provider-specific, non-secret settings and validation;
- add the store-unavailable error code without mapping it yet;
- adapt existing test doubles mechanically, with no Redis type in Application.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter FullyQualifiedName~CreateSchedulingRequestCommandHandlerTests
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --no-restore --filter FullyQualifiedName~SchedulingRateLimitOptions
```

Definition of done: the asynchronous abstraction compiles, the charge point is unchanged, and all
existing healthy-limiter handler behavior is green.

### W02 — Bounded single-instance provider

Files:

- Replace or refactor
  `src/TripMate.Infrastructure/Services/InMemoryGenerateRateLimiter.cs`.
- Modify
  `tests/TripMate.Infrastructure.UnitTests/Services/InMemoryGenerateRateLimiterTests.cs`.
- Add a small internal metrics abstraction only if required to keep W02 tests deterministic; the
  public metrics contract is completed in W06.

Red tests first:

- idle entries disappear without that same user calling again;
- a sweep removes timestamps and user state together;
- state never exceeds `MaxTrackedUsers` under many one-time users;
- an at-capacity new user fails closed after a sweep finds no expired entries;
- cooldown precedence and the exact 60-second boundary match the specification;
- eight or more simultaneous requests cannot over-grant.

Implementation:

- keep one synchronization boundary or an equivalently safe keyed design;
- track last activity/expiry and sweep on a bounded cadence;
- enforce the hard cap before inserting a new user;
- never evict live state merely to admit a new user, because that would permit quota bypass.

Verification:

```powershell
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --no-restore --filter FullyQualifiedName~InMemoryGenerateRateLimiterTests
```

Definition of done: single-instance mode is semantically compatible, bounded, and self-cleaning.

### W03 — Atomic Redis provider

Files:

- Modify `src/TripMate.Infrastructure/TripMate.Infrastructure.csproj` to add the approved
  `StackExchange.Redis` package version.
- Add `src/TripMate.Infrastructure/Services/RedisGenerateRateLimiter.cs`.
- Add a repository-owned Lua script under
  `src/TripMate.Infrastructure/Services/Redis/` and include it as an embedded resource, or store it
  as an internal constant if repository review shows that is the established convention.
- Add focused pure result-parser/key-builder tests under
  `tests/TripMate.Infrastructure.UnitTests/Services/`.

Red tests first:

- Redis keys for the state and registry share the configured cluster hash tag;
- unique members preserve same-millisecond acquisitions;
- every script result maps to accepted, cooldown, quota, or store-unavailable correctly;
- retry-after milliseconds round up to the existing 1–60 second contract;
- cancellation is rethrown; Redis timeout/connection/script/result failures fail closed;
- logs and keys never expose connection credentials.

Implementation:

- use one singleton multiplexer supplied by DI;
- execute one script per acquisition; use Redis `TIME` inside the authoritative operation;
- atomically trim the 60-second window, apply cooldown then quota, insert on acceptance, set TTL,
  trim/update the active registry, and return decision/cardinality/eviction data;
- do not retry an ambiguous accepted mutation in a way that could insert another event;
- never call the single-instance provider from Redis error handling.

Verification:

```powershell
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --no-restore --filter "FullyQualifiedName~RedisGenerateRateLimiter|FullyQualifiedName~RedisRateLimit"
```

Definition of done: the Redis adapter is cancellation-aware, fail-closed, cluster-slot-safe, and
has no process-local fallback path.

### W04 — Composition, configuration, and HTTP failure mapping

Files:

- Modify `src/TripMate.Infrastructure/DependencyInjection.cs`.
- Modify `src/TripMate.Api/Common/ApiControllerBase.cs`.
- Modify `src/TripMate.Api/appsettings.json`.
- Modify `src/TripMate.Api/appsettings.Development.json` only in the narrow
  `SchedulingRateLimit` section, preserving the developer's existing changes.
- Modify `src/TripMate.Api/appsettings.Testing.json` if the test host requires an explicit provider.
- Modify `.env.example` if present; add names only, never credentials.
- Modify `docker-compose.yml` to add one shared Redis service and pass the connection string when
  the Compose profile uses distributed mode.
- Modify
  `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestEndpointTests.cs`.

Red tests first:

- configured mode resolves exactly one provider;
- Redis mode fails startup when its named connection string is absent;
- store-unavailable maps to HTTP 503 with the specified ProblemDetails error code/message;
- cooldown/quota still map to 429 and preserve `Retry-After`;
- store failure releases the generation reservation and no provider/generator work begins.

Implementation:

- register the multiplexer and Redis limiter only for Redis mode;
- register the bounded in-memory limiter only for explicit single-instance mode;
- select Redis in default/production settings and explicit single-instance mode in local test
  settings;
- ensure connection strings come from secrets/environment and are never logged.

Verification:

```powershell
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-restore --filter FullyQualifiedName~CreateSchedulingRequestEndpointTests
```

Definition of done: configuration cannot silently select the wrong authority, 429 stays compatible,
and unavailable Redis produces a controlled 503 before expensive generation.

### W05 — Real Redis cross-instance and lifecycle verification

Files:

- Modify `tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj` only if a Redis
  fixture dependency or script copy item is required.
- Add `tests/TripMate.Api.IntegrationTests/Scheduling/GenerateRateLimiterRedisTests.cs`.
- Add a Redis fixture under `tests/TripMate.Api.IntegrationTests/Fixtures/` following the existing
  external-service skip/report conventions.
- Modify `docker-compose.yml` only if W04 did not already add the testable Redis service.

Real-store tests, written red first:

- two independent limiter/service-provider instances sharing Redis collectively grant at most the
  configured rolling quota;
- simultaneous same-millisecond calls grant exactly one permit during cooldown;
- the exact 60-second boundary admits the next request;
- a rejected call does not extend TTL;
- an idle state key expires and registry cardinality converges after prune/sample;
- killing or blocking Redis yields store-unavailable, no local grant, and recovery after Redis is
  restored;
- keys and registry remain isolated by configured prefix and test run ID.

Verification, using the repository's explicit Redis-test connection setting:

```powershell
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-restore --filter "Category=Redis"
```

Definition of done: cross-instance quota, atomic concurrency, TTL lifecycle, fail-closed outage, and
recovery are proven against real Redis. If the fixture is unavailable, this work item remains
incomplete rather than being replaced by mocks.

### W06 — Metrics, dashboard contract, and outage hygiene

Files:

- Add `src/TripMate.Infrastructure/Services/GenerateRateLimiterMetrics.cs`.
- Modify both limiter providers to emit the approved measurements.
- Add `tests/TripMate.Infrastructure.UnitTests/Services/GenerateRateLimiterMetricsTests.cs`.
- Add `docs/operations/uc10-generation-rate-limiter-observability.md`.
- Add a dashboard definition under the repository's deployment/observability location if one
  exists at implementation time; otherwise record the external dashboard link and reproducible
  panel queries in the operations document.

Red tests first:

- exactly one outcome is emitted per completed acquisition;
- accepted, cooldown, quota, and store-failure outcomes are distinguishable;
- latency, active-entry cardinality, and eviction are observable;
- the approved provider/outcome dimensions are the only dimensions;
- user ID, Redis key/endpoint, idempotency key, and exception text are absent;
- repeated store failures do not create unbounded log volume.

Implementation:

- use `System.Diagnostics.Metrics`, not per-user logs;
- report exact local cardinality and Redis registry-derived cardinality;
- add bounded/rate-limited structured logging for store failure;
- document dashboard panels, queries, runbook, recovery check, and deployment-owned alerts.

Verification:

```powershell
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --no-restore --filter FullyQualifiedName~GenerateRateLimiterMetricsTests
```

Definition of done: the required dashboard signals are exportable, privacy-safe, exercised by
tests, and documented for operations.

### W07 — Regression, two-instance smoke test, review, and evidence

Files:

- Update the status/evidence sections of the source spec and this plan.
- Update only the operations evidence document/dashboard artifact created in W06.

Actions:

1. Run the two-instance Redis smoke/load test and record accepted/rejected totals, TTL cleanup,
   limiter p50/p95/p99, Redis CPU/memory, outage behavior, and dashboard evidence.
2. Run targeted suites after every fix, then formatter and the full solution once.
3. Perform two review passes: specification compliance, then repository standards/code quality.
4. Fix all critical findings and rerun affected verification.
5. Inspect `git diff` and `git status`; preserve every unrelated pre-existing change.
6. Do not commit, push, merge, or open a PR without explicit developer instruction.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --no-restore
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-restore
dotnet format TripMate.slnx --verify-no-changes
```

Definition of done: all acceptance criteria have recorded evidence, real Redis tests ran, dashboard
signals were observed, formatting and tests pass, and no unrelated changes were introduced.

## Dependency Order

```text
W01 → W02 → W03 → W04 → W05 → W06 → W07
```

W02 and W03 may be developed on separate isolated branches after W01 is approved and complete, but
they must be integrated before W04. Do not advance past a work item with red targeted verification
or an unresolved critical review finding.

## Risk Controls

| Risk | Control | Evidence |
|---|---|---|
| Cross-instance over-grant | one Redis Lua decision using shared state/server time | W05 concurrency tests |
| Same-millisecond event collision | unique opaque sorted-set members | W03/W05 tests |
| Redis Cluster cross-slot failure | common configured hash tag for script keys | W03 key tests, W05 real Redis |
| Store outage bypasses quota | fixed fail-closed policy; no local fallback | W03/W04/W05 outage tests |
| Limiter failure strands reservation | owner-checked release before returning 503 | W01/W04 handler tests |
| Local memory grows forever | idle TTL, proactive sweep, hard capacity | W02 stress tests |
| Rejections keep state alive | TTL refresh only on acceptance | W05 TTL test |
| Host clock skew changes quota | Redis server time in distributed mode | W03 script review, W05 tests |
| Metrics leak user identity/cardinality | fixed low-cardinality dimensions, no user labels | W06 listener tests |
| Redis registry becomes a hot key | load measurement and one hash slot documented; revisit only with evidence | W07 load evidence |
| Existing 429 contract regresses | endpoint/header regression tests | W01/W04 |
| User work is overwritten | path-level diff audit; no reset/checkout of unrelated files | W07 |

## Definition of Done

- [x] Spec and plan explicitly approved before implementation.
- [ ] Distributed Redis limiter enforces one cluster-wide rolling quota atomically (implementation and tests present; real-Redis CI execution pending).
- [x] Cooldown, quota, 429 codes, metadata, and `Retry-After` remain compatible.
- [ ] Redis user keys expire and active cardinality converges after idle TTL (real-Redis CI execution pending).
- [ ] Redis failure is fail-closed 503 and never invokes a local fallback or expensive generation (unit/handler coverage green; real-Redis outage test pending).
- [x] Single-instance state is idle-evicted, capacity-bounded, and concurrency-safe.
- [x] Replay is uncharged and one fresh generation is charged exactly once.
- [ ] Two-instance real Redis tests pass, including concurrency, TTL, outage, and recovery (nine tests are present and enforced without skips in CI; not run locally because Docker/Redis is unavailable).
- [ ] Metrics export accepted/rejected/store-failure/latency/cardinality without user labels; deployment dashboard and two-instance smoke evidence remain pending.
- [x] No database migration or unrelated scheduling semantic change is present.
- [x] Targeted/full tests and formatting pass; both review axes have no unresolved critical finding.
- [x] No commit, push, merge, or PR occurs without explicit instruction.

## Verification Evidence

1. **Unit Testing (`TripMate.Application.UnitTests`):**
   - 1,160 tests passed, 0 failed locally in Release configuration.
   - Verified async `TryAcquireAsync` acquisition, charge-point placement, reservation release on rate-limit/outage failure, and untouched replay semantics.

2. **Infrastructure Unit Testing (`TripMate.Infrastructure.UnitTests`):**
   - 286 tests passed, 0 failed, 1 skipped locally in Release configuration.
   - Focused limiter/options/metrics suite passes 56/56, covering proactive sweep, capacity fail-closed behavior, concurrency, key formation, result parsing, command timeout/cancellation, explicit provider validation, telemetry expiry, and privacy-safe dimensions.

3. **Real Redis Integration Testing (`TripMate.Api.IntegrationTests` - `GenerateRateLimiterRedisTests`):**
   - Nine real-store tests cover independent service-provider quota, same-millisecond cooldown, exact rolling-window boundary, TTL non-extension and actual idle expiry, registry cleanup, fail-closed outage, same-instance recovery, and prefix isolation.
   - They are enforced without skips by `.github/workflows/backend-tests.yml` using `redis:7-alpine`.
   - Local execution is pending because Docker Desktop/Redis is unavailable in the current environment; no pass result is claimed here.

4. **API Integration Testing (`TripMate.Api.IntegrationTests`):**
   - 347 tests passed, 0 failed, 171 skipped locally in Release configuration; the skips are external-service suites, including nine Redis cases without a local endpoint.
   - Verified HTTP 429 response structure and `Retry-After` header for cooldown and quota rejection.
   - Verified HTTP 503 Service Unavailable mapping for `planning.generation_rate_limiter_unavailable`.
   - Full suite verified healthy end-to-end.

5. **Code Style & Formatting:**
   - `dotnet format TripMate.slnx --verify-no-changes --no-restore` completed successfully.

6. **Observability Runbook:**
   - Reproducible dashboard-query and alert templates are published in `docs/operations/uc10-generation-rate-limiter-observability.md`; deployment-specific thresholds and two-instance dashboard evidence remain pending.
