# UC-40 — View Customer Bookings (Backend Specification)

## Status

Approved by the developer on 2026-10-27, including both former open questions (decisions are in the
body: `canCancel` / `cancelBlockedReason` instead of `cancellationWindowExpired`, and the `summary`
basis).

## Scope

Allows an authenticated, approved Tour Operator to list and open the customer bookings placed on
**its own** tour packages (SRS 3.8.4.1). Read-only.

Included:
- `GET /api/v1/operator/bookings` — paginated, filtered list with a summary block.
- `GET /api/v1/operator/bookings/{bookingId}` — booking detail (tour/schedule, booker contact,
  participants, ticket/check-in summary, payment and coupon summary, cancel eligibility).
- Actor isolation by `Tour.OperatorUserId == currentUserId` on every query path.
- Check-in status derived from `Ticket`.

Excluded:
- Cancelling a booking (UC-41, already implemented) and refunds (UC-42; refund progress is already
  readable through `GET /api/v1/bookings/{bookingId}/refund`).
- Recording a check-in (UC-43, mobile only, not implemented yet).
- Any write, schema change or migration. The existing schema is the source of truth.
- Mobile. SRS 3.8.4.1 defines UC-40 on the Next.js web application only.

## Business Rules

1. **BR-105 (Actor isolation)**: an operator sees only bookings whose
   `TourSchedule.Tour.OperatorUserId` equals the signed-in operator id. The same predicate is applied
   to the list, the summary, the detail endpoint, and to any `tourId` / `scheduleId` filter.
2. **Approved operator only**: the caller must hold role `TourOperator`, with an `Approved`
   `OperatorProfile` and an `Active` account. Reuse the check and `OperatorTourErrorCodes.AccessRequired`
   used by `GetOperatorRevenueReportQueryHandler`.
3. **BR-52 / CR-01 (Pagination)**: default page size 20, maximum 100, page number starts at 1.
4. **BR-77 (Read-only transaction data)**: payment information is a read-only projection of the
   verified payment; this use case never modifies it.
5. **CR-07 (Dates)**: stored and returned as UTC (`...Utc` suffix, ISO-8601). Date-only filter inputs
   are interpreted in `Asia/Ho_Chi_Minh` (UTC+7): `departureFrom` is inclusive from 00:00 local,
   `departureTo` is inclusive through 23:59:59.999 local.
6. **All booking statuses are listed** (`PendingPayment`, `Confirmed`, `Cancelled`, `Completed`,
   `Expired`); the optional `status` filter narrows them. Enum values follow the BE enums
   (`BookingStatus`, `PaymentStatus`), serialized as in `GetPlatformBookingsQuery`.

## Traveler information policy (confirmed)

| Source | Returned | Never returned |
|---|---|---|
| Booker (`Users`, via `Booking.TravelerUserId`) | `userId`, `fullName`, `email`, `phoneNumber` (phone only on the detail endpoint) | password hash, status, any other user column |
| `BookingParticipant` | `fullName`, `participantType` | `phoneNumber`, `email`, `nationality`, `idDocumentType`, `idDocumentNumber` (encrypted or not) |
| `PaymentTransaction` | `gateway`, `gatewayTransactionRef`, `amount`, `status`, `succeededAtUtc` | `rawResponse`, `checkoutUrl`, `qrCode`, `paymentLinkId`, `orderCode` |

The participant restriction is a whitelist in the response mapper: new columns added to
`BookingParticipant` later must not leak by default.

## Check-in derivation (confirmed)

Per booking, using its `Ticket` rows (UC-29 issues one ticket per booking, but the rule does not
depend on that):

- `checkInStatus = "CheckedIn"` when any ticket has `Status == Used` **or** `CheckedInAtUtc != null`;
  otherwise `"NotCheckedIn"` (including bookings with no ticket, or with `Cancelled`/`Expired` tickets).
- `checkedInAtUtc` = the earliest non-null `CheckedInAtUtc` among the tickets, else `null`.

Until UC-43 exists, no code path sets these fields, so every booking reports `NotCheckedIn`. The
contract is final; only the data becomes live when UC-43 lands.

## API Contracts

Both endpoints live in a new `OperatorBookingsController` (`[Authorize(Roles = nameof(UserRole.TourOperator))]`,
route `api/v1/operator/bookings`), thin: bind → `Sender.Send` → `Ok(result.Value)` or `HandleFailure(result)`.
No response envelope (AGENTS §2.3). Ids are strings, as in `PlatformBookingListItemDto`.

### 1. `GET /api/v1/operator/bookings`

Query parameters (all optional):

| Name | Type | Notes |
|---|---|---|
| `tourId` | long | must be > 0 and owned by the operator, else 403 |
| `scheduleId` | long | must be > 0 and belong to an owned tour, else 403 |
| `status` | `BookingStatus` | |
| `paymentStatus` | `PaymentStatus` | |
| `departureFrom` | `DateOnly` (`YYYY-MM-DD`) | schedule `StartAtUtc`, Vietnam time, inclusive |
| `departureTo` | `DateOnly` (`YYYY-MM-DD`) | inclusive; must be ≥ `departureFrom` |
| `keyword` | string ≤ 200 | case-insensitive contains on booking code, booker full name, booker email; `%`, `_`, `[`, `\` escaped as in `GetPlatformBookingsQueryHandler` |
| `pageNumber` | int ≥ 1 | default 1 |
| `pageSize` | int 1..100 | default 20 |

Constants (`DefaultPageSize`, `MaximumPageSize`, `MaximumKeywordLength`) live on
`GetOperatorBookingsQuery` and are referenced by the validator and tests (AGENTS §4.1).

Ordering: `BookedAtUtc` descending, then `Id` descending (stable). No client-selectable sort.

Response `200 OK`:

```json
{
  "pageNumber": 1,
  "pageSize": 20,
  "totalCount": 37,
  "totalPages": 2,
  "summary": {
    "totalBookings": 37,
    "totalParticipants": 112,
    "totalConfirmedAmount": 98400000.00
  },
  "items": [
    {
      "bookingId": "1201",
      "bookingCode": "BK-20261020-0007",
      "tour": { "tourId": "101", "title": "Ba Na Hills full-day tour" },
      "schedule": { "scheduleId": "311", "startAtUtc": "2026-11-02T00:00:00Z", "endAtUtc": "2026-11-02T10:00:00Z" },
      "booker": { "userId": "55", "fullName": "Nguyen Van A", "email": "a@example.com" },
      "quantity": 3,
      "totalAmount": 2400000.00,
      "discountAmount": 100000.00,
      "status": "Confirmed",
      "paymentStatus": "Paid",
      "checkInStatus": "NotCheckedIn",
      "checkedInAtUtc": null,
      "bookedAtUtc": "2026-10-20T03:15:00Z"
    }
  ]
}
```

`summary` is computed over the **whole filtered set**, not the current page:
- `totalBookings` = every booking matching the filters, whatever its status (equals `totalCount`).
- `totalParticipants` = sum of `Booking.Quantity` over matching bookings with status `Confirmed` or
  `Completed` only.
- `totalConfirmedAmount` = sum of `Booking.TotalAmount` over matching bookings with status `Confirmed`
  or `Completed` only.

`totalParticipants` and `totalConfirmedAmount` share one basis so the operator's headcount reflects the
people who will actually travel.

An empty result is `200 OK` with `items: []`, `totalCount: 0`, zeroed summary (the FE shows MSG128).

### 2. `GET /api/v1/operator/bookings/{bookingId:long}`

Response `200 OK`:

```json
{
  "bookingId": "1201",
  "bookingCode": "BK-20261020-0007",
  "status": "Confirmed",
  "paymentStatus": "Paid",
  "bookedAtUtc": "2026-10-20T03:15:00Z",
  "cancelledAtUtc": null,
  "cancelReason": null,
  "tour": { "tourId": "101", "title": "Ba Na Hills full-day tour" },
  "schedule": {
    "scheduleId": "311",
    "startAtUtc": "2026-11-02T00:00:00Z",
    "endAtUtc": "2026-11-02T10:00:00Z",
    "meetingPoint": "Da Nang Cruise Port"
  },
  "booker": { "userId": "55", "fullName": "Nguyen Van A", "email": "a@example.com", "phoneNumber": "+84901234567" },
  "quantity": 3,
  "participants": [
    { "fullName": "Nguyen Van A", "participantType": "Adult" },
    { "fullName": "Nguyen Thi B", "participantType": "Child" }
  ],
  "ticket": {
    "ticketCode": "TK-0000001201",
    "status": "Valid",
    "issuedAtUtc": "2026-10-20T03:16:00Z",
    "expiresAtUtc": null
  },
  "checkInStatus": "NotCheckedIn",
  "checkedInAtUtc": null,
  "payment": {
    "gateway": "PayOS",
    "gatewayTransactionRef": "FT26102012345",
    "status": "Success",
    "amount": 2400000.00,
    "succeededAtUtc": "2026-10-20T03:15:40Z"
  },
  "paidAmount": 2400000.00,
  "subtotalAmount": 2500000.00,
  "discountAmount": 100000.00,
  "totalAmount": 2400000.00,
  "coupon": { "code": "SUMMER10", "name": "Summer 10%", "discountAmount": 100000.00 },
  "canCancel": true,
  "cancelBlockedReason": null
}
```

Field rules:
- `ticket` is `null` when the booking has no ticket. When several tickets exist, return the most
  recently issued one; `checkInStatus` / `checkedInAtUtc` still follow the all-tickets rule above.
- `payment` is the latest `PaymentTransaction` of the booking with `TransactionType == Payment` and
  `Status == Success`; `null` when none. This is the same evidence rule as
  `BookingCancellationPaymentVerifier` minus the amount-equality condition (a display read must not
  hide a payment that disagrees with the booking total).
- `paidAmount` = `payment.amount` when `payment` is present, otherwise `0`. Refunds are **not**
  subtracted; refund progress stays on the existing refund endpoint.
- `coupon` is `null` when `Booking.VoucherId` is null; otherwise `Voucher.Code`, `Voucher.Name`,
  and `Booking.DiscountAmount`.
- `subtotalAmount` = `Booking.SubtotalAmount` (`TotalAmount + DiscountAmount`).
- `canCancel` mirrors the UC-41 rule for a Tour Operator exactly: `Status ∈ {PendingPayment,
  Confirmed}` **and** no ticket with `Status == Used`. `cancelBlockedReason` is `null` when
  `canCancel` is true, otherwise `InvalidStatus` (terminal status) or `CheckedIn` (used ticket), as
  constants. To avoid two sources of truth, this rule is extracted into one shared helper that
  `CancelBookingCommandHandler` and `GetBookingCancellationPreviewQueryHandler` also call, with their
  behaviour unchanged (covered by their existing tests).
- **No `cancellationWindowExpired` field.** UC-41 as built (`specs/UC-41-spec.md`,
  `CancelBookingCommandHandler`) applies no time window to a Tour Operator: only the Traveler is
  limited (less than 24 h before departure → `window_closed`). The cancel button therefore depends only
  on `canCancel` / `cancelBlockedReason`. If the team later defines an operator deadline, it is written
  into UC-41 first and then exposed here through the same shared helper. The FE (later step) switches
  to `canCancel` + `cancelBlockedReason` and drops its mock `cancellationWindowExpired`.

## Error & Validation Handling

| Condition | HTTP | Code | SRS |
|---|---|---|---|
| Unauthenticated | 401 | existing auth middleware | — |
| Role is not `TourOperator`, or application not approved, or account not Active | 403 | `operator_tour.access_required` (reuse) | MSG126 |
| `tourId`/`scheduleId` ≤ 0, `pageNumber` < 1, `pageSize` outside 1..100, keyword > 200 chars | 400 | `operator_booking.invalid_filter` | — |
| `departureFrom > departureTo` | 400 | `operator_booking.invalid_date_range` | MSG31 (see note below) |
| `tourId` / `scheduleId` exists but is not owned by the operator | 403 | `operator_booking.forbidden` | MSG126 / BR-105 |
| `tourId` / `scheduleId` does not exist | 403 | `operator_booking.forbidden` | MSG126 (identical to "not owned", so a probe cannot tell the two apart) |
| Detail: booking does not exist | 404 | `operator_booking.not_found` | MSG126 |
| Detail: booking exists on another operator's tour | 403 | `operator_booking.forbidden` | MSG126 / BR-105 |
| Unexpected failure | 500 | existing handler | MSG127 |

Notes:
- **MSG31, not MSG29.** SRS 3.8.4.1 names MSG29 for an invalid date range, but in the SRS message catalogue MSG29 is the
  invalid latitude/longitude text and MSG31 ("End date must be on or after the start date.") is the date-range
  message. The FE (UC-40 web spec, D1) and the approved UC-44 spec use MSG31, so this contract does too.
- The 403-for-foreign / 404-for-missing split follows the precedent of UC-41 and UC-44. Booking ids are
  already distinguishable this way through UC-41, so this adds no new disclosure.
- New error codes are added to `OperatorBookingErrorCodes` and **must be added to the status map in
  `ApiControllerBase.HandleFailure`**. An unmapped code falls through to 400, which would silently
  turn `forbidden` (403) and `not_found` (404) into 400, so all new codes are mapped explicitly.
  `invalid_filter` comes from the FluentValidation validator (400 `ValidationProblemDetails` whose
  `errorCode` extension is the rule's `WithErrorCode` value, not passing through `HandleFailure`).
  `invalid_date_range` is returned by the handler as a `Result` failure, like
  `GetPlatformBookingsQueryHandler`, and is mapped to 400 in `HandleFailure`.

## Implementation Notes

Files (Application layer, `Features/Operator/Bookings/`):
- `Common/OperatorBookingErrorCodes.cs`, `Common/OperatorBookingAccess.cs` (approved-operator check,
  shared by both handlers), `Common/BookingCheckIn.cs` (the check-in derivation),
  `Common/BookingCancelEligibility.cs` (the shared `canCancel` rule).
- `List/GetOperatorBookingsQuery.cs`, `...Handler.cs`, `...Validator.cs`.
- `Get/GetOperatorBookingQuery.cs`, `...Handler.cs`.
- Presentation: `Controllers/V1/OperatorBookingsController.cs`; `HandleFailure` mappings.

Query design:
- `AsNoTracking()` throughout (pure reads, nothing modified).
- Scope with `booking.TourSchedule.Tour.OperatorUserId == operatorId` **before** any other predicate.
- Projection with `Select` into page rows; no loading of full entity graphs, no `Include` of
  participants on the list endpoint. Check-in status is derived **in memory** from a light ticket
  snapshot (booking id, status, check-in timestamp) loaded with one `IN (...)` lookup for the page's
  booking ids only (at most `MaximumPageSize`), so SQL translation never depends on `Any`/`Min` over
  the value-converted check-in column (decided on 2026-10-27 because SQL Server is not available to
  verify translation on the development machine).
- Summary aggregates run on the filtered `IQueryable` (not on the page): a count plus two nullable
  `Sum` queries over the `Confirmed`/`Completed` subset.
- Booker data comes from `dbContext.Users`, as in `GetPlatformBookingsQueryHandler`.
- Existing index `IX_Bookings_TourSchedule(tour_schedule_id, status)` serves the join path. **No
  migration** is planned; if verification on the Azure UAT data shows a poor plan, a separate
  additive index migration will be proposed rather than assumed here.

## Acceptance Criteria

1. An approved operator receives only bookings on their own tours; bookings of another operator are
   absent from `items` **and** from `summary` / `totalCount`.
2. A non-operator role, an unapproved operator, or an inactive account gets 403.
3. Detail of another operator's booking returns 403; of a non-existent id returns 404; neither leaks
   any field of the booking.
4. Filters `tourId`, `scheduleId`, `status`, `paymentStatus`, departure range and `keyword` work alone
   and combined; the departure range respects Vietnam-time day boundaries (e.g. a schedule starting
   `2026-11-01T17:30:00Z` falls on `2026-11-02`).
5. `keyword` matches booking code, booker full name and booker email; `%`/`_` in the keyword are
   treated literally.
6. Pagination returns correct `totalCount`/`totalPages`; page beyond the end returns empty `items`;
   `pageSize` > 100 is rejected with 400.
7. `checkInStatus`/`checkedInAtUtc` follow the derivation rule for: no ticket, `Valid` ticket,
   `Used` ticket, `Used` with null `CheckedInAtUtc`, non-null `CheckedInAtUtc` with non-`Used` status,
   `Cancelled` ticket.
8. Responses never contain participant phone/email/nationality/ID-document fields, payment
   `rawResponse`/`checkoutUrl`/`qrCode`/`paymentLinkId`/`orderCode`, or booker fields beyond the table
   above (asserted on the serialized JSON).
9. `paidAmount`, `coupon`, `payment`, `canCancel`, `cancelBlockedReason` are correct for: unpaid
   pending, paid confirmed, cancelled, expired, with and without coupon, with a used ticket.
10. The refactored cancel-eligibility helper leaves UC-41 behaviour and its existing tests unchanged.
11. `dotnet build` and `dotnet test` pass; no migration, no schema change.

## Test Plan (TDD, Red → Green)

Unit tests under `tests/TripMate.Application.UnitTests/Features/Operator/Bookings/` using the
existing in-memory/`IApplicationDbContext` test fixtures: isolation between two operators,
authorization matrix, every filter and its boundary, keyword escaping, pagination and summary,
check-in derivation table, payment/coupon/`paidAmount` mapping, `canCancel` table, the
no-sensitive-field assertion, and validator rules. Controller-level tests for the status mapping of
each new error code.
