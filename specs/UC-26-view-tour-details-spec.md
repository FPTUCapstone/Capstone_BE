# UC-26 View Tour Details — Backend spec

Status: **PROPOSED — awaiting developer approval.** Items marked *(open)* need an explicit yes/no.

SRS: Report 3 §3.5.3 (UC-26 "View Tour Details"), BR-54, BR-56, BR-59, BR-60, BR-96, messages MSG65, MSG127, MSG128.
Actors: Guest and Traveler. Viewing needs no credentials; authentication is only required to book (UC-27).

## Product outcome

A Guest or Traveler opens one publicly available tour and sees everything needed to decide whether to
book: operator, gallery, price, destinations, day-by-day itinerary with its POIs, inclusions and
exclusions, cancellation policy, upcoming departures with the remaining slots **at display time**, and
the aggregate rating. The page must never reveal a Draft, Pending, Rejected, unpublished, or
inactive-operator tour.

## Decisions already approved (2026-10-17)

| Topic | Decision |
|---|---|
| Reviews | Minimal: return only the aggregate rating (`average`, `count`). No public review list in this UC; clients show MSG128 when `count` is 0 and otherwise show the aggregate only. A public review list is a later task. |
| Versions | A tour version is a separate `commerce.Tours` row. The endpoint serves a row only when that row itself is publicly visible (same predicate as UC-24). Any other version returns 404. |
| Operator | Company name and contact phone only. Address, tax code, licence number, commission and documents are never exposed. |

## Scope

In: one anonymous read endpoint and its DTO, the shared "publicly visible tour" rule, availability
classification, tests, OpenAPI.
Out: review list, booking, recommendations (UC-25), payment, any Web/Mobile change, any write.

## API contract

`GET /api/v1/tours/{tourId}` — `[AllowAnonymous]`, `Cache-Control: no-store`, ids are 64-bit integers.

Responses:
- `200` `TourDetailDto` (below).
- `404` ProblemDetails, code `tour.not_found` (MSG65) for: unknown id, id ≤ 0, non-public tour, tour of an
  unapproved or inactive operator. The response must not reveal which of those it was.
- `500` standard ProblemDetails (clients show MSG127).

A route value that is not a valid 64-bit integer does not match the route and is a framework 404.

### `TourDetailDto`

Identity values are **strings** (large signed Int64 safe), prices are whole VND `long`, timestamps UTC.

| Field | Notes |
|---|---|
| `tourId` | string |
| `title`, `description` | description may be null for legacy rows |
| `destination` | derived summary text (e.g. `Hội An, Đà Nẵng`) |
| `destinations` | ordered catalogue names (`TourDestinations.sequence_no`) |
| `durationDays` | int |
| `basePrice`, `currency` | `long`, `"VND"` |
| `operator` | `{ name, contactPhone? }` only |
| `images` | `[{ url, altText, caption?, isPrimary }]`, active media only, primary first then `sort_order` |
| `inclusions`, `exclusions` | string or null |
| `cancellationPolicy` | `{ rules: [{ daysBeforeDeparture, cancellationFeePercent }] }` from the tour's snapshot, longest notice first; empty list if none |
| `itinerary` | `[{ dayNo, items: [{ sequenceNo, poi: { poiId, name, address?, latitude, longitude }, plannedTime?, stayDurationMinutes, notes? }] }]` ordered by day then sequence |
| `schedules` | see below |
| `hasBookableSchedule` | true when any schedule is bookable (drives the Book Now button, BR-60) |
| `rating` | `{ average: number?, count: int }`, from the existing published-review aggregate (BR-96); `average` null when `count` is 0 |
| `asOfUtc` | the instant availability was computed (BR-59: a snapshot, not a reservation) |

`schedules` — every `Scheduled` departure that starts in the future, ordered by start:
`{ scheduleId, startAtUtc, endAtUtc, meetingPoint?, registrationDeadlineAtUtc?, adultPrice, childPrice?, infantPrice?, remainingSlots?, availabilityStatus, bookable }`.
- `adultPrice` = departure adult price, falling back to `basePrice` for legacy rows with null.
- `availabilityStatus`: `available` (reserved < total), `soldOut` (reserved ≥ total), `unknown` (capacity
  snapshot invalid: total ≤ 0, reserved < 0 or reserved > total). For `unknown`, `remainingSlots` is null.
  Same classification as UC-24 search.
- `bookable` = `availabilityStatus == available` and now < `registrationDeadlineAtUtc` when one is set.
- Cancelled, Completed and past departures are not listed (they cannot be booked, BR-60).

## Domain rules

1. **Public visibility (BR-56)** — identical to UC-24: `Status = Published`, or `Status = Approved` with
   `PublishedAtUtc <= now`; and the operator profile is `Approved` and its user `Active`.
2. A draft or any non-visible tour is indistinguishable from a missing one (404).
3. Availability is read in one consistent snapshot (serializable read, like search) so slots and
   schedules cannot come from different instants.
4. Sensitive fields are projected out in the query; entities are never serialized.
5. Query cost is bounded: a fixed number of set-based queries independent of the number of schedules,
   itinerary items or images (no per-row queries).
6. The unknown-capacity case is logged as a warning without leaking schedule details (as in search).

## Open questions *(open)*

1. **POI no longer active:** show itinerary POIs even if the catalogue POI was later deactivated
   (proposed: yes — the tour was approved with it, and hiding a stop would misstate the itinerary).
2. **Schedule window:** list all future `Scheduled` departures, or cap (proposed: no cap; a tour has few).
3. **`address` of a POI:** include (public catalogue data) — proposed yes.

## Acceptance tests

1. Anonymous GET of a Published tour returns 200 with all sections populated and the exact field set.
2. Draft, Pending, Rejected, Inactive, Approved-but-not-yet-published, unknown id and id 0 all return 404 with
   `tour.not_found` and an identical body.
3. A tour whose operator is not Approved or whose user is not Active returns 404.
4. Another version row (not itself visible) returns 404 while the visible row returns 200.
5. Large Int64 ids are returned as strings and round-trip.
6. Availability: available / soldOut / unknown classified like UC-24; `remainingSlots` correct;
   `bookable` false when sold out or past the registration deadline; `hasBookableSchedule` consistent.
7. Past, Cancelled and Completed departures are not listed.
8. Legacy departure with null adult price shows the tour base price.
9. Inactive/soft-deleted media is not returned; primary image first.
10. Operator exposure is limited to name and phone (a serialization test asserts no other operator field).
11. `rating` equals the TM-79 aggregate (published canonical reviews) and is `{null, 0}` with no reviews.
12. Parity: for the same fixtures, the set of tours that UC-24 search returns equals the set whose detail
    returns 200.
13. `Cache-Control: no-store`; anonymous access works with and without an Authorization header.
14. Query count is constant when schedules, items and images are multiplied.
