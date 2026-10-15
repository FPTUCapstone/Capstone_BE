# TM-211 internal raw Tour rating projection

Approved by the user on 2026-10-15 for internal engineering only. R-SOURCE remains approved. Public R-ROUND, R-PLACEMENT, R-POI and G-ROLLOUT remain pending.

## Scope reconciliation

The supplied `TM211_INTERNAL_RAW_PROJECTION_SPEC.md` was read in full and reconciled with the user's eight constraints. The optional count representation is resolved to the existing `int` contract. No business decision is needed for this internal slice.

`TourRatingAggregateProjection` contains `TourId`, nullable decimal `RawAverage`, integer `ReviewCount`, and derived nullable decimal `DisplayAverage`. Display uses the unchanged `TripReviewAggregateDto.Round` helper after materialization. It is not a translated filter/sort column or a public rounding decision. SQL consumers use only `RawAverage` for rating predicates and ordering.

`QueryTourRatings(context, tours)` accepts a composable `IQueryable<Tour>` scope. It returns one row per distinct scoped Tour ID, including nullable averages and zero count when no contribution exists. The caller remains responsible for Tour visibility, filters and transaction lifetime. Query construction performs no I/O.

The existing Tour contribution expression is extracted once and shared with `ReadTourAsync`. Canonical rows use exact Tour ID, `Published`, and `OverallRating`. Legacy rows use exact Tour target and exclude any matching Published canonical commerce booking. The overlap predicate is not restricted to the same Tour, and service booking IDs remain a separate namespace. `Concat` retains SQL `UNION ALL`; no legacy publication, policy version, provenance or data is modified. Existing overlap logging remains unchanged.

The old TM-79 reader retains its rounded response, integer count and cancellation behavior. `QueryPoiReviews`, POI consumers, public DTOs, routes, DI, schema and indexing remain unchanged. No semantic API is activated.

## Acceptance criteria

- R03: ratings 4, 5, 5 retain SQL raw mean 4.666666 and existing display 4.7. Legacy overlap is excluded without mutating rows.
- R11: raw mean >= 4.6 includes this Tour; >= 4.7 excludes it. Exact 4.5 is inclusive. Null mean is excluded by a supplied minimum rating, but an unfiltered scoped Tour has null averages/count zero.
- R12: filter and global raw order precede count and page slicing. Verify multiple pages, null last, title/ID ties, empty beyond-last page and a fixed number of SQL commands as scope grows.
- Preserve legitimate equal-valued contributions, commerce/service namespaces, old Tour and POI behavior, and caller cancellation.
- Generated SQL contains AVG, UNION ALL, grouping, left join and server-side paging; no ROUND in rating predicates/order and no per-Tour reader loop.

SQL AVG retains the provider's decimal precision rather than rounding to a display digit. It is a finite SQL decimal value, not an assertion of infinite rational precision. Display is never used to reconstruct a raw value.

## Boundaries and files

Only `TripReviewAggregateReader.cs`, `TourRatingAggregateProjection.cs`, the existing `TripReviewAggregateSqlServerTests.cs`, this specification and the matching plan are changed. The projection is Application query plumbing, not an HTTP DTO. R12 verifies internal composition, not a completed Search V2 or semantic global-coverage implementation.
