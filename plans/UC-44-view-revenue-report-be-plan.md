# UC-44 — View Revenue Report (Backend Plan)

## Preconditions

- Approve `specs/UC-44-view-revenue-report-be-spec.md`.
- Ensure clean branch `feature/linhnv-view-revenue-report` rebased from `develop`.

## Task 1 — Data Access & Aggregation Foundation

Define query DTOs and EF Core repository methods to aggregate booking revenue:
- Query `commerce.Bookings` joined with `commerce.TourSchedules` and `catalog.Tours` where `Tour.OperatorUserId == currentOperatorId` and `Booking.Status IN ('Confirmed', 'Completed')` and `Booking.PaymentStatus == 'Paid'`.
- Calculate sum of total amount, discount amount, commission amount (via `OperatorProfiles.CommissionRate`), bookings count, and participant count.
- Implement time-series grouping helper supporting Daily, Weekly, and Monthly bucketing aligned with Vietnam timezone (`Asia/Ho_Chi_Minh`).

**Done when:** Unit tests verify correct aggregation figures and boundary handling across mock booking sets.

## Task 2 — Application Query & Handler

Implement MediatR query `GetOperatorRevenueReportQuery` and `GetOperatorRevenueToursQuery`:
- Implement validator `GetOperatorRevenueReportQueryValidator` enforcing valid ISO dates and `startDate <= endDate` (`MSG29`).
- Query handler validates that the caller holds an approved Tour Operator profile (`MSG126`).
- If `tourId` is provided, verify that the tour is owned by the current operator (`MSG126`).
- Retrieve summary KPIs, time-series data points, and tour detail items. Return zero-valued summary when no matching records exist.

**Done when:** Handler tests assert strict actor isolation (BR-105), validation failures (MSG29), empty results (MSG128 representation), and correct revenue formulas (BR-112).

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
