# TM-78 Backend Technical Specification — UC-32 View Trip History

## 1. Document Control

| Field | Value |
| --- | --- |
| Task ID | TM-78 |
| Use case | UC-32 — View Trip History (Report 3 §3.7.1) |
| Status | **Approved 2026-10-21** (P1–P9 plus tie-breaking rules T1–T3, §6.1) — plan in `plans/TM-78-plan.md` |
| Date | 2026-10-21 |
| Baseline | `develop` @ `62d5672` |
| Branch | `feature/tm-78-trip-history` (created after approval) |
| Predecessor context | TM-79 (UC-33 canonical review; froze the UC-32 handoff contract), UC-13 (navigation sessions), UC-27/41/42 (booking, cancellation, refund) |
| Downstream | Capstone_FE (`/account/trips`) and Capstone_Mobile (`trip_history`), currently `PENDING_BE_INTEGRATION` stubs |

## 2. Executive Summary

Add a read-only Traveler API that lists and opens the Traveler's own trips, grouped as
`Upcoming`, `Completed` and `Cancelled`. A record is either a tour booking
(`commerce.Bookings`) or a self-planned itinerary (`planning.Itineraries` plus its
`trip.TripSessions`). Each record carries the actions the UI may offer
(`canReview`, `canViewETicket`, `canViewRefund`) and, for reviewable bookings, the typed
`ReviewableRecordRef` frozen by TM-79. Nothing is created, changed or deleted (PC-03).

## 3. Scope

### In Scope
- `GET /api/v1/traveler/trips` — paginated list with tab, trip-type and date-range filters.
- `GET /api/v1/traveler/trips/{kind}/{id}` — detail of one owned record.
- Two record kinds: `commerceBooking` (tour booking) and `itinerary` (self-planned).
- Visited-location data for completed itineraries from `trip.TripSessionItems`.
- Refund summary for cancelled bookings from `payment.Refunds`; the refund `channel` is the
  `PaymentGateway` of the original `PaymentTransaction` referenced by `Refund.TransactionId`
  (there is no stored channel column).
- Existing-review state for bookings from `TripReview`.
- Unit and integration tests; no schema change.

### Out of Scope
- Service bookings (`commercial.ServiceBookings`): the Domain has no entity for them yet. The
  `kind` value space is left open so they can be added later without a contract break.
- Any write: review submission (TM-79), cancellation (UC-41), refund processing (UC-42).
- E-ticket retrieval itself (UC-29); this task exposes only whether one can be opened.
- Itineraries shared through a travel group (BR-90: own records only).
- Distance, CSP match-rate and average-rating statistics (no defined source).
- FE and Mobile changes (separate tasks, FE then Mobile).

## 4. Verified Baseline

- No Trip History endpoint exists on `develop`. FE `tripReviewBff.ts` and Mobile
  `TripHistoryRepositoryImpl` both fail closed with `PENDING_BE_INTEGRATION`.
- `GET /api/v1/navigation-sessions` supports only `state=Open`.
  `GET /api/v1/itineraries` returns summaries without visit data or a completion flag.
- `Itinerary.CompletedStatus` exists but is never assigned. `BookingStatus.Completed` is read by
  revenue code and by TM-79 review eligibility, but no handler in `src` sets it. Completed
  bookings therefore come from data written elsewhere (seed or a future UC-43/completion task).
- The only runtime signal that a self-planned trip finished is `TripSession.FsmState =
  Completed` with `EndedAtUtc` and `CompletionReason` set by
  `PUT /navigation-sessions/{id}/completion` or expiry.
- `ReviewableRecordRef` (`src/TripMate.Application/Features/TripReviews/Common/ReviewableRecordRef.cs`)
  supports only `commerceBooking` and `serviceBooking`. TM-79 states a standalone itinerary
  has no review identity.
- `TripReviewErrorCodes` shows the error-code convention: `<feature>.<snake_case>`.
- List conventions come from `ListMyItinerariesQuery`: `DefaultPageSize = 20`, `MaxPageSize = 50`,
  a FluentValidation validator, `Result<T>` plus `HandleFailure`.

## 5. Decisions Confirmed by the Developer (2026-10-21)

1. First delivery covers **tour bookings + self-planned itineraries** only.
2. A self-planned trip is Completed when its `TripSession.FsmState = Completed`.
3. Route stays `/api/v1/traveler/trips`; identity is the pair `(kind, id)`, never a bare id.

## 6. Proposed Rules — Need Explicit Approval

These are not stated in Report 3 or the TM-79 contract. They are proposals, marked **P**, and
must be approved or changed before the plan is written.

| # | Proposal |
| --- | --- |
| P1 | **Booking tab mapping** by stored status only, with no inference from dates: `Confirmed` and `PendingPayment` → Upcoming; `Completed` → Completed; `Cancelled` and `Expired` → Cancelled. A `Confirmed` booking whose schedule already ended stays Upcoming until something marks it `Completed`. |
| P2 | **Itinerary tab mapping**: any itinerary with a Completed `TripSession` → Completed; `Itinerary.Status = Cancelled` → Cancelled; `Active` without a Completed session → Upcoming; `Draft` is excluded (not a trip yet). |
| P3 | **Itinerary scope**: only `source_type` `Manual` and `CSPGenerated` owned by the Traveler (`TravelerUserId`). `BookedTour` itineraries are excluded because the booking already represents that trip. |
| P4 | **Itinerary versions**: a Completed record is the version the session froze (`TripSession.ItineraryId`). Upcoming and Cancelled records are the newest version per scheduling request, reusing the rule in `ListMyItinerariesQueryHandler`. |
| P5 | **Departure date** used for sort and date filter: booking → `TourSchedule.StartAtUtc`; itinerary → `TripSession.StartedAtUtc` if a session exists, else `ValidFromUtc`, else `CreatedAtUtc`. |
| P6 | **Review actions**: `canReview` is true only for a `Completed` booking with no existing `TripReview` for its `BookingId` (BR-91, BR-92). It is always false for `itinerary` records, and `reviewableRecord` is null for them (TM-79). |
| P7 | **`canViewETicket`** is true for `Confirmed` or `Completed` bookings that have at least one `Ticket` in a `Valid` or `Used` state. The first such ticket's id is returned as `ticketId` for the UC-29 link. |
| P8 | **`canViewRefund`** is true for a `Cancelled` booking that has at least one `Refund`. |
| P9 | **Summary** on the Completed tab: `totalCompletedTrips` (count under the active filters, not only the current page) and `totalVisitedPois` (count of `Reached` session items). Nothing else. |

### 6.1 Deterministic Tie-Breaking (approved with P1–P9)

P1–P9 are approved as written. The developer added three ordering rules so results are fixed and
testable. They refine P2, P4, P5, P7 and P8.

| # | Rule |
| --- | --- |
| T1 | **Session choice (refines P2, P4, P5).** When an itinerary has several `TripSession` rows, the *representative session* is the latest Completed one: `Where(FsmState == Completed).OrderByDescending(s => s.EndedAtUtc).ThenByDescending(s => s.Id).First()`. Completed status, `StartedAtUtc` for the departure date, the frozen `ItineraryId`, `visitedCount` and the detail `stops[]` visit data all come from that one session. Sessions that are not Completed never influence tab, date or counts. |
| T2 | **Ticket choice (refines P7).** `ticketId` is the first eligible ticket ordered by state `Valid` before `Used`, then `Ticket.Id` ascending. `Cancelled` and `Expired` tickets are never eligible. |
| T3 | **Refund choice (refines P8).** When a cancelled booking has several `Refund` rows, the detail `refund` object uses the one with the highest `Refund.Id` (`OrderByDescending(r => r.Id)`). `canViewRefund` stays true when any refund exists. |

| T4 | **Effective expiry (added 2026-10-21, developer decision; extends T1).** Navigation closes an expired session lazily (`TripSession.ExpireIfDue`), and no background job persists it, so a session can be past `ExpiresAtUtc` while the database still says Navigating/Exploring/Interrupted. For Trip History such a session counts as **Completed**: `FsmState != Completed AND ExpiresAtUtc <= now` (at the boundary instant it is expired, matching `ExpireIfDue`). Its end instant is `ExpiresAtUtc` and its reason is `Expired`, exactly what `ExpireIfDue` would store. T1 ordering then applies to the *effective* end instant (`EndedAtUtc` for a stored Completed session, `ExpiresAtUtc` for an expired open one), `Id` descending as the tie-break. The read never writes. A session with no `ExpiresAtUtc` and not Completed is still open. |

Notes: a Completed session always has `EndedAtUtc` set by the completion path; if a row ever has it
null, descending order places it last, and `Id` still breaks ties. The T1 rule is implemented once,
as a shared query helper, so list, detail, summary and counts cannot disagree.

## 7. API Contract

All routes: `Authorize(Policy = TripReviewAuthorizationPolicies.ActiveTraveler)` (Active Traveler
account, matching SRS "Credentials required"). No synthetic envelope; failures use RFC 7807
`ProblemDetails` with a stable `errorCode`.

### 7.1 `GET /api/v1/traveler/trips`

| Query | Type | Rule |
| --- | --- | --- |
| `tab` | `Upcoming` \| `Completed` \| `Cancelled` | Required. Allowed values live on the query record as constants (§4.1 of AGENTS.md). |
| `tripType` | `All` \| `TourBooking` \| `SelfPlannedItinerary` | Optional, default `All`. |
| `fromDate`, `toDate` | `yyyy-MM-dd` | Optional. Day boundaries are evaluated in `Asia/Ho_Chi_Minh` (CR-07), inclusive. `fromDate > toDate` is invalid. |
| `page`, `pageSize` | int | Default `1` and `20`; `pageSize` 1–50 (CR-01, same limits as `ListMyItinerariesQuery`). |

Sort: departure date descending, then `kind`, then `id` descending so pages are stable.

Response `200`:

```json
{
  "items": [
    {
      "kind": "commerceBooking",
      "id": 123,
      "tripType": "TourBooking",
      "tab": "Completed",
      "title": "Tour name",
      "departureAtUtc": "2026-10-01T02:00:00Z",
      "bookingCode": "TM-ABC123",
      "participantCount": 2,
      "totalAmount": 3200000,
      "status": "Completed",
      "reviewableRecord": { "kind": "commerceBooking", "id": 123 },
      "actions": {
        "canReview": true,
        "canViewETicket": true,
        "ticketId": 77,
        "canViewRefund": false
      },
      "review": { "exists": false }
    },
    {
      "kind": "itinerary",
      "id": 45,
      "tripType": "SelfPlannedItinerary",
      "tab": "Completed",
      "title": "Da Nang 2 days",
      "departureAtUtc": "2026-09-20T01:00:00Z",
      "stopCount": 6,
      "visitedCount": 5,
      "status": "Completed",
      "reviewableRecord": null,
      "actions": { "canReview": false, "canViewETicket": false, "ticketId": null, "canViewRefund": false },
      "review": null
    }
  ],
  "summary": { "totalCompletedTrips": 14, "totalVisitedPois": 63 },
  "page": 1,
  "pageSize": 20,
  "totalCount": 14
}
```

- `summary` is returned only when `tab = Completed`; otherwise it is omitted.
- `review` is `{ "exists": bool, "reviewId"?, "rating"? }` for bookings and `null` for itineraries.
- An empty result is `200` with `items: []` (UI shows MSG128); it is not an error.
- Dates are UTC instants; the client formats them (CR-07).

### 7.2 `GET /api/v1/traveler/trips/{kind}/{id}`

`kind` is `commerceBooking` or `itinerary`; `id` is a positive 64-bit integer. Returns the list item
fields plus:

- **commerceBooking**: `tour` (title, destination summary), `schedule` (start/end UTC, meeting
  point), `participants` (count by type), `cancellation` (`cancelledAtUtc`, `reason`) and `refund`
  (`status`, `amount`, `channel`) when cancelled.
- **itinerary**: `validFromUtc`, `validToUtc`, ordered `stops[]` with `poiId`, `poiName`, planned
  arrival/departure and, when a Completed session exists, `visitStatus`
  (`Reached`/`Skipped`/`Pending`), `reachedAtUtc`, `skippedAtUtc`; plus session `startedAtUtc`,
  `endedAtUtc`, `completionReason`.

A record the caller does not own, or that does not exist, returns the same `404` so existence is
not leaked.

### 7.3 Errors

| HTTP | `errorCode` | When |
| --- | --- | --- |
| 400 | `trip_history.invalid_tab` | Missing or unknown `tab`. |
| 400 | `trip_history.invalid_trip_type` | Unknown `tripType`. |
| 400 | `trip_history.invalid_date_range` | Bad date format or `fromDate > toDate` (SRS MSG29). |
| 400 | `trip_history.invalid_paging` | `page < 1` or `pageSize` outside 1–50. |
| 400 | `trip_history.invalid_record` | Unknown `kind` or non-positive `id`. |
| 401 / 403 | `trip_history.unauthorized` / `trip_history.forbidden` | Not signed in / not an Active Traveler. |
| 404 | `trip_history.record_not_found` | Missing or not owned. |

System failure (MSG127) is the standard `500` `ProblemDetails`.

## 8. Design

- New feature folder `src/TripMate.Application/Features/TripHistory/` with
  `List/ListTravelerTripsQuery(+Handler,+Validator)`, `Get/GetTravelerTripQuery(+Handler,+Validator)`,
  `Common/TripHistoryErrorCodes`, `Common/TripRecordKinds`, and response records.
- New `TravelerTripsController` in `Controllers/V1`, thin: bind, call `Sender`, `HandleFailure`.
- Queries read through `IApplicationDbContext` with `AsNoTracking()`; no repository abstraction.
  Booking and itinerary streams are projected to one common row shape and merged in the database
  where possible; if EF cannot translate the union, each stream is counted and paged by sort key
  and merged in memory over the bounded page window (decided in the plan, with a test).
- Constants for tabs, trip types and kinds sit on the query record; no raw string literals.
- Review state reads `TripReview` by `BookingId` for the page's bookings in one query (no N+1).
- No schema change, so no migration. If query plans need an index, it ships as a separate
  idempotent script and is flagged for approval.

## 9. Acceptance Criteria

1. A Traveler sees only their own bookings and itineraries (BR-90); another Traveler's id returns `404`.
2. Each record appears in exactly one tab, following P1–P3.
3. Completed itineraries report `visitedCount` equal to the `Reached` items of the frozen session.
4. `canReview` follows P6; a reviewed booking never offers review (BR-92); an incomplete one never does (BR-91).
5. Cancelled bookings expose the refund state, amount and channel when a refund exists.
6. Sort is departure date descending and pages never repeat or skip a record.
7. Invalid filters return the §7.3 codes; empty results return `200` with no items.
8. The endpoints modify no row (PC-03), verified by a test asserting unchanged row versions.
9. `dotnet test` passes with the existing suite unchanged.

## 10. Test Strategy

- Unit: tab classification (P1–P3), departure-date selection (P5), action derivation (P6–P8),
  validator bounds, `ReviewableRecordRef` mapping.
- Integration (`TripMate.Api.IntegrationTests/TripHistory/`): ownership isolation, each tab,
  filters and Vietnam day boundaries, paging stability, 401/403/404, refund projection, session
  visit data, no-write assertion.
- Written test-first per AGENTS.md §1.2 step 4.

## 11. Risks

| Risk | Mitigation |
| --- | --- |
| No code sets `Booking.Status = Completed`, so the Completed tab may be empty in a real environment until completion exists. | State it in the PR; seed data covers verification. No date-based guess (P1). |
| `Itinerary.CompletedStatus` is unused, so the itinerary Completed tab relies on `TripSession`. | Confirmed decision 2; documented here. |
| Union paging across two tables may be costly. | Bounded window plus a test; index proposal only if measured. |
| FE/Mobile currently read `trips[]` and a bare `tripId`. | They change in their own tasks to `items[]` and `(kind, id)`. |

## 12. Open Items for the Developer

Approve or change P1–P9 (§6). The plan (`plans/TM-78-plan.md`) is not written until this spec is
approved.
