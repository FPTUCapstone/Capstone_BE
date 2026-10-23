# UC-59 View Active Trip Details Backend Implementation Plan

Status: **Approved 2026-09-30** (spec decisions D2–D5 and route error contract approved).
Tasks are atomic and sequenced; each ends green before the next starts (TDD: failing test first).

Branch: `feature/linhnv-view-active-trip-details` (baseline: latest `feature/linhnv-view-active-trips`
merge of `origin/develop`, `dotnet test` verified green on 2026-09-30).

Approved spec decisions carried into this plan: D1–D6 (`specs/UC-59-spec.md`), approved
2026-09-30 with the amended D3 wording and D5 metadata contract.

## Task 1 — Read-only domain mappings + ItineraryItem schema alignment

New `TripMate.Domain` entities + `TripMate.Infrastructure` `IEntityTypeConfiguration` mapping the
existing v7 tables (no `SaveChanges` from these mappings; the only write in this UC is the
BR-130 audit row added in Task 3):

- `TripStateHistory` → `trip.TripStateHistory` (`from_state`, `to_state`, `reason`,
  `triggered_by` check values as constants, `changed_at` `AsUtcDateTime2`).
- `TripLocationLog` → `trip.TripLocationLogs` (`latitude`, `longitude`, `recorded_at`,
  `synced_at`, `is_offline_captured`).
- `WeatherEvent` → `trip.WeatherEvents` (`region_name`, coordinates, `event_type`, `severity`
  check constants, `description`, `valid_from/to`, `source`, `ingested_at`).
- `ReroutingEvent` → `trip.ReroutingEvents` (`incident_id`, `session_id`,
  `proposed_itinerary_snapshot`, `status` check constants, `proposed_at`, `decided_at`).

**ItineraryItem schema alignment (required by UC-59 progress reporting).** The SQL table
(`tripmate_schema_v7.sql`, `planning.ItineraryItems`) allows NULL `planned_arrival`/
`planned_departure` and carries a `status` column, but the typed entity does not reflect this:

1. Add `Status` to `ItineraryItem` with string constants (`Planned`, `Visited`, `Skipped`)
   matching the column CHECK, defaulting to `Planned` for new items.
2. Map `status` in `ItineraryItemConfiguration` (VARCHAR(10), required).
3. Make `PlannedArrivalUtc`/`PlannedDepartureUtc` nullable (`DateTimeOffset?`) and remove the
   `.IsRequired()` on both timestamp mappings so the entity matches the nullable columns.
4. Keep the write-path factory behavior safe: `Create()` still requires both timestamps and
   validates `departure > arrival` for items it creates (existing scheduling write paths are
   unchanged); nullable values are therefore only observed on rows persisted by external
   flows. Any divergence between factory invariants and schema nullability must be called out
   in review rather than silently widened.
5. Persistence model tests cover the status mapping and nullable timestamps, including a
   SQL Server test (`Category=SqlServer`) with an itinerary item whose planned times are NULL
   and `status = 'Visited'`/'Skipped'.

Wire `DbSet<T>` into `IApplicationDbContext`, `ApplicationDbContext`, and `TestDbContext`.
**No migration** (database-first). Verify: new/updated persistence tests pass, full suite green
(including scheduling tests that exercise `ItineraryItem.Create`).

DoD: entities compile; `dotnet test` green; no SQL file touched.

## Task 2 — Query, validator, DTOs

`src/TripMate.Application/Features/Admin/ActiveTrips/GetDetails/`:

- `GetActiveTripDetailsQuery(long TripId)` with FluentValidation: positive integer only
  (otherwise 400 ProblemDetails with field error, same plumbing as UC-58 list).
- DTO records exactly matching the spec response shape: detail root, `currentLocation`,
  `itineraryProgress` items (with `status` and nullable planned timestamps from Task 1),
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

1. Authorization identical to UC-58 (role check → `Forbidden`).
2. Load session with change tracking disabled; missing **or** FSM state not in
   `Navigating/Exploring/Interrupted` → `Failure(ActiveTripNotFound)` → 404 (D2).
3. Assemble: overview reusing UC-58 calculations (extract shared helpers where the list handler
   logic is genuinely identical — group/traveler, destination, currentDay, members, openAlerts —
   without duplicating divergent rules); `currentLocation` + `lastSyncedAtUtc`; itinerary
   progress ordered by `sequence_no` with POI names and item status; state history ascending;
   incidents descending with nested weather events; rerouting events with
   `hasProposedItinerarySnapshot` and no snapshot content (D5); location trail selected as the
   latest 100 logs by `recorded_at` descending then returned ascending (D4); `groupPanel` built
   from `social.GroupMembers`/`social.TravelGroups` via the shared itinerary — active members
   only, ordered by `joined_at` then `user_id`, sharing-state flag without coordinates (D7);
   `null` when no group is linked.
4. Write the BR-130 access-audit row (D8): `AuditLog.CreateRecordedOutcome(actorUserId,
   AuditActionTypes.ActiveTripDetailsViewed, AuditEntityTypes.TripSession, tripId, nowUtc,
   AuditOutcome.Success)` — new constants on the existing pattern — then `SaveChangesAsync` for
   the audit row only. **Fail-closed (D8):** an audit persist failure must fail the request
   (`500`) rather than serve GPS-bearing detail without a trail. This is the single permitted
   write; trip data is never mutated.

Unit tests first: null coordinates → `currentLocation: null`; null weather link → `weatherEvent:
null`; null planned timestamps → nullable DTO values; empty sections render as empty arrays;
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

- HTTP: `401` (no token), `403` (non-admin), `404` (malformed non-integer segment such as `1abc`
  via route constraint, unknown id), `400` (segment matches `long` but ≤ 0), `404` (`Planning`
  session, `Completed` session), `200` happy path.
- SQL Server gated (`Category=SqlServer`): seed real-schema rows across
  `TripSessions`/`TripStateHistory`/`TripLocationLogs`/`WeatherEvents`/`Incidents`/
  `ReroutingEvents`/`ItineraryItems`/`GroupMembers`; assert joins, ordering, trail bound, and
  UTC serialization, plus an itinerary item with NULL planned times and a non-default status,
  `groupPanel` members with `LocationSharingEnabled` states, and the persisted `AuditLogs` row
  (`ViewActiveTripDetails`) written by a successful access.

## Task 6 — Verification and review

1. `dotnet format TripMate.slnx --no-restore --verify-no-changes`
2. `dotnet build TripMate.slnx -c Release` (0 warnings/errors)
3. `dotnet test TripMate.slnx -c Release` (record SQL-skipped vs SQL-run counts honestly)
4. Spec-compliance review, then code-quality review (severity-based; Critical blocks).

Non-goals guard: no schema change, no mutation endpoints, no snapshot serialization, no
notification fan-out.
