# UC-44 — View Revenue Report (Backend Specification)

## Status

Draft for approval. This specification defines the Backend contract and business logic for Tour Operator Revenue Reporting (UC-44). Implementation starts only after this specification and its plan are approved.

## Scope

Allows an authenticated and approved Tour Operator to retrieve aggregated financial summaries and analytics strictly isolated to their own tour activities on TripMate.

Included in scope:
- Filter validation (date range, period granularity, optional tour package filter).
- Actor role and approval verification (Tour Operator role, approved operator status).
- Strict actor isolation (filtering by `Tour.OperatorUserId`).
- Aggregation of gross revenue, voucher discounts, platform commission deductions, refunds, net amounts, booking counts, and participant counts.
- Time-series aggregation by daily, weekly, or monthly granularity for charting.
- Tour-level breakdown aggregation for reporting tables.
- Tour selection options endpoint (listing owned tour packages, including reportable historical tours, for filter dropdown).

Excluded from scope:
- Export file generation (UC-45: Excel/CSV/PDF export).
- Payout settlement request creation and admin approval (UC-46 / UC-53).
- Modification of bookings, transactions, or commission configurations (read-only query).

## Business Rules & Principles

1. **BR-105 (Strict Actor Isolation)**: A Tour Operator may view only the financial figures and bookings of tour packages owned by that Tour Operator (`Tour.OperatorUserId == CurrentUser.Id`). Querying or filtering by a tour owned by another operator must return 403 Forbidden with `MSG126` / `MSG84`.
2. **BR-109 (Eligible Bookings)**: Revenue is computed only from bookings whose status is `Confirmed` or `Completed`, and with verified payment (`PaymentStatus.Paid`). Unpaid, pending payment, or cancelled without verified payment bookings are excluded from gross revenue.
3. **BR-112 (Financial Calculation)**:
   - `PaymentsReceived` = sum of `Booking.TotalAmount` for eligible bookings after voucher discounts.
   - `DiscountsGiven` = sum of `Booking.DiscountAmount` for the same eligible bookings.
   - `GrossRevenue` = `PaymentsReceived + DiscountsGiven` (commercial value before discounts).
   - `RefundedAmount` = sum of refund records with completed/succeeded status whose **refund completion time** falls in the selected Vietnam-time period, for bookings owned by the operator. A refund is not required to have been created in the selected period.
   - `CommissionAmount` = `PaymentsReceived * (CommissionRate / 100)` (platform commission on collected sales, before refunds).
   - `NetAmount` = `PaymentsReceived - RefundedAmount - CommissionAmount`.
4. **BR-114 (Commission Authority)**: The commission rate is read from `OperatorProfile.CommissionRate` (default 10.00%) maintained by the Platform Administrator. The operator cannot modify or override the commission rate.
5. **CR-07 (Timezone, Date & Week Standard)**: All date boundary comparisons and bucketing use Vietnam time (`Asia/Ho_Chi_Minh`, UTC+7). Input dates are formatted as ISO dates (`YYYY-MM-DD`). Daily and monthly buckets use local calendar boundaries. Weekly buckets use ISO weeks (Monday–Sunday); the first and last bucket are clipped to the selected date range while retaining their ISO `periodKey`.
6. **Idempotence & Read-Only**: This use case performs purely deterministic aggregation queries without mutating any database entity.

## API Contracts

### 1. GET `/api/v1/operator/revenue/report`

**Authorization**: Required `Role: TourOperator`, Active account, `ApprovalStatus: Approved`.

**Query Parameters**:
- `startDate` (string, required): Format `YYYY-MM-DD`
- `endDate` (string, required): Format `YYYY-MM-DD`
- `granularity` (string, optional): `'daily'` | `'weekly'` | `'monthly'` (default: `'weekly'`)
- `tourId` (long, optional): Filter by a specific tour package ID owned by the operator

**Response `200 OK`**:
```json
{
  "summary": {
    "grossRevenue": 248600000.00,
    "paymentsReceived": 241200000.00,
    "discountsGiven": 31400000.00,
    "refundedAmount": 5850000.00,
    "platformCommission": 24100000.00,
    "commissionRatePercent": 10.00,
    "netAmount": 187250000.00,
    "totalBookings": 149,
    "totalParticipants": 341
  },
  "chartData": [
    {
      "periodKey": "2026-W31",
      "periodLabel": "W31",
      "startDate": "2026-08-01",
      "endDate": "2026-08-07",
      "grossRevenue": 52000000.00,
      "commission": 5200000.00,
      "netAmount": 42000000.00,
      "refundedAmount": 0.00,
      "bookingsCount": 32
    }
  ],
  "tourDetails": [
    {
      "tourId": 101,
      "tourCode": "TOUR-00101",
      "tourName": "Ba Na Hills full-day tour",
      "bookingsCount": 86,
      "participantsCount": 214,
      "grossRevenue": 168400000.00,
      "discountAmount": 24100000.00,
      "refundedAmount": 0.00,
      "commission": 14430000.00,
      "netAmount": 129870000.00
    }
  ],
  "appliedFilters": {
    "startDate": "2026-08-01",
    "endDate": "2026-08-26",
    "granularity": "weekly",
    "tourId": null
  }
}
```

### 2. GET `/api/v1/operator/revenue/tours`

Returns tour packages owned by the operator for the filter dropdown, including inactive/archived packages that have reportable historical bookings. The API may mark currently publishable tours with `isActive`; the UI must not hide a returned historical option.

**Response `200 OK`**:
```json
[
  {
    "id": 101,
    "code": "TOUR-00101",
    "title": "Ba Na Hills full-day tour",
    "isActive": true
  }
]
```

## Error & Validation Handling

| Condition | HTTP Status | Code / Message | SRS Ref |
|---|---|---|---|
| User is not authenticated or not a Tour Operator | 401 / 403 | `AUTH_FORBIDDEN` / `MSG126` ("You do not have permission to access this function.") | BR-105, CR-12 |
| Tour Operator application is not approved | 403 Forbidden | `OPERATOR_NOT_APPROVED` / `MSG126` | SRS 3.8.5.1 |
| Missing `startDate` or `endDate` | 400 Bad Request | `VALIDATION_REQUIRED_FIELD` / `MSG01` | CR-04 |
| `startDate > endDate` | 400 Bad Request | `INVALID_PERIOD_RANGE` / `MSG29` ("Invalid date range.") | SRS 3.8.5.1, 4.a1 |
| Target `tourId` does not belong to operator | 403 Forbidden | `OPERATOR_RESOURCE_FORBIDDEN` / `MSG126` | BR-105 |
| Invalid `granularity` | 400 Bad Request | `VALIDATION_INVALID_VALUE` / `MSG29` | CR-07 |
| No bookings found in period | 200 OK | Returns zero-filled summary, empty `chartData`, empty `tourDetails` (Frontend renders `MSG128`) | SRS 3.8.5.1, 5.a1 |
| Database / Server timeout | 500 Internal Error | `SYSTEM_ERROR` / `MSG127` | CR-13 |
