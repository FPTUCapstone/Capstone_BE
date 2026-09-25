# TM-56 Final Review Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make UC-10 migration upgrades fail-safe for every owned default and prove POI search/scheduling share radius semantics on SQL Server.

**Architecture:** Migration scripts use an expected-to-actual left join so an absent default is observable and repaired. Same-name defaults with another target or literal fail. GET search and POST scheduling use the shared inclusive equirectangular radius rule and POI-ID final ordering.

**Tech Stack:** .NET 10, SQL Server, EF Core, xUnit, FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-09-24-tm-56-backend-final-review-remediation.md`

## Global Constraints

- Database-first SQL scripts are canonical; do not create EF migrations.
- Missing defaults are repaired; a named wrong-shape default fails loudly.
- Radius membership is inclusive and identical for GET search and POST scheduling.
- Multi-day is authoritative; one-day code is unchanged in this remediation.

## Review Focus

- All six owned defaults absent: upgrade restores canonical metadata.
- Correct-name but wrong-literal/target defaults: upgrade rejects them.
- Cardinal inside/boundary/outside POIs: GET and POST agree.
- Equal name/distance records across pages: unique stable ID order.
- Disabled or untrusted non-negative-cost constraint: migration rejects it.

---

### Task 1: Cover the canonical default inventory with real SQL tests

**Files:**
- Modify: `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestSqlServerTests.cs`

**Interfaces:** Consumes `ApplySchedulingMigrationsAsync(SqlServerTestDatabase database)` and produces parameterized tests for every default's absence and wrong shape.

- [ ] Write failing parameterized tests which drop each of `time_zone_id`, `rest_preference`, `mandatory_poi_ids_json`, `return_to_start`, `transport_mode`, and `item_kind`, apply both migrations twice, and assert the canonical name, column and normalized literal.
- [ ] Run `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SchedulingMigration_WhenCanonicalDefaultIsMissing"`; expect failure because an inner join cannot see an absent default.
- [ ] Replace each named default with a wrong literal or target and assert SQL error 51000.

### Task 2: Repair missing defaults and verify all actual metadata

**Files:**
- Modify: `database/migrations/20260914_add_scheduling_request_generation.sql`
- Modify: `database/migrations/20260915_extend_scheduling_request_contract.sql`
- Modify: `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestSqlServerTests.cs`

**Interfaces:** Consumes inventory rows `(schema, table, column, name, normalized definition)` and produces an idempotent, fully verified default inventory.

- [ ] Replace both default inventory `INNER JOIN` queries with expected rows left-joined to default constraint and column metadata; null actual metadata, wrong parent, wrong column, and wrong normalized literal must all be mismatches.
- [ ] Add absence-only repair: add the canonical default only when its parent column has none; never overwrite a same-name wrong-shape object; final left-join validation throws 51000.
- [ ] Run `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SchedulingMigration"`; expect all real SQL tests to pass without skips.
- [ ] Commit with `fix(scheduling): validate complete default inventory`.

### Task 3: Prove radius and paging contract with SQL endpoint tests

**Files:**
- Modify: `tests/TripMate.Api.IntegrationTests/PointsOfInterest/SearchSelectablePoisSqlServerTests.cs`
- Modify: `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestSqlServerTests.cs`
- Modify only if tests expose a mismatch: `src/TripMate.Application/Features/PointsOfInterest/Search/SearchSelectablePoisQueryHandler.cs`

**Interfaces:** Consumes shared `GeoDistance.EquirectangularKilometers`; produces GET/POST boundary parity and stable page-order evidence.

- [ ] Seed north, south, east and west POIs just inside, exactly on and just outside radius; assert search returns exactly the POIs scheduling accepts as mandatory.
- [ ] Seed equal-distance/equal-name POIs across two pages; assert repeated page calls are complete, duplicate-free, and ascending by ID at final tie-break.
- [ ] Run `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~SearchSelectablePoisSqlServerTests|FullyQualifiedName~CreateSchedulingRequestSqlServerTests"`; update query code only if the evidence exposes a mismatch.
- [ ] Commit with `test(scheduling): prove shared POI radius semantics`.

### Task 4: Revalidate and refresh PR evidence

**Files:**
- Modify remotely: PR #22 description.

- [ ] Run `dotnet format TripMate.slnx --verify-no-changes --no-restore`, `dotnet build TripMate.slnx -c Release --no-restore`, `dotnet test TripMate.slnx -c Release --no-restore`, SQL Server category tests, and `git diff --check`.
- [ ] Update PR #22 with final SHA, exact command evidence, and the approved multi-day source-of-truth note; push and request exact-SHA re-review.
