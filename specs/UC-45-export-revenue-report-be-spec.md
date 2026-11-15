# UC-45 — Export Revenue Report (Backend Specification)

## Scope and SRS traceability

This specification implements SRS §3.8.5.2 for an approved Tour Operator. It exports the same operator-owned financial data and the same filter snapshot used by UC-44. It supports Excel (`xlsx`), CSV (`csv`), and PDF (`pdf`), with either `summary` or `summary_and_details` content.

It includes synchronous downloads, durable asynchronous exports, export history, authorized re-downloads, and export-event auditing. It excludes payout calculation, settlement requests, changing bookings/refunds/commission rates, and client-side financial calculation.

## Invariants

1. **BR-105 / actor isolation:** every report, export record, stored asset, and download URL is scoped to the authenticated operator. An ID supplied by another operator is indistinguishable from an unavailable resource.
2. **BR-109 / BR-112:** the export calls the UC-44 aggregation service; it must not reimplement a divergent calculation. Confirmed or completed bookings with verified payments, completed refunds, and historical payment commission snapshots remain the source of truth.
3. **BR-110 / filter snapshot:** `startDate`, `endDate`, `granularity`, `tourId`, `format`, and `scope` are persisted in an immutable export record before generation. Retrying or downloading an export never reads the current UI filters.
4. **BR-114:** exports show the commission captured on each successful payment, never the operator's current commission rate.
5. **CR-07:** report labels, date values, and financial period boundaries use `Asia/Ho_Chi_Minh`; dates rendered in files use `dd/MM/yyyy`; amounts use VND without floating-point conversion.
6. Empty matching data returns `MSG128`; no empty file, placeholder file, or partial asset is produced.

## API contract

All endpoints require an active, approved `TourOperator` account.

### `POST /api/v1/operator/revenue/exports`

Request body:

```json
{
  "startDate": "2026-08-01",
  "endDate": "2026-08-31",
  "granularity": "weekly",
  "tourId": 101,
  "format": "xlsx",
  "scope": "summary_and_details"
}
```

- `granularity`: `daily`, `weekly`, or `monthly`.
- `format`: `xlsx`, `csv`, or `pdf`.
- `scope`: `summary` or `summary_and_details`.
- The request is a server-side snapshot, not a serialized UI report or client-supplied totals.

Response:

- `201 Created` with an export record in `Ready` state when generation is within the configured synchronous detail-row threshold. It contains a short-lived download URL.
- `202 Accepted` with an export record in `Queued` state when the detail-row threshold is exceeded. The worker generates it later.
- `400` for missing format/scope, invalid date range, invalid format/scope/granularity (`MSG01` / validation error).
- `403` for a non-approved operator or a foreign tour filter (`MSG126`).
- `422` with `MSG128` when no reportable records match.
- `500` with `MSG117` when generation or durable scheduling fails.

### `GET /api/v1/operator/revenue/exports`

Returns paginated history owned by the caller, newest first. Each item contains `id`, snapshot filters, `format`, `scope`, `status` (`Queued`, `Processing`, `Ready`, `Failed`, `Expired`), `requestedAtUtc`, `completedAtUtc`, `expiresAtUtc`, `fileName`, and a download URL only while ready and unexpired.

### `GET /api/v1/operator/revenue/exports/{exportId}/download`

Rechecks ownership and ready/expiry state, then returns a short-lived storage URL or streams the stored file. A missing, foreign, failed, or expired export must never disclose whether another operator has one. Interrupted browser downloads are a client concern (`MSG106`); the ready record remains downloadable until expiry.

## Export content

Every file has a title, generated-at timestamp, applied filters, and currency/timezone note. Summary includes Gross Revenue, Payments Received, Discounts Given, Refunded Amount, Net Sales, Platform Commission, Net Amount, booking count, and participant count. `summary_and_details` additionally includes the tour-level detail rows returned by UC-44.

- **XLSX:** `Summary` and, when requested, `Details` worksheets.
- **PDF:** summary section followed by the requested tour-detail table with repeating table headers and page numbers.
- **CSV:** UTF-8 with BOM, formula-injection-safe cells. Because CSV has no worksheets, it contains a metadata/summary section followed by a blank row and the detail table when requested. It is never renamed to `.xlsx` or `.pdf`.

The generated name is opaque and collision-safe, for example `tripmate-revenue-20260801-20260831-<exportId>.xlsx`; user-controlled tour names are not used as paths.

## Persistence and background processing

Add a `reporting.RevenueExports` table/entity with operator ID, serialized immutable filter snapshot, format, scope, status, timestamps, row count, generated file metadata/storage key, expiry, failure code, and a concurrency token. Do not store a bearer download URL or report data in the record.

The request transaction creates the record and audit event. A durable database-backed worker claims queued records atomically, regenerates from the saved filters, writes a complete asset to private storage, and changes the row to `Ready` only after storage succeeds. On failure it records a safe failure code and changes the row to `Failed`; partial assets are deleted and never exposed. The threshold and retention duration are configuration values, not client inputs. Expiry removes access and a cleanup job deletes the private asset.

Export generation requires an `IRevenueExportRenderer` (XLSX, CSV, PDF) and `IRevenueExportStorage` abstraction. Storage returns only short-lived, operator-authorized download links. The queue/worker must be restart-safe; an in-memory fire-and-forget task is not acceptable.

## Audit and observability

Write an audit event at request creation containing actor ID, export ID, snapshot filters, format, scope, mode (synchronous/asynchronous), and outcome. It must contain no generated bytes, payment identifiers, or signed URLs. Log worker failures with export ID and correlation ID only. Metrics should count requested, completed, failed, queued, and expired exports.

## Test acceptance criteria

- Valid exports in all three formats and both scopes contain the agreed summary/detail structure and safe filenames/content types.
- The exact same UC-44 aggregation data is used for display and export, including historical commission and completed-refund timing.
- Foreign tour IDs, foreign export IDs, unapproved operators, empty results, invalid input, failed storage, retry, expiry, and concurrent worker claims are covered.
- The asynchronous path survives restart/retry and never serves a partial file.
