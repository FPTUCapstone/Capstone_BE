# UC-58 View Active Trips Backend Specification

## Status

Approved by the developer on 2026-09-26.

## Sources and approved decisions

- SRS section 3.9.6.1, BR-32, BR-51, BR-52, BR-115, BR-124, CR-01, CR-02, CR-07, MSG29, MSG126, MSG127, and MSG128.
- UC-58 is an Administrator Web-only, read-only monitoring function.
- Approved on 2026-09-26: an active trip session has FSM state `Navigating`, `Exploring`, or `Interrupted`. `Planning` and `Completed` are excluded.
- Approved on 2026-09-26: trip code is derived as `TRIP-{session_id}`; this use case does not add a database column.
- Approved on 2026-09-26: itinerary source `BookedTour` maps to `Tour`; `CSPGenerated` and `Manual` map to `SelfPlanned`.
- Approved on 2026-09-26: a Self-Planned trip whose schema has no canonical destination returns `null`; the Web displays `Not available`.
- Approved on 2026-09-26: open alerts are incidents whose `resolved_at` is `NULL`.

## Scope

The Backend exposes one Administrator-only query that returns summary counters and a deterministic, filtered, paginated collection of active trip sessions. The query does not modify trips, itineraries, memberships, bookings, incidents, or any other record.

This specification also permits the minimum domain/EF read mappings required for `trip.TripSessions`, `trip.Incidents`, and the existing `planning.Itineraries.source_tour_id` relationship. The typed `Itinerary` entity must expose nullable `SourceTourId`/`SourceTour` members and a `BookedTourSourceType` constant; `TripSession` and `Incident` must be present on every application/production/test DbContext surface. It does not change the SQL schema.

## API contract

### Endpoint

`GET /api/v1/admin/trips/active`

Authentication and authorization:

- A valid authenticated Administrator receives the result.
- Missing or invalid authentication returns `401`.
- A valid non-Administrator identity returns `403` using the project ProblemDetails contract (`MSG126` at the Web boundary).

### Query parameters

| Parameter | Type | Default | Rules |
| --- | --- | --- | --- |
| `keyword` | string? | `null` | Trimmed; maximum 200 characters; case-insensitive substring match against derived Trip Code, linked Group Name, or Tour Title. Search runs only when the Web submits it. |
| `tripType` | string? | `null` | `SelfPlanned` or `Tour`; omission means all. |
| `destination` | string? | `null` | Trimmed; maximum 300 characters; case-insensitive match against a Tour's normalized destination names. A Self-Planned row never matches a non-empty destination filter because it has no canonical destination. |
| `startDateFrom` | date? | `null` | ISO calendar date `yyyy-MM-dd`, interpreted in `Asia/Ho_Chi_Minh`; inclusive. |
| `startDateTo` | date? | `null` | ISO calendar date `yyyy-MM-dd`, interpreted in `Asia/Ho_Chi_Minh`; inclusive. Must not precede `startDateFrom`. |
| `alertState` | string? | `null` | `WithOpenAlerts` or `WithoutOpenAlerts`; omission means all. |
| `pageNumber` | integer | `1` | Minimum 1. |
| `pageSize` | integer | `20` | Minimum 1, maximum 100. The Web uses 20 in accordance with CR-01. |

Malformed or semantically invalid query parameters return `400` ProblemDetails with field errors. An inverted date range maps to MSG29 in the Web.

Date/time normalization follows CR-07 and the Backend persistence convention:

- `startDateFrom` and `startDateTo` are date-only API inputs, so `yyyy-MM-dd` is their transport format even though rendered dates use `dd/MM/yyyy`.
- The inclusive local range is converted to a half-open UTC interval: local midnight at `startDateFrom` is inclusive, and local midnight on the day after `startDateTo` is exclusive. The implementation must not construct an end-of-day value such as `23:59:59.999`.
- Stored timestamps and timestamp response fields are UTC. `startedAtUtc` is serialized as ISO 8601 with an explicit UTC designator, for example `2026-09-26T01:30:00Z`.
- Calendar calculations first convert both the injected UTC clock value and `startedAtUtc` to `Asia/Ho_Chi_Minh`; browser, server, and database machine time zones must not affect the result.

### Successful response

`200 OK`

```json
{
  "summary": {
    "activeTrips": 12,
    "tripsWithOpenAlerts": 3,
    "travelersOnTrip": 26
  },
  "pageNumber": 1,
  "pageSize": 20,
  "totalCount": 12,
  "totalPages": 1,
  "items": [
    {
      "tripId": "42",
      "tripCode": "TRIP-42",
      "tripType": "Tour",
      "currentState": "Navigating",
      "groupOrTraveler": "Da Nang Weekend Group",
      "destination": "Da Nang",
      "startedAtUtc": "2026-09-26T01:30:00Z",
      "currentDay": 1,
      "members": 4,
      "openAlerts": 1
    }
  ]
}
```

An empty match is still `200 OK`, with `totalCount = 0`, `totalPages = 0`, and `items = []`; the Web renders MSG128. Summary counters remain operational totals for all active trips and are not narrowed by the submitted filters.

## Data mapping and calculations

- `tripId`: decimal string representation of `trip.TripSessions.session_id`. It is serialized as a JSON string because SQL Server `BIGINT` can exceed JavaScript's safe integer range.
- `tripCode`: invariant `TRIP-` plus the decimal session ID, without locale formatting or zero padding.
- Trip-code search applies the normalized keyword to that complete derived string. For example, `trip-42` and `42` both match `TRIP-42`; matching remains substring-based, so tests must use IDs that avoid accidental overlap when asserting a single row.
- Active set: `fsm_state IN ('Navigating','Exploring','Interrupted')` evaluated at query time.
- `currentState`: the current `fsm_state` value.
- `tripType`: `Tour` only for `Itineraries.source_type = 'BookedTour'`; otherwise `SelfPlanned`.
- `groupOrTraveler`: join `TripSessions` to `TravelGroups` through their shared `itinerary_id` only. Use distinct group names that are non-null and non-whitespace, ordered by group ID and joined with `, `. If no usable group name remains, use the session Traveler's full name, falling back to email. No direct TravelGroup-to-TripSession FK exists.
- `destination`: for a Tour, use distinct destination names ordered by `TourDestinations.sequence_no` and joined with `, `; for Self-Planned, return `null`.
- `startedAtUtc`: `TripSessions.started_at`, returned as UTC. It remains nullable because the current schema permits `NULL`; a malformed active row must not crash the list.
- `currentDay`: `null` when `started_at` is null; otherwise `max(1, local calendar date now - local calendar start date + 1)`, using `Asia/Ho_Chi_Minh`.
- `members`: distinct members whose typed status equals `GroupMemberStatus.Active`, across groups linked through the itinerary; if there are no linked groups, `1` for the session Traveler. Raw status strings are not used.
- `openAlerts`: count of incidents for the session with `resolved_at IS NULL`.
- `travelersOnTrip`: distinct user IDs across session Travelers and active members of groups linked to active sessions. A user present in several active sessions is counted once.
- `tripsWithOpenAlerts`: distinct active sessions having at least one unresolved incident.

Results are ordered by `started_at DESC`, with null start timestamps last, then `session_id DESC`. Pagination is applied only after filters and deterministic ordering.

## Acceptance criteria

1. Only Administrators can retrieve the endpoint.
2. The active-state definition, derived trip code, trip type, destination fallback, member count, alert count, and summary counters follow this specification.
3. Keyword, trip type, destination, local start-date range, and alert-state filters compose correctly.
4. Invalid inputs return `400`; authorization failures return `401`/`403`; unexpected failures do not expose database details.
5. Empty results return a successful empty page rather than `404`.
6. The query is read-only and performs no `SaveChanges` operation.
7. Unit tests cover validation and calculation boundaries; API integration tests cover `401`, `403`, `200`, filtering, deterministic pagination, and empty results; SQL Server integration tests verify joins and counts against the real schema.
8. OpenAPI documents the query values, bounds, response DTO, and authorization responses.

## Non-goals and dependencies

- UC-59 trip details, exact Traveler location, itinerary mutation, group mutation, incident resolution, rerouting decisions, and any intervention action are excluded.
- No GPS coordinates are returned by the list endpoint (BR-51).
- A human-readable persisted Trip Code and a canonical Self-Planned destination require separate schema/business decisions.
- Runtime trip creation/FSM execution is not implemented by this use case; tests may seed records directly.
- Administrator login/session correctness is an external dependency and must be integrated before the full Web flow can be accepted.
