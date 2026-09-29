# TM-56 Review Remediation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use `superpowers:executing-plans` to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the verified UC-10 idempotency, schema-source-of-truth, routing-scale, regression-evidence, and seeded-image secret findings before PR #22 is revalidated.

**Architecture:** Keep idempotency in `SchedulingRequest`: construct and persist a request before every post-validation feasibility decision, mark infeasible requests `Failed`, and replay the stored failure. Canonicalize all persisted input before hashing, cap the candidate set deterministically before the route matrix, and keep `tripmate_schema_v7.sql` equivalent to a new database after migrations. The seeded database image must not retain a plaintext SA password in image configuration.

**Tech Stack:** .NET 10, EF Core SQL Server, xUnit/FluentAssertions, SQL scripts, Docker BuildKit, OpenRouteService adapter.

**Spec:** `specs/TM-56-spec.md`

## Global Constraints

- Database-first: SQL scripts are canonical; never create EF migrations.
- Same idempotency key plus the same normalized payload replays the original success or 422 failure.
- Unexpected persistence/provider failures roll back and do not consume an idempotency key.
- Matrix input is capped at 40 candidates plus start/end, selected deterministically after existing eligibility/ranking data is available.
- Do not commit credentials, connection strings, images, generated artifacts, or `.env` files.

## Review Focus

- Retry an invalid end POI with the same key: one failed request record and the same 422 code must result.
- Equivalent coordinate representations that normalize to six decimals must replay, not return 409.
- Initial generation and same-key replay must expose the persisted leg duration.
- A newly initialized database from `tripmate_schema_v7.sql` must contain the UC10 scheduling/item/cost invariants without depending on migrations.
- A large eligible-POI set must pass no more than 42 matrix locations and preserve mandatory POIs.

---

### Task 1: Pin current response and failed-idempotency defects

**Files:**
- Modify: `tests/TripMate.Application.UnitTests/Features/Scheduling/Create/CreateSchedulingRequestCommandHandlerTests.cs`
- Modify: `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestEndpointTests.cs`

- [ ] Add a test that generates a feasible itinerary, retries its identical command, and asserts a non-null `TravelDurationToNextMinutes` equality for both responses.
- [ ] Add tests for invalid end POI, mandatory POI outside the selected radius, empty eligible set, and `PublicTransit`: each repeats the same key and asserts one failed request plus `planning.constraints_infeasible` on both calls.
- [ ] Run the focused test filter and confirm RED because early feasibility returns do not save/replay a request.

### Task 2: Persist post-validation infeasible operations and canonicalize hashes

**Files:**
- Modify: `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs`
- Modify: `src/TripMate.Domain/Entities/SchedulingRequest.cs` only if a public canonicalization helper is needed

- [ ] Introduce a canonical command representation: UTC start, trimmed time zone, four coordinates rounded away-from-zero to six decimals, sorted mandatory IDs, and the exact persisted scalar values.
- [ ] Compute the hash and create `SchedulingRequest` from that representation before feasibility checks; use a single helper to mark/save/return an infeasible result.
- [ ] Preserve the existing transaction and application lock. Do not persist validation failures rejected before the handler.
- [ ] Run focused tests and confirm GREEN.

### Task 3: Bound route-matrix input deterministically

**Files:**
- Modify: `src/TripMate.Application/Features/Scheduling/Common/SchedulingGenerationOptions.cs`
- Modify: `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs`
- Modify: `src/TripMate.Api/appsettings.json`
- Modify: `tests/TripMate.Application.UnitTests/Features/Scheduling/Create/CreateSchedulingRequestCommandHandlerTests.cs`

- [ ] Add `MaxMatrixCandidates` with default/configured value `40` and validation-safe fallback to 40.
- [ ] Rank eligible optional POIs using the existing preference, scenic, photo, distance, cost, and ID ordering; retain all mandatory POIs, then take the remaining capacity. If mandatory POIs exceed the cap, return infeasible rather than silently omit one.
- [ ] Add a test with more than 40 eligible candidates proving the route provider receives at most 42 points and all mandatory POIs remain candidates.
- [ ] Run focused tests and confirm GREEN.

### Task 4: Restore database-first schema parity and cost invariant

**Files:**
- Modify: `database/tripmate_schema_v7.sql`
- Modify: `database/migrations/20260914_add_scheduling_request_generation.sql`
- Create or modify: `tests/TripMate.Api.IntegrationTests/...` schema parity SQL Server test following existing migration-test conventions

- [ ] Add all UC10 `SchedulingRequests` and `ItineraryItems` columns, constraints, indexes, and FKs to the full schema; include `CHECK (estimated_visit_cost IS NULL OR estimated_visit_cost >= 0)`.
- [ ] Make the upgrade migration create the same non-negative cost check idempotently.
- [ ] Add a SQL Server test that creates a database from the canonical full schema and a legacy database upgraded by migrations, then asserts matching columns/constraints for UC10.
- [ ] Run the SQL Server integration test against Docker and confirm GREEN.

### Task 5: Remove plaintext SA password from seeded image configuration

**Files:**
- Modify: `database/Dockerfile.seeded`
- Modify: `database/seed-image.sh`
- Modify: `database/README.md`

- [ ] Use a BuildKit secret only during the seed `RUN`; do not set `MSSQL_SA_PASSWORD` in `ENV` or leave its plaintext in build metadata.
- [ ] Document the required BuildKit command and runtime password handling, without writing a value into docs or image labels.
- [ ] Build the seeded image locally using a temporary secret and inspect image configuration/history for the absence of `MSSQL_SA_PASSWORD` plaintext.

### Task 6: Rebase, evidence, and PR hygiene

**Files:**
- Modify remotely only after local verification: PR #22 body and resolved review thread

- [ ] Rebase `feature/khanhpq-create-scheduling-request` on the current `origin/develop`; do not squash or merge develop into the branch.
- [ ] Run `dotnet format --verify-no-changes`, `dotnet build`, `dotnet test`, SQL Server integration tests, and `git diff --check` on the rebased head.
- [ ] Replace blocked/not-run PR-body statements and checkboxes with exact successful/failed command evidence, then resolve the outdated duration thread only after its assertions are present.

## Self-review

- P1 failures are persisted only after command validation; validation failures do not consume keys.
- Canonical hash inputs exactly match persisted normalizations.
- The matrix bound cannot exclude a mandatory POI.
- Full schema can bootstrap the current EF model without relying on migrations; migration remains idempotent for existing databases.
- Seeded image configuration exposes no SA password and the README does not ask developers to bake one into an image.
