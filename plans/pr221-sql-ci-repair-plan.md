# PR 221 SQL regression repair plan

1. Inspect CI log and existing refund/readiness implementation; preserve the observed failing assertions.
2. Update shared `OperatorTourPackageSqlServerTests` fixture with a Ready synthetic payment channel and add a Disabled-channel submission regression in `TourSubmissionSqlServerTests`.
3. Add failed/completed refund-order regressions in `AcceptedRefundTripHistoryTests`, then correct `GetTravelerTripQueryHandler` ranking. Keep active-obligation precedence explicit in existing handler tests.
4. Run TripHistory unit tests and focused SQL tour/submission/history tests serially against isolated databases. Check changed-file formatting and independently review the diff.
5. Commit/push only the repair files and follow the new CI run. No live database, real payOS or production changes.

## Verification

- CI baseline reproduced: 4 SQL failures, 821 passed.
- Added failed-before/after-completed refund regressions; both failed before the fix.
- TripHistory unit suite: 247 passed, no skips.
- Focused SQL suite: all 4 prior failures plus Disabled-channel negative case passed (5/5), isolated databases only.
- Independent two-pass refund review passed; tour fixture review confirms gate/authorization/concurrency assertions remain intact.
- No live schema or production configuration changes.
