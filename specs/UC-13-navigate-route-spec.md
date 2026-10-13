# UC-13 Navigate Route — Complete Backend Specification

## Status

Revision 3 (2026-10-12) remediates the review of `078935ddc37514e53f8df23a077e9c3f306a9322`. It
keeps revision 2's progress rules (flexible order, skip and revisit, `Exploring`), which deviate
from Report 3 V2 §3.3.4. That deviation is recorded in
[`docs/analysis/uc13-navigation-progress-rules-decision.md`](../docs/analysis/uc13-navigation-progress-rules-decision.md),
**whose approval is pending**. Revision 3 changes:

- completion follows PC-03: the reach that leaves every item `Reached` closes the session as
  `RouteFinished`; an explicit completion is always `TravelerStopped`, and expiry always `Expired`;
- `nextItemId` is defined for sessions where only skipped items remain, and for completed sessions;
- device event times are bounded by a complete causal watermark;
- start, reach, skip, and depart check access inside the mutation transaction with locks held to
  the commit, and re-check on every retry;
- UC-59 Active Trip details reads the session's own progress and shares UC-58's expiry rule.

Revision 2 (2026-10-09) superseded revision 1 (2026-10-08), which enforced strictly sequential
progress, terminal skips, and immediate auto-completion.

Implemented locally (uncommitted) and verified on 2026-10-12 with SQL Server enabled
(`TRIPMATE_SQLSERVER_TEST_CONNECTION` → local `tripmate-sqlserver` container); see the plan for
the test results.

Pending: approval of the decision record, commit/PR (developer decision), dev/staging
duplicate-open-session preflight before deployment, and the Mobile changes listed at the end.

Implementation branch: `feature/phuctv-uc13-navigation-backend`

Baseline inspected: `develop` at `0075fcb`

Companion client: Flutter mobile application only

Map and directions provider: Mapbox

## Sources and source priority

1. Report 3 V2 section 3.3.4 for purpose, FSM vocabulary, and post-conditions (PC-03 completion).
2. The UC-13 progress-rules decision record (flexible order, skip and revisit, `Exploring` phase,
   trip time window, session expiry, device-reported event time); approval pending.
3. Current Backend and Mobile code for existing technical contracts.

Report 3 V2 assigns BR-28, BR-31, BR-35, and BR-40 different meanings in different sections and
references unrelated messages (MSG32, MSG35, MSG36, MSG37, MSG47, MSG48) in UC-13. This
specification states behavior directly; the Report 3 text must be corrected separately.

## Purpose

UC-13 lets an authenticated Traveler follow an accessible Active itinerary on Mobile. Backend
validates the start, freezes the navigable items, and records the Traveler's progress: which
items were reached or skipped, whether the Traveler is moving or visiting, and how the trip ended.
Mobile owns GPS, arrival detection, deviation detection, and Mapbox guidance.

## Resolved decisions

| Decision                     | Resolution                                                                                         |
| ---------------------------- | -------------------------------------------------------------------------------------------------- |
| Directions                   | Mobile calls Mapbox directly. Backend never proxies Mapbox.                                       |
| Session ownership            | One open session per Traveler. Group members each own their own session.                          |
| Progress storage             | Per-session snapshot rows. `planning.ItineraryItems.status` is not written, because a shared group itinerary cannot hold per-Traveler progress. |
| Visit order                  | Flexible (deviation, see the decision record). The suggested next item is advisory; any Pending or Skipped item may be reached. |
| Skip                         | Any Pending item, mandatory or not. Mobile asks for confirmation. A Skipped item may be reached later. |
| Visiting phase               | Reaching an item moves the session to `Exploring`; the Traveler departs back to `Navigating`.    |
| Completion                   | Automatic (`RouteFinished`) on the reach that leaves every item Reached (PC-03). The Traveler may stop earlier (`TravelerStopped`); otherwise the session expires (`Expired`). |
| Trip time window             | A session may start from `firstPlannedArrival − EarlyStartWindow` until the expiry time.          |
| Expiry                       | `lastPlannedDeparture + ExpiryGracePeriod`, evaluated lazily; no background job.                 |
| Event time                   | Mobile may send the observed time; Backend accepts it only within the session's valid range.      |
| Itinerary status on finish   | Unchanged. Navigation never modifies the itinerary, its items, or its version.                   |
| Rerouting                    | UC-15. Rebinding a session to a new version is UC-15 scope; UC-13 keeps `poiId` to enable it.    |
| Offline sync                 | UC-16/UC-72. UC-13 only accepts device event times so queued actions keep their real time.       |

## Configuration

Bound from section `NavigationSession` into `NavigationSessionOptions`, validated on start.

| Option                     | Default    | Rule              |
| -------------------------- | ---------- | ----------------- |
| `EarlyStartWindow`         | `03:00:00` | greater than zero |
| `ExpiryGracePeriod`        | `06:00:00` | greater than zero |
| `ClientClockSkewTolerance` | `00:02:00` | zero or greater   |

Arrival radius, dwell time, sampling interval, and deviation threshold are Mobile settings and are
not Backend configuration.

## Domain model and invariants

### Session states

`TripSession` is the aggregate root. UC-13 uses three FSM states:

```text
            start                 reach item
  (none) ───────► Navigating ─────────────────► Exploring
                     ▲   │                         │  │
                     │   │        depart           │  │ reach another item
                     │   └◄────────────────────────┘  │ (stays Exploring)
                     │                                 │
                     └── last reach / stop / expire ─► Completed ◄── last reach / stop / expire
```

- A session starts directly in `Navigating`.
- Reaching an item while `Navigating` moves the session to `Exploring` and records the item as the
  exploring item.
- Reaching another item while `Exploring` keeps the state and replaces the exploring item; it is
  not a state transition and writes no history row.
- The reach that leaves every snapshot item `Reached` moves the session from `Navigating` or
  `Exploring` straight to `Completed` with `RouteFinished` (one history row, no `Exploring` row).
- Departing from `Exploring` returns to `Navigating` and clears the exploring item. Departing while
  already `Navigating` is an idempotent no-op.
- Stopping or expiring moves `Navigating` or `Exploring` to `Completed` and clears the exploring
  item. A `Completed` session is immutable.
- `Planning` and `Interrupted` remain reserved for other flows; UC-13 never writes them. GPS loss
  and battery suspension are Mobile-local states.

### Session data

- Traveler, resolved itinerary ID/version, requested itinerary ID, start idempotency key.
- `StartedAtUtc`, `ExpiresAtUtc`, `EndedAtUtc` (UTC).
- `FsmState`, `CompletionReason`, `ExploringItemId`.
- `ExpiresAtUtc = max(snapshot PlannedDepartureUtc) + ExpiryGracePeriod`, fixed at start. It is
  null only for legacy rows created before this migration; such rows never expire.

### Navigable item snapshot

At start, an itinerary item is navigable when it belongs to the resolved itinerary version and has
a loaded POI (`Visit` items; POI coordinates are non-nullable). The Backend snapshots, ordered by
`sequenceNo`: item ID, sequence, POI ID, POI name, latitude, longitude, planned arrival, planned
departure, and the mandatory flag.

- The snapshot is immutable. Later itinerary status/version changes or POI edits never alter it.
- A session cannot start without at least one navigable item.
- Each snapshot item has a derived status:

| `ReachedAtUtc` | `SkippedAtUtc` | Status    |
| -------------- | -------------- | --------- |
| null           | null           | `Pending` |
| null           | set            | `Skipped` |
| set            | any            | `Reached` |

- Allowed item transitions: `Pending → Reached`, `Pending → Skipped`, `Skipped → Reached`.
  `Reached` is final. `SkippedAtUtc` is kept after a later reach as history.
- The suggested next item (`nextItemId`) is the `Pending` item with the lowest `sequenceNo` of an
  open session. It is null when the session is `Completed`, and also when only `Skipped` items
  remain: skipped items stay reachable on request but are never suggested again. Such a session is
  still open; it ends when the skipped items are reached (`RouteFinished`), when the Traveler stops,
  or at expiry.

### Event time resolution

Reach, skip, and depart accept an optional device-observed time `occurredAtUtc`:

- absent → Backend clock;
- earlier than the session's causal watermark, or later than `now + ClientClockSkewTolerance` →
  Backend clock;
- otherwise → `min(occurredAtUtc, now)`.

The causal watermark is the latest recorded event of the session: `StartedAtUtc`, every state
history time, and every item `ReachedAtUtc` and `SkippedAtUtc`. Item times count separately because
a reach while `Exploring` and every skip change no state and write no history row. A late offline
event can therefore never be stored before an event the Backend already recorded (for example a
revisit with `ReachedAtUtc` earlier than the item's `SkippedAtUtc`).

Invalid device times never reject the request, so queued offline actions cannot get stuck. Reach
and skip are idempotent per item, and depart is idempotent per state, so a retried request is safe.
Depart carries no item reference, so Mobile must replay its offline queue in FIFO order. Start,
finish, and expiry always use Backend time (expiry uses `ExpiresAtUtc` itself).

### Completion reasons

| Trigger                                              | Completion reason |
| ---------------------------------------------------- | ----------------- |
| A reach leaves every snapshot item `Reached`         | `RouteFinished`   |
| The Traveler completes (stops) an open session       | `TravelerStopped` |
| An open session passes its expiry                    | `Expired`         |

`RouteFinished` therefore always means every scheduled item was reached. A session with an unreached
skipped item, or with no reached item, ends as `TravelerStopped` or `Expired`. Expiry sets
`EndedAtUtc = ExpiresAtUtc`, so the outcome is deterministic regardless of when it is first
observed.

### Lazy expiry

There is no background job. A non-completed session whose `ExpiresAtUtc <= now` is expired:

- **Mutations** (start, reach, skip, depart, finish) persist the expiry inside their transaction
  before applying the requested action.
- **Reads** (session GET, open-session list) apply the same expiry to the in-memory, untracked
  entity and return the effective representation without writing. A later mutation persists
  exactly the same values.
- The open-session list excludes sessions that are effectively expired.
- Admin Active Trips, both the UC-58 list and the UC-59 detail, use one shared predicate
  (`ActiveTripSessions.WhereEffectivelyActive`): an open FSM state and `ExpiresAtUtc` null or later
  than now. An effectively expired session is absent from the list and its detail returns `404`.

### Admin Active Trips progress (UC-59)

The detail's `itineraryProgress` is the session's own snapshot (`trip.TripSessionItems`), ordered
by sequence, with the derived item status (`Pending`, `Reached`, `Skipped`). It never reads
`planning.ItineraryItems.status`, which a shared group itinerary cannot hold per Traveler.
Non-navigable items (for example `Rest`) are not part of a session's progress.

### Transition audit

Every real state change writes exactly one `TripStateHistory` row in the same transaction:

| Transition                    | Triggered by | Reason              |
| ----------------------------- | ------------ | ------------------- |
| `null → Navigating`           | `Traveler`   | `NavigationStarted` |
| `Navigating → Exploring`      | `Traveler`   | `ItemReached`       |
| `Exploring → Navigating`      | `Traveler`   | `TravelerDeparted`  |
| `* → Completed` (last reach)  | `Traveler`   | `RouteFinished`     |
| `* → Completed` (stop)        | `Traveler`   | `TravelerStopped`   |
| `* → Completed` (expiry)      | `System`     | `Expired`           |

Replays and same-state operations write no history. State names, reasons, item statuses, and
triggers are Domain constants.

## Database contract

Do not modify `database/tripmate_schema_v7.sql`. The single rerunnable migration
`database/migrations/20261008_add_uc13_navigation_execution.sql` must converge to the contract
below from three starting points: schema v7 without UC-13, the revision-1 UC-13 shape (already
applied to local development databases), and the current contract (rerun).

`trip.TripSessions` additions:

| Column                   | Type             | Rule                                                              |
| ------------------------ | ---------------- | ----------------------------------------------------------------- |
| `requested_itinerary_id` | `BIGINT`         | NOT NULL, FK to `planning.Itineraries`, legacy backfill `itinerary_id` |
| `start_idempotency_key`  | `UNIQUEIDENTIFIER` | NOT NULL, no default, legacy backfill `NEWID()` per row          |
| `completion_reason`      | `VARCHAR(24)`    | NULL or `RouteFinished`, `TravelerStopped`, `Expired`             |
| `expires_at`             | `DATETIME2`      | NULL (legacy only)                                                |
| `exploring_item_id`      | `BIGINT`         | NULL; CHECK `exploring_item_id IS NULL OR fsm_state = 'Exploring'` |
| `row_version`            | `ROWVERSION`     | EF concurrency token                                              |

Indexes: unique `(traveler_user_id, start_idempotency_key)`; filtered unique
`(traveler_user_id) WHERE ended_at IS NULL`.

A completion-reason CHECK that does not allow exactly the three reasons (the revision-1 constraint)
is dropped and recreated `WITH CHECK`. If existing rows violate the new constraint, the migration
fails loudly instead of rewriting data.

`trip.TripSessionItems`:

```sql
session_id         BIGINT        NOT NULL  -- FK trip.TripSessions ON DELETE CASCADE
itinerary_item_id  BIGINT        NOT NULL  -- FK planning.ItineraryItems
sequence_no        INT           NOT NULL
poi_id             BIGINT        NOT NULL  -- snapshot, no catalog FK
poi_name           NVARCHAR(200) NOT NULL
latitude           DECIMAL(9,6)  NOT NULL
longitude          DECIMAL(9,6)  NOT NULL
planned_arrival    DATETIME2     NOT NULL
planned_departure  DATETIME2     NOT NULL
is_mandatory       BIT           NOT NULL
reached_at         DATETIME2     NULL
skipped_at         DATETIME2     NULL
PRIMARY KEY (session_id, itinerary_item_id)
UNIQUE (session_id, sequence_no)
```

When upgrading a revision-1 table, `planned_arrival`, `planned_departure`, and `is_mandatory` are
added nullable, backfilled from `planning.ItineraryItems`, then altered to NOT NULL; unresolvable
rows fail the migration. The migration sets the filtered-index SET options explicitly, preflights
duplicate open sessions, validates prerequisites, and is safe to run repeatedly.

Revision 3 needs no schema change.

## REST resource interface

All endpoints require the `Traveler` role and take the actor only from the authenticated user.
Session IDs never authorize alone.

### 1. Itinerary waypoints (unchanged)

`GET /api/v1/itineraries/{itineraryId}` — each item exposes nullable `latitude` and `longitude`
(both POI values or both null).

### 2. Start a session

```http
POST /api/v1/itineraries/{itineraryId}/navigation-sessions
Idempotency-Key: <non-empty UUID>
```

No body. Check order:

1. Idempotency replay: same Traveler + key returns the original session (`201`) after
   re-authorizing current access; a different requested itinerary ID returns `409`.
2. Resolve current version and access (`404`/`403`).
3. Resolved version must be `Active` (`409`).
4. At least one navigable item (`422 navigation.no_navigable_items`).
5. Every navigable item has a planned arrival and departure (`422
   navigation.itinerary_schedule_incomplete`). `ItineraryItem` planned times are nullable in the
   database; a POI is never silently dropped from the route.
6. `now` must be within `[min(PlannedArrival) − EarlyStartWindow, max(PlannedDeparture) +
   ExpiryGracePeriod)` (`409 navigation.outside_trip_window`).
7. An open session of the same Traveler that is past its expiry is expired first (persisted with
   its history row). Any other open session returns `409 active_session_exists` with
   `activeSessionId`, `activeSessionLocation`, and the `Location` header.
8. Create session, snapshot, and start history atomically; `201` with
   `Location: /api/v1/navigation-sessions/{sessionId}`.

Concurrent starts cannot create two open sessions; a unique violation re-runs the attempt, which
then replays the winner or returns `active_session_exists`.

### 3. Read and resume

```http
GET /api/v1/navigation-sessions/{sessionId}
GET /api/v1/navigation-sessions?state=Open
```

`state` must be exactly `Open` (`ListNavigationSessionsQuery.OpenStateFilter`); missing or other
values return `400 navigation.unsupported_state_filter`. The list returns zero or one session in
`Navigating` or `Exploring` that is not effectively expired. Reads require session ownership and
current itinerary access, use `AsNoTracking`, and never write.

### 4. Reach an item

```http
PUT /api/v1/navigation-sessions/{sessionId}/reached-items/{itemId}
Content-Type: application/json   (body optional)

{ "occurredAtUtc": "2026-10-20T02:15:00Z" }
```

- Item not in the snapshot → `422 navigation.item_not_navigable`.
- Item already `Reached` → `200` with the stored result, even if the session is now `Completed`.
- Session `Completed` (including just expired) → `409 navigation.session_completed`.
- Otherwise the item becomes `Reached` (Pending or Skipped). If every item is now `Reached`, the
  session closes as `Completed` with `RouteFinished` and `endedAtUtc` equal to the reach time;
  otherwise it becomes or stays `Exploring` with this item as the exploring item. `200` returns the
  session.

### 5. Skip an item

```http
PUT /api/v1/navigation-sessions/{sessionId}/skipped-items/{itemId}
```

Same optional body. Not in snapshot → `422`; already `Skipped` → `200` replay; already `Reached`
→ `409 navigation.item_already_reached`; session `Completed` → `409`; otherwise `Skipped`, `200`.
Skipping never changes the session state and never completes it.

### 6. Depart (Exploring → Navigating)

```http
PUT /api/v1/navigation-sessions/{sessionId}/state
Content-Type: application/json

{ "state": "Navigating", "occurredAtUtc": "2026-10-20T03:00:00Z" }
```

`state` is required and must equal `Navigating` (validation `400` otherwise). From `Exploring` the
session returns to `Navigating`; when already `Navigating` the call is an idempotent `200`; a
`Completed` session returns `409 navigation.session_completed`.

### 7. Stop

```http
PUT /api/v1/navigation-sessions/{sessionId}/completion
```

No body. Closes an open session with `TravelerStopped`; a fully reached route has already closed
itself with `RouteFinished`. Repeating it, or calling it after expiry or an earlier completion,
returns `200` with the existing completed representation and never rewrites the reason. The session
owner may stop even after itinerary access was revoked; that response contains only the owner's
session and frozen snapshot.

## Session representation

```json
{
  "sessionId": 501,
  "itineraryId": 91,
  "itineraryVersion": 3,
  "state": "Exploring",
  "completionReason": null,
  "startedAtUtc": "2026-10-20T01:30:00Z",
  "expiresAtUtc": "2026-10-20T16:00:00Z",
  "endedAtUtc": null,
  "exploringItemId": 1001,
  "nextItemId": 1002,
  "items": [
    {
      "itemId": 1001,
      "sequenceNo": 1,
      "poiId": 12,
      "poiName": "Marble Mountains",
      "latitude": 16.003300,
      "longitude": 108.263500,
      "plannedArrivalUtc": "2026-10-20T02:00:00Z",
      "plannedDepartureUtc": "2026-10-20T03:30:00Z",
      "isMandatory": true,
      "status": "Reached",
      "reachedAtUtc": "2026-10-20T02:15:00Z",
      "skippedAtUtc": null
    }
  ],
  "transportMode": "Walking"
}
```

`transportMode` is the mode of the itinerary's scheduling request (`Walking`, `Motorbike`, `Car`,
`PublicTransit`), or `null` for a manual itinerary. Mobile requests turn-by-turn routes for the same
mode the CSP planned with (walking routes for walking trips).

No GPS position, Mapbox token, route geometry, or other Traveler data is exposed.

## Authorization

- Start, read, reach, skip, and depart require session ownership (when a session exists) and
  current itinerary access through `IItineraryAccessService`.
- Start, reach, skip, and depart check ownership and access inside the mutation transaction, and
  the access reads keep their locks until the commit (see the concurrency contract). A membership
  revocation therefore commits either before the check, and the action is denied, or after the
  action has committed. Every retry repeats the check.
- Finish requires only session ownership, so revoked access never leaves an unclosable session.
- Unknown session → `404`; a known session of another Traveler → `403` (current project policy).

## Error contract

| Status | Error code                                      | Condition                                                  |
| -----: | ----------------------------------------------- | ---------------------------------------------------------- |
| `400`  | validation ProblemDetails                       | Non-positive ID, missing/empty `Idempotency-Key`, invalid depart `state`. |
| `400`  | `navigation.unsupported_state_filter`           | List filter is not exactly `Open`.                          |
| `401`  | authentication challenge                        | Missing or invalid authentication.                          |
| `403`  | `navigation.access_denied`                      | Not the session owner, or itinerary access missing.        |
| `404`  | `itinerary.not_found`                           | Itinerary absent.                                           |
| `404`  | `navigation.session_not_found`                  | Session absent.                                             |
| `409`  | `navigation.itinerary_not_active`               | Resolved itinerary is not Active.                           |
| `409`  | `navigation.outside_trip_window`                | Start outside the trip time window.                         |
| `409`  | `navigation.active_session_exists`              | Traveler has another open, unexpired session.               |
| `409`  | `navigation.idempotency_key_payload_mismatch`   | Start key reused for another itinerary.                     |
| `409`  | `navigation.item_already_reached`               | Skip requested for a Reached item.                          |
| `409`  | `navigation.session_completed`                  | Progress requested on a Completed session.                  |
| `422`  | `navigation.no_navigable_items`                 | Active itinerary has no navigable item.                     |
| `422`  | `navigation.itinerary_schedule_incomplete`      | A navigable item has no planned arrival or departure.       |
| `422`  | `navigation.item_not_navigable`                 | Item is not in the session snapshot.                        |
| `500`  | sanitized ProblemDetails                        | Unexpected failure.                                         |

`navigation.item_out_of_order` from revision 1 is removed.

## Transaction and concurrency contract

- Every mutation runs in one `READ COMMITTED` transaction: load the tracked session, check
  ownership and current access, persist lazy expiry, apply the action, write history, save.
- The access check (start, reach, skip, depart) runs with `SERIALIZABLE` locking inside that
  transaction (`IApplicationDbContext.ExecuteWithSerializableReadsAsync`). SQL Server holds its
  key-range locks on the itinerary, group, and membership rows until the commit, while the session
  row itself is never read under those locks. A concurrent revocation waits for the mutation to
  commit, and same-session mutations keep relying on the row-version fence rather than blocking.
- `TripSessions.row_version` is the optimistic concurrency token. Every mutation that changes the
  session or any snapshot item forces the session row into the save so its `row_version` advances.
- On `DbUpdateConcurrencyException` or a deadlock (SQL Server error 1205), the handler clears
  tracked state and re-executes the whole attempt, authorization included, against fresh state, up
  to three attempts. Start also re-runs on a unique-index violation. Replays then resolve
  naturally: a duplicate reach returns the first stored timestamp; a reach that lost to a stop
  returns `409 session_completed`; a stop that lost to anything returns the authoritative result;
  two concurrent reaches of the last two items close the route exactly once. No retry can append a
  duplicate history row.
- Unexpected exhaustion of attempts surfaces as a sanitized `500`.
- No network call occurs inside the transaction.

## Security, privacy, and observability

- No endpoint accepts, stores, or logs GPS. Existing `current_latitude`, `current_longitude`, and
  `last_synced_at` remain unused.
- Metrics use only a bounded `outcome` dimension: starts, reaches, skips, departures, completions,
  expirations. Never label with user, session, itinerary, or item IDs.
- Logs never contain coordinates, idempotency keys, or tokens.

## Acceptance criteria

1. Itinerary detail exposes the nullable coordinate pair without changing existing fields.
2. Start succeeds only for an accessible Active itinerary with a navigable item, inside the trip
   window; it freezes item, POI, coordinate, planned-time, and mandatory data.
3. Start before the window or after the expiry returns `409 navigation.outside_trip_window`.
4. Start replay is stable and authorized; a different itinerary for the key returns `409`.
5. An expired open session is closed during the next start, which then succeeds; an unexpired open
   session returns `409 active_session_exists` with recovery metadata.
6. Items can be reached in any order; reaching moves to `Exploring`; reaching another item while
   exploring writes no history.
7. Any Pending item can be skipped; a Skipped item can be reached later; skipping a Reached item
   returns `409 item_already_reached`; replays keep the first timestamp.
8. Depart returns `Exploring` to `Navigating`; repeating it is a no-op.
9. The reach that leaves every item Reached closes the session with `RouteFinished`; an explicit
   completion always records `TravelerStopped`, including with only skipped items left or nothing
   reached; repeating it never changes the reason.
10. Expiry always closes an open session with `Expired` at `ExpiresAtUtc`; reads show the effective
    expired state without writing; the open list, the UC-58 list, and the UC-59 detail exclude
    expired sessions with one shared rule.
11. Device event times inside the valid range and not before the causal watermark are stored;
    older, out-of-range, or absent ones use Backend time.
12. Each real state change has exactly one history row; concurrent duplicate or conflicting
    mutations produce one authoritative result.
13. Navigation never changes itinerary content, item status, or version; no GPS is stored.
14. SQL Server tests prove migration convergence from v7, from revision 1, and on rerun, plus
    rollback and concurrency behavior.
15. OpenAPI documents every route, body, header, schema, and error response.
16. SQL Server tests prove that a membership revocation is ordered with start, reach, skip, and
    depart: committed before the access check, the action is denied and writes nothing; attempted
    after it, the revocation waits until the action commits.
17. UC-59 detail shows the requested session's own item progress after reach and skip, end to end
    through the API.
18. `nextItemId` is null for a completed session and when only skipped items remain.

## End-to-end dependency on Mobile

Backend completion does not complete the mobile-only use case. Mobile must still:

- start only Active itineraries and surface `outside_trip_window` with the trip date;
- handle `active_session_exists` with Resume or Stop;
- acquire GPS, render Mapbox guidance to the Traveler-selected target, and detect deviation
  against that target route;
- detect arrival with radius plus dwell time, ask for confirmation, and offer a manual
  "I have arrived" action;
- call reach, skip, depart, and completion; queue them offline with `occurredAtUtc` and replay the
  queue in FIFO order;
- show the Exploring panel; treat a reach response whose `state` is `Completed` as "route
  finished"; when `nextItemId` is null on an open session, offer the skipped items for a revisit or
  Stop.

## Non-goals

- Backend Mapbox proxy, route geometry, ETA, deviation, or arrival detection.
- GPS storage, location sharing (UC-22), alerts (UC-14), rerouting and session rebinding (UC-15),
  offline packages and sync (UC-16/UC-72).
- Changing itinerary or itinerary-item status on finish.
- Background expiry jobs.
