# UC 67 Export Statistical Reports Backend Specification

## Status and scope

Approved implementation baseline for Administrator-only platform reporting. UC-67 exposes `GET /api/v1/admin/statistical-reports`, `GET /periods`, and `GET /export`. It is separate from UC-44 operator revenue and UC-45 operator export: UC-67 reports TripMate platform commission across the platform.

## Financial definition

Platform Revenue is the immutable TripMate commission recognized by the financial source, not GMV and not the operator payout amount.

`net platform revenue = verified tour commission + recognized service commission - processed refund commission reversals`

Tour commission uses the `commission_rate_applied` captured when a successful payment is verified. Refund reversal uses the rate stored on the original successful payment and belongs to the period in which the refund is processed. It never mutates a report snapshot that already exists. Service commission uses `commercial.ServiceBookings.commission_amount`; service bookings currently do not have an operator or destination dimension.

## Period and snapshot rules

All figures target Vietnam-local calendar periods, stored as UTC boundaries. The migration backfills finished months, quarters, and years as closed. `StatisticalPeriodCloseBackgroundService` then runs daily and closes only completed Vietnam-local months, quarters, and years; it never closes a current period. The first valid request for an exact closed-period criterion stores JSON in `reporting.StatisticalReportSnapshots`; future views and exports reuse that payload. The unique criteria hash handles concurrent generation safely.

## Report types and filters

| Report | Supported booking type | Filter behaviour |
| --- | --- | --- |
| Platform Revenue | All, Tour Package, Commercial Service | Operator/destination apply only to tours; service with either filter is rejected. |
| Booking Volume | All, Tour Package, Commercial Service | Same scope rule as Platform Revenue. |
| User Growth | All only | Platform-wide; operator and destination are rejected. |
| Tour Operator Performance | All or Tour Package | Tour data only. |
| Destination Popularity | All or Tour Package | Tour data only. |

## Export and idempotency

CSV and XLSX are supported. The caller supplies a UUID `exportOperationId`; `reporting.StatisticalReportExportOperations` enforces one format and criteria set for each UUID. The exported file is rendered from the same closed-period snapshot as the screen. PDF is intentionally outside the approved scope and is recorded as an SRS follow-up.

## Error catalogue

The API uses feature error codes such as `statistical_report.period_not_closed` rather than reusing unrelated message identifiers. Existing MSG29, MSG117, MSG136–MSG138, and MSG149 are not valid UC-67 messages. Any displayed catalogue message must be selected by semantic meaning; a new catalogue record is required when no matching message exists.

## Migration

`database/migrations/20261023_uc67_reporting_periods.sql` creates `reporting.ClosedStatisticalPeriods`, `reporting.StatisticalReportSnapshots`, and `reporting.StatisticalReportExportOperations`. It is idempotent and must be applied through the normal `db-init` deployment path before this API is enabled.
