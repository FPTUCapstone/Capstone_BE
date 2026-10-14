# UC-64 / UC-73 / UC-74 Staff Payout Request Review Specification

## Status

**Implemented on `feature/linhnv-review-payout-request` per explicit developer instruction**, ahead of the usual wait-for-approval gate (AGENTS.md §1.2). D1, D3, D4, D5 are developer-confirmed. D2's 10-character minimum is still a recommendation pending final Lead/PO sign-off — it is implemented as `Payout.RejectionReasonMinLength` (a single constant), so the Lead/PO can change it in one place if they settle on a different minimum. D6 (MSG157) is implemented, also pending the SRS being updated to list it.

Source of truth: `Report3_Software-Requirement-Specification (2).docx`, §2.2 Use Case Descriptions (UC-64, UC-73, UC-74), §3.1.2.2 screen list (items 39–41: Payout Request Review, Approve Payout Request Confirmation, Reject Payout Request Confirmation), and the shared list rules in §2.2 (View Payout Records) and §3.9.9.1.

## Scope

In scope (backend only):

| UC | Function | Actor | Status transition |
| --- | --- | --- | --- |
| UC-64 | Review Payout Request: read the detailed financial and settlement information of one request | Staff | none (read-only) |
| UC-73 | Approve Payout Request | Staff | `PendingReview` → `ApprovedForSettlement` |
| UC-74 | Reject Payout Request (reason required) | Staff | `PendingReview` → `Rejected` |

Out of scope:

- UC-63 list (View Payout Records): implemented on `feature/linhnv-view-payout-records`, not in this branch.
- UC-65 Confirm Payout Settlement (Administrator; `ApprovedForSettlement` → `SettlementProcessing`).
- Payout-processing worker, gateway integration, `Settled` / `SettlementFailed`.
- UC-46 Request Payout Settlement (creation of `PendingReview` requests). `Payout.CreatePendingReview(...)` exists only as the minimal entity-construction factory needed by this task's own tests (and by UC-46 later); it has no settlement-period eligibility, duplicate-request, or booking-aggregation logic.
- Contributing bookings in the UC-64 response: no `Booking` entity exists anywhere in this codebase yet, so `GetPayoutDetailQueryHandler` always returns an empty list for this field. This is a known gap, not a bug — flagged for the developer, not silently decided. UC-64's other sections (header, amounts, beneficiary, status) are unaffected.
- Web and Mobile screens (tracked in `Capstone_FE`, separate spec).
- Administrator rejection of a payout request: not in scope. Rejection is Staff-only (decision D1).
- Bank account capture by the Tour Operator (UC-46 / 3.8.5.3 beneficiary area): owned by the UC-46 task. This spec only reads the snapshot (see D3 and the dependency note).

## Dependencies (must be merged or rebased before implementation)

Status: **rebased.** `feature/linhnv-view-payout-records` was rebased onto `develop` (1b0cc9f, 6 commits, build green, 15 payout unit tests pass, 2 SQL Server tests skipped), and `feature/linhnv-review-payout-request` was rebased onto it (fast-forward, no own commits yet). Local only: the remote branches still need a force-push by the developer.

The payout code below comes from `feature/linhnv-view-payout-records` and must be on this branch's history:

- `Payout` entity and `PayoutConfiguration` (`src/TripMate.Domain/Entities/Payout.cs`, `Infrastructure/Persistence/Configurations/PayoutConfiguration.cs`).
- Lifecycle schema `database/migrations/20261012_add_payout_lifecycle_foundation.sql` and `database/tripmate_schema_v7.sql` changes.
- `Staff` user role (`database/migrations/20261012_add_staff_role.sql`, `UserRole.cs`).
- `GET /api/v1/admin/payouts` (UC-63 Staff scope) and the `PayoutErrorCodes` catalogue.
- Commit `dc1b362 feat(payment): scope payout review to staff`.

Implementation starts only after the spec is approved and the baseline is re-measured on this branch (AGENTS.md §1.2 step 2).

## Authoritative state model

```text
PendingReview --UC-73 Staff approve--> ApprovedForSettlement
PendingReview --UC-74 Staff reject (reason required)--> Rejected
```

Other states (`SettlementProcessing`, `Settled`, `SettlementFailed`) are read by these endpoints only as current status. They must not be changed here. A request not in `PendingReview` cannot be approved or rejected.

## Data and business rules

- BR-112: net payout = gross revenue − platform commission. Amounts are the persisted snapshot created by UC-46. Review and decision endpoints never recalculate them.
- BR-114: the commission rate shown is the rate stored for the request's settlement period.
- BR-79: monetary values are in VND.
- BR-130: access to the payout detail, including beneficiary (bank) information, is written to the audit log.
- Rejection reason (D2): required; trimmed; 10 to 500 characters after trimming. The 500 limit matches `Payout.RejectionReasonMaxLength`, `OperatorProfile.RejectionReasonMaxLength` and `Tour.RejectionReasonMaxLength` already in the codebase. Stored with the decision and visible to the Tour Operator. Reason length is validated in the handler and the validator, with the value taken from the `Payout` constant.
- Beneficiary bank data (D3): the UC-64 response returns `bankName`, `accountHolder` and `maskedAccountNumber` only. The masked value keeps the last 4 digits (for example `XXXXXXXX1234`) and is produced on the server. The full account number is never returned by any endpoint in this spec and no reveal action exists here. Bank data is the snapshot captured when the request was created (UC-46), not the Tour Operator's current profile.
- Access to beneficiary data is written to the audit log (BR-130) with actor, payout ID and timestamp; the audit entry records that the data was shown, never the account number.
- Resubmission after rejection (D4): a `Rejected` request does not block a new request for the same closed settlement period. The Tour Operator submits a new request through UC-46; the rejected request is kept unchanged as history. The UC-46 check "no payout request of the period is already pending" counts only `PendingReview` and later non-rejected states.
- Staff decisions must record the reviewer identity and timestamp (UTC).

## API contract

Base: `/api/v1/admin/payouts`. Staff only (server-enforced role check; the path does not grant Administrator).

### UC-64 Review detail

`GET /api/v1/admin/payouts/{payoutId}`

- `payoutId`: decimal string ID.
- Response 200, DTO directly (no envelope):

```json
{
  "payoutId": "123",
  "payoutCode": "PO-123",
  "status": "PendingReview",
  "settlementPeriod": { "startDate": "2026-09-01", "endDate": "2026-09-30" },
  "tourOperator": { "operatorId": "45", "companyName": "..." },
  "grossRevenue": 12000000,
  "commissionRate": 0.1,
  "commissionAmount": 1200000,
  "netPayoutAmount": 10800000,
  "requestedAtUtc": "2026-10-03T08:15:00Z",
  "contributingBookings": [
    { "bookingId": "9001", "bookingCode": "BK-9001", "tourName": "...", "completedAtUtc": "...", "paidAmount": 0, "refundedAmount": 0, "netAmount": 0 }
  ],
  "beneficiary": { "bankName": "...", "accountHolder": "...", "maskedAccountNumber": "******1234" },
  "rejectionReason": null
}
```

- Errors: 401 (no session), 403 (not Staff), 404 (unknown ID, `errorCode` from `PayoutErrorCodes`), 500 (no infrastructure detail).
- Writes an audit record for the access (BR-130).

### UC-73 Approve

`POST /api/v1/admin/payouts/{payoutId}/approve`

- Request body: none. No `operatorId` or `status` accepted from client.
- Idempotency: a repeated approve of an `ApprovedForSettlement` request returns 409 with the current status; it does not re-approve. A concurrent double submit must not produce two state changes (conditional update on `Status = PendingReview`).
- Response 200: `{ "payoutId": "123", "status": "ApprovedForSettlement", "decidedAtUtc": "..." }`.
- Errors: 401, 403, 404, 409 (not in `PendingReview`), 500.

### UC-74 Reject

`POST /api/v1/admin/payouts/{payoutId}/reject`

- Request body: `{ "reason": "string" }`.
- Validation: `reason` required after trim; reject empty, whitespace-only, or over max length with 400 and a stable `errorCode` plus `errors.reason`.
- Same concurrency rule as approve (conditional update on `PendingReview`).
- Response 200: `{ "payoutId": "123", "status": "Rejected", "decidedAtUtc": "...", "rejectionReason": "..." }`.
- Errors: 400, 401, 403, 404, 409, 500.

### 409 and 400 error bodies

- 409 (request not in `PendingReview`): `ProblemDetails` with `errorCode`, `title` and an extension `currentStatus` (one of the payout statuses). The FE treats `currentStatus` as informational and re-fetches the detail.
- 400 invalid reason: `ProblemDetails` with a stable `errorCode` (for example `payout.rejection_reason_invalid`) and `errors.reason`; the FE maps it to MSG157 with `min_length`=10 and `max_length`=500.
- `commissionRate` is a ratio (`0.1` = 10%), not a percentage.

### Error envelope

Use `Result<T>` and `HandleFailure` → RFC 7807 `ProblemDetails` with the project's extension fields. No `{ success, data, errors }` wrapper.

## Architecture rules applied

- Clean Architecture: handlers in `TripMate.Application/Features/Admin/Payouts/{GetDetail,Approve,Reject}`; controller only binds, authorizes, dispatches, maps.
- Persistence through feature ports or the agreed DbContext abstraction; no generic repository.
- Read query (UC-64) uses `AsNoTracking()`. Approve and reject are commands and keep change tracking enabled (AGENTS.md §3.2).
- Database-first: no EF migrations. Any new column (for example `ReviewedByUserId`, `ReviewedAtUtc`, `RejectionReason`) is added as an idempotent SQL script with the full schema updated.
- Timestamps mapped with `AsUtcDateTime2()`.
- No magic strings: status and error codes via domain constants.

## Acceptance criteria

1. Staff can read the detail of a `PendingReview` request, and the response contains only the fields listed above.
2. Administrator, Tour Operator, Traveler and anonymous callers receive 403 or 401 on all three endpoints.
3. Approve moves `PendingReview` → `ApprovedForSettlement` exactly once; a second approve returns 409 and changes nothing.
4. Reject requires a non-blank reason, moves `PendingReview` → `Rejected`, stores the reason, and is visible in the Tour Operator's view.
5. No endpoint recalculates amounts or changes amounts, bookings, or bank data.
6. Detail access writes an audit record that contains the actor and payout ID.
7. Concurrency: two simultaneous approves (or approve + reject) produce exactly one state change; the loser gets 409.
8. Tests run against SQL Server for the conditional update and the concurrency case (EF InMemory is not sufficient, per TEAM_ENGINEERING_RULES §13).
9. Rejected reason is accepted at 10 and 500 characters and rejected at 9 and 501 (after trim).
10. The UC-64 response never contains a full account number; `maskedAccountNumber` shows only the last 4 digits.
11. After a rejection, the Tour Operator can create a new request for the same settlement period; the rejected record is unchanged.

## Test scenarios (from TEAM_ENGINEERING_RULES)

- Auth matrix: no token, expired token, Administrator, Tour Operator, Staff (allowed).
- Validation: reason empty, whitespace, max+1, null.
- State: approve/reject from `ApprovedForSettlement`, `Rejected`, `SettlementProcessing` → 409.
- D01: a failure after the status update and audit write rolls back both (single transaction).
- D02: round-trip of `ReviewedAtUtc` and `RequestedAtUtc` via a fresh DbContext.
- I02: two concurrent approve requests with two DbContexts behind a barrier → one commit, one 409.
- Schema: fresh vs upgraded DB inventory diff must be empty for the new columns.

## Decisions

- **D1 Actor:** Staff approves (UC-73) and rejects (UC-74). Administrator confirms settlement (UC-65). Confirmed by developer. SRS §3.8.5.3 line "The Administrator rejects the request" is treated as a documentation error and must be corrected in the SRS.
- **D2 Reason length:** 10–500 characters after trim. Recommended by the codebase convention (500 is used by three existing rejection reasons). The 10-character minimum is a recommendation for PO confirmation.
- **D3 Bank data:** masked server-side, last 4 digits only, snapshot from UC-46, audit-logged, no reveal. Based on common practice: Stripe exposes only `last4` and a fingerprint for connected accounts, and recommends not handling raw account numbers on the platform server; PCI guidance limits displayed card digits to BIN plus last four and restricts further access by business need. Source links are in the review summary.
- **D6 Message for invalid reason:** MSG157 from the message catalogue (`database/migrations/20261014_add_msg157_rejection_reason_length.sql`, also seeded in `tripmate_schema_v7.sql`). Backend returns 400 with `errorCode` for this case; the FE maps it to MSG157.
- **D4 Resubmission:** allowed after rejection, as a new request for the same period.

- **D5 Snapshot columns (approved by developer):** `payment.Payouts` gets three nullable columns, added in migration `database/migrations/20261014_add_payout_beneficiary_snapshot.sql` and in the full schema `database/tripmate_schema_v7.sql`:
  - `beneficiary_bank_name` NVARCHAR(100)
  - `beneficiary_account_holder` NVARCHAR(200)
  - `beneficiary_account_last4` CHAR(4), checked to be exactly 4 digits when set
  - Check `CK_Payouts_BeneficiarySnapshot`: either all three are NULL or all three are set.
  - The full account number is not stored. Encrypted storage is deferred to UC-65.
  - UC-46 is not implemented yet, so these columns stay NULL in real flows. Tests and fixtures set them explicitly and mark them as fixtures.
  - UC-64 returns `maskedAccountNumber` as `null` when `beneficiary_account_last4` is NULL. The UI shows "Not available".

### Still open

1. Whether `10` as the minimum reason length is accepted by the PO.
2. Contributing bookings in UC-64 are always empty pending a `Booking` entity (see Scope). The developer should confirm whether UC-64 ships without this section for now, or waits for UC-46/booking work.

## Implementation notes (as built)

- `Payout.Approve`/`Payout.Reject` are the only state-transition entry points; both guard the `PendingReview` precondition and return `false` instead of throwing when it does not hold, so the Application handler can map that to a 409 without a try/catch around business logic.
- Concurrency (I02) uses `Payout.Status` as an EF Core concurrency token (`.IsConcurrencyToken()`), the same technique already used for `User.Status` and `OperatorProfile.ApprovalStatus`. No new column, no explicit row-version. A losing concurrent request's entire `SaveChangesAsync` (state change + its own audit insert) rolls back atomically; it never leaves a partial audit trail. Verified against real SQL Server with two concurrent HTTP requests behind a `SaveChangesInterceptor` barrier (`PayoutReviewConcurrencySqlServerTests`), not just simulated in InMemory.
- 409 responses include a `currentStatus` extension (via `Result.ErrorMetadata`); the Web spec treats it as informational only and re-fetches the detail as the source of truth.
- 400 responses for an invalid rejection reason include both `errorCode` (`payout.rejection_reason_invalid`) and `errors.reason` (via `ErrorMetadata["errors"]`), so the Web can map either to MSG157.
- `commissionRate` in the UC-64 response is derived (`CommissionAmount / GrossRevenue`, `0` when `GrossRevenue` is `0`) from the two amounts already persisted by UC-63; it is not a new calculation and nothing is recalculated for the approve/reject decision itself.
- Audit: `PayoutDetailsViewed` (BR-130, fail-closed — written and committed before the beneficiary data is returned), `PayoutApproved`, `PayoutRejected` (with the reason stored on the audit row too).
