# UC-59 View Active Trip Details Backend Implementation Plan

Status: **Amended 2026-10-11** for Staff-only monitoring and consent-based location disclosure.
Tasks are atomic and sequenced; each ends green before the next starts (TDD: failing test first).

Branch: `feature/linhnv-staff-active-trip-access` (rebased on `origin/develop`, including the
merged UC-13 navigation execution model).

Approved implementation decisions carried into this plan: Staff-only actor, explicit
`OperationsStaff` consent per traveler, server-side redaction for legacy/unknown-owner coordinates,
and fail-closed access audit after successful response assembly and before the payload is returned.
The SRS actor conflict remains recorded as `SRS_INTERNAL_CONFLICT` and is resolved by the approved
Staff-only Web baseline.

## Task 1 — Read-only domain mappings + UC-13 session-progress integration

New `TripMate.Domain` entities + `TripMate.Infrastructure` `IEntityTypeConfiguration` mapping the
existing v7 tables and a database-first privacy migration:

- `TripStateHistory` → `trip.TripStateHistory` (`from_state`, `to_state`, `reason`,
  `triggered_by` check values as constants, `changed_at` `AsUtcDateTime2`).
- `TripLocationLog` → `trip.TripLocationLogs` (`latitude`, `longitude`, `recorded_at`,
  `synced_at`, `is_offline_captured`, nullable legacy-safe `subject_user_id`).
- `TripLocationDisclosureConsent` → `trip.TripLocationDisclosureConsents`; only an unrevoked
  `OperationsStaff` consent authorizes precise disclosure to Staff. `GroupMember.LocationSharingEnabled`
  remains a group-only state and cannot authorize Staff access.
- `WeatherEvent` → `trip.WeatherEvents` (`region_name`, coordinates, `event_type`, `severity`
  check constants, `description`, `valid_from/to`, `source`, `ingested_at`).
- `ReroutingEvent` → `trip.ReroutingEvents` (`incident_id`, `session_id`,
  `proposed_itinerary_snapshot`, `status` check constants, `proposed_at`, `decided_at`).

**Privacy migration.** Add `subject_user_id` to `TripLocationLogs`,
`current_location_subject_user_id` to `TripSessions`, and the consent table/indexes. Legacy rows are
not backfilled and therefore remain redacted. The migration is idempotent.

**UC-13 session-progress integration.** Use the merged `trip.TripSessionItems` mapping exposed by
`IApplicationDbContext`. UC-59 reads this session-bound snapshot by `session_id`; it must not use
or mutate `planning.ItineraryItems.status`, because a shared itinerary cannot represent one
traveler's navigation progress. The snapshot supplies POI name, planned timestamps, sequence, and
the `Pending`/`Reached`/`Skipped` state derived from `reached_at` and `skipped_at`.

No UC-59 migration or change to the itinerary planning write path is required. Verify with unit and
SQL Server integration tests that session progress can change while the shared itinerary status stays
unchanged.

DoD: entities compile; `dotnet test` green; no SQL file touched.

## Task 2 — Query, validator, DTOs

`src/TripMate.Application/Features/Admin/ActiveTrips/GetDetails/`:

- `GetActiveTripDetailsQuery(long TripId)` with FluentValidation: positive integer only
  (otherwise 400 ProblemDetails with field error, same plumbing as UC-58 list).
- DTO records exactly matching the spec response shape: detail root, `currentLocation`,
  `itineraryProgress` items sourced from the UC-13 session snapshot,
  `stateHistory` items, `incident` + nested `weatherEvent`,
  `reroutingEvent` (status, proposed/decided timestamps, `hasProposedItinerarySnapshot`),
  `locationTrail` point, and `groupPanel` entries (`groupId`, `groupName`, `hostName`,
  `members[]` with `userId`, `fullName`, `joinedAtUtc`, `isLocationSharingEnabled`).
  IDs serialized as strings per UC-58 convention.
- Reuse UC-58 `ActiveTripErrorCodes`-style codes: add `admin.active_trip_not_found` (proposed
  MSG133 wording: "Active trip not found or no longer available for monitoring.") alongside
  existing `Forbidden` code.

Verify: validator unit tests (0, negative, valid) green.

## Task 3 — Handler assembly (TDD)

`GetActiveTripDetailsQueryHandler`:

1. Authorization identical to UC-58: Staff-only role check → `Forbidden`; Administrator has no implicit access under the approved implementation decision.
2. Load session with change tracking disabled; missing **or** FSM state not in
   `Navigating/Exploring/Interrupted` → `Failure(ActiveTripNotFound)` → 404 (D2).
3. Assemble: overview reusing UC-58 calculations (extract shared helpers where the list handler
   logic is genuinely identical — group/traveler, destination, currentDay, members, openAlerts —
   without duplicating divergent rules); calculate the active `OperationsStaff` consent set before
   any GPS-bearing query, return `currentLocation` only when its explicit subject owner is in that
   set, and return only location-trail points owned by consenting subjects; legacy/unknown owners
   and missing or revoked consent are redacted; include non-coordinate `locationDisclosure`
   metadata; itinerary
   progress ordered by `sequence_no` with session-snapshot POI names and item status; state history ascending;
   incidents descending with nested weather events; rerouting events with
   `hasProposedItinerarySnapshot` and no snapshot content (D5); location trail selected as the
   latest 100 logs by `recorded_at` descending then returned ascending (D4); `groupPanel` built
   from `social.GroupMembers`/`social.TravelGroups` via the shared itinerary — active members
   only, ordered by `joined_at` then `user_id`, sharing-state flag without coordinates (D7);
   `null` when no group is linked.
4. After every detail query and DTO mapping succeeds, write the BR-130 access-audit row (D8): `AuditLog.CreateRecordedOutcome(actorUserId,
   AuditActionTypes.ActiveTripDetailsViewed, AuditEntityTypes.TripSession, tripId, nowUtc,
   AuditOutcome.Success)` — new constants on the existing pattern — then `SaveChangesAsync` for
   the audit row only. **Fail-closed (D8):** an audit persist failure must fail the request
   (`500`) rather than serve GPS-bearing detail without a trail. This is the single permitted
   write; trip data is never mutated.

Unit tests first: null coordinates → `currentLocation: null`; null weather link → `weatherEvent:
null`; empty sections render as empty arrays;
exactly 101 logs → 100 returned; inactive session (`Completed`, `Planning`) → not-found;
`groupPanel` boundaries (no group → `null`; inactive/removed members excluded; host name
resolved; sharing flag surfaced verbatim); audit row constructed with the correct
actor/action/entity/ID and outcome (D8); member/currentDay boundaries mirror UC-58 expectations.
Handler tests also assert the audit write occurs exactly once per successful access and that an
audit persistence failure surfaces as a failure result (fail-closed).

## Task 4 — Controller endpoint + OpenAPI

`AdminActiveTripsController`: `[HttpGet("{tripId:long}")]` returning
`Ok(result.Value)` / `HandleFailure(result)`; route `api/v1/admin/trips/active/{tripId}` maps the
`404` code to ProblemDetails. `ProducesResponseType` annotations for `200/400/401/403/404/500`.
Error contract per the approved spec: malformed non-integer segments never match the `:long`
constraint → framework `404`; segments matching `long` but ≤ 0 → `400` from the validator;
unknown or non-active session → application `404`. Update/extend the OpenAPI integration test
from UC-58 to assert the new path and responses.

## Task 5 — Integration tests

Extend `tests/TripMate.Api.IntegrationTests/ActiveTrips/`:

- HTTP: `401` (no token), `403` (non-Staff), `404` (malformed non-integer segment such as `1abc`
  via route constraint, unknown id), `400` (segment matches `long` but ≤ 0), `404` (`Planning`
  session, `Completed` session), `200` happy path.
- SQL Server gated (`Category=SqlServer`): seed real-schema rows across
  `TripSessions`/`TripStateHistory`/`TripLocationLogs`/`WeatherEvents`/`Incidents`/
  `ReroutingEvents`/`TripSessionItems`/`GroupMembers`; assert joins, ordering, trail bound, and
  UTC serialization, plus reached/skipped session-item progress without mutation of the shared itinerary,
  `groupPanel` members with `LocationSharingEnabled` states, effective/missing/revoked/mixed
  `OperationsStaff` location consent redaction, and the persisted `AuditLogs` row
  (`ViewActiveTripDetails`) written by a successful access.

## Task 6 — Verification and review

1. `dotnet format TripMate.slnx --no-restore --verify-no-changes`
2. `dotnet build TripMate.slnx -c Release` (0 warnings/errors)
3. `dotnet test TripMate.slnx -c Release` (record SQL-skipped vs SQL-run counts honestly)
4. Spec-compliance review, then code-quality review (severity-based; Critical blocks).

Non-goals guard: no schema change, no mutation endpoints, no snapshot serialization, no
notification fan-out.
