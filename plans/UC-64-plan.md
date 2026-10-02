# UC-64 View Payout Details Backend Implementation Plan

Status: **Draft — contingent on approval of `specs/UC-64-spec.md`.** Tasks are atomic and
sequenced; each ends green before the next starts (TDD: failing test first).

Branch: `feature/linhnv-view-payout-details` — **stacked on** `feature/linhnv-view-payout-records`
(UC-63, `cd41d94`): it inherits the `Payout` mapping, `AdminPayoutsController`, the
`PayoutErrorCodes`, and the FE list screen. Baseline `dotnet test` verified green on 2026-10-01.

## Task 1 — Read-only booking/financial mappings

New entities + `IEntityTypeConfiguration` (database-first, no migration):

- `PayoutItem` → `payment.PayoutItems` (`payout_id` FK → `Payout` with `PayoutItems` collection
  nav, `booking_id` FK → `Booking`, `amount` `HasPrecision(12,2)`).
- `Booking` → `commerce.Bookings` (`booking_code` VARCHAR(30), `tour_schedule_id` FK →
  `TourSchedule`, `traveler_user_id`, `total_amount`, `status`, `payment_status`, `booked_at`,
  `cancelled_at`; only fields this UC reads).
- `PaymentTransaction` → `payment.PaymentTransactions` (`booking_id`, `transaction_type`,
  `status`, `amount`, timestamps).
- `Refund` → `payment.Refunds` (`booking_id`, `amount`, `status`, timestamps).

Wire `DbSet<T>` into `IApplicationDbContext`, `ApplicationDbContext`, `TestDbContext`,
`TestApiDbContext`, and the password-reset fake. Persistence model tests for all four
(table/schema/columns/precision/converter/FKs). **No SQL file touched.**

DoD: build + persistence tests green.

## Task 2 — Query, DTOs, error codes

`src/TripMate.Application/Features/Admin/Payouts/GetDetails/`:

- `GetPayoutDetailsQuery(long PayoutId)`; validator: positive integer only (≤ 0 → 400 via the
  shared route/validator contract; malformed segments never match `{id:long}` → framework 404).
- `PayoutDetailsDto(PayoutId, PayoutCode, Operator, PeriodStart, PeriodEnd, GrossRevenue,
  CommissionRate, CommissionAmount, NetAmount, RequestedAtUtc, Status, Bookings[])`;
  `PayoutBookingDto(BookingId, BookingCode, TourName, PaidAmount, RefundedAmount, NetAmount)`.
- Reuse `PayoutErrorCodes.Forbidden`; no new codes — missing record uses the locked MSG128 text
  (D3).

Verify: validator unit tests green.

## Task 3 — Handler assembly (TDD)

`GetPayoutDetailsQueryHandler`:

1. Administrator role check → `Forbidden`.
2. Load payout (AsNoTracking) with `Operator.CompanyName` + `CommissionRate`; missing →
   `Failure(not-found)` → 404 with MSG128 text (D3). No audit row on 404.
3. BR-130 audit (D8): `AuditLog.CreateRecordedOutcome(actor, AuditActionTypes.ViewPayoutDetails,
   AuditEntityTypes.Payout, id, now, AuditOutcome.Success)` — new constants — then
   `SaveChangesAsync`; **fail-closed** (audit failure → exception → 500).
4. Assemble bookings: `PayoutItems` of the payout joined to `Bookings` (ordered by
   `booking_code`), tour name via `TourSchedule.Tour.Title` (null-safe), `paidAmount` =
   correlated sum of successful `Payment` transactions, `refundedAmount` = correlated sum of
   `Processed` refunds, `netAmount` = `PayoutItems.amount`.

Unit tests first: forbidden; unknown id → not-found (MSG128 text, no audit row); audit row
constructed correctly and persisted exactly once; audit persistence failure propagates
(fail-closed); assembly boundaries — empty bookings, booking without schedule ("Not available"
tour name), multiple payments on one booking, refund excluded until `Processed`.

## Task 4 — Controller endpoint + OpenAPI

`AdminPayoutsController` (already on this branch from UC-63): add
`[HttpGet("{id:long}")]` with `ProducesResponseType` for `200/400/401/403/404/500`;
`HandleFailure` mapping for the not-found code → 404. OpenAPI test asserting the path
parameter, DTO schemas (`PayoutDetailsDto`, `PayoutBookingDto`), and error responses.

## Task 5 — Integration tests

- HTTP (`TripMateApiFactory`): `401`, `403`, `400` (id ≤ 0), `404` malformed segment (`1abc`),
  `404` unknown id (assert MSG128 text), `200` happy path (data seeded via SQL).
- SQL Server gated (`Category=SqlServer`): seed operator + payout + `PayoutItems` + bookings
  (one with schedule/tour, one without; multiple payments incl. a `Failed` one; refunds incl.
  a `Pending` one) — assert joins, per-booking sums, ordering, audit row, and UTC/CR-07
  serialization.

## Task 6 — Verification and review

1. `dotnet format TripMate.slnx --no-restore --verify-no-changes`
2. `dotnet build TripMate.slnx -c Release` (0 warnings/errors)
3. `dotnet test TripMate.slnx -c Release` (record SQL-skipped vs SQL-run honestly)
4. Spec-compliance review, then code-quality review (severity-based; Critical blocks).

Non-goals guard: no settlement action, no export, no bank-account fields, no schema change, no
recalculation (PC-02).
