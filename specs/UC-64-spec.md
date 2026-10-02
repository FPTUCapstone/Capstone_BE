# UC-64 View Payout Details Backend Specification

## Status

**Proposed — pending developer approval.** Branch `feature/linhnv-view-payout-details` is
stacked on `feature/linhnv-view-payout-records` (UC-63 implementation, pushed `cd41d94`) —
this UC consumes UC-63's `Payout` mapping and enables its disabled [View Details] row action.
Implementation follows `plans/UC-64-plan.md` only after approval.

## Sources

- SRS §3.9.9.2 "View Payout Details" (read in full from Report 3 (2)): Administrator verifies
  the computation of one payout record against the underlying bookings before settlement.
  Read-only; the settlement action itself is §3.9.9.3 (UC-65).
- Section business rules: **BR-115** (Administrator role), **BR-109** (gross revenue comes from
  Confirmed/Completed bookings of the period), **BR-112** (net = gross − commission),
  **BR-114** (commission rate presented with the computed amount), **BR-79** (VND),
  **BR-130** (access to payout details **including transfer information** is audited).
- Locked messages: MSG126 (authorization), MSG127 (system), **MSG128 for a missing selected
  record** — the SRS names MSG128 explicitly for this context ("displays MSG128 and returns to
  the list"), so unlike UC-59 (where SRS was silent and MSG133 was proposed) the locked MSG128
  text is followed literally here (D3).
- Schema truth: `payment.PayoutItems` (per-booking payout rows), `commerce.Bookings`
  (`booking_code`, `tour_schedule_id`, amounts, `status`), `payment.PaymentTransactions`,
  `payment.Refunds`, `commerce.TourSchedules` → `commerce.Tours` (tour title).

## Facts vs proposed decisions

| # | Item | Type | Resolution |
| --- | --- | --- | --- |
| D1 | Data source | Fact-derived | Everything returned **as persisted**; no recalculation (PC-02: nothing modified). Booking `paid` = SUM(`PaymentTransactions.amount` where type `Payment`, status `Success`); `refunded` = SUM(`Refunds.amount` where status `Processed`); booking `netAmount` = `PayoutItems.amount` (the engine's per-booking figure that sums into `Payouts.net_amount`). |
| D2 | Endpoint | Proposed | `GET /api/v1/admin/payouts/{id}` with the `{id:long}` route constraint — malformed segment → 404 (framework), ≤ 0 → 400 (validator), unknown id → 404 (D3). No detail actions in this UC. |
| D3 | Missing record message | Proposed | 404 with the **locked MSG128 text** "No records found matching your criteria." — the SRS explicitly names MSG128 for this situation and returns the Administrator to the list (2.a1). Difference from UC-59's proposed MSG133 recorded: there SRS was silent, here the reference is explicit. |
| D4 | Payout header | Fact-derived | Derived `payoutCode` (`PO-{id}`, UC-63 D8), operator company name, settlement period, `requestedAtUtc` (nullable, CR-07), status. |
| D5 | Amount breakdown | Proposed | `grossRevenue`, `commissionAmount`, `netAmount` from the record, plus `commissionRate` from `OperatorProfile.CommissionRate`. **BR-114 limitation recorded:** the schema stores only the operator's current rate — no per-period snapshot exists, so the displayed rate is the current configured value, not a historical reconstruction. |
| D6 | Contributing bookings | Proposed | `payment.PayoutItems` → `commerce.Bookings`: `bookingCode`, `tourName` (via `TourSchedules` → `Tours.title`; "Not available" when the booking has no schedule), `paidAmount`, `refundedAmount` (per D1), `netAmount` = `PayoutItems.amount`. **"Completion Date" gap:** `commerce.Bookings` has no completion timestamp (only `status='Completed'` + `cancelled_at`) — the SRS column cannot be sourced from the schema; the column is omitted and the defect recorded rather than substituting an invented date (e.g. `booked_at`) — flagged for team reconciliation. |
| D7 | Transfer information | Proposed | **Bank account storage does not exist in schema v7** (no table/columns for operator bank details; MSG113 references configuration that has no data model). The transfer section is therefore **omitted** and the SRS-vs-schema defect recorded; delivering it requires a schema addition (migration v8) plus the operator-side UC-46 scope — both out of this UC. BR-130 auditing still applies to the whole details access. |
| D8 | BR-130 audit | Proposed | One `ViewPayoutDetails` audit row (new `AuditActionTypes` constant; entity type `Payout`) per successful read, actor = requesting Administrator, **fail-closed** like UC-59: audit persist failure → 500, no payload. This matters more than usual because the details expose financial breakdown. |
| D9 | [Confirm Settlement] button | Proposed | The SRS places this button on the details screen, but the action is UC-65's. UC-64 renders it **disabled** with an explanatory title (same pattern as UC-63's [View Details]); UC-65 will enable it. |
| D10 | [Export Payout Statement] | Proposed | Deferred. The SRS alternative flow references **MSG116**, whose locked §5.3 content is "Application rejected. Notification sent to operator." — a wrong reference; no export format is approved anywhere; and the statement has no owning storage. The button renders **disabled** with a recorded defect; a proposed MSG135 ("Payout statement exported successfully.") is recorded for the future task that implements export (PC-04 applies then). |
| D11 | Serialization | Approved (UC-63 D5) | Money as JSON numbers (BR-79 VND), IDs as strings, periods `yyyy-MM-dd`, timestamps UTC ISO (CR-07 display on the Web). |

## Scope

One Administrator-only, read-only detail endpoint. Adds the minimum read-only Domain mappings
that UC-63 did not require: `PayoutItem` → `payment.PayoutItems` (FK to `Payout` + `Booking`),
`Booking` → `commerce.Bookings` (nav to existing `TourSchedule` → `Tour`),
`PaymentTransaction` → `payment.PaymentTransactions`, `Refund` → `payment.Refunds`. No SQL
schema change; the only write is the BR-130 audit row (D8).

## API contract

### Endpoint

`GET /api/v1/admin/payouts/{id}`

- Missing/invalid authentication → `401`; non-Administrator → `403` (MSG126, BR-115); segment
  matches `long` but ≤ 0 → `400`; unknown id → `404` with the locked MSG128 text (D3);
  unexpected failure → `500`. Audit row written only on the `200` path (D8).

### Successful response (`200 OK`)

```json
{
  "payoutId": "1",
  "payoutCode": "PO-1",
  "operator": { "userId": "2", "companyName": "Danang Tourist Co., Ltd" },
  "periodStart": "2026-08-01",
  "periodEnd": "2026-08-31",
  "grossRevenue": 10000000.00,
  "commissionRate": 10.00,
  "commissionAmount": 1000000.00,
  "netAmount": 9000000.00,
  "requestedAtUtc": "2026-09-01T02:30:00Z",
  "status": "Requested",
  "bookings": [
    {
      "bookingId": "801",
      "bookingCode": "BK-2026-000801",
      "tourName": "Da Nang Explorer",
      "paidAmount": 5000000.00,
      "refundedAmount": 0.00,
      "netAmount": 4500000.00
    }
  ]
}
```

- `bookings` is ordered by `booking_code` ascending and may be empty (a payout record exists
  without contributing rows in the fixture — rendered as MSG128 on the Web).
- Transfer information is intentionally absent (D7).

## Acceptance criteria

1. Only Administrators retrieve the endpoint; `401`/`403` per the ProblemDetails contract (BR-115).
2. Malformed segment → 404 (route constraint); ≤ 0 → 400; unknown id → 404 with the locked MSG128 text (D3).
3. The response follows the DTO exactly: derived `payoutCode`, string IDs, numeric VND money, `commissionRate` from the operator profile, nullable `requestedAtUtc`, ordered `bookings` with per-booking paid/refunded/net (D1/D5/D6).
4. Every successful read persists exactly one `ViewPayoutDetails` audit row; audit failure fails the request (D8, fail-closed).
5. The handler is read-only otherwise — no payout, booking, or refund record is modified (PC-02).
6. Unit tests cover assembly boundaries (no bookings, booking without schedule, zero refunds, multiple payments per booking, audit row construction, audit-failure propagation); API integration tests cover `401`, `403`, `400`, `404`, `200`, and audit persistence; SQL Server integration tests verify real-schema joins, aggregation, and the audit row.
7. OpenAPI documents the path parameter, response DTO, and error responses.

## Non-goals and dependencies

- No settlement confirmation (UC-65 — the button stays disabled), no export (D10), no bank-account storage (D7), no payout recalculation, no notification fan-out.
- Runtime payout generation and bank-account modeling are external dependencies (UC-46 + future schema).
- Administrator login/session correctness is an external dependency (admin cookie contract).
