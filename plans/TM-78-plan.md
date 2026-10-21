# TM-78 Implementation Plan — UC-32 View Trip History (Backend)

Spec: `specs/TM-78-spec.md` (approved 2026-10-21, P1–P9 and tie-breaking T1–T3).
Branch: `feature/tm-78-trip-history` (from `develop` @ `62d5672`).
Status: **Approved 2026-10-21; implemented (Tasks 1–11), see §4a** — assumptions A1–A6 and the 1,000-key window cap (§4) accepted.
The cap is enforced in the list validator (Task 2): `page * pageSize > 1000` →
`trip_history.invalid_paging`. Baseline on `develop` @ `62d5672`: `dotnet test TripMate.slnx` green
(13 + 922 + 2389 + 591 passed; 3 + 470 skipped; exit 0).

## 0. Ground Rules

- TDD per AGENTS.md §1.2: write the failing test, confirm it fails for the right reason, write the
  minimum code, refactor, run the task's verification command. Code written before its test is deleted.
- One task at a time. After each task: two-pass self-review (spec compliance, then code quality).
  A Critical finding blocks the next task.
- No schema change, no EF migration, no commit, push or PR unless the developer asks.
- No raw string literals for tabs, trip types, kinds, statuses or error codes (AGENTS.md §4.1).
- Reads use `AsNoTracking()`. Controllers hold no logic.

## 1. Assumptions Introduced by This Plan (confirm with approval)

These fill small gaps the spec leaves open. Each is one line to change if you disagree.

| # | Assumption | Why |
| --- | --- | --- |
| A1 | "Newest version" in P4 means the newest **non-Draft** version per `SchedulingRequestId`. | A regenerated, unaccepted Draft v2 must not hide the Active v1 from Upcoming. `ListMyItineraries` shows the Draft instead, but history excludes Drafts (P2). |
| A2 | The representative session (T1) must belong to the caller: `TripSession.TravelerUserId == callerId`. | BR-90. A group member's session on a shared itinerary is not the owner's trip. |
| A3 | For a Completed itinerary, `stopCount` is the count of the representative session's items, so `visitedCount <= stopCount`. For other tabs, `stopCount` counts the itinerary's `Visit` items (as `ListMyItineraries` does). | Session items are the frozen snapshot that `visitedCount` is measured against. |
| A4 | Vietnam day boundaries use a fixed `+07:00` offset (Vietnam has no DST), not a platform time-zone lookup. | Other handlers already use `TimeSpan.FromHours(7)`; avoids Windows/Linux zone-id differences. |
| A5 | Cross-kind sort tie-break is departure descending, then `kind` ordinal ascending (`commerceBooking` before `itinerary`), then `id` descending. | Spec §7.1 leaves the kind order implicit. |
| A6 | A booking with several `TripReview` rows (legacy conflict) reports the lowest `Id`. | Deterministic; one row is the normal case. |

## 2. Task Breakdown

Test projects: `tests/TripMate.Application.UnitTests` (U) and `tests/TripMate.Api.IntegrationTests` (I).
Filter used below: `--filter "FullyQualifiedName~TripHistory"`.

### Task 1 — Contracts, constants and pure classification rules

- **Files (new):** `Features/TripHistory/Common/TripHistoryErrorCodes.cs`, `TripRecordKinds.cs`,
  `TripHistoryClassifier.cs`, `VietnamDayRange.cs`; tests `Features/TripHistory/TripHistoryClassifierTests.cs`,
  `VietnamDayRangeTests.cs`.
- **Tests first (U):**
  - Booking status → tab for all five `BookingStatus` values (P1).
  - Itinerary tab from (`Status`, has-completed-session) for all four statuses (P2): Draft → excluded.
  - Departure date selection (P5, T1): completed session `StartedAtUtc` wins, else `ValidFromUtc`, else `CreatedAtUtc`.
  - `VietnamDayRange`: `2026-10-21` → `[2026-10-20T17:00Z, 2026-10-21T17:00Z)`; `fromDate > toDate` rejected.
  - Kind constants equal the TM-79 wire values (`commerceBooking`) and `itinerary`.
- **Code:** static classifier and range helper, error-code constants (`trip_history.invalid_tab`,
  `invalid_trip_type`, `invalid_date_range`, `invalid_paging`, `invalid_record`, `unauthorized`,
  `forbidden`, `record_not_found`). The classifier is the single place P1/P2/P5 live.
- **Verify:** `dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~TripHistory"`.
- **DoD:** every P1/P2/P5 row covered; no I/O, no `DateTime.Now`.

### Task 2 — List and detail queries with validators and error-code mapping

- **Files (new):** `List/ListTravelerTripsQuery.cs` (record, constants `TabUpcoming`/`TabCompleted`/
  `TabCancelled`, `TripTypeAll`/`TripTypeTourBooking`/`TripTypeSelfPlanned`, `AllowedTabs`,
  `AllowedTripTypes`, `DefaultPageSize = 20`, `MaxPageSize = 50`), `ListTravelerTripsQueryValidator.cs`,
  `Get/GetTravelerTripQuery.cs`, `GetTravelerTripQueryValidator.cs`; tests for both validators.
- **Tests first (U):** each invalid input yields its own `errorCode` — missing/unknown tab, unknown
  trip type, malformed date, `fromDate > toDate`, `page < 1`, `pageSize` 0 and 51, unknown `kind`,
  non-positive `id`. Valid boundary values (`pageSize` 1 and 50) pass.
- **Code:** FluentValidation rules with `.WithErrorCode(...)` from constants. Add a validation
  exception filter that turns the first failure's error code into the project's `ErrorCodeProblemDetails`,
  mirroring `TripReviewValidationExceptionFilterAttribute` (read it first; reuse if generic).
- **Verify:** same filter as Task 1.
- **DoD:** validators reference query constants, never literals.

### Task 3 — Representative completed session (T1) as one shared query helper

- **Files (new):** `Common/TripSessionQueries.cs` with
  `RepresentativeCompletedSessions(IQueryable<TripSession>, long travelerId)`; tests.
- **Tests first (U, `TestDbContext`):** two Completed sessions on one itinerary → the later `EndedAtUtc`
  wins; equal `EndedAtUtc` → higher `Id` wins; a non-Completed session is ignored; another Traveler's
  session is ignored (A2); one row per itinerary.
- **Code:** `Where(Completed && TravelerUserId == id && !Any(other better))` using the T1 ordering.
  No `GroupBy` + `First` shape EF cannot translate. List, detail and summary all call this helper.
- **Verify:** same filter.
- **DoD:** the T1 ordering exists in exactly one place.

### Task 4 — Booking stream (keys, tab, filters, count)

- **Files (new):** `Common/TripBookingStream.cs`; tests.
- **Tests first (U):** only the caller's bookings (BR-90); tab per P1; `tripType` excludes the stream;
  date filter on `TourSchedule.StartAtUtc` with day boundaries; count equals key rows; window is
  `(departure desc, id desc)`.
- **Code:** projects `TripKey(kind, id, departureAtUtc)` rows plus total count, honoring a window size.
- **Verify:** same filter.
- **DoD:** no tracking, no entity materialization beyond keys.

### Task 5 — Itinerary stream (keys, tab, filters, count)

- **Files (new):** `Common/TripItineraryStream.cs`; tests.
- **Tests first (U):** only `Manual`/`CSPGenerated` owned by the caller (P3); `BookedTour` and group-shared
  excluded; Draft excluded; Completed via Task 3 helper and reported at the frozen `ItineraryId` (P4);
  Active without a completed session → Upcoming; Cancelled → Cancelled; newest non-Draft version per
  scheduling request for Upcoming/Cancelled (A1); a Completed v1 and an Active v2 both appear, in different tabs;
  departure date per P5; date filter and count.
- **Code:** same `TripKey` projection as Task 4.
- **Verify:** same filter.
- **DoD:** each itinerary lands in exactly one tab.

### Task 6 — List handler: merge, order, window and total

- **Files (new):** `List/ListTravelerTripsQueryHandler.cs`; tests.
- **Tests first (U):** keys from both streams merge by A5 order; page 2 continues page 1 with no repeat or
  skip across a kind boundary and across equal departure dates; `totalCount` is the sum of both stream
  counts; `tripType` limits to one stream; empty tab → `items: []`, `totalCount: 0`.
- **Code:** take `page * pageSize` keys from each stream, merge in memory, slice the page window. The
  window is bounded by `MaxPageSize`; the plan adds a hard cap on `page` (see §4) to bound cost.
- **Verify:** same filter.
- **DoD:** no per-row queries in this task (hydration is Task 7).

### Task 7 — Hydration: cards, actions, ticket (T2), review state, visited counts

- **Files (new):** `Common/TripHistoryResponses.cs` (item, actions, review state), `Common/TripCardHydrator.cs`; tests.
- **Tests first (U):**
  - `canReview` true only for Completed + not reviewed bookings; false for reviewed; false for Upcoming/Cancelled;
    always false for itineraries; `reviewableRecord` null for itineraries (P6).
  - `ticketId`: with `Valid` id 9 and `Used` id 3 → 9 (Valid first); two `Valid` ids 8 and 5 → 5; only `Used`
    ids 4 and 2 → 2; `Cancelled`/`Expired` ignored; `canViewETicket` also requires Confirmed/Completed (P7, T2).
  - `canViewRefund` true for a Cancelled booking with any refund (P8).
  - Itinerary `visitedCount` = `Reached` items of the representative session; `stopCount` per A3.
  - One batched query per related table for a page of N cards (assert query count does not grow with N).
- **Code:** hydrate only the page's keys; batch `Tickets`, `Refunds`, `TripReviews`, `TripSessionItems`.
- **Verify:** same filter.
- **DoD:** no N+1; ticket ordering uses an explicit `Valid`-first expression, not enum ordinals.

### Task 8 — Completed-tab summary

- **Files:** extend the list handler; tests.
- **Tests first (U):** `summary` present only on `Completed`; `totalCompletedTrips` equals the filtered total, not the
  page size; `totalVisitedPois` counts `Reached` items of representative sessions under the active filters;
  `tripType = TourBooking` gives visited `0`.
- **Verify:** same filter.
- **DoD:** summary and list use the same filtered sets, so they cannot disagree.

### Task 9 — Detail handler

- **Files (new):** `Get/GetTravelerTripQueryHandler.cs`, detail response records; tests.
- **Tests first (U):**
  - Booking: tour title and destination, schedule start/end/meeting point, participant counts by `ParticipantType`,
    cancellation info; refund object is the highest `Refund.Id` (T3) with `channel` from the original
    `PaymentTransaction.Gateway`; a refund with null `TransactionId` yields a null channel, not an error.
  - Itinerary: ordered stops with `visitStatus`/`reachedAtUtc`/`skippedAtUtc` from the representative session;
    non-completed itineraries list planned stops with no visit data; session `startedAtUtc`/`endedAtUtc`/`completionReason`.
  - Not found and not owned both return `record_not_found`; a Draft or `BookedTour` itinerary is not found.
- **Verify:** same filter.
- **DoD:** detail uses the same helpers as the list, so tab and counts match.

### Task 10 — Controller, authorization and HTTP behavior

- **Files (new):** `Api/Controllers/V1/TravelerTripsController.cs`; integration tests
  `Api.IntegrationTests/TripHistory/TravelerTripsEndpointTests.cs`.
- **Tests first (I, `TripMateApiFactory`):** `401` without a token; `403` for non-Traveler and for a non-Active
  Traveler, with `trip_history.*` codes; `200` list and detail shapes match spec §7; ownership isolation;
  every `400` code; `404` for another Traveler's record; filters and Vietnam day boundary through HTTP;
  paging stability over 25 mixed records; **no-write** check that row versions of bookings, itineraries,
  sessions and reviews are unchanged after reads (PC-03).
- **Code:** `[Authorize(Policy = TripReviewAuthorizationPolicies.ActiveTraveler)]` plus `AuthorizationProblemDetails`
  with `trip_history.unauthorized`/`forbidden`; `[Route("api/v1/traveler/trips")]`; `[HttpGet]` and
  `[HttpGet("{kind}/{id:long}")]`; `ProducesResponseType` for every status. If the shared policy reuses
  `TripReview` naming, keep it and note it; do not duplicate the policy.
- **Verify:** `dotnet test tests/TripMate.Api.IntegrationTests --filter "FullyQualifiedName~TripHistory"`.
- **DoD:** controller only binds, sends and maps; OpenAPI shows both routes.

### Task 11 — SQL Server translation test, contract doc, full verification

- **Files (new):** `Api.IntegrationTests/TripHistory/TravelerTripsSqlServerTests.cs` (pattern:
  `ActiveTripsSqlServerTests`), `docs/TM-78-api-contract.md` (for FE and Mobile), plus a one-line pointer in
  `docs/TM-79-api-contract.md` replacing "future record/action contract".
- **Tests:** the Task 6/7 scenarios against real SQL Server (the in-memory provider cannot prove that the
  representative-session and newest-version subqueries translate). Skipped, with a stated reason, only if Docker is unavailable.
- **Verify:** `dotnet build TripMate.slnx`, `dotnet test TripMate.slnx`, then the repo's format check
  (`dotnet format TripMate.slnx --verify-no-changes`, matching the CI "format" step).
- **DoD:** whole suite green, format clean, contract doc matches the shipped JSON field for field, final
  two-pass review of the whole diff against the spec's acceptance criteria 1–9.

## 3. Traceability

| Spec item | Task |
| --- | --- |
| P1 booking tabs | 1, 4 |
| P2, P3, P4 itinerary tabs, scope, versions | 1, 5 |
| P5 departure date | 1, 4, 5 |
| P6 review actions | 7 |
| P7 + T2 ticket | 7 |
| P8 + T3 refund | 7, 9 |
| P9 summary | 8 |
| T1 representative session | 3, 5, 7, 8, 9 |
| §7.1 / §7.2 contract | 2, 6, 9, 10 |
| §7.3 errors | 1, 2, 10 |
| AC 1 ownership | 4, 5, 10 |
| AC 8 no write | 10 |

## 4. Risks and Decisions Needing a Look

- **Deep paging cost.** The merge reads `page * pageSize` keys from each stream. The plan caps `page` so the window
  stays bounded; the cap value (proposal: window of at most 1,000 keys per stream) is added to the validator
  with error `trip_history.invalid_paging`. Say if you prefer no cap.
- **Baseline.** The full `dotnet test` baseline on `develop` is recorded before Task 1; no task starts on a red baseline.
- **Completed data.** No handler sets `Booking.Status = Completed` today (spec §4); integration tests seed it directly.
- **Out of band.** Any index the SQL Server run shows to be needed ships as a separate idempotent script and is flagged first.

## 4a. Execution Log and Deviations (2026-10-21)

Tasks 1–11 were executed in order, test first. Differences from the plan as approved:

| # | Deviation | Reason |
| --- | --- | --- |
| D1 | `ListTravelerTripsQuery` (constants only) and the response records were created in Task 1/2, not Task 2/6/7. | The classifier and the `IRequest<…>` types need them; no behavior moved. |
| D2 | **No new validation exception filter** (Task 2/10). Validators use `.WithErrorCode(...)`. | `ValidationException` already carries error codes and `ExceptionHandlingMiddleware` puts the first one in `errorCode` for every non-admin-trips route. Verified by the endpoint tests. |
| D3 | **T4 added to the spec** (effective expiry), changing the Task 3 helper to take `nowUtc` and adding `IsEffectivelyCompleted`/`EffectiveEndedAtUtc`/`EffectiveCompletionReason`. | Developer decision after discovering that expiry is only applied lazily and no job persists it. |
| D4 | Task 5/6/7 split the key reader (`TripHistoryKeyReader`, `TripKeyMerger`) from the handler; Task 8 summary is `TripItineraryStream.CountVisitedPoisAsync`. | Pure merge logic is unit-testable without a database. |
| D5 | The N+1 query-count assertion moved from Task 7 (in-memory) to Task 11 (SQL Server command interceptor). | The in-memory provider has no command log. |
| D6 | **Bug found by the SQL Server run and fixed:** `commerce.Bookings.tour_schedule_id` is nullable (itinerary-linked bookings); the count did not join the schedule and over-counted. `TripBookingStream.Filter` now requires `TourScheduleId > 0`. | In-memory cannot hold such a row; only the real database exposed it. |
| D7 | Detail does not apply the A1 newest-version rule: opening a superseded non-Draft version by id still works. | The list is where duplicates would confuse; a deep link to an old id is harmless. |
| D8 | An `Itinerary.Status = Completed` row with no completed session is excluded everywhere. | Decision 2 defines Completed by the session; the status is never assigned today. |
| D9 | Non-numeric `page`/`pageSize` returns the framework `400` without `errorCode`. | Binding rejects it before the validators; documented in the contract. |

## 5. Finishing

When all tasks pass and are reviewed, stop. Present the options — merge, open a PR, keep the branch, or
discard — and act only on the developer's choice (AGENTS.md §1.2 step 6). FE and Mobile start only after that
decision, against `docs/TM-78-api-contract.md`.
