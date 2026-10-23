# TM-78 Backend API contract — UC-32 View Trip History

Status: **implemented on `feature/tm-78-trip-history`, verified 2026-10-21.** Authority:
`specs/TM-78-spec.md`, `plans/TM-78-plan.md`, Report 3 §3.7.1. For Capstone_FE and Capstone_Mobile.

Read-only. Nothing here creates, changes or deletes a booking, itinerary, session or review (PC-03).

## What changed for clients

The provisional contract the FE/Mobile stubs assumed is replaced:

| Stub assumption | Real contract |
| --- | --- |
| response `trips[]` | response `items[]` |
| record id `tripId` (a bare id) | record identity is the pair **`kind` + `id`** — never use the id alone |
| detail `GET /traveler/trips/{tripId}` | `GET /api/v1/traveler/trips/{kind}/{id}` |
| `501 PENDING_BE_INTEGRATION` | `200` / the errors below |

`kind` is `commerceBooking` (tour booking) or `itinerary` (self-planned). `commerceBooking` is the same
wire value as `ReviewableRecordRef.kind` in the TM-79 review contract; use `reviewableRecord` from an item
to navigate to UC-33 and never convert between `id`, `bookingId` and `itineraryId`.

## Auth

`Authorization: Bearer <traveler token>`. Active Traveler account only.

| HTTP | `errorCode` | When |
| --- | --- | --- |
| 401 | `trip_history.unauthorized` | Not signed in. |
| 403 | `trip_history.forbidden` | Not a Traveler, or the account is not Active. |

## `GET /api/v1/traveler/trips`

| Query | Rule |
| --- | --- |
| `tab` | **Required.** Exactly `Upcoming`, `Completed` or `Cancelled` (case-sensitive). |
| `tripType` | Optional. `All` (default), `TourBooking`, `SelfPlannedItinerary`. |
| `fromDate`, `toDate` | Optional `yyyy-MM-dd`, inclusive, evaluated as `Asia/Ho_Chi_Minh` calendar days. |
| `page`, `pageSize` | Default `1` and `20`. `pageSize` 1–50. `page × pageSize` must not exceed **1000**. |

Order: departure descending; on equal departure `commerceBooking` before `itinerary`; then `id` descending.
Pages never repeat or skip a record.

```json
{
  "items": [
    {
      "kind": "commerceBooking", "id": 123, "tripType": "TourBooking", "tab": "Completed",
      "title": "Hoi An Lantern Tour", "departureAtUtc": "2026-10-01T02:00:00+00:00", "status": "Completed",
      "bookingCode": "TM-000123", "participantCount": 2, "totalAmount": 3200000,
      "reviewableRecord": { "kind": "commerceBooking", "id": 123 },
      "actions": { "canReview": true, "canViewETicket": true, "ticketId": 77, "canViewRefund": false },
      "review": { "exists": false }
    },
    {
      "kind": "itinerary", "id": 45, "tripType": "SelfPlannedItinerary", "tab": "Completed",
      "title": "Da Nang 2 days", "departureAtUtc": "2026-09-20T01:00:00+00:00", "status": "Completed",
      "stopCount": 6, "visitedCount": 5,
      "reviewableRecord": null,
      "actions": { "canReview": false, "canViewETicket": false, "ticketId": null, "canViewRefund": false },
      "review": null
    }
  ],
  "summary": { "totalCompletedTrips": 14, "totalVisitedPois": 63 },
  "page": 1, "pageSize": 20, "totalCount": 14
}
```

- `summary` is present **only when `tab=Completed`**. `totalCompletedTrips` equals `totalCount` (all pages,
  under the active filters). `totalVisitedPois` counts reached stops.
- Booking-only members (`bookingCode`, `participantCount`, `totalAmount`) and itinerary-only members
  (`stopCount`, `visitedCount`) are **omitted** for the other kind. `visitedCount` exists only for Completed
  itineraries. `reviewableRecord` and `review` are always present (`null` for itineraries).
- `review` is `{ "exists": true, "reviewId": 41, "rating": 4 }` when reviewed; `reviewId`/`rating` are omitted otherwise.
- `actions.ticketId` is the id for the UC-29 e-ticket link. Never substitute the booking id.
- Dates are UTC instants (`+00:00`); format them as `dd/MM/yyyy` in `Asia/Ho_Chi_Minh` (CR-07).
- No records is `200` with `items: []` and `totalCount: 0` (show MSG128), not an error.

### Tab and action rules

| Record | Upcoming | Completed | Cancelled |
| --- | --- | --- | --- |
| Booking | `PendingPayment`, `Confirmed` | `Completed` | `Cancelled`, `Expired` |
| Itinerary | `Active`, no completed session | has a completed session | `Cancelled`, no completed session |

- Booking tabs follow the **stored status only**; a `Confirmed` booking whose tour already ended stays
  Upcoming until something marks it `Completed`.
- A self-planned trip is Completed when its trip session is Completed **or has passed its expiry** (a session
  nobody reopened is treated as expired, as navigation would). The list never writes this back.
- `canReview`: Completed booking not yet reviewed. Always `false` for itineraries.
- `canViewETicket`: Confirmed or Completed booking with a Valid/Used ticket (Valid preferred, then lowest id).
- `canViewRefund`: Cancelled booking with at least one refund.
- Only the Traveler's own records are returned; itineraries shared through a group and itineraries created
  for a booked tour are not listed.

### Errors (`400`, `errorCode` always present)

| `errorCode` | When |
| --- | --- |
| `trip_history.invalid_tab` | `tab` missing or not one of the three values. |
| `trip_history.invalid_trip_type` | Unknown `tripType`. |
| `trip_history.invalid_date_range` | Malformed date or `fromDate` after `toDate` (MSG29). |
| `trip_history.invalid_paging` | `page < 1`, `pageSize` outside 1–50, or `page × pageSize > 1000`. |

A non-numeric `page`/`pageSize` is rejected by the framework with a plain `400` validation problem that has
**no** `errorCode`; clients should send integers. A system failure is the standard `500` problem (MSG127).

## `GET /api/v1/traveler/trips/{kind}/{id}`

Returns the same fields as a list item plus the detail below. Missing records and records that belong to
someone else return the identical `404` `trip_history.record_not_found`.

`400 trip_history.invalid_record`: `kind` is not `commerceBooking`/`itinerary`, or `id` is not a positive
64-bit whole number.

**`commerceBooking`**

```json
{ "...": "list item fields",
  "tour": { "title": "Hoi An Lantern Tour", "destinationSummary": "Hoi An" },
  "schedule": { "startAtUtc": "…", "endAtUtc": "…", "meetingPoint": "Old Town gate" },
  "participants": { "adult": 2, "child": 1, "infant": 0 },
  "cancellation": { "cancelledAtUtc": "…", "reason": "Change of plans" },
  "refund": { "status": "Processed", "amount": 800000, "channel": "VNPAY" } }
```

`cancellation` and `refund` appear only for a `Cancelled` booking; `refund` only if one exists. When
several refunds exist the one with the highest id is returned. `channel` is the gateway of the original
payment and is `null` when it cannot be resolved.

**`itinerary`**

```json
{ "...": "list item fields",
  "validFromUtc": "…", "validToUtc": "…",
  "stops": [ { "sequenceNo": 1, "poiId": 9, "poiName": "Dragon Bridge",
               "plannedArrivalUtc": "…", "plannedDepartureUtc": "…",
               "visitStatus": "Reached", "reachedAtUtc": "…" } ],
  "session": { "startedAtUtc": "…", "endedAtUtc": "…", "completionReason": "TravelerStopped" } }
```

For a Completed itinerary the stops come from the representative session with `visitStatus`
(`Reached`/`Skipped`/`Pending`) and the reached/skipped time; `session.completionReason` is
`RouteFinished`, `TravelerStopped` or `Expired`. Other itineraries list their planned stops without visit
fields and have no `session`. If an itinerary has several completed sessions, the one that ended last is used.

## Known limits

- Service bookings are not included yet (no entity); `kind` is open for a later `serviceBooking`.
- A booking linked to an itinerary instead of a tour schedule is not a tour booking and is not listed.
- No Booking is set to `Completed` by any backend flow today, so the Completed tab of tour bookings stays
  empty until that exists or data is seeded.
