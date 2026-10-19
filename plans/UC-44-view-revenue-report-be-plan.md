# UC-44 — View Revenue Report (Backend Plan)

## Preconditions

- Approve `specs/UC-44-view-revenue-report-be-spec.md`.
- Ensure clean branch `feature/linhnv-view-revenue-report` rebased from `develop`.

## Task 0 — Financial Data Prerequisites

- Map `payment.Refunds` as a Domain entity and `DbSet`; support `Pending`, `Processed`, and `Failed` states and the `processed_at` timestamp.
- Add immutable `SucceededAtUtc` and `CommissionRateApplied` fields to successful payment transactions. Backfill/migrate existing data with an explicit documented policy; do not use mutable `updated_at` as an accounting date.
- Capture the operator commission rate at payment success. A later administrator rate change must not restate historic sales or refund adjustments.

**Done when:** migration and mapping tests prove a processed refund and a successful payment can be queried with their immutable accounting timestamps and applied rate.

## Task 1 — Data Access & Aggregation Foundation

Define query DTOs and EF Core repository methods to aggregate booking revenue:
- Query `commerce.Bookings` joined with `commerce.TourSchedules` and `catalog.Tours` where `Tour.OperatorUserId == currentOperatorId` and `Booking.Status IN ('Confirmed', 'Completed')` and `Booking.PaymentStatus == 'Paid'`.
- Select sales by successful payment timestamp and refunds by completed refund timestamp, both in the requested Vietnam-time range.
- Calculate `GrossRevenue`, `DiscountsGiven`, `PaymentsReceived`, `RefundedAmount`, `NetSales`, `CommissionAmount`, `NetAmount`, booking count, and participant count using BR-112. Count one canonical successful payment per booking and apply the rate snapshotted on that payment.
- Implement time-series grouping helper supporting daily, ISO weekly (Monday–Sunday), and monthly buckets aligned with Vietnam timezone (`Asia/Ho_Chi_Minh`); clip edge buckets to the requested date range.

**Done when:** Unit tests verify BR-112 calculations, retry de-duplication, completed-refund date selection, historical commission-rate stability, and Vietnam-time daily/monthly and ISO-week boundaries across mock booking sets.

## Task 2 — Application Query & Handler

Implement MediatR query `GetOperatorRevenueReportQuery` and `GetOperatorRevenueToursQuery`:
- Implement validator `GetOperatorRevenueReportQueryValidator` enforcing valid ISO dates and `startDate <= endDate` (`MSG31`).
- Query handler validates that the caller holds an approved Tour Operator profile (`MSG126`).
- If `tourId` is provided, verify that the tour is owned by the current operator (`MSG126`).
- Retrieve summary KPIs, time-series data points, and tour detail items. Return zero-valued summary when no matching records exist.
- Return owned historical tour options as well as active options so a report filtered to an older tour can be recreated.

**Done when:** Handler tests assert strict actor isolation (BR-105), validation failures including invalid granularity (MSG29), empty results (MSG128 representation), historical-tour filters, and correct revenue formulas (BR-112).

## Task 3 — Controller & Authorization Endpoints

Expose endpoints in `src/TripMate.Api/Controllers/V1/OperatorRevenueController.cs`:
- `GET /api/v1/operator/revenue/report`
- `GET /api/v1/operator/revenue/tours`
- Apply `[Authorize(Roles = nameof(UserRole.TourOperator))]` and require verified operator approval.
- Map errors to standard `ProblemDetails` / Result responses.

**Done when:** Integration tests assert that unauthenticated users or travelers get 401/403, and authorized operators receive truthful revenue figures.

## Task 4 — Verification & Format

- Run `dotnet format TripMate.slnx --verify-no-changes`.
- Run `dotnet build --configuration Release`.
- Run all unit and integration test suites.
