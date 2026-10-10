# UC-13 Navigate Route — Implementation Plan (Revision 2)

## Status and scope

Approved by the developer on 2026-10-09 together with
[`specs/UC-13-navigate-route-spec.md`](../specs/UC-13-navigate-route-spec.md) revision 2.

Repository: `Capstone_BE` — branch `feature/phuctv-uc13-navigation-backend` (uncommitted
revision-1 work already on the branch is reworked in place; nothing has been merged or deployed).

Revision 2 replaces strict sequential progress with flexible order, skip/revisit, the `Exploring`
phase, the trip time window, lazy expiry, and device event times. Mobile work stays separate.

Do not commit, push, or open a PR without an explicit developer request.

## Baseline (2026-10-09)

- `dotnet build TripMate.slnx -c Release` — 0 warnings, 0 errors.
- `dotnet test` — all green; with `TRIPMATE_SQLSERVER_TEST_CONNECTION` pointing at the local
  `tripmate-sqlserver` container, all 20 Navigation/ActiveTrips tests pass with no skips.
- The local `TripMateDb` already contains the revision-1 `trip.TripSessionItems` table, so the
  migration must upgrade that shape.

## Architectural shape

- `TripSession` stays the deep module: `Start`, `ReachItem`, `SkipItem`, `Depart`, `Finish`,
  `ExpireIfDue`, `ResolveEventTime`. All state, history, and item bookkeeping stays inside it.
- `TripSessionItem` owns derived status and its own allowed transitions.
- One Application helper, `NavigationSessionMutator`, owns the shared mutation pipeline
  (ownership, access, transaction, lazy expiry, concurrency fence, retry). Reach, skip, depart, and
  finish handlers only describe their domain call.
- `NavigationSessionOptions` follows the existing options pattern (Application class with
  defaults, Infrastructure binding + `IValidateOptions`, `appsettings.json` section).
- Controllers stay thin; a single optional request body type carries `occurredAtUtc`.

## Verification commands

Focused (per task):

```powershell
dotnet test tests/TripMate.Application.UnitTests -c Release --filter "FullyQualifiedName~Navigation|FullyQualifiedName~TripSession"
dotnet test tests/TripMate.Api.IntegrationTests -c Release --filter "FullyQualifiedName~Navigation|FullyQualifiedName~ActiveTrips|FullyQualifiedName~Itinerary"
```

SQL Server suites need `TRIPMATE_SQLSERVER_TEST_CONNECTION` (local container on port 14330, built
from `SA_PASSWORD` in the untracked `.env`; never committed).

Final:

```powershell
dotnet format TripMate.slnx --verify-no-changes --no-restore
dotnet build TripMate.slnx -c Release --no-restore --nologo
dotnet test TripMate.slnx -c Release --no-build --nologo
git diff --check
```

## Tasks

Each task is Red → Green → Refactor, followed by a spec-compliance and code-quality review.
Critical review findings block the next task.

### T1 — Domain: snapshot item status and transitions

Files: `src/TripMate.Domain/Entities/TripSessionItem.cs`,
`tests/TripMate.Application.UnitTests/Domain/TripSessionItemTests.cs` (new).

Tests first:

- `Snapshot` stores planned arrival/departure (UTC, arrival ≤ departure) and the mandatory flag;
  rejects non-UTC or inverted times.
- Derived status: Pending → Reached, Pending → Skipped, Skipped → Reached (keeps `SkippedAtUtc`).
- Status constants `Pending`, `Reached`, `Skipped`.

DoD: item tests green; revision-1 domain tests updated to the new `Snapshot` signature.

### T2 — Domain: session aggregate

Files: `TripSession.cs`, `TripStateHistory.cs`, `TripSessionTests.cs`.

Tests first:

- `Start` requires `expiresAtUtc > startedAtUtc`, stores it, enters `Navigating`, one history row.
- `ReachItem` in any order; `Navigating → Exploring` with `ItemReached` history; reaching another
  item while exploring changes `ExploringItemId` without history; Skipped → Reached allowed;
  already-Reached returns `Replayed` even when Completed; Completed rejects new reaches; unknown
  item returns `NotInSnapshot`.
- `SkipItem`: Pending → Skipped; replay; Reached → `AlreadyReached`; Completed rejects; no state
  change.
- `Depart`: `Exploring → Navigating` with `TravelerDeparted`; no-op when Navigating; Completed
  rejects.
- `Finish`: `RouteFinished` without Pending items, else `TravelerStopped`; replay returns false and
  keeps the reason; clears `ExploringItemId`.
- `ExpireIfDue`: no-op before expiry or for legacy null expiry; `Expired` vs `RouteFinished`;
  `EndedAtUtc == ExpiresAtUtc`; `System` trigger; idempotent.
- `ResolveEventTime`: absent, before start, beyond skew, and in-range cases.
- All values come from Domain constants; outcome enum replaces `OutOfOrder` with the new cases.

DoD: domain suite green.

### T3 — Migration and persistence mapping

Files: `database/migrations/20261008_add_uc13_navigation_execution.sql`,
`TripSessionConfiguration.cs`, `TripSessionItemConfiguration.cs`,
`ActiveTripPersistenceModelTests.cs`, `NavigationExecutionMigrationSqlServerTests.cs`.

Tests first:

- Model test: new columns (`expires_at`, `exploring_item_id`, `planned_arrival`,
  `planned_departure`, `is_mandatory`, `skipped_at`) with UTC converters; `Status` unmapped.
- SQL Server migration tests:
  - from schema v7 with legacy rows, run twice under hostile SET options (existing test, extended
    to the new columns and constraints);
  - from the revision-1 shape with an existing session and item: columns added and backfilled from
    `planning.ItineraryItems`, completion-reason CHECK replaced, rerun safe;
  - revision-1 upgrade fails loudly when a row holds `AllItemsReached`.

Migration work as specified in the Database contract section.

DoD: model + migration tests green against SQL Server.

### T4 — Options and response contract

Files: `Navigation/Common/NavigationSessionOptions.cs` (+ validator),
`NavigationSessionResponse.cs`, `NavigationErrorCodes.cs`, `NavigationSessionMetrics.cs`,
`Infrastructure/DependencyInjection.cs`, `appsettings.json`, options/metrics unit tests.

Tests first: options validator rejects non-positive windows and negative skew; response maps
status, planned times, mandatory flag, `exploringItemId`, `expiresAtUtc`, and the suggested next
item; metrics expose bounded counters for skips, departures, and expirations.

### T5 — Start handler

Tests first (Application unit + endpoint):

- success inside the window; too early and after expiry return `outside_trip_window` with no rows;
- expired open session is closed (`Expired` history) and the new session is created;
- unexpired open session returns `active_session_exists` metadata;
- existing replay, mismatch, inactive, no-waypoint, access cases stay green.

### T6 — Shared mutation pipeline + reach/skip/depart/finish handlers

Files: `Navigation/Common/NavigationSessionMutator.cs`, `Reach/*`, `Skip/*` (new), `Depart/*`
(new), `Complete/*`.

Tests first (Application unit with the in-memory test context, plus SQL Server for concurrency):

- each handler: 404, foreign 403, revoked access 403 (finish still allowed), not-in-snapshot 422,
  completed 409, replay 200, device time accepted/ignored, lazy expiry persisted before the
  action;
- SQL Server: stop-wins vs reach returns `session_completed` with one terminal history row;
  concurrent duplicate reach keeps the first timestamp; concurrent skip vs reach of different
  items both succeed after retry; start rollback unchanged.

### T7 — Read handlers and Admin Active Trips

- Session GET and `?state=Open` list apply in-memory expiry without writing; the list excludes
  expired sessions; `Navigating` and `Exploring` are both open; unsupported filter `400`.
- `GetActiveTripsQueryHandler` excludes sessions whose `ExpiresAtUtc` is past
  (SQL Server test seeds one expired and one live session).

### T8 — API surface and OpenAPI

- `NavigationSessionsController`: skip, state, optional bodies via
  `NavigationProgressRequest` / `DepartNavigationSessionRequest`; error mapping for new codes.
- Endpoint tests: full lifecycle (start → reach 2 before 1 → depart → skip → revisit skipped →
  finish), empty-body PUTs, invalid depart state `400`, missing `Idempotency-Key` `400`, list
  filter.
- OpenAPI tests: new routes, request bodies, item status schema, nullable fields.

### T9 — Exact-head verification and documentation

- Full verification commands with SQL Server enabled and no skips in UC-13 suites.
- Spec status updated with evidence; record the local `TripMateDb` upgrade note.

## Required review focus

1. Flexible order and skip/revisit rules match the item transition table exactly.
2. Exploring transitions and history rows are exact; replays write nothing.
3. Lazy expiry is deterministic and identical between reads and writes.
4. Retry-on-concurrency never duplicates history or overwrites first timestamps.
5. Migration converges from v7, revision 1, and itself.
6. No GPS, no Mapbox, no itinerary mutation.

## Definition of done

- Every acceptance criterion in the spec has automated evidence.
- Full solution build, format, and tests pass, including SQL Server suites without skips.
- No commit, push, PR, or merge without explicit developer instruction.
