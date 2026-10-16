# UC-26 plan (Backend)

Pending approval of `specs/UC-26-view-tour-details-spec.md`. Every task is test first (red → green → refactor)
and ends with the listed verification. Baseline before starting: `dotnet build`, `dotnet test` unit projects and
`dotnet format --verify-no-changes` on a clean checkout of `develop`.

## Task 1 — Shared visibility and availability rules
Add `TourVisibility.PublicTour(now)` (expression) and `TourAvailability.Classify(total, reserved)` under
`Features/Tours/Common`. Unit tests for every status/operator/date combination and the capacity table
(available, soldOut, unknown). Do not refactor UC-24 in this task; parity is proven by Task 5 instead.
Verify: new unit tests.

## Task 2 — Query, DTO and handler
`GetTourDetailQuery(long TourId)` → `Result<TourDetailDto>`. Handler runs inside one serializable read, uses
projections (no entity serialization) and fixed-size set queries for destinations, media, policy rules,
itinerary with POIs, schedules, and the existing `TripReviewAggregateReader.ReadTourAsync` for the rating.
Adds `TourErrorCodes.NotFound = "tour.not_found"` and its 404 mapping. Handler tests on the unit DbContext for
each acceptance rule that does not depend on SQL behaviour.
Verify: handler tests.

## Task 3 — Controller and OpenAPI
`GET /api/v1/tours/{tourId:long}` on `ToursController` (`AllowAnonymous`, `no-store`, standard problem
responses); schema documented. Endpoint tests for 200/404, headers, and the exact serialized field set
(including the operator allow-list).
Verify: endpoint tests.

## Task 4 — SQL Server integration tests
Against Docker SQL Server: visibility matrix, version rows, big Int64 ids, schedules filtering, null adult price,
media filtering, rating aggregate, and the constant-query-count test (command interceptor).
Verify: `TripMate.Api.IntegrationTests` tours namespace.

## Task 5 — UC-24 parity test
For one shared fixture set, assert search-returned ids equal detail-200 ids. Guards against the two public
predicates drifting.
Verify: that test.

## Task 6 — Verification and review
Full build, all test projects, format check; two-pass spec/quality review; update the spec status. No push
until the developer chooses.

## Follow-ups (not in this UC)
- Public review list endpoint (needs publication/display-name rules from UC-33 owners).
- Refactor UC-24 to use the shared visibility expression once parity tests are in place.
- Web: replace `PENDING_BE_INTEGRATION` in `getTourDetail`, map this DTO.
- Mobile: rebase PR #31 and implement its remote data source against this contract.
