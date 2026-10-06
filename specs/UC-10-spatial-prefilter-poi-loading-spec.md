# UC-10 Spatial Prefilter and Targeted POI Loading Specification

## Status

Implemented locally; SQL Server CI verification pending.

## Problem

UC-10 currently materializes every active POI together with `Category`, `OpeningHours`, and
`PoiTags -> Tag` before applying the request radius in memory.

- `CreateSchedulingRequestCommandHandler.PrepareGenerationAsync` performs the load before
  ranking and generation.
- `CreateSchedulingRequestCommandHandler.FinalizeAsync` repeats the load inside the
  `SERIALIZABLE` finalization transaction for snapshot revalidation.
- Both `ItineraryVersionService.CreateRegeneratedVersionAsync` and
  `ItineraryVersionService.CreateAdjustedVersionAsync` perform the same unbounded load.

Consequently, row transfer, entity materialization, join amplification, allocation, and
transactional read scope grow with the entire catalog instead of the request's geographic and
candidate scope. A change to an unrelated distant POI can also invalidate a generation snapshot.

## Goals

1. Apply a sargable latitude/longitude bounding-box predicate in SQL before materialization.
2. Preserve the existing exact equirectangular distance check as the authoritative radius gate.
3. Preserve mandatory and end POIs for validation even when they are outside the bounding box.
4. Avoid loading the related-entity graph for active POIs outside the request's geographic and
   explicit-ID scope.
5. Revalidate the same request-scoped source data in Phase 3 without calling the AI provider again.
6. Apply equivalent loading behavior to both itinerary-version operations.
7. Preserve the existing split-query shape without introducing N+1 queries.

## Non-goals

- No public request/response or HTTP-status change.
- No change to ranking weights, provider-pool limits, route calculation, feasibility rules, or
  itinerary selection semantics.
- No spatial column, spatial index, or database migration in this change. The existing
  `IX_POIs_LatLng(latitude, longitude)` remains available; actual plan/index usage must be measured
  rather than assumed.
- No second AI/provider ranking call during finalization.
- No rule forcing a custom end POI to be inside the exploration radius.

## Required Semantic Invariants

1. The exact radius comparison remains
   `GeoDistance.EquirectangularKilometers(...) <= SearchRadiusKm`.
2. An active mandatory POI outside the exact radius is resolved and returns the existing
   `planning.constraints_infeasible` result with
   `A mandatory location is unavailable or outside the selected area.`
3. A missing, inactive, or otherwise unavailable mandatory POI produces the same result as today.
4. An active custom end POI may be outside the exploration radius and remains a valid endpoint.
5. A missing or inactive custom end POI returns the existing ending-location-unavailable result.
6. Optional candidate ordering, provider-pool membership, matrix cap, and generated itinerary are
   identical to the baseline for an unchanged data snapshot.
7. `Status == Active` applies to both spatial and explicit-ID branches. An explicit ID must never
   make an inactive POI eligible.

## Technical Design

### 1. Bounding box

Reuse `LocationBounds.From(latitude, longitude, radiusKm)` and the constants in `GeoDistance`;
do not duplicate approximate constants in the scheduling code.

`CreateSchedulingRequestCommand` stores a decimal radius while `LocationBounds.From` accepts an
integer. Pass `(int)Math.Ceiling(searchRadiusKm)`. This intentionally permits bounded over-fetch
but cannot exclude an exact-radius candidate. The subsequent exact-distance comparison remains
authoritative.

The SQL spatial branch is inclusive:

```text
Latitude  >= MinimumLatitude  AND Latitude  <= MaximumLatitude
Longitude >= MinimumLongitude AND Longitude <= MaximumLongitude
```

The current distance function does not normalize antimeridian longitude deltas. This change must
remain behaviorally consistent with that function; global geodesic/antimeridian support is a
separate change.

### 2. Request-scoped entity-graph load

The create and itinerary-version flows apply this active-POI predicate before materialization:

```text
Status == Active AND (inside bounding box OR ID is mandatory OR ID is custom end)
```

The query retains the existing `AsNoTracking`/`AsSplitQuery` includes for category, opening hours,
and tags. This increment bounds that graph load to POIs inside the box plus explicit mandatory/end
IDs instead of loading the entire active catalog. A lightweight screening projection followed by
a provider-pool-only detail load is a separate future optimization and is not claimed here.

After materialization:

1. Resolve the active custom end independently of the radius.
2. Apply planning-readiness and exact-distance gates to visit candidates.
3. Validate all mandatory IDs against the exact candidate set.
4. Rank optional candidates and preserve the existing frozen provider-pool behavior.

### 4. Snapshot scope and Phase 3

The snapshot must represent every database input capable of changing the prepared result:

- normalized traveler preference tokens;
- the complete bounded candidate universe for this request, including ranking and feasibility
  fields, coordinates, category, opening hours, and tags;
- personal-behavior aggregation for the exact optional candidates when behavior ranking is used;
- full feasibility/explanation fields for that bounded candidate universe.

Phase 3 repeats the same bounded query and behavior aggregation. It hashes the same canonical
snapshot shape used in Phase 2. Phase 3 must not invoke the AI ranking provider or recompute a
different provider pool.

Expected behavior:

- metadata changes to a distant POI that stays outside scope do not cause a retry;
- insertion into the bounded candidate universe causes a mismatch;
- moving a POI from outside to inside, or from inside to outside, causes a mismatch;
- changes to a selected POI's feasibility data cause a mismatch.

### 5. Itinerary version flows

Both `CreateRegeneratedVersionAsync` and `CreateAdjustedVersionAsync` use the same bounding and
explicit-ID rules. They retain their existing deterministic preference scoring and matrix
limiting behavior.

For adjusted versions, `orderedVisitPoiIds` keep their existing validation semantics. The loader
must not silently add an outside-radius ordered visit to the candidate set.

### 6. Query-shape constraints

- All queries are `AsNoTracking`.
- Coordinate comparisons and explicit-ID filtering are translated to SQL.
- No `AsEnumerable`, `ToList`, or other client-materialization boundary appears before the SQL
  bounding predicate.
- POI-catalog reads retain the constant `AsSplitQuery` command shape and contain no
  per-candidate/N+1 query pattern.

## Acceptance Criteria

1. SQL Server command capture proves the POI SQL contains active status, inclusive latitude,
   longitude, and explicit mandatory/end ID predicates.
2. A distant, non-explicit POI is absent from the request-scoped POI results.
3. An active mandatory POI outside the bounding box is resolved, then rejected by exact radius
   with the existing infeasible code/message.
4. An active custom end POI outside the bounding box is resolved and remains usable; missing or
   inactive end POIs preserve the current failure contract.
5. Candidates exactly on/just inside the radius remain included. Bounding-box corner candidates
   outside the circle are over-fetched by the SQL query and removed by the exact-distance gate.
6. For unchanged data, candidate eligibility, ordering, provider pool, matrix candidates, and final
   result match the pre-change behavior.
7. Phase 2 and Phase 3 hash the same canonical bounded data. Outside-to-inside and
   inside-to-outside coordinate changes trigger retry; an unrelated distant metadata change does
   not.
8. Both regenerated and adjusted itinerary-version flows exclude distant POIs without changing
   their public behavior.
9. The bounded POI load uses split queries with no per-candidate query.
10. A future performance benchmark should record elapsed time, SQL logical reads/returned rows,
    and managed allocation for representative catalog sizes.
11. All existing Application, Infrastructure, and relevant API integration tests pass.

## Delivery Constraints

- Follow TDD: observe the new behavioral and SQL-shape tests fail before production changes.
- SQL translation claims must be tested against SQL Server; EF InMemory tests are insufficient.
- Do not commit or push without explicit developer instruction.
- Preserve unrelated uncommitted developer work.
