# UC-63 View Payout Records Backend Implementation Plan

Status: **Approved 2026-10-01** (spec rewritten against SRS §3.9.9.1; decisions D1–D10 approved
by the developer). Tasks are atomic and sequenced; each ends green before the next starts
(TDD: failing test first).

Branch: `feature/linhnv-view-payout-records` (baseline: latest `origin/develop`, `dotnet test`
verified green).

## Task 1 — Payout read-only mapping

`TripMate.Domain/Entities/Payout.cs` + `TripMate.Infrastructure/Persistence/Configurations/PayoutConfiguration.cs`
mapping `payment.Payouts` (database-first, no migration):

- `payout_id` PK identity; `operator_user_id` FK → `dbo.OperatorProfiles` (navigation
  `Operator`, `DeleteBehavior.Restrict`); `period_start`/`period_end` (DATE store type);
  `gross_revenue`/`commission_amount`/`net_amount` `HasPrecision(14,2)`; `status` VARCHAR(12)
  with the five CHECK values as string constants; `requested_at`, `confirmed_by`,
  `confirmed_at` mapped (UC-64/65 will need them) but not exposed by the UC-63 DTO except
  `requested_at` (D9).
- Wire `DbSet<Payout>` into `IApplicationDbContext`, `ApplicationDbContext`, `TestDbContext`,
  `TestApiDbContext`, and the password-reset fake context.
- Persistence model tests (table/schema/columns/precision/converter + FK to OperatorProfile).

DoD: build + persistence tests green; no SQL file touched.

## Task 2 — Query, validator, DTOs, error codes

`src/TripMate.Application/Features/Admin/Payouts/GetList/`:

- `GetPayoutsQuery(Keyword, Status, PeriodFrom, PeriodTo, PageNumber, PageSize)` +
  FluentValidation: keyword max 200; `status` in the five-value superset (D3a);
  `PeriodFrom ≤ PeriodTo` (D3b) with error code `PayoutErrorCodes.InvalidPeriodRange`
  (`payout.invalid_period_range`, proposed MSG134 wording — D10); page bounds (CR-01).
- DTO records exactly matching the spec response: `PayoutsResponseDto(PayoutSummaryDto,
  PageNumber, PageSize, TotalCount, TotalPages, Items)`; summary = `PayoutSummaryDto(
  PendingRequests, TotalRequestedAmount, TotalConfirmedAmount)`; item =
  `PayoutListItemDto(PayoutId, PayoutCode, Operator{UserId, CompanyName}, PeriodStart,
  PeriodEnd, GrossRevenue, CommissionAmount, NetAmount, RequestedAtUtc, Status)`.
  Derived `payoutCode = $"PO-{payoutId}"` (D8, constant documented). IDs as strings; money as
  decimal (D5); `requestedAtUtc` nullable (D9). **No commission rate, no operator email** (D6).
- `PayoutErrorCodes { Forbidden, InvalidPeriodRange }` with the standard MSG126 message;
  invalid-range message uses the proposed MSG134 text.

Verify: validator unit tests green (status allow-list, inverted range, keyword length, bounds).

## Task 3 — Handler assembly (TDD)

`GetPayoutsQueryHandler` following the UC-58 list handler structure:

1. Administrator role check → `Forbidden` (BR-115).
2. Read-only query over `Payouts` (`AsNoTracking`): composed AND filters — keyword matches
   derived code or company name (evaluate code match against `payout_id` converted to the
   `PO-` form, and company name via the OperatorProfile join); status exact; period window
   (`period_end ≥ periodFrom AND period_start ≤ periodTo`).
3. Deterministic ordering `period_start DESC → operator_user_id DESC → payout_id DESC`;
   count + skip/take pagination after filters (BR-52/CR-01).
4. Summary over the **filtered** set (D4): pending-or-requested count; `SUM(net_amount)` for
   {Pending, Requested}; `SUM(net_amount)` for {Confirmed, Paid}. Rejected contributes to
   neither amount.
5. No `SaveChanges` anywhere (D7).

Unit tests first: forbidden; empty DB → successful empty page with zeroed summary; each filter
composes; keyword matches both code and company name; ordering stability with duplicate
`period_start` rows; summary formulas (Rejected excluded from both amounts, Paid counted in
confirmed, Pending counted in requested). Data-dependent verification lands in the SQL tests.

## Task 4 — Controller endpoint + OpenAPI

New `AdminPayoutsController` (`[Authorize(Roles = "Administrator")]`,
`[Route("api/v1/admin/payouts")]`) with `[HttpGet]`, `HandleFailure`, and
`ProducesResponseType` annotations for `200/400/401/403/500`. Error-code → status mapping for
`Forbidden` (403) and `InvalidPeriodRange` (400) in `ApiControllerBase`. OpenAPI integration
test asserting the path, query parameters, response codes, and DTO schemas (string
`payoutId`, derived `payoutCode`, numeric money, summary counters).

## Task 5 — Integration tests

- HTTP (`TripMateApiFactory`): `401` anonymous, `403` traveler, `400` invalid status /
  inverted period range (assert proposed MSG134 code) / oversized keyword, `200` empty page.
- SQL Server gated (`Category=SqlServer`): seed operators + payouts across all five statuses
  and multiple periods (including two rows with identical `period_start` to prove
  tie-breaking, decimal amounts proving the net CHECK, one `Pending` row with `requested_at`
  NULL, one `Paid` row); assert joins (company name only — no email/rate in payload), filter
  composition, keyword on code and company name, ordering, pagination, and the three filtered
  summary totals.

## Task 6 — Verification and review

1. `dotnet format TripMate.slnx --no-restore --verify-no-changes`
2. `dotnet build TripMate.slnx -c Release` (0 warnings/errors)
3. `dotnet test TripMate.slnx -c Release` (record SQL-skipped vs SQL-run honestly)
4. Spec-compliance review, then code-quality review (severity-based; Critical blocks).

Non-goals guard: no detail route, no mutation endpoints, no `PayoutItems` mapping, no amount
recalculation, no audit write, no commission rate or operator email in the payload.
