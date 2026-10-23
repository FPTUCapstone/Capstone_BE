# UC-46 — Request Payout Settlement (Backend Plan)

## Status

**Blocked on spec approval.** Do not start until `specs/UC-46-request-payout-settlement-be-spec.md` v3 is approved. OPEN-1 (refund hold = 3 days) is decided; OPEN-5 (minimum payout) and OPEN-4 (message numbers) still need confirmation. Task 0 can start once the spec is approved.

Code built from this plan is correct but inert against real data until the spec's **Preconditions** section ships: (1) an automatic Completed transition for bookings (no such job/handler exists yet), and (2) extending `CancelBookingCommandHandler` to allow a refund within the 3-day hold after completion (it currently rejects any booking that is not `PendingPayment`/`Confirmed`). Neither is part of this plan; both are separate follow-up work.

## Preconditions

- Branch `feature/linhnv-request-payout-settlement`, clean baseline, `dotnet build` and the unit suite green before Task 0.
- Spec v3 committed so the team reviews one version (see Task 8).

## Task 0 — Database changes (database-first, no EF migration)

- `dbo.Bookings`: add `payout_eligible_at_utc`. Backfill existing bookings from schedule end plus the OPEN-1 hold. Bookings that have not completed keep the value and are excluded until `Completed`.
- `payment.Payouts`: add `payout_code` (unique), `settlement_month`, `cut_off_at_utc`, and the amount columns from spec v3 (`payments_received`, `discounts_given`, `refunded_amount`, `net_sales`, `commission_amount`, `payable_net_before_adjustments`, `adjustments_applied`, `payable_net_amount`, `booking_count`, `participant_count`, `minimum_amount_checked`). Replace `CK_Payouts_NetAmount` with the spec checks. Add masked and protected bank columns.
- Status CHECK: add `Cancelled`. Active unique index: keyed by `(operator_user_id, settlement_month)` and filtered to `PendingReview`, `ApprovedForSettlement`, `SettlementProcessing`, `SettlementFailed`.
- `payment.PayoutItems`: add `payment_transaction_id`, `paid_amount`, `discount_amount`, `commission_rate_applied`, `commission_amount`; unique `(payout_id, payment_transaction_id)`.
- New `payment.PayoutRefundItems` and `payment.PayoutAdjustments` tables per spec.
- `dbo.OperatorProfiles`: `bank_name`, `account_holder_name`, `account_number_protected`, `account_number_masked`.
- `dbo.SystemConfigs`: seed `payout.minimum_amount_vnd` with the confirmed value (OPEN-5).

**Done when:** script runs on a clean database and on the existing seed; CHECK accepts `Cancelled`; the active index rejects a second active row for the same operator and month.

## Task 1 — Eligibility snapshot (RED → GREEN)

- Tests: booking creation sets `payout_eligible_at_utc = schedule end + hold`; the value is not changed by later updates; a booking not yet `Completed` is excluded from settlement regardless of the timestamp.
- Implement in the booking creation path. Keep the value a domain property set once.

**Done when:** tests pass; existing booking tests remain green.

## Task 2 — Shared calculator (RED → GREEN)

- Extract a calculator that takes a booking set and a cut-off, and returns the totals in spec v3 "Calculation". Pure logic, no database access inside.
- Refund bound check: a booking whose refunds exceed its payments is reported as a fault, never a negative payable.
- Rewire `OperatorRevenueAggregator` (currently Option B, lines 40–45) so UC-44 uses the period-refund rule of the approved UC-44 BR-112. This changes UC-44 figures for refunds that cross periods: update the affected UC-44 tests and state the change in the PR.

Tests:
- Payment-only and refund-in-same-period cases.
- Refund after cut-off is excluded from the snapshot.
- Per-payment rate with a rate change after payment.
- Full refund yields zero commission and never negative commission.
- Refund exceeding payment is a fault.
- The UC-44 summary and the UC-46 totals agree for the same booking set.

**Done when:** UC-44 and UC-46 call the same function, and the updated UC-44 tests pass.

## Task 3 — Adjustments ledger (RED → GREEN)

- When a refund is processed for a booking whose payout snapshot cut-off has passed (or whose booking is eligible but not yet in a payout after eligibility), create one `PayoutAdjustment` with the negative refund amount, unique per `source_refund_id`.
- A late refund for a booking already in a submitted payout also creates an adjustment; the snapshot is never changed.
- Request creation applies outstanding adjustments up to the payable amount; the remainder stays outstanding. Application is recorded with `applied_payout_id` and `applied_amount`.
- Cancel and reject return the applied amounts to outstanding.

Tests: one adjustment per refund (idempotent on repeated notification); partial application; carry-forward; return on cancel and reject; no double application under concurrent requests.

**Done when:** adjustment lifecycle tests pass and the snapshot is unchanged in all cases.

## Task 4 — Queries (RED → GREEN)

- `GetOperatorSettlementMonthsQuery` (by `year`, default current year): per month, eligible bookings, amounts from the calculator, outstanding adjustments, `requestable`, and `blockReason` (`MONTH_NOT_CLOSED`, `NO_ELIGIBLE_BOOKING`, `PAYOUT_NOT_REQUESTABLE`, `ACTIVE_PAYOUT_EXISTS`, `BELOW_MINIMUM_PAYOUT`).
- `GetOperatorPayoutHistoryQuery`: paged, newest first, `cancellable` only for `PendingReview`.
- Foreign operator sees the same empty result as an unknown ID (BR-105).

Tests: block reasons for each condition; month boundary in Vietnam time; history paging; actor isolation.

## Task 5 — Commands (RED → GREEN)

`RequestPayoutCommand`, `CancelPayoutCommand`, `GetBeneficiaryBankAccountQuery`, `UpsertBeneficiaryBankAccountCommand`, each with a validator.

Request handler, in a serializable transaction:
1. Validate `year` and `month` (`INVALID_SETTLEMENT_MONTH` on out-of-range).
2. Month must be closed (`MONTH_NOT_CLOSED`).
3. Recompute the month with the calculator; reject `NO_ELIGIBLE_BOOKING` and `PAYOUT_NOT_REQUESTABLE` (negative).
4. Check active payout (`ACTIVE_PAYOUT_EXISTS`) and minimum from `SystemConfigs` (`BELOW_MINIMUM_PAYOUT`, inclusive comparison).
5. Apply adjustments, insert header, items, refund items, adjustment applications, copy bank values.
6. Assign `payout_code` in the same transaction; unique violation on the active index maps to `ACTIVE_PAYOUT_EXISTS`.

Cancel: `PendingReview` → `Cancelled` with MSG129, returns adjustments. Other status → `PAYOUT_NOT_CANCELLABLE` (409).

Bank commands: `MSG01` on empty required fields; account number stored via `IPiiProtector`; response masked.

Tests: each row of the spec error table; concurrent requests yield one active payout; rollback leaves no partial rows.

## Task 6 — Controller and error mapping

- `src/TripMate.Api/Controllers/V1/OperatorPayoutsController.cs`: routes from the spec. `[Authorize(Roles = nameof(UserRole.TourOperator))]`.
- Error codes in `src/TripMate.Application/Features/Operator/Payouts/Common/OperatorPayoutErrorCodes.cs`, one constant per code in the spec table.
- Map in `ApiControllerBase.HandleFailure` to the statuses in the spec. Do not map to `MSG29`, `MSG118`, `MSG119`, `MSG120`, or MSG131–136, and do not reuse `OperatorTourErrorCodes.AccessRequired`.
- Messages with "pending OPEN-4" in the spec return the error code and no display text until PO assigns numbers.

Tests: integration tests for 401 (anonymous), 403 (traveler, unapproved operator), 404 (foreign code), and each mapped status.

## Task 7 — Verification

- `dotnet format TripMate.slnx --verify-no-changes` on touched files.
- `dotnet build --configuration Release`.
- Full unit suite and affected integration tests (SQL Server for the index and concurrency tests).
- Swagger manual pass: create request, cancel, re-request the same month.

**Done when:** all checks pass and each spec testing requirement maps to a named test in the PR description.

## Task 8 — Commit the spec and plan

Commit spec v3 and this plan to `feature/linhnv-request-payout-settlement` before any implementation, so the team reviews one version. Include the FE spec and plan commit in the same review.
