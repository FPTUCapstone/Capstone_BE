# UC-10 Spatial Prefilter and Targeted POI Loading Implementation Plan

## Status

Implemented locally; SQL Server CI verification pending.

## Goal

Resolve UC-10 Audit Issue #3 without changing itinerary semantics: prefilter the candidate universe
in SQL before loading its related graph and revalidate the same request-scoped data during
finalization.

## Source

`specs/UC-10-spatial-prefilter-poi-loading-spec.md`

## Workspace Rules

- Work on the current branch and preserve all unrelated dirty-worktree changes.
- Do not commit, push, add a migration, or change public API contracts without explicit approval.
- Treat recorded baseline counts as execution evidence, not permanent facts; capture fresh counts
  when implementation begins.

## Work Items

### W01 - Characterization and red boundary tests

Modify/add tests in:

- `tests/TripMate.Application.UnitTests/Common/Geo/`
- `tests/TripMate.Application.UnitTests/Features/Scheduling/Create/CreateSchedulingRequestCommandHandlerTests.cs`
- `tests/TripMate.Application.UnitTests/Features/Itineraries/ItineraryVersionServiceTests.cs`

Add characterization tests before production changes:

1. Exact-radius candidate inclusion and just-outside exclusion.
2. Bounding-box corner over-fetch followed by exact-distance exclusion.
3. Active mandatory POI outside the radius returns the existing mandatory infeasible result.
4. Active end POI outside the radius remains valid.
5. Missing/inactive mandatory and end POIs preserve existing failures.
6. Distant optional POIs do not affect generated candidate ordering/result.
7. Both `CreateRegeneratedVersionAsync` and `CreateAdjustedVersionAsync` cover distant, mandatory,
   and end-POI behavior.

Keep the existing `GeoDistance` behavior authoritative. Do not introduce a new distance formula.

### W02 - SQL Server query-shape and snapshot-scope tests

Modify/add tests in:

- `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestSqlServerTests.cs`
- a SQL Server itinerary-version test fixture under
  `tests/TripMate.Api.IntegrationTests/Itineraries/`
- `tests/TripMate.Application.UnitTests/Features/Scheduling/SchedulingGenerationSnapshotTests.cs`

Use the existing SQL Server fixture and command interceptor to assert:

1. Stage-A SQL contains active-status, inclusive coordinate, and mandatory/end-ID predicates.
2. No POI query materializes before the coordinate predicate is applied.
3. The split-query command shape is independent of the number of candidates.
4. No per-POI/N+1 reads occur.
5. A distant POI metadata mutation between preparation and finalization does not retry.
6. Outside-to-inside, inside-to-outside, and selected-feasibility changes do retry.
7. Phase 3 itself never calls the AI ranking provider. Each Phase-2 preparation attempt calls it at
   most once; the existing single snapshot retry may therefore produce one additional call.

Tests that assert generated SQL must run against SQL Server, not EF InMemory.

### W03 - Introduce request-scoped POI loading

Production areas:

- `src/TripMate.Application/Common/Geo/LocationBounds.cs` only if a reusable overload is required;
  otherwise reuse it unchanged.
- a shared internal POI-loading/query component in `TripMate.Application`.
- the Application DI registration if the component is injected.

Implementation steps:

1. Compute bounds with
   `(int)Math.Ceiling(searchRadiusKm)` and `LocationBounds.From(...)`.
2. Build one reusable active predicate for `(inside bounds OR mandatory ID OR end ID)`.
3. Apply the predicate before the existing `AsSplitQuery` graph materialization.
4. Apply the existing exact equirectangular radius check after materialization.
5. Keep results deterministic wherever snapshot serialization depends on ordering.

### W04 - Bound create scheduling and finalization loads

Modify:

- `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs`
- `src/TripMate.Application/Features/Scheduling/Common/SchedulingGenerationSnapshot.cs`
- related internal records/tests.

Implementation steps:

1. Replace `LoadActivePoisAsync` in `PrepareGenerationAsync` with the bounded graph load.
2. Preserve end and mandatory validation semantics.
3. Keep ranking and provider-pool behavior unchanged.
4. In `FinalizeAsync`, repeat the identical bounded load. Do not call the AI provider or derive a
   new provider pool.
5. Keep disappear/inactive races as snapshot changes or controlled infeasible results.

Run the handler, snapshot, ranking, idempotency, concurrency, and reservation test groups after
this work item.

### W05 - Apply the shared loading pipeline to both version operations

Modify:

- `src/TripMate.Application/Features/Itineraries/Common/ItineraryVersionService.cs`
- `tests/TripMate.Application.UnitTests/Features/Itineraries/ItineraryVersionServiceTests.cs`

Implementation steps:

1. Use the bounded load in both `CreateRegeneratedVersionAsync` and
   `CreateAdjustedVersionAsync`.
2. Preserve their current deterministic preference scoring and `LimitMatrixCandidates` ordering.
3. Load full details only for the bounded geographic scope plus mandatory/end IDs.
4. Keep an active end outside the radius valid.
5. Do not silently admit an outside-radius `orderedVisitPoiId` in adjusted mode.
6. Extract shared mapping only where create/version policies are genuinely identical.

### W06 - SQL verification and performance evidence

1. Run the new SQL Server tests and inspect captured commands for coordinate and ID predicates.
2. Confirm the split-query shape and absence of N+1 reads for create, regenerate, and adjust.
3. Capture the actual SQL Server execution plan or `STATISTICS IO` for representative spatial and
   explicit-ID requests. Record whether `IX_POIs_LatLng` is used; do not infer use from its presence.
4. Run a reproducible non-CI benchmark at 1k, 10k, and 100k POIs with a constant local candidate
   population. Record returned rows/logical reads, elapsed time, and managed allocation.
5. Compare candidate IDs, provider-pool IDs, matrix IDs, and final itinerary against the baseline
   fixture to prove semantic equivalence.

If the optimizer still scans excessively because of the spatial/ID `OR`, evaluate a SQL `UNION` or
two bounded queries and deduplicate by ID. A spatial type/index remains a separate migration.

### W07 - Final regression and documentation

Run, in order:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --no-restore
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --no-restore --filter "Category=SqlServer"
dotnet format TripMate.slnx --verify-no-changes
```

Record exact passed/failed/skipped counts and the SQL/benchmark artifact location. Then update the
spec and plan to `Completed` only when all acceptance criteria have evidence. Inspect `git status`
and confirm no unrelated file was modified.

## Definition of Done

- [x] Red characterization and snapshot-scope tests were observed before the fix.
- [x] No full-catalog POI entity-graph load remains in create, regenerate, or adjust flows.
- [x] The entity graph is SQL-bounded; exact-distance semantics remain unchanged.
- [x] Mandatory-outside and end-outside contracts are preserved.
- [x] Phase 3 revalidates the same canonical bounded scope without a second AI-provider call.
- [x] Unrelated distant changes do not trigger a retry.
- [ ] SQL Server tests prove translation and bounding-box predicate application.
- [x] Both itinerary-version operations use the bounded loading rules.
- [x] Existing non-SQL Application, Infrastructure, and API integration tests pass.
- [x] Formatting passes and no unrelated files, commits, pushes, or migrations were introduced.

## Verification Evidence

- Red/green: `Handle_DistantPoiMutationOutsideBoundingBox_DoesNotTriggerSnapshotRetry` failed
  before the bounded query and passed afterward.
- `dotnet build TripMate.slnx --no-restore`: passed with 0 warnings and 0 errors.
- Application unit tests: 1,159 passed, 0 failed.
- Infrastructure unit tests: 232 passed, 1 external Cloudinary smoke test skipped.
- API integration tests without a SQL Server connection: 344 passed, 162 SQL Server tests skipped.
- `dotnet format TripMate.slnx --verify-no-changes --no-restore`: passed.
- The targeted SQL Server test compiled, but local execution is pending because the configured
  local SQL Express instance failed authentication (`Cannot generate SSPI context`). CI must run
  `PoiLoad_AppliesSpatialAndExplicitIdPredicatesBeforeMaterialization` without skips.
