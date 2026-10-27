# UC-40 — View Customer Bookings: Implementation Plan

Source of truth: `specs/UC-40-view-customer-bookings-spec.md` (Approved 2026-10-27).
Branch: `feature/uc40-view-customer-bookings` (from `develop` @ `02b0f744`).
Scope guard: read-only, no migration, no schema change, no change to UC-41 behaviour.

## Working rules for every task

- TDD: write the failing test, run it and see it fail for the right reason, write the minimum code,
  see it pass, refactor, re-run. Code written before its test is deleted and restarted.
- One task at a time. After each task: run its verification command, then review the diff against
  the spec. A **Critical** finding blocks the next task; a **Minor** one may be deferred with the
  developer's agreement.
- Application feature folder: `src/TripMate.Application/Features/Operator/Bookings/`. Unit tests:
  `tests/TripMate.Application.UnitTests/Features/Operator/Bookings/` (InMemory `TestDbContext`,
  `FakeCurrentUserService`, helpers from `OperatorRevenueTestFixtures`).
- No magic strings (AGENTS §4.1): query/error/blocked-reason constants are `public const string`.
- Do not commit, push or open a PR until the developer chooses (Task 10).

Focused unit command used below:
`dotnet test tests/TripMate.Application.UnitTests --nologo --filter "FullyQualifiedName~Features.Operator.Bookings"`

## Task 0 — Baseline (workspace isolation)

Confirm `dotnet build TripMate.slnx` and `dotnet test TripMate.slnx` are green on the clean branch
before any change. A baseline run was started on this branch; its result is reported with this plan.
**DoD:** baseline result recorded; any pre-existing failure is listed and agreed before Task 1.

## Task 1 — Shared cancel-eligibility helper (refactors UC-41, no behaviour change)

Spec: "`canCancel` … extracted into one shared helper".

- **Red:** `Features/Bookings/Common/BookingCancelEligibilityTests.cs`: table of `canCancel` /
  `BlockedReason` for status × ticket-state: `PendingPayment`/`Confirmed` with no ticket / `Valid`
  ticket → allowed; with a `Used` ticket → `CheckedIn`; `Cancelled`/`Completed`/`Expired` → `InvalidStatus`;
  terminal status **and** `Used` ticket → `InvalidStatus` (status is checked first).
- **Green:** new `src/TripMate.Application/Features/Bookings/Common/BookingCancelEligibility.cs`
  with `Evaluate(BookingStatus, bool hasUsedTicket)` returning `(bool CanCancel, string? BlockedReason)`,
  and constants `BlockedInvalidStatus = "InvalidStatus"`, `BlockedCheckedIn = "CheckedIn"`.
- **Refactor:** `CancelBookingCommandHandler` (the `Status is not (...) || Tickets.Any(Used)` condition)
  and `BookingCancellationPreviewPolicy.Evaluate` call the helper. Error code (`InvalidState`), message
  and order of checks stay identical.
- **Files:** the helper + its test; edits to `Bookings/Cancel/CancelBookingCommandHandler.cs`,
  `Bookings/Preview/GetBookingCancellationPreviewQueryHandler.cs`.
- **Verify:** `dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~Features.Bookings"`
  and the UC-41 endpoint tests `FullyQualifiedName~CancelBookingEndpointTests`, all **unmodified** and green.
- **DoD:** no existing UC-41 test edited; helper table green.

## Task 2 — Error codes, operator access check, HandleFailure mapping

- **Red:** `OperatorBookingAccessTests`: unauthenticated user, role `Traveler`, operator with
  `Pending`/`Rejected` profile, operator whose user is not `Active`, no profile → failure
  `OperatorTourErrorCodes.AccessRequired`; approved + active operator → success with the operator id.
- **Green:** `Common/OperatorBookingErrorCodes.cs` (`operator_booking.invalid_filter`,
  `.invalid_date_range`, `.forbidden`, `.not_found`) and `Common/OperatorBookingAccess.cs`
  (extract the `OperatorProfiles` Approved + `Users` Active check used by
  `GetOperatorRevenueReportQueryHandler`; do **not** change the revenue handler, YAGNI/zero-regression).
- **Mapping:** in `src/TripMate.Api/Common/ApiControllerBase.cs` add `Forbidden` → 403, `NotFound` → 404,
  `InvalidDateRange` → 400 (`InvalidFilter` is validator-produced; still map it to 400 for any
  handler-side use). The mapping test is covered by the endpoint tests in Task 8.
- **Verify:** focused unit command.
- **DoD:** four codes exist, are mapped, and the access check is tested for every denial path.

## Task 3 — Check-in derivation

- **Red:** `BookingCheckInTests` table: no tickets; `Valid` + null; `Used` + null timestamp;
  `Used` + timestamp; non-`Used` status + non-null `CheckedInAtUtc` → `CheckedIn`; `Cancelled`/`Expired`
  tickets → `NotCheckedIn`; two tickets → earliest non-null timestamp.
- **Green:** `Common/BookingCheckIn.cs`: constants `CheckedIn` / `NotCheckedIn`, an
  `Expression<Func<Ticket, bool>>` predicate (`Status == Used || CheckedInAtUtc != null`) reused for
  SQL translation in the list projection, and an in-memory `Resolve(IEnumerable<Ticket>)` for the detail
  endpoint built on the same predicate (single source of truth).
- **Verify:** focused unit command.
- **DoD:** derivation table green; predicate is a plain expression (translatable by EF).

## Task 4 — List query contract and validator

- **Red:** `GetOperatorBookingsQueryValidatorTests`: `tourId`/`scheduleId` ≤ 0, `pageNumber` < 1,
  `pageSize` 0 and 101, keyword of 201 chars (whitespace trimmed first) → invalid with
  `operator_booking.invalid_filter`; defaults and boundary values (`pageSize` 1 and 100, keyword of
  200) → valid.
- **Green:** `List/GetOperatorBookingsQuery.cs` (record with `DefaultPageSize = 20`,
  `MaximumPageSize = 100`, `MaximumKeywordLength = 200`; params per spec; `DateOnly? DepartureFrom/To`),
  response DTOs (`OperatorBookingsResponseDto`, `OperatorBookingSummaryDto`, `OperatorBookingListItemDto`,
  tour/schedule/booker sub-DTOs, string ids), and `GetOperatorBookingsQueryValidator.cs`.
- **Verify:** focused unit command.
- **DoD:** validator tests green; DTO shape matches the spec example field-for-field.

## Task 5 — List handler (split into five red/green cycles in one handler)

All tests seed two operators (A = caller, B = other) with tours, schedules, bookings, tickets.

- **5a Authorization, isolation, paging, order.** Red: non-operator / unapproved → access error;
  A sees only A's bookings in `items`, `totalCount` and `summary`; empty result → `200` shape with zero
  summary; `pageNumber` past the end → empty `items`; order `BookedAtUtc` desc then `Id` desc.
  Green: handler skeleton using `OperatorBookingAccess`, scope predicate
  `TourSchedule.Tour.OperatorUserId == operatorId` first, `AsNoTracking`, `Select` projection, paging.
- **5b Filters.** Red: `status`, `paymentStatus`, `tourId`, `scheduleId` alone and combined; `tourId` /
  `scheduleId` owned by B or nonexistent → `operator_booking.forbidden`; `departureFrom > departureTo`
  → `operator_booking.invalid_date_range`; Vietnam-time day boundary (a schedule starting
  `2026-11-01T17:30:00Z` is on `2026-11-02`; `departureTo` includes the whole local day).
  Green: filters, ownership pre-check by one `AnyAsync`, date bounds converted from `Asia/Ho_Chi_Minh`
  to UTC via a single helper (no hard-coded `+7` literals scattered).
- **5c Keyword.** Red: matches booking code, booker full name, booker email, case-insensitively;
  `%`, `_`, `[`, `\` are literal; does not match a booker on B's tours. Green: `EF.Functions.Like`
  with the same escaping as `GetPlatformBookingsQueryHandler`. If InMemory cannot evaluate the
  escaped `Like`, the unit test covers code/name/email matching with a plain keyword and the escaping
  cases move to the SQL Server test in Task 9 (see Risks).
- **5d Summary.** Red: `totalBookings` counts every status; `totalParticipants` and
  `totalConfirmedAmount` sum only `Confirmed` + `Completed`; computed over the whole filtered set (page
  size 1 still returns full summary); excludes B's bookings. Green: one aggregate over the filtered
  query.
- **5e Check-in projection.** Red: items report `checkInStatus` / `checkedInAtUtc` per Task 3 rules,
  including a booking with no ticket. Green: project via the shared predicate and a correlated
  earliest-timestamp subquery (see Risks for the translation check).
- **Files:** `List/GetOperatorBookingsQueryHandler.cs` + `GetOperatorBookingsQueryHandlerTests.cs`.
- **Verify:** focused unit command after each of 5a–5e.
- **DoD:** every acceptance criterion 1, 2, 4, 5, 6, 7 (list part) has a passing test.

## Task 6 — Detail query and handler

- **Red:** `GetOperatorBookingQueryHandlerTests`: booking on B's tour → `operator_booking.forbidden`
  with no booking data; unknown id → `operator_booking.not_found`; non-operator / unapproved →
  access error. Mapping cases: full mapping (tour, schedule incl. `meetingPoint`, booker incl.
  `phoneNumber`, `subtotalAmount`); participants whitelist; no ticket → `ticket: null`; several tickets
  → latest issued; `payment` = latest `Success` + `Payment`-type transaction, ignoring `Pending`/`Failed`
  and refund-type rows, `null` when none; `paidAmount` = payment amount or `0` (and a payment whose
  amount differs from the total is still shown); `coupon` present/absent; `canCancel` +
  `cancelBlockedReason` through `BookingCancelEligibility` for pending-unpaid, paid-confirmed,
  cancelled, expired, used-ticket cases. Sensitive-field test: serialize the response with System.Text.Json
  and assert none of `idDocument*`, `nationality`, participant `phoneNumber`/`email`, `rawResponse`,
  `checkoutUrl`, `qrCode`, `paymentLinkId`, `orderCode` appear (acceptance criterion 8).
- **Green:** `Get/GetOperatorBookingQuery.cs` (+ DTOs), `Get/GetOperatorBookingQueryHandler.cs`.
  Existence and ownership resolved in one query (select the owner id first, then decide 404 vs 403) so
  the booking graph is loaded only for the owner. Participant mapping is an explicit whitelist
  (`fullName`, `participantType`).
- **Verify:** focused unit command.
- **DoD:** acceptance criteria 3, 7 (detail), 8, 9 covered.

## Task 7 — Controller

- **Red:** compile-level/routing assertions are covered in Task 8; start with the endpoint tests there
  (they fail until the controller exists).
- **Green:** `src/TripMate.Api/Controllers/V1/OperatorBookingsController.cs`:
  `[Authorize(Roles = nameof(UserRole.TourOperator))]`, `[Route("api/v1/operator/bookings")]`, two thin
  actions (`GET ""` and `GET "{bookingId:long}"`), `Ok(result.Value)` or `HandleFailure(result)`. Query
  parameter names bound with the exact names in the spec; no DbContext use, no business logic.
- **Verify:** `dotnet build TripMate.slnx` (0 new warnings).
- **DoD:** both routes resolve; handlers are discovered by the existing MediatR/validator registration
  (no DI change expected; confirm, and add registration only if the build/tests show it is needed).

## Task 8 — Endpoint (HTTP) tests

`tests/TripMate.Api.IntegrationTests/Bookings/OperatorBookingsEndpointTests.cs`, modelled on
`PlatformBookingsEndpointTests` (`TripMateApiFactory`, `CreateAuthenticatedClient`).

- Anonymous → 401; `Traveler` and `Staff` roles → 403; approved operator with no bookings → 200 empty
  page with `pageSize` 20 and zero summary; seeded bookings of two operators → only own bookings;
  foreign booking detail → 403; missing → 404; `pageSize=101` → 400 with `errorCode` =
  `operator_booking.invalid_filter`; `departureFrom > departureTo` → 400 with
  `operator_booking.invalid_date_range`; foreign `tourId` filter → 403.
- **Verify:** `dotnet test tests/TripMate.Api.IntegrationTests --filter "FullyQualifiedName~OperatorBookingsEndpointTests"`.
- **DoD:** every status/`errorCode` row of the spec's error table is asserted; JSON property names match
  the spec examples.

## Task 9 — SQL Server translation tests

`tests/TripMate.Api.IntegrationTests/Bookings/OperatorBookingsSqlServerTests.cs`, using
`[SqlServerFact]` / `SqlServerTestDatabase` like `PlatformBookingsSqlServerTests` (skipped
automatically where SQL Server is unavailable).

- Seed via the real schema SQL: two operators, tickets in each state, a participant with an ID
  document. Assert the list + detail handlers translate and return correct data on SQL Server:
  Like escaping (`%`, `_`, `[`), Vietnam-day boundaries, summary aggregates, earliest `checkedInAtUtc`
  subquery, ordering tie-break, and that participant ID-document data never appears.
- **Verify:** `dotnet test tests/TripMate.Api.IntegrationTests --filter "Category=SqlServer&FullyQualifiedName~OperatorBookings"`.
- **DoD:** tests pass when SQL Server is available, or the report states they were skipped and why.
  A skip is not reported as verified.

## Task 10 — Contract doc, full verification, finish

1. `docs/UC-40-api-contract.md` for FE handoff (modelled on `docs/UC-29-api-contract.md`): both
   endpoints, field table, error table, and the FE migration notes already agreed (add `Expired`
   status; use `canCancel` + `cancelBlockedReason`; remove mock participant age/notes and
   `cancellationWindowExpired`; `PaymentGateway` values; refund progress stays on
   `GET /api/v1/bookings/{id}/refund`).
2. `dotnet build TripMate.slnx`, `dotnet format --verify-no-changes` on touched projects if the repo
   uses it, then `dotnet test TripMate.slnx`; compare with the Task 0 baseline.
3. Final diff review against the spec's acceptance criteria 1–11 (a checklist in the PR description),
   confirm no secrets, no `.env`, no migration, no UC-41 test changes.
4. Present the options **merge / open PR / keep branch / discard** and act only on the choice. The PR
   description lists the endpoints, states "no migration", and includes the verification evidence.

## Risks and how the plan handles them

| Risk | Handling |
|---|---|
| InMemory provider cannot evaluate `EF.Functions.Like` with an escape character, so keyword escaping cannot be proven in unit tests | Unit tests prove matching with plain keywords; escaping is proven on SQL Server (Task 9). If SQL Server is unavailable the report says escaping is unverified. |
| `Min` / correlated subquery over the `AsUtcDateTime2` value-converted `checked_in_at` may not translate on SQL Server | Use an ordered `FirstOrDefault` subquery instead of an aggregate; proven in Task 9; fallback is to compute the timestamp in memory for the page rows only (page size ≤ 100). |
| Query cost on large operators (join through schedule → tour) | Index `IX_Bookings_TourSchedule` exists; no migration. If Task 9 or UAT data shows a poor plan, propose a separate additive index migration for approval, do not add it here. |
| Refactoring UC-41 handlers regresses cancellation | Task 1 changes only the shared condition, existing tests are not edited and must stay green. |
| Check-in is always `NotCheckedIn` until UC-43 exists | Documented in the spec and the contract doc; tests seed `Used` tickets directly to prove the derivation. |
| Verification claims on Azure | Out of scope here: the endpoints are not run against the Azure UAT database in this plan. If the developer wants an Azure read-only check after merge, it uses `Capstone_BE/.env.azure` and is a separate, approved step. |

## Order and checkpoints

0 → 1 → 2 → 3 → 4 → 5a–5e → 6 → 7 → 8 → 9 → 10. Review checkpoint after every task; the developer
is asked only at the end (Task 10) unless a Critical finding or an ambiguity in the spec appears,
in which case work stops and the question goes to the developer before continuing.
