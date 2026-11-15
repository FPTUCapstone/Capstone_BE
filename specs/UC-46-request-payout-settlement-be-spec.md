# UC-46 — Request Payout Settlement (Backend Specification)

## Status

Draft v3 — pending developer/PO approval. Supersedes v2. Built on the approved UC-44 BE spec (revenue formula and reporting dates), the UC-45 BE spec (immutable snapshot and audit pattern), SRS §3.8.5.3, and the approved UC-64/73/74 staff review flow. Items marked **OPEN** need a value or confirmation before implementation.

## Preconditions (external dependencies, not built by this spec)

UC-46 reads `Booking.Status == Completed` and relies on a 3-day post-completion refund hold. Neither exists yet in the codebase as of this draft:

1. **No automatic Completed transition exists.** `BookingStatus.Completed` is defined and already queried by UC-44/UC-45/UC-46, but nothing in the codebase ever sets a booking to it — there is no scheduled job or UC-43 check-in handler that does this. Confirmed by a full-repository search. Until this ships, `OperatorPayoutEligibilityReader` finds zero eligible bookings on real data, even though the code is correct.
2. **Refund is currently blocked entirely once a booking is no longer `PendingPayment`/`Confirmed`.** `CancelBookingCommandHandler` (the only code path that creates a `Refund`) rejects cancellation once `Status` is anything else, or once any ticket is `Used`. The planned design — auto-complete on trip end, then allow refund for 3 more days, then lock — requires extending that guard to also accept `Completed` bookings within the hold window. This is not yet implemented.

Both are planned future work (confirmed 2026-10-22): completion becomes automatic when a trip ends, and refund stays possible for 3 days afterward. UC-46's `refund_hold_days = 3` default (OPEN-1) is written for that design and needs no change; it simply has no real data to act on until both land. UC-46 can be built, reviewed, and merged now — it will return empty settlement-month lists against production data until dependency 1 ships, which is expected, not a bug.

## Scope

An approved Tour Operator can request settlement of the net amount of one settlement month, cancel a request still awaiting review, and see their payout history. Staff review (UC-64, UC-73, UC-74), Administrator settlement (UC-65), and gateway transfer are downstream and are specified separately.

Included:
- Listing settlement months with payable amounts and request eligibility.
- Request creation with an immutable snapshot of bookings, payments, refunds, rates, adjustments applied, and beneficiary bank data.
- Payout history with the cancel action where allowed.
- Cancellation while the request is `PendingReview`.
- Read (masked) and update of the beneficiary bank account.

Excluded:
- Any amount computed by the client.
- Payout detail endpoint. The SRS does not define an operator detail screen for UC-46. Snapshot detail is kept for accounting and audit only.
- Staff/Administrator transitions, gateway integration, and bank transfer.
- Mobile. Web-first release; see OPEN-6.

## Business rules

1. **BR-105 Actor isolation.** Every month, payout, item, adjustment, and bank record is scoped to the authenticated operator. A foreign or unknown payout code returns 404 with the same body as an unknown code.
2. **BR-109 Revenue base (UC-44).** UC-44 reporting uses `Confirmed` and `Completed` bookings with verified payment, dated by successful-payment date. This is a reporting view and is not a settlement base.
3. **BR-111 Settlement base (UC-46).** A booking enters a payout only when all of the following hold:
   - Status is `Completed`.
   - `payoutEligibleAtUtc <= now`.
   - It is not already in an active or settled payout (see BR-113).
4. **Payout eligibility (OPEN-1, decided).** Each booking has an immutable `payoutEligibleAtUtc`, snapshotted at booking creation as the tour schedule's end time plus `refund_hold_days` (3). The payout settlement month of a booking is the Vietnam-time month of `payoutEligibleAtUtc`, not the payment month. See **Preconditions** above: this rule has no real data to act on until the Completed auto-transition and the post-completion refund window both ship.
5. **BR-112 Payable formula.** Shared calculator with UC-44 (see Calculation). Only the booking set changes: UC-44 passes its reporting set, UC-46 passes its eligible set. Refunds attach to the booking's payments and commission rates.
6. **Refund bound.** For each booking, `Σ processed refunds ≤ Σ payments`. A violation is a data error: refund creation must reject it, and the calculator treats a violation as a fault, not a negative payable.
7. **No negative payable.** A month whose payable net is below zero is not requestable (`PAYOUT_NOT_REQUESTABLE`) and no payout is recorded with a negative amount.
8. **Late refunds are adjustments.** A refund processed after the payout snapshot's cut-off (the request time of the payout that included its booking, or after `payoutEligibleAtUtc` for an unsubmitted booking) is never added to an existing snapshot. It creates a **payout adjustment** (negative amount) against the operator. The next payout request for that operator deducts the outstanding adjustments and records them in its snapshot. Adjustments never make a payout negative: any excess carries forward.
9. **BR-113 One active request per settlement month.** At most one active payout per operator and month. Active statuses are `PendingReview`, `ApprovedForSettlement`, `SettlementProcessing`, and `SettlementFailed`. `SettlementFailed` is retryable and blocks a new request for that month until Staff/Administrator resolves it. Only `Rejected` and `Cancelled` reopen the month.
10. **BR-114 Captured rates.** Commission uses the `CommissionRateApplied` snapshotted on each successful payment. Later rate changes never alter a submitted payout.
11. **Immutable snapshot.** A submitted payout is a complete accounting record. Only status and review/settlement fields change, and only through the lifecycle below.
12. **Minimum payout (OPEN-5).** `payableNetAmount >= minimum` is requestable, including equality. The minimum is read from `SystemConfigs`. A change applies only to requests created afterwards; submitted snapshots keep the minimum they were checked against.
13. **CR-07.** Dates are `YYYY-MM-DD` in the API and interpreted in `Asia/Ho_Chi_Minh`. Amounts are VND integers or `DECIMAL(14,2)` with no fractional VND in the minimum. No floating-point arithmetic.
14. **Audit.** Request creation, cancellation, adjustment creation and application, and bank-detail changes write an audit event with actor, payout code or month, amounts, and outcome. Audit never contains the full account number.

## Calculation

Input: a booking set `S` and the operator's adjustments `A` outstanding at the cut-off.

For each booking `b` in `S`:
- `paid_b` = sum of the canonical successful payment per booking (earliest `SucceededAtUtc`), as UC-44.
- `discount_b` = booking discount, for reporting.
- `refunds_b` = sum of processed refunds of `b` with `ProcessedAtUtc <= cut-off`.
- `commission_b` = Σ over its payments `round(amount_p × rate_p / 100, 2)`, minus Σ over its refunds `round(refund_r × rate_b_p / 100, 2)`, where the refund uses the rate of the payment it reverses.

Totals:
- `PaymentsReceived` = Σ `paid_b`.
- `DiscountsGiven` = Σ `discount_b`.
- `RefundedAmount` = Σ `refunds_b`.
- `NetSales` = `PaymentsReceived − RefundedAmount`.
- `CommissionAmount` = Σ `commission_b`.
- `PayableNetBeforeAdjustments` = `NetSales − CommissionAmount`.
- `AdjustmentsApplied` = Σ outstanding adjustments taken into this payout, capped so that `PayableNet = PayableNetBeforeAdjustments − AdjustmentsApplied ≥ 0`. Any remainder stays outstanding.
- `PayableNet` = `PayableNetBeforeAdjustments − AdjustmentsApplied`.

UC-44 calls the same calculator with its reporting set and does not apply adjustments. The period list, request preview, and snapshot write call the same function and cannot diverge.

**Implementation note.** The current `OperatorRevenueAggregator` (lines 40–45) refunds by booking regardless of processing time ("Option B"). This does not match the approved UC-44 BR-112. It must be aligned to the calculator above before UC-46 reuses it. This is a code change, not a spec change.

## Settlement month

- A settlement month `M` (Vietnam calendar month) contains the bookings whose `payoutEligibleAtUtc` falls in `M`.
- A month is **closed** when the current time is at or after the start of `M+1` (Vietnam time). All its bookings are then eligible.
- A month is requestable when it is closed, has at least one eligible booking, the payable net meets the minimum, and no active payout exists for it.

## OPEN decisions

**OPEN-1 — Eligibility hold (decided 2026-10-22).** `payoutEligibleAtUtc` = schedule end + `refund_hold_days` (3, global, from `SystemConfigs`), snapshotted at booking creation. This matches the confirmed future design: a trip auto-completes on trip end, stays refundable for 3 more days, then locks. Depends on the two Preconditions above, neither of which exists yet — see that section for what must ship first. No further PO input needed on the value itself.

**OPEN-2 — Formula authority.** Adopt the calculator above as the single shared implementation. UC-44 remains the reporting authority for display. The scope matrix's `SRS_FINANCIAL_FORMULA_DECISION_PENDING` flag must be closed by PO before release.

**OPEN-3 — Late refund policy.** The adjustment mechanism in rule 8 is a recommendation. PO must confirm that exceptional refunds (duplicate verified payment, emergency cancellation per BR-132) are handled by adjustment, not by reopening a settled payout.

**OPEN-4 — Message catalogue.** The payout-specific outcomes use error codes only in this spec. The locked SRS 5.3 list already uses `MSG131`–`MSG136` for other outcomes (account lock/unlock, booking cancellation, POI). Do not reuse them. New message numbers must be assigned by PO after reconciliation with the locked list and pending proposals (UC-64/73/74, UC-68), and only then added to the catalogue.

**OPEN-5 — Minimum payout (decided).** The minimum is a `SystemConfigs` value read by key `payout.minimum_amount_vnd`, inclusive. Production is seeded with 5,000,000 VND. Development and staging environments may be seeded with a lower value through configuration so that developers and students can test without real funds. The lower value is set by seed or database update only. No code path may bypass the minimum based on environment. The SRS places this in the Administrator's algorithm configuration; no admin UI is added by UC-46.

**OPEN-6 — Mobile.** Web-first release for UC-46, consistent with UC-44 and UC-45. The scope matrix is annotated to show Mobile as deferred, pending an SRS platform amendment. This is a documented scope decision, not an omission.

## Data model

Schema changes are database-first (`database/`), with no EF migration.

**`dbo.Bookings` (new column)**
- `payout_eligible_at_utc` (not null after migration), set at creation, never updated.

**`payment.Payouts` (header)**
- `payout_id`, `payout_code` (`PO-{YYYYMM}-{payout_id:D6}`, unique, assigned in the creating transaction).
- `operator_user_id`, `settlement_month` (first day of month), `cut_off_at_utc`.
- `payments_received`, `discounts_given`, `refunded_amount`, `net_sales`, `commission_amount`, `payable_net_before_adjustments`, `adjustments_applied`, `payable_net_amount`.
- `booking_count`, `participant_count`, `minimum_amount_checked`.
- Beneficiary snapshot: `bank_name`, `account_holder_name`, `account_number_masked`, `account_number_protected`.
- `status`, `requested_at`, `reviewed_by`, `reviewed_at`, `rejection_reason`, `settlement_authorized_by`, `settlement_authorized_at`, `gateway_reference`, `gateway_failure_reason`, `gateway_attempt_count`, `last_gateway_attempt_at`, `cancelled_at`.
- Checks: `net_sales = payments_received − refunded_amount`; `payable_net_amount = payable_net_before_adjustments − adjustments_applied`; `payable_net_amount >= 0`.

**`payment.PayoutItems` (per booking and payment)**
- `payout_item_id`, `payout_id`, `booking_id`, `payment_transaction_id`.
- `paid_amount`, `discount_amount`, `commission_rate_applied`, `commission_amount`.
- Unique `(payout_id, payment_transaction_id)`.

**`payment.PayoutRefundItems` (per refund)**
- `payout_refund_item_id`, `payout_id`, `refund_id`, `booking_id`, `refunded_amount`, `commission_rate_applied`, `commission_reduction`.
- Unique `(payout_id, refund_id)`.

**`payment.PayoutAdjustments` (ledger)**
- `adjustment_id`, `operator_user_id`, `source_refund_id` (unique), `amount` (negative), `created_at`.
- `applied_payout_id` (nullable), `applied_amount` (nullable), `applied_at`.
- A row is applied at most once. Partial application is allowed; the remainder stays outstanding.

Items, adjustment applications, and the header are inserted in one transaction.

## Lifecycle

| From | To | Actor | Notes |
|---|---|---|---|
| — | `PendingReview` | Operator (request) | Creates snapshot; month becomes active; applied adjustments are marked. |
| `PendingReview` | `Cancelled` | Operator (cancel) | Month reopens; applied adjustments return to outstanding. |
| `PendingReview` | `Rejected` | Staff (UC-74) | Requires reason; month reopens; adjustments return to outstanding. |
| `PendingReview` | `ApprovedForSettlement` | Staff (UC-73) | Still active. |
| `ApprovedForSettlement` | `SettlementProcessing` | Administrator (UC-65) | Still active. |
| `SettlementProcessing` | `Settled` | Gateway result | Terminal. Adjustments are final. |
| `SettlementProcessing` | `SettlementFailed` | Gateway result | Still active; retryable. |

The operator acts only on `PendingReview`. The status CHECK constraint must add `Cancelled`, and the active unique index must include `SettlementFailed`.

## API contract

All endpoints require an active account with `Role: TourOperator` and `ApprovalStatus: Approved`.

### `GET /api/v1/operator/payouts/settlement-months?year=2026&limit=…`

Months of the requested year (or the most recent months when `year` is omitted) that contain bookings with `payoutEligibleAtUtc`.

Each item: `year`, `month`, `monthStatus` (`Open` | `Closed`), `eligibleBookingCount`, `paymentsReceived`, `discountsGiven`, `refundedAmount`, `netSales`, `commissionAmount`, `adjustmentsOutstanding`, `payableNetAmount`, `requestable`, `blockReason` (`null` | `MONTH_NOT_CLOSED` | `NO_ELIGIBLE_BOOKING` | `NOT_REQUESTABLE_NEGATIVE` | `ACTIVE_PAYOUT_EXISTS` | `BELOW_MINIMUM_PAYOUT`), `activePayoutCode`.

### `POST /api/v1/operator/payouts`

Request: `{ "year": 2026, "month": 9 }`. The backend derives the month boundaries. Free-form start and end dates are not accepted.

- `201 Created`: payout summary with `payoutCode`, `status: PendingReview`, and the amounts above, plus masked bank data.

### `GET /api/v1/operator/payouts`

Paged history, newest first: `payoutCode`, `year`, `month`, `payableNetAmount`, `status`, `requestedAt`, `settledAt` (nullable), `rejectionReason` (nullable), `cancellable`.

No detail endpoint in UC-46 (see Scope).

### `POST /api/v1/operator/payouts/{payoutCode}/cancel`

Allowed only from `PendingReview`. Sets `Cancelled`, records `cancelled_at`, returns the adjustments to outstanding, and writes audit. Non-cancellable status returns 409 with the current status. Unknown or foreign code returns 404.

### `GET /api/v1/operator/payouts/bank-account`

Returns `accountHolderName`, `bankName`, `maskedAccountNumber`, `updatedAt`. The full number is never returned.

### `PUT /api/v1/operator/payouts/bank-account`

Body: `accountHolderName`, `bankName`, `accountNumber`. All three are required. The number is stored protected (`IPiiProtector`); only its masked form is kept for display. Applies to payouts requested afterwards.

## Error contract

Error codes are stable and internal. Display text is owned by the FE.

| Condition | HTTP | `errorCode` | Message |
|---|---|---|---|
| Not authenticated | 401 | `AUTH_REQUIRED` | — |
| Not a Tour Operator, or application not approved | 403 | `OPERATOR_NOT_APPROVED` | MSG126 |
| Missing or empty required field | 400 | `VALIDATION_REQUIRED_FIELD` | MSG01 |
| Invalid month (not 1–12, or future-invalid) | 400 | `INVALID_SETTLEMENT_MONTH` | pending OPEN-4 |
| Month not closed | 422 | `MONTH_NOT_CLOSED` | pending OPEN-4 |
| No eligible booking in month | 422 | `NO_ELIGIBLE_BOOKING` | MSG128 |
| Payable net negative | 422 | `PAYOUT_NOT_REQUESTABLE` | pending OPEN-4 |
| Another active payout exists for the month | 409 | `ACTIVE_PAYOUT_EXISTS` | pending OPEN-4 |
| Payable net below minimum | 422 | `BELOW_MINIMUM_PAYOUT` | pending OPEN-4 |
| Payout not found or foreign | 404 | `PAYOUT_NOT_FOUND` | MSG128 |
| Cancel from non-cancellable status | 409 | `PAYOUT_NOT_CANCELLABLE` | pending OPEN-4 |
| Cancel succeeded | 200 | — | MSG129 |
| Persistence or transient failure | 500 | `SYSTEM_ERROR` | MSG127 |

"Pending OPEN-4" means the code is returned and the display text is held as a placeholder until PO assigns message numbers. Do not use MSG29, MSG31, MSG118, MSG119, MSG120, or MSG131–MSG136 for these outcomes.

## Concurrency

- Request creation runs in a serializable transaction: recompute the month, check requestability, check active payouts, check minimum, apply adjustments, insert header, items, and adjustment applications.
- The filtered unique index on active statuses (`settlement_month`, `operator_user_id`) is the final guard. A unique violation maps to `ACTIVE_PAYOUT_EXISTS`, never 500.
- A booking can appear in at most one active payout: enforced by a unique constraint on the item's booking among active headers, or by the transactional check, documented in the plan.
- `payout_code` is unique; a conflict retries code assignment inside the same transaction.

## Audit and observability

- Audit events: `PAYOUT_REQUESTED`, `PAYOUT_CANCELLED`, `ADJUSTMENT_CREATED`, `ADJUSTMENT_APPLIED`, `BANK_ACCOUNT_UPDATED`. Each carries actor, payout code or month, amounts, and outcome. No account number.
- Metrics: requests created, rejected by reason, cancelled, outstanding adjustments count and total.

## Testing requirements

- **Eligibility:** booking before hold is excluded; booking with non-`Completed` status is excluded; month derived from eligibility, not payment month; Vietnam-time boundary at 23:59 and 00:00.
- **Formula:** payment-only month; refund in same month; refund after eligibility becomes adjustment; per-payment rate; full refund gives zero commission and never negative commission; refund exceeding payment is rejected.
- **Negative and minimum:** payable exactly at minimum is requestable; below minimum is `BELOW_MINIMUM_PAYOUT`; negative payable is `PAYOUT_NOT_REQUESTABLE`; adjustments carry forward when they exceed the payable.
- **Lifecycle:** cancel only from `PendingReview`; `SettlementFailed` blocks a new request; `Rejected` and `Cancelled` reopen the month and return adjustments.
- **Snapshot:** later refunds or rate changes do not alter a submitted payout; items sum to header totals.
- **Security:** traveler and unapproved operator get 401/403; foreign payout code returns 404; bank read never returns the full number; audit never contains it.
- **Concurrency:** two simultaneous requests for one month yield one active payout (SQL Server integration test).
- Each error row above is asserted by at least one test.

## Known limitations

- **MVP gap (accepted, to revisit):** UC-74 (Reject) does not exist yet. When built, it must call `PayoutAdjustment.Revert()` on every adjustment applied to the rejected payout, the same way `CancelPayoutCommandHandler` does — otherwise those amounts are permanently stuck.
- **Permanent, by design (UC-44 is final, out of scope to change):** UC-44's "Net Payout" (by payment month, Confirmed+Completed) and UC-46's "Payable Net" (by completion+hold month, Completed only) can show different numbers for the same booking in different months. UC-44 is not modified by this spec. Mitigation stays entirely inside UC-46: an "available to withdraw" summary on the Payout Settlement screen itself, with a tooltip noting the different basis, never a change to UC-44's labels or figures.
- **Permanent, accepted, out of scope:** UC-44 recomputes live from current booking status on every call, so a closed month's report can change if a booking is cancelled afterward. This is existing, shipped UC-44/45 behavior and is not touched here. A frozen record of any period already exists via UC-45 export (BR-110 immutable filter snapshot) — this is a pre-existing capability, not a new one. UC-46's own payout snapshots are immutable and do not have this problem.
