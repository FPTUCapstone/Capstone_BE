# TM-83 implementation plan

1. Capture focused baseline and inspect authoring, shared readiness and submission paths.
2. Add failing tests for legacy missing data, note/timestamp persistence, ownership/roles, concurrent duplicate submission and migration parity.
3. Extend the existing Backend slice and mappings without weakening public visibility or Staff authorization.
4. Extend Web and Mobile contracts and confirmation/read-only behavior with regression tests first.
5. Run appropriate automated checks, independently review, fix findings and rerun affected checks.
6. Run isolated local services, seed local-only fixtures and exercise real browser/emulator flows. Inspect screenshots, requests and errors; fix observable problems and report evidence and limits.

## Backend progress (isolated tm83-submit-tour-be)

- Focused clean baseline: 131 operator-tour application tests passed.
- TDD red: 8 incomplete legacy tour/departure cases failed against original readiness; metadata contract tests failed compilation before implementation. Staff read tests failed compilation before Staff read implementation.
- Implemented optional submission note/timestamp, shared readiness, transaction-owned per-tour lock, nullable SQL columns and idempotent migration `database/migrations/20261118_tm83_tour_submission.sql` (no historical backfill).
- Added Staff-only `GET /api/v1/admin/tours?page=1&pageSize=20` Pending queue (`items/page/pageSize/totalCount`; oldest first; legacy unknown timestamps remain null), and `GET /api/v1/admin/tours/{tourId}` sharing operator detail projection, including submission metadata. Active Staff database check plus existing JWT session validation; no Administrator access expansion.
- Checkpoint: focused application tests 146 passed; persistence mapping tests 5 passed; focused SQL migration + 4-way concurrency/Staff handoff 2 passed (one 200, three 409, one audit).
- Parent owns actual runtime DB/server/UI. No runtime fixtures created here. Full SQL suite intentionally not run.
- Remaining: compatibility/public-gate SQL test, final unit/build/format, two-pass self-review and final evidence.
