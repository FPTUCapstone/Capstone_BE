# UC-39 Backend Verification

## Coupon usage audit — 2026-10-26

- Target: local Docker SQL Server `tripmate-sqlserver`, database `TripMateDb`.
- Source: `database/queries/uc39_coupon_usage_audit.sql`.
- Safety: the script contains only CTEs and one `SELECT`; it does not mutate schema or data.
- Slot definition checked: pending-payment bookings and bookings that reached paid status remain occupied; terminal unpaid `Expired` or `Cancelled` bookings are released.
- Credentials were read only from the container environment and were not printed or persisted.

Result:

| voucher_id | code | stored_used_count | expected_occupied_count | difference | terminal unpaid redemptions | usage_limit |
| ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | `UC39E2EACTIVE` | 2 | 0 | 2 | 0 | 10 |

The local fixture coupon has a counter mismatch: `used_count` is two greater than the occupied-redemption count. No data was corrected. This result must not be used to infer production impact; run the same read-only audit against each intended deployment database before deciding whether an idempotent correction is required.

## SQL Server behavior — 2026-10-26

The focused `UpdateCouponSqlServerTests` suite ran against disposable
`TripMate_Test_<GUID>` databases created from the current schema and migrations.
No test mutated `TripMateDb`.

- Concurrent redemption versus limit reduction preserves `used_count <= usage_limit`; the losing update returns `coupon.usage_conflict` without a partial field or tour-scope change.
- Two concurrent expiry workers release one voucher slot exactly once and retain the redemption audit row.
- Concurrent cancellation and expiry cannot make `used_count` negative; one replacement redemption can reuse the released slot without exceeding the limit.
- A booking created before a coupon update retains its `100,000` discount and `900,000` total; a later booking uses the updated `200,000` discount and `800,000` total.

Final focused run: **4 passed, 0 failed, 0 skipped**. The original three race tests also passed three consecutive runs before the booking snapshot case was added.

## Final gates and review — 2026-10-26

- `dotnet build TripMate.slnx --no-restore`: passed with zero errors. Existing dependency advisories and two unrelated compiler warnings remain visible.
- `dotnet test TripMate.slnx --no-build --no-restore`: 5,680 passed, 0 failed; 501 environment-gated tests were skipped (498 API integration and 3 infrastructure provider tests).
- UC-39 SQL Server tests were run separately with the local connection enabled: 4 passed, 0 failed, 0 skipped.
- UC-39 endpoint tests: 8 passed, including `422 coupon.tour_not_eligible` when activation is attempted after an applicable tour becomes inactive.
- Spec review findings were resolved: activation now revalidates tour eligibility, and SQL coverage proves old-booking snapshots versus new-booking terms.
- Standards review found no Clean Architecture, Result/ProblemDetails, database-first, tracking, secret-handling or branch violation. The shared coupon-tour eligibility rule removes drift between create, update and activation. The occupied-redemption predicate remains duplicated in quote/create intentionally to avoid a premature EF-query abstraction.

The audit mismatch belongs to a local E2E fixture whose counter was seeded without redemption rows; it is evidence that deployment databases must be audited, not evidence for an automatic correction migration. No correction SQL was created and no data was changed.
