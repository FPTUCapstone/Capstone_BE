# UC-45 — Export Revenue Report (Backend Plan)

## Preconditions

- UC-44's shared revenue aggregation contract is available and approved.
- Confirm private object storage and the approved .NET libraries/licenses for XLSX and PDF generation before implementation.
- Preserve the existing `feature/linhnv-export-revenue-report` branch and do not modify UC-46 payout behavior.

## Task 1 — Domain, schema, and options

1. Add `RevenueExport` domain entity/state machine and EF mapping under a `reporting` schema.
2. Add a migration for snapshot filters, format/scope, status, storage metadata, audit-friendly timestamps, expiry, failure metadata, and an optimistic concurrency token/indexes for operator history and worker claims.
3. Add strongly typed export options for synchronous row threshold, storage retention, and worker batch/retry policy.

**Done when:** SQL integration tests prove migration idempotence, state transitions, operator-history index, and concurrent claims.

## Task 2 — Shared financial snapshot and render model

1. Extract/reuse a server-only UC-44 report query so display and export have exactly one financial calculation path.
2. Define immutable request/filter and renderer DTOs; validate ISO dates, timezone semantics, format, scope, and operator ownership before creating an export record.
3. Implement a formatter-neutral report document model with summary, applied filters, and optional tour detail rows.

**Done when:** unit tests prove BR-105, BR-109, BR-110, BR-112, BR-114, CR-07, empty-data behavior, and no client total is accepted.

## Task 3 — File renderers and private storage

1. Implement CSV, XLSX, and PDF renderers behind `IRevenueExportRenderer`; enforce correct content type, extension, VND/date formatting, CSV formula neutralization, and scope-specific content.
2. Implement `IRevenueExportStorage` using the project's private-object-storage pattern and short-lived download URLs.
3. Ensure temporary files/streams are disposed and incomplete uploads are removed on every error path.

**Done when:** renderer tests inspect workbook sheets, PDF sections, CSV escaping, and content-disposition metadata without external storage.

## Task 4 — Synchronous request and audit endpoints

1. Add create, history, and download MediatR commands/queries plus controller endpoints.
2. Create and audit the snapshot before generation. Generate small exports synchronously; return `201 Ready` only after private storage succeeds.
3. Return the canonical SRS errors: `MSG01`, `MSG126`, `MSG128`, `MSG117`; do not return a false success or a partial file.

**Done when:** API integration tests cover authorization, foreign resources, valid direct download, empty report, invalid request, audit persistence, and interrupted-download reattempt.

## Task 5 — Durable asynchronous exports

1. Implement atomic queue claim/release backed by `RevenueExports`; register a hosted worker with bounded batch size and retry/backoff.
2. When the configured row threshold is exceeded, return `202 Queued`; expose status through history and notify the existing notification channel when ready/fails.
3. Add expiry cleanup for private assets and state transition to `Expired`.

**Done when:** integration tests cover queued-to-ready, retry-after-render/storage failure, duplicate worker race, restart safety, notification dispatch, expiry, and forbidden cross-operator download.

## Task 6 — Verification

Run formatter, Release build, unit tests, SQL Server integration suite, and focused export API/worker tests. Verify a single rendered sample of each format manually for Vietnamese dates, VND values, summary-only, and summary-plus-details.
