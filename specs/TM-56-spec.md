# Specification: TM-56 [UC-10] Create Scheduling Request

**Feature**: Create Scheduling Request and generate a one-day itinerary  
**Jira Ticket**: TM-56  
**Use Case**: UC-10  
**Branch**: `feature/khanhpq-create-scheduling-request`  
**Target Repositories**: `Capstone_BE`, `Capstone_Mobile`  
**Status**: Approved for planning

## Objective

Let an authenticated Traveler generate a practical one-day draft itinerary.
The system must produce an ordered, time-feasible plan that respects the
Traveler's start, end, transport, time, mandatory POIs, budget, opening-hours,
and optional saved preferences. If it cannot satisfy every hard constraint, it
must return an explicit infeasible outcome instead of a partial itinerary.

## Scope

- Mobile form for selecting locations, time, transport, budget, and mandatory
  POIs without making the Traveler type latitude/longitude.
- Traveler-facing read-only POI search needed by the form.
- Backend request validation, deterministic constraint-aware generation,
  persistence, idempotency, and result contract.
- One-day ordered-list result with estimated arrival/departure, travel duration,
  visit duration, per-stop estimated cost, total cost, total duration, and
  scheduled rest breaks where requested or needed for a practical day.
- Existing saved Traveler preferences are used as soft ranking input when a
  profile exists; generation still works without a profile.

## Explicitly Out of Scope

- A map result, turn-by-turn navigation, live traffic, weather rerouting,
  restaurant reservations, background location, multi-day planning, and
  comparison of alternate plans.
- Partial/best-effort results when a mandatory POI cannot fit.
- Editing an itinerary after generation, itinerary history UI, and public/group
  sharing. The created draft is persisted for later UCs.
- A paid map provider, in-app map rendering, and public-transit timetables.
  The backend calls an external route-time provider; its key is server-side
  configuration and is never bundled in the Mobile app.

## Product Decisions

### Traveler input

- `startAt` and `timeZoneId` are required. The Mobile MVP sends
  `Asia/Ho_Chi_Minh`; the API stores UTC timestamps and the supplied IANA time
  zone for opening-hours evaluation and display.
- The start location is required. Mobile offers current device location or a
  selected POI; location permission denial must still permit POI selection.
- The exploration centre/destination is required and is selected from a POI.
  Candidate POIs must be within the submitted search radius of this centre.
- The ending point is independent of the exploration centre. The Traveler may
  select an ending POI or request `returnToStart`; exactly one is required.
- `availableMinutes` is required and must be from 60 to 720 minutes. The
  scheduled trip must start and end on the same local calendar day.
- `transportMode` is required for this request. Mobile defaults it from the
  saved preference when present but the Traveler can override it.
- `searchRadiusKm` is required and must be from 1 to 50 km.
- `budgetVnd` is optional, positive when supplied, and means estimated spend
  per Traveler for POI visits only. Food, tickets not represented by POI cost,
  and transport cost are not included in this MVP.
- `mandatoryPoiIds` is optional, must contain distinct active POIs, and is
  capped at six. Every selected POI must appear exactly once in a successful
  itinerary.
- `restPreference` is required and is one of `Auto`, `None`, or `Frequent`.
  Mobile defaults to `Auto`. `None` means no named rest-stop recommendation,
  not that the generator may eliminate its safety buffer.

### Data quality and constraint rules

- Add a nullable estimated visit cost to POIs. `0` means verified free;
  `null` means the cost is unknown.
- When the Traveler supplies a budget, an auto-selected POI with unknown cost
  is ineligible. A mandatory POI with unknown cost makes the request
  infeasible. The result must label every amount as estimated.
- A POI with explicitly recorded opening hours is eligible only when its whole
  planned visit fits that local-day opening interval. A POI with no opening-hour
  record is excluded from automatic selection and may not be mandatory; this
  avoids presenting an unverified POI as known to be open.
- The generated itinerary includes all mandatory POIs, starts at the submitted
  start location, and reaches the requested end before the available duration
  is exhausted.
- A successful itinerary is a `Draft`. Group membership or itinerary sharing
  never grants itinerary edit ownership.
- A rest candidate must have coordinates, a known opening interval, and a
  category suitable for a break (for example cafe, restaurant, or sheltered
  rest area). Its purchase cost is not assumed to be zero and is excluded from
  the current POI-visit budget unless a verified cost is later modeled.

### Generation algorithm

- The MVP implementation is a deterministic, feasibility-first CSP heuristic;
  it is not an external AI service.
- It loads active candidates inside the exploration radius and their opening
  hours, then evaluates mandatory POIs first. Up to six mandatory POIs are
  permuted to find the least-duration feasible sequence.
- Travel duration is calculated through `IRouteDurationProvider`. The first
  development implementation calls openrouteservice Directions/Matrix V2 with
  a server-side API key and road-network profiles. Haversine is permitted only
  to prefilter candidates before the provider call; it is never used as a
  displayed leg duration. If the provider is unavailable, generation fails
  safely with MSG127 rather than returning a misleading schedule.
- `Motorbike` is modeled using the road-driving profile in this MVP and Mobile
  labels resulting travel times as estimates. The product must not claim that
  the provider supplies motorbike-specific routing.
- The generator reserves a configurable transition buffer for every leg and a
  final return buffer. With `Auto`, it attempts one 30-45 minute rest break
  after roughly 2.5-3 hours of continuous travel plus visits when the day is
  at least five hours. With `Frequent`, it targets a break at most every two
  hours. The break is a `Rest` itinerary item, not an attraction visit. If no
  qualified rest POI is available, it reserves the break time without naming a
  venue.
- The algorithm adds optional POIs greedily only when arrival/departure,
  opening hours, budget, and eventual end location remain feasible. Ranking
  favors saved interests, scenic score, photo rating, shorter travel distance,
  and lower estimated cost.
- If the mandatory sequence is infeasible, or no candidate can form a valid
  itinerary, return the defined infeasible result. Do not omit mandatory POIs
  and do not fabricate a partial success.

## HTTP Contract

### Search selectable POIs

`GET /api/v1/points-of-interest/search`

Query parameters: `query`, `latitude`, `longitude`, `radiusKm`, `page`, and
`pageSize`. It is a Traveler-facing read model and returns only active POIs
needed for location/mandatory-POI selection: ID, name, address, latitude,
longitude, estimated visit duration, estimated cost, and opening-hours-known
status. It never exposes admin-only fields.

### Generate itinerary

`POST /api/v1/scheduling-requests`

Required header:

```text
Idempotency-Key: <UUID>
```

Request body:

```json
{
  "startAt": "2026-10-20T08:00:00+07:00",
  "timeZoneId": "Asia/Ho_Chi_Minh",
  "startLatitude": 16.0544,
  "startLongitude": 108.2022,
  "explorationLatitude": 16.0471,
  "explorationLongitude": 108.2068,
  "endPoiId": 88,
  "returnToStart": false,
  "availableMinutes": 480,
  "transportMode": "Motorbike",
  "searchRadiusKm": 10,
  "budgetVnd": 800000,
  "mandatoryPoiIds": [12, 28],
  "restPreference": "Auto"
}
```

Success response (`201 Created`) is a raw DTO:

```json
{
  "schedulingRequestId": 24,
  "itineraryId": 91,
  "title": "Generated itinerary - 20 Oct 2026",
  "status": "Draft",
  "totalEstimatedCost": 650000,
  "totalDurationMinutes": 455,
  "items": [
    {
      "sequenceNo": 1,
      "poiId": 12,
      "poiName": "Marble Mountains",
      "itemKind": "Visit",
      "plannedArrival": "2026-10-20T08:30:00Z",
      "plannedDeparture": "2026-10-20T10:00:00Z",
      "stayDurationMinutes": 60,
      "travelDurationToNextMinutes": 25,
      "estimatedCost": 40000,
      "isMandatory": true,
      "recommendationReason": "Mandatory location"
    }
  ]
}
```

### Failure contract

| Condition | HTTP status | Error code | Mobile outcome |
| --- | --- | --- | --- |
| Missing/malformed idempotency key | 400 | standard ProblemDetails | validation error |
| Invalid field, invalid coordinates, duplicate mandatory POI, invalid end choice | 400 | `planning.invalid_request` | inline field error |
| Unauthenticated/non-Traveler caller | 401/403 | existing auth handling | MSG125/MSG126 |
| Mandatory POI missing/inactive/outside radius/unknown hours/cost under budget | 422 | `planning.constraints_infeasible` | infeasible explanation, keep form |
| No feasible route within time, hours, cost, and end constraints | 422 | `planning.constraints_infeasible` | infeasible explanation, keep form |
| Same key and same payload after success | 201 | replay original itinerary | navigate to original result |
| Same key with different normalized payload | 409 | `planning.idempotency_key_payload_mismatch` | request a new attempt |
| Unexpected service/persistence failure | 5xx | sanitized ProblemDetails | MSG127; no partial itinerary |

## Persistence and Atomicity

Database-first SQL scripts are mandatory; EF migrations are forbidden.

- Extend `planning.SchedulingRequests` with UTC `start_at`, `time_zone_id`,
  end-location fields, transport mode, `idempotency_key`, `request_hash`, and
  nullable `failure_code`. Add unique `(traveler_user_id, idempotency_key)`.
- Extend `catalog.POIs` with nullable non-negative `estimated_visit_cost`.
- Add an itinerary-item kind (`Visit` or `Rest`) so the UI and downstream
  functionality do not mistake a suggested break for a mandatory attraction.
- Seed only a curated, planning-ready Da Nang sample set. Coordinates and
  basic categories may originate from OpenStreetMap with visible attribution;
  opening hours and visit prices must come from an official venue or Da Nang
  Tourism source and carry a source URL plus verification timestamp.
- Persist a completed scheduling request, its generated draft itinerary, and
  every itinerary item in one transaction. Use navigations for same-transaction
  inserts whose generated identity keys are not available before save.
- A committed infeasible request has status `Failed` and
  `failure_code = constraints_infeasible`; replaying its key returns the same
  422 result. Unexpected failures roll back the entire transaction and do not
  consume the idempotency key.
- Use a serializable transaction and SQL Server application lock derived from
  `(travelerUserId, idempotencyKey)` before looking up/replaying/creating the
  scheduling request. This prevents concurrent double submission.
- All `DateTimeOffset` properties mapped to SQL Server `DATETIME2` use
  `.AsUtcDateTime2()`.

## Mobile Experience

1. Traveler opens **Create Itinerary** from the Traveler area.
2. Mobile loads/searches active POIs for start, exploration centre, end, and
   optional mandatory selection.
3. The form defaults start from GPS when permission is granted, transport from
   profile when present, and otherwise provides explicit selections.
4. Generate validates locally, creates one UUID operation key per attempt,
   disables duplicate submission, and sends the API request.
5. The generating state explains that times and costs are estimates.
6. A success result shows ordered stops, estimated timings/costs, scheduled
   breaks, total summary, and a **Create another itinerary** action. A rest
   item says that food/drink spend is excluded unless explicitly priced. The
   persisted draft remains available for later product flows.
7. Infeasible and unexpected failure states preserve form values. Infeasible
   copy explains the actionable reason returned by the API without exposing
   implementation details.

## Acceptance Criteria

- A valid request creates exactly one completed scheduling request, one draft
  itinerary, and ordered itinerary items atomically.
- Every successful itinerary includes all mandatory POIs once, respects the
  requested duration/end point, and has no planned visit outside known opening
  hours.
- Budgeted results include only POIs with known costs and never exceed budget.
- Infeasible requests produce no itinerary and return 422 with an actionable
  constraint reason.
- A same-key retry returns the original completed itinerary; concurrent same-key
  submissions never create a second itinerary.
- A same key with different normalized input returns 409.
- Missing Traveler preferences do not block generation.
- Mobile supports a GPS-denied manual-start path, loading, validation,
  infeasible, system-error, and success states without raw exceptions.
- Every displayed duration and cost is labelled estimated; the form/result do
  not promise traffic, weather, or map-navigation accuracy not in MVP scope.
- A scheduled rest break does not replace a mandatory POI, violate the final
  end constraint, or silently add an unknown food/drink cost to the budget.

## Required Verification

- Domain tests for request, itinerary, itinerary-item, cost, and temporal
  invariants.
- Generator tests for mandatory sequencing, opening-hour exclusions, budget,
  end-route feasibility, deterministic ranking, and infeasible cases.
- SQL Server integration tests for atomic persistence, rollback, idempotency
  replay/mismatch, and concurrent same-key requests.
- Endpoint/OpenAPI tests for header, validation, 401/403, 422, 409, and 201.
- Mobile repository/Cubit/widget tests for input, GPS fallback, loading,
  infeasible retention, retry/idempotency, and result rendering.
- Required Backend and Mobile format/build/test quality gates on final heads.
