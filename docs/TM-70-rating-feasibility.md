# TM-70 Phase 1.3 — Rating Feasibility and Index Evidence

**Status:** `FEASIBILITY_PASS` / `BLOCKED_SHARED_REVIEW_GATE`
**Docs authority:** `Capstone_Docs@2ad6916dc1ff4f314c19e13258c088533a93cd9a`
**Backend baseline:** `c8cc7f4f88da0441c3607560ce58b9ea022544e0`
**Measured:** 2026-10-14

## Scope and verdict

This checkpoint proves that confirmed `Published` canonical Tour reviews can be
aggregated, filtered, sorted, and paginated set-wise on SQL Server with a
covering index. It does not implement TM-70 Phase 2 Search Tours V2.

The independently safe implementation change is one additive filtered index on
`social.TripReviews`:

```sql
CREATE INDEX IX_TripReviews_TourPublicationRating
    ON social.TripReviews(tour_id, publication_status)
    INCLUDE (overall_rating, booking_id)
    WHERE tour_id IS NOT NULL;
```

No Review state, row, writer, endpoint, or public API behavior changes. The
migration is transactional, idempotent, rejects a same-name wrong-shape index,
and is mirrored in the full schema and EF metadata.

## Approved eligibility exercised by the feasibility tests

The test-only feasibility query uses only rows that have positive publication
evidence in the canonical `social.TripReviews` table:

```text
tour_id = requested Tour
AND publication_status = 'Published'
```

It proves:

- no confirmed Published review produces `null` average and count zero;
- one and multiple confirmed Published reviews aggregate correctly;
- the unrounded arithmetic mean of ratings `4, 5, 5` is used for the inclusive
  `4.6` boundary and fails the `4.7` boundary;
- public projection rounding is deliberately not selected in this checkpoint;
- other Tour IDs and POI rows do not contribute;
- publication-unknown legacy `social.Reviews` rows do not enter the approved
  feasibility query;
- unrated Tours sort after rated Tours;
- one set-based statement returns a 20-row page without per-Tour queries.

Hidden/deleted Review states and deletion behavior remain deferred by RLC-D3
and RLC-D4. No test or implementation invents those states.

## SQL Server evidence

Environment:

- isolated disposable databases named `TripMate_Test_<guid>`;
- local SQL Server `16.0.4265.3`, Developer Edition, SQL Server 2022;
- dataset for plan comparison: 200 Tours and 4,000 confirmed Published canonical
  Tour reviews;
- result page: 20 rows;
- credentials and connection strings were not written to the repository.

Measured actual-plan result for the same set-based rating page query:

| Shape | `social.TripReviews` logical reads | Result rows |
|---|---:|---:|
| Covering filtered index present | 210 | 20 |
| Index removed in the disposable test database | 5,202 | 20 |

The actual execution plan references
`IX_TripReviews_TourPublicationRating`. The automated test restores the index by
running the production migration after the no-index comparison and verifies its
exact shape. This is targeted feasibility evidence, not a production NFR claim;
dataset scale, hardware, and workload differ from production.

## Shared Review compatibility finding

The current repository has two Review stores:

1. `social.TripReviews` — canonical typed-parent records with an explicit
   `publication_status = 'Published'` constraint;
2. legacy polymorphic `social.Reviews` — no publication-status metadata.

`TripReviewAggregateReader.ReadTourAsync` currently concatenates canonical
Published Tour ratings with legacy `social.Reviews` Tour ratings when no
canonical booking overlap exists. Because legacy rows have no trustworthy
publication provenance, this existing runtime behavior does not yet implement
the owner-approved Published-only RLC-D5/RLC-D6 eligibility contract.

The same shared boundary affects POI consumers. Existing detail/explore paths
use the shared aggregate reader, while POI recommendation logic also reads
legacy Reviews. This PR intentionally does not alter those consumers, classify
legacy rows, add `publication_status` to `social.Reviews`, or change writers.

Therefore:

- Phase 1.3 feasibility and the canonical covering index are reviewable;
- full Rating Foundation implementation is **not** claimed;
- production rollout of a shared legacy Review lifecycle remains
  `BLOCKED_SHARED_REVIEW_GATE`;
- Phase 2 rating filter/sort cannot be declared complete until the cross-module
  Tour/POI eligibility and mixed-version deployment gate has explicit ownership
  and approval.

## Verification contract

Focused tests cover:

- fresh-schema index inventory;
- missing-index upgrade and rerun idempotency;
- wrong-shape rejection without rewriting the conflicting index;
- confirmed Published aggregation and null behavior;
- legacy/POI/other-Tour isolation;
- unrounded minimum-rating thresholds;
- rating order with unrated Tours last;
- actual-plan index selection, logical reads, and page-bounded result count.

The migration changes no data, so no seed/backfill or category decision is
coupled to this PR. TM-70 Phase 1.2 remains independently blocked on Product/BA
taxonomy and mapping approval.

## Verification evidence

Executed against the isolated local SQL Server identified above:

- RED contract: the focused fresh-schema test failed because the expected
  four-column index inventory was absent (`0 passed / 1 failed / 0 skipped`);
- focused TM-70 feasibility tests: `6 passed / 0 failed / 0 skipped`;
- TM-70 plus existing aggregate regression: `31 passed / 0 failed / 0 skipped`;
- affected fresh/upgrade parity tests after wiring the additive migration:
  `3 passed / 0 failed / 0 skipped`;
- full SQL-enabled solution regression: `3,631 passed / 0 failed / 13 skipped`.
  The skips are existing explicit opt-in Redis/Cloudinary integration tests and
  were not counted as passes;
- Release build: succeeded with `0 errors`; existing package-vulnerability
  warnings for `System.Security.Cryptography.Pkcs 10.0.0` remain;
- scoped formatter verification for changed C# files: passed;
- solution-wide formatter verification: blocked by pre-existing whitespace in
  `GetActiveTripDetailsQueryHandler.cs` and
  `CreateSchedulingRequestCommandHandler.cs`, both unchanged by this work;
- post-test residue check: zero `TripMate_Test_%` databases.

Fresh-schema shape, missing-index upgrade, rerun idempotency, wrong-shape
rollback, and full fresh/upgrade inventory parity are executable SQL tests.
