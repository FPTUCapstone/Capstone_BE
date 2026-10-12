# UC-59 View Active Trip Details Backend Specification

## Status

**Amended 2026-10-11** for Staff ownership and location disclosure consent.
Implementation is in progress on `feature/linhnv-view-active-trip-details`; verification is
tracked in `plans/UC-59-plan.md`.

## Sources and SRS analysis

- Current SRS `Capstone_Docs/Report3_Software-Requirement-Specification (1).docx`
  (SHA-256 `E078E898424056BAB3D7BFF992734D7B38781483C2FA3C455AC9DB01F17DFD7D`),
  Table 3 UC-59 and Table 4 Active Trip Details: the older detailed section says Administrator inspects detailed operational information for a
  **selected active trip**: current Traveler location, itinerary progress, current FSM state,
  detected incidents, weather-related disruptions, route deviations, rerouting events, and other
  synchronized trip-monitoring information. **Monitoring only** unless separate intervention
  functions are explicitly defined.
- **SRS_INTERNAL_CONFLICT:** the UC catalogue assigns UC-58/59 to Staff, while the older detailed sections name Administrator. This implementation follows the approved **Staff-only** decision; the SRS must be reconciled before release.
- Approved UC-58 decisions (committed `specs/UC-58-spec.md`): active session = FSM state
  `Navigating`/`Exploring`/`Interrupted`; trip code = `TRIP-{session_id}`; `BookedTour` → `Tour`,
  else `SelfPlanned`; open alert = incident with `resolved_at IS NULL`; `tripId` serialized as a
  JSON string (BIGINT safety); CR-07 `Asia/Ho_Chi_Minh` timezone handling; Administrator
  authorization; MSG126/MSG127/MSG128.
- Schema truth `database/tripmate_schema_v7.sql`: `trip.TripSessions`, `trip.TripStateHistory`,
  `trip.TripLocationLogs`, `trip.WeatherEvents`, `trip.Incidents`, `trip.ReroutingEvents`,
  `planning.ItineraryItems` (status `Planned`/`Visited`/`Skipped`).

### Facts vs decisions

| # | Item | Type | Resolution |
| --- | --- | --- | --- |
| D1 | Detail dataset | Fact-derived | Every SRS-listed information class maps to the v7 schema; the privacy amendment adds explicit ownership and Operations Staff consent before a precise coordinate is disclosed. |
| D2 | "Active trip" boundary | **Approved 2026-09-30** | Same definition as UC-58: `fsm_state IN ('Navigating','Exploring','Interrupted')` at read time. A trip outside the active monitoring states is treated as not found for UC-59: a missing **or non-active** session (`Planning`, `Completed`, and any other state) returns `404`. Rationale: the entry surface is the UC-58 active-trips list, so a trip that has since completed is no longer in monitoring scope. |
| D3 | Not-found message | **Approved 2026-09-30 (proposed MSG133)** | No locked §5.3 message covers a missing active trip; MSG129 is a success toast and MSG128 is a list/filter "no records" message, so neither is reused. Following the UC-69/MSG132 precedent: runtime code `admin.active_trip_not_found`, **proposed** MSG133 wording (not locked SRS content): `"Active trip not found or no longer available for monitoring."` — the phrasing covers a trip that still exists but has left the active monitoring scope. |
| D4 | Location trail size | **Approved 2026-09-30** | `TripLocationLogs` is unbounded; the backend selects the **latest 100** logs by `recorded_at` descending, then returns them ordered ascending for display. A map/timeline enhancement may add pagination or a time window in a separate approval. |
| D5 | `proposed_itinerary_snapshot` | **Approved 2026-09-30** | The `NVARCHAR(MAX)` snapshot content is **excluded** from the response. Rerouting events expose metadata only: `status`, `proposedAtUtc`, `decidedAtUtc`, and `hasProposedItinerarySnapshot` (boolean). Viewing snapshot content requires a separate endpoint/UC approval. |
| D6 | Refresh semantics | Out of scope | SRS defines no auto-refresh; the client fetches on demand. |
| D7 | Group panel with members | **Approved 2026-09-30 (UC-59 design decision)** | The response adds a `groupPanel` section: group name, group host, and the member list with joined timestamp and location-sharing state. Member **coordinates are not exposed** by the panel — only the sharing state flag; precise shared location presentation remains a Mobile capability. When no group is linked (Self-Planned), `groupPanel` is `null` and the overview's traveler identity covers the single participant. Legacy rows with a NULL/blank group name display `Group {groupId}`. |
| D8 | Access audit | **Approved 2026-10-12** | Every successful detail access writes one `AuditLogs` row: action type `ViewActiveTripDetails`, affected entity `TripSession`, actor = requesting Staff, outcome `Success`. Audit metadata records disclosure state/counts only, never coordinates. `Success` is persisted only after all data queries and response mapping complete, immediately before the response is returned; an audit persistence failure is fail-closed and returns no detail payload. |
| D9 | Location ownership | **Approved 2026-10-11** | Each `TripLocationLog` and session current coordinate must identify its traveler subject. Legacy/unknown-owner coordinates are redacted. |
| D10 | Consent scopes | **Approved 2026-10-11** | `GroupMember.LocationSharingEnabled` remains group-only. A separate active `OperationsStaff` consent is required to disclose a traveler's precise location to Staff. Missing, disabled, or revoked consent redacts the coordinate and historical trail. |

## Scope

One Staff-only query endpoint returning the full operational detail of a single active
trip session. Adds the minimum **read-only** Domain entities and EF mappings that UC-58 did not
require: `trip.TripStateHistory`, `trip.TripLocationLogs`, `trip.WeatherEvents`,
`trip.ReroutingEvents`, and the explicit location disclosure consent schema.

Trip data is never mutated. The single permitted write is the BR-130 access-audit row (D8),
which is required behavior, not an intervention.

## API contract

### Endpoint

`GET /api/v1/admin/trips/active/{tripId}`

- `tripId` is the decimal `session_id` bound as a signed 64-bit integer via the `{tripId:long}` route constraint. The route constraint and validator define the error contract together:
  - A **malformed non-integer** path segment (`1abc`, `1.5`, `1e3`) does not match the route; the framework responds `404` before the application sees the request. This matches the UC-69 controller precedent.
  - A segment that matches `long` but is **zero or negative** passes the route and returns `400` ProblemDetails with a field error from query validation.
- Missing/invalid authentication → `401`; valid non-Staff → `403` (MSG126 at the Web boundary); unknown **or non-active** session → `404` (D2/D3); unexpected failure → `500` without database details.

### Successful response (`200 OK`)

```json
{
  "tripId": "42",
  "tripCode": "TRIP-42",
  "tripType": "Tour",
  "currentState": "Interrupted",
  "groupOrTraveler": "Da Nang Weekend Group",
  "destination": "Da Nang",
  "startedAtUtc": "2026-09-26T01:30:00Z",
  "lastSyncedAtUtc": "2026-09-26T03:12:44Z",
  "currentDay": 1,
  "members": 4,
  "openAlerts": 1,
  "groupPanel": [
    { "groupId": "9", "groupName": "Da Nang Weekend Group", "hostName": "Nguyen Van A",
      "members": [
        { "userId": "101", "fullName": "Nguyen Van A", "joinedAtUtc": "2026-09-20T12:00:00Z",
          "isLocationSharingEnabled": true },
        { "userId": "102", "fullName": "Tran Thi B", "joinedAtUtc": "2026-09-21T08:30:00Z",
          "isLocationSharingEnabled": false }
      ] }
  ],
  "locationDisclosure": { "state": "Partial", "sharedMemberCount": 1, "hiddenMemberCount": 1 },
  "currentLocation": { "latitude": 16.0612, "longitude": 108.2277, "asOfUtc": "2026-09-26T03:12:44Z" },
  "itineraryProgress": [
    { "itemId": "901", "sequenceNo": 1, "poiId": "17", "poiName": "My Khe Beach",
      "itemKind": "Visit", "status": "Visited",
      "plannedArrivalUtc": "2026-09-26T02:00:00Z", "plannedDepartureUtc": "2026-09-26T04:00:00Z",
      "stayDurationMinutes": 120 }
  ],
  "stateHistory": [
    { "fromState": null, "toState": "Navigating", "reason": "Trip started",
      "triggeredBy": "Traveler", "changedAtUtc": "2026-09-26T01:30:00Z" }
  ],
  "incidents": [
    { "incidentId": "77", "incidentType": "SevereWeather", "description": "Heavy rain warning",
      "detectedAtUtc": "2026-09-26T02:55:10Z", "resolvedAtUtc": null,
      "weatherEvent": { "eventType": "HeavyRain", "severity": "Severe",
        "regionName": "Da Nang", "validFromUtc": "2026-09-26T02:00:00Z", "validToUtc": null } }
  ],
  "reroutingEvents": [
    { "reroutingId": "31", "incidentId": "77", "status": "Proposed",
      "proposedAtUtc": "2026-09-26T02:56:00Z", "decidedAtUtc": null,
      "hasProposedItinerarySnapshot": true }
  ],
  "locationTrail": [
    { "latitude": 16.0601, "longitude": 108.2266, "recordedAtUtc": "2026-09-26T03:10:00Z",
      "isOfflineCaptured": false }
  ]
}
```

### Data mapping

- `locationDisclosure`: `Full`, `Partial`, or `Redacted` plus shared/hidden member counts. It contains no coordinates.
- `currentLocation`: returned only when both coordinates exist, `current_location_subject_user_id` identifies the traveler, and that traveler has effective `OperationsStaff` consent; otherwise `null`.
- `groupOrTraveler`, `destination`, `tripType`, `currentDay`, `members`, `openAlerts`: identical calculations to UC-58 (same helper logic, no duplication of divergent rules).
- `groupPanel` (D7): groups linked through the shared `itinerary_id`. `groupPanel` is `null` when no group is linked (Self-Planned); otherwise it is an **array** with one entry per linked group, ordered by group ID, each exposing `groupId`, `groupName`, `hostName` (`TravelGroups.HostUser.FullName`), and its **active** members (`GroupMemberStatus.Active`) ordered by `joined_at` then `user_id`, each with `userId`, `fullName`, `joinedAtUtc`, and `isLocationSharingEnabled` (BR-50/BR-51 state flag only — no member coordinates).
- `itineraryProgress`: `planning.ItineraryItems` of the session itinerary ordered by `sequence_no`; `poiName` from `catalog.POIs.name` when linked; timestamps UTC (`AsUtcDateTime2`).
- `stateHistory`: `trip.TripStateHistory` ordered by `changed_at` ascending, then `history_id`; `triggeredBy` may be `System`/`Traveler`/`Administrator` or null.
- `incidents`: all incidents of the session ordered by `detected_at` descending; each embeds its linked `trip.WeatherEvents` row (`weatherEvent: null` when `weather_event_id` is null). Route deviations are incidents with type `RouteDeviation`; no separate section is invented.
- `reroutingEvents`: `trip.ReroutingEvents` ordered by `proposed_at` descending; snapshot content excluded (D5) — each event exposes only `status`, `proposedAtUtc`, `decidedAtUtc`, and `hasProposedItinerarySnapshot`.
- `locationTrail`: the latest 100 attributable `trip.TripLocationLogs` rows whose subject has effective `OperationsStaff` consent, selected descending then returned ascending by `recorded_at`/`log_id`; `isOfflineCaptured` is surfaced. Legacy logs or revoked/missing-consent subjects are omitted.

## Acceptance criteria

1. Only Staff can retrieve the endpoint; `401`/`403` follow the project ProblemDetails contract.
2. Non-integer, non-positive, missing, **and non-active** session IDs return `404`/`400` exactly as specified.
3. The response contains every section above with schema-faithful field names, UTC timestamps, and nullable behavior per the mapping section, including the `groupPanel` member list (D7) with joined timestamps and location-sharing flags but **no member coordinates**.
4. Every successful `200` response has exactly one persisted `AuditLogs` row per D8 (`ViewActiveTripDetails`, entity `TripSession`, actor = requesting Staff); a failed audit persist yields `500` and no detail payload (fail-closed).
5. Trip data is never mutated; the only write is the BR-130 audit row. Unrelated data is not exposed.
6. Unit tests cover handler assembly, boundaries (null coordinates, null weather link, null planned timestamps, empty trail/history, >100 logs, group panel null/multi-group/inactive-member exclusion, audit row construction), and validation; API integration tests cover `401`, `403`, `400`, `404`, `200`, inactive-session `404`, and audit-row persistence; SQL Server integration tests verify real-schema joins (state history, incidents→weather, rerouting, trail bound), plus enabled, missing, revoked, and mixed-member Staff location-consent redaction, and the persisted audit row.
7. OpenAPI documents the path parameter, response DTO, and error responses.

## Non-goals and dependencies

- No intervention: incident resolution, rerouting accept/reject, itinerary/session mutation, notifications — all excluded (`BLOCKED` until a separate UC is approved).
- No live map/geocoding rendering on the Web (coordinates only, consistent with UC-52).
- No real-time push/streaming; refresh is client-initiated (D6).
- Runtime trip creation/FSM execution remains unimplemented; tests seed records directly.
- Staff login/session correctness is an external dependency (administration cookie contract).
