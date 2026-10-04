# UC-67 Export Statistical Reports Backend Implementation Plan

Status: **Draft — NOT ready for implementation.** Blocked on the four team decisions
(DEC-1 export formats, DEC-2 service bookings, DEC-3 revenue formula/period assignment,
DEC-4 generate→export consistency mechanism) recorded in `specs/UC-67-spec.md`. Tasks start
only after those are approved; Task 4 additionally depends on DEC-1.

Branch: `feature/linhnv-export-statistical-reports`.

## Baseline evidence (recorded 2026-10-01, per re-review R6)

- Commit: `aba94657592281122d4d075db67683c55a9f83f8` (`origin/develop` tip), working tree
  clean except this UC's untracked spec/plan files.
- `dotnet build TripMate.slnx -c Release` — Build succeeded.
- `dotnet test TripMate.slnx -c Release` — 222 Infrastructure + 1019 Application + 305 API
  integration passed, 0 failed (SQL-gated tests skipped locally; they run in CI).

## Task 1 — Booking/Refund read-only mappings

`Booking` → `commerce.Bookings` and `Refund` → `payment.Refunds` (shapes proven on the
payout-details branch), configurations, DbSet wiring into the five context surfaces,
persistence model tests. `PaymentTransaction` is deliberately **not** mapped (DEC-3 revenue
formula uses booking amounts, eliminating double counting). No SQL file touched.

## Task 2 — Contracts, validator, error codes, CSV builder

`src/TripMate.Application/Features/Admin/Statistics/`:

- `StatisticsErrorCodes`: `Forbidden`, `PeriodNotClosed` (`statistics.period_not_closed`,
  proposed M3 wording), `ResultExpired` (`statistics.result_expired`, proposed MSG138),
  `AuditUnavailable` (`admin.statistics_audit_unavailable`, MSG127 text); proposed MSG136/137
  wordings recorded for the FE.
- `GenerateStatisticalReportCommand` + validator: report type ∈ five-value set; period type ∈
  {Month, Quarter, Year}; **period alignment** (Month: `periodStart` = first day and
  `periodEnd` = last day of the same month; Quarter/Year equivalently — misalignment → 400
  field error); keyword length limits; wrong-filter-for-type → 400.
- Per-type bucket/summary DTO records exactly per the spec table (D11 label rule:
  `MM/yyyy`, `Q<n>/yyyy`, `yyyy` + `bucketStart` sort key).
- **Result token service** (DEC-4 option A): protect/unprotect the serialized
  `{criteria, figures, generatedAtUtc}` payload with `IDataProtectionProvider`
  (purpose `"TripMate.UC67.StatisticsResultToken"`, 30-minute TTL check on unpack) + unit
  tests (round-trip, expiry, tamper).
- **CSV builder** (pure, unit-testable): per-type columns per the spec table, UTF-8 BOM,
  deterministic ordering.

## Task 3 — Closure + aggregation + export handlers (TDD)

`GenerateStatisticalReportCommandHandler` / `ExportStatisticalReportCommandHandler`:

1. Authorization → `Forbidden`.
2. Closure (BR-129): `periodEnd` compared against the first day of the current Vietnam month
   via `IDateTimeProvider` + `VietnamCalendar` → `PeriodNotClosed` (422) when not closed.
3. Aggregation per type (D2/DEC-3): Bookings `status ∈ {Confirmed, Completed}` by
   `booked_at` Vietnam local time; revenue = SUM(`total_amount`) − SUM(processed refunds of
   those bookings); bucketing via the +07 conversion; keyword filters on company/destination
   names. Read-only on generate.
4. Export: unpack the token (expiry → `ResultExpired`; tamper → 400), build the CSV **from the
   unpacked figures** (BR-110), audit (`StatisticalReportExported`, `afterData` =
   type/period/format/filters), `SaveChangesAsync` wrapped fail-closed →
   `AuditUnavailable` (503); register `admin.statistics_audit_unavailable` in
   `AuditFailureBehaviour` so the rejected attempt is recorded as
   `audit.database_write_failed` (UC-57 CI lesson).

Unit tests first: forbidden; misaligned periods (each type); closure boundaries (last day of
the previous month passes; first day of the current month fails); bucket edges around Vietnam
midnight (UTC timestamps either side of +07 midnight); each report type's formula (Confirmed/
Completed population only, refund processed-only, multi-refund sums); empty data; CSV shape
per type; token round-trip/expiry/tamper; audit row construction and fail-closed propagation.

## Task 4 — Controller endpoints + OpenAPI

`AdminStatisticsController` (`[Authorize(Roles = "Administrator")]`,
`[Route("api/v1/admin/statistics")]`): `[HttpPost("reports/generate")]` → DTO + token;
`[HttpPost("reports/export")]` → `File(stream, "text/csv", fileName)` gated on DEC-1.
`HandleFailure` mappings: `Forbidden` 403, `PeriodNotClosed` 422, `ResultExpired` 422,
`AuditUnavailable` 503. OpenAPI test for both paths, bodies, and codes.

## Task 5 — Integration tests

- HTTP: `401`, `403`, `400` (missing type/period, misaligned), `422` (open period with a fixed
  clock; expired token), `200` empty generate, `200` CSV export headers/content.
- SQL Server gated: seed users/bookings/refunds around bucket edges and both filters; assert
  every report type's aggregation, the CSV contents, and the `StatisticalReportExported` audit
  row. **Executed locally with `TRIPMATE_SQLSERVER_TEST_CONNECTION` before every push** (the
  UC-57 CI lesson is now a standing rule for this UC).

## Task 6 — Verification and review

1. `dotnet format TripMate.slnx --no-restore --verify-no-changes`
2. `dotnet build TripMate.slnx -c Release` (0 warnings/errors)
3. `dotnet test TripMate.slnx -c Release` + local SQL-gated run (results recorded in the PR)
4. Spec-compliance review, then code-quality review.

Non-goals guard: no Excel/PDF until DEC-1; no service bookings until DEC-2; no
`PaymentTransaction` mapping; no schema change; the only write is the export audit row.
