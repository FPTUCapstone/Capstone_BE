# UC-64 / UC-73 / UC-74 Implementation Plan

Spec: `specs/UC-64-73-74-review-payout-request-spec.md` (not yet approved).

Status: draft. Do not execute until the spec is approved and the open questions are answered.

## Task 0 — Baseline (blocking)

- Done: `feature/linhnv-view-payout-records` rebased onto `develop` (1b0cc9f); `feature/linhnv-review-payout-request` rebased onto it. Build green after restore; 15 payout unit tests pass; 2 SQL Server integration tests skipped (no SQL Server in this run).
- Remaining: `dotnet test` full run on the review branch; SQL Server run of the skipped tests before any merge. Remote branches need a developer-approved force-push.
- Record: head SHA, base SHA, SDK version, DB name, and the commands with exit codes.
- Done when: baseline results are recorded in the PR description or a verification note.

## Task 1 — Domain: review transitions

Files:
- `src/TripMate.Domain/Entities/Payout.cs`: add `Approve(reviewerUserId, utcNow)` and `Reject(reviewerUserId, reason, utcNow)` factory/domain methods. Each returns a domain result; invalid source status returns a failure.
- `src/TripMate.Domain/Enums/PayoutStatus` (or the existing constants): use constants only, no raw strings.

Tests (write first, `tests/TripMate.Infrastructure.UnitTests` or domain test project):
- Approve from `PendingReview` → `ApprovedForSettlement`.
- Approve from any other status → failure, state unchanged.
- Reject without reason / whitespace / under 10 or over 500 characters after trim → failure (boundaries 9/10 and 500/501).
- Reject from `PendingReview` → `Rejected` with reason stored.

Definition of done: domain tests green; no EF or HTTP reference in Domain.

## Task 2 — Schema: beneficiary snapshot (done in code, verification pending)

Review fields (`reviewed_by`, `reviewed_at`, `rejection_reason`) already exist from the UC-63 lifecycle migration, so only the snapshot columns are new (spec D5).

Files:
- `database/migrations/20261014_add_payout_beneficiary_snapshot.sql`: idempotent, adds the three nullable columns and `CK_Payouts_BeneficiarySnapshot`. Non-destructive.
- `database/tripmate_schema_v7.sql`: the same columns and constraint in `CREATE TABLE payment.Payouts`.
- `Domain/Entities/Payout.cs` and `Infrastructure/.../PayoutConfiguration.cs`: properties, `char(4)` mapping, check constraint.

Tests:
- `tests/TripMate.Infrastructure.UnitTests/Persistence/PayoutPersistenceModelTests.cs`: column mapping, nullability, check-constraint list.
- `tests/TripMate.Api.IntegrationTests/Payouts/PayoutBeneficiarySnapshotMigrationSqlServerTests.cs`: migration on a DB with the previous shape, run twice; valid NULL and valid 4-digit rows accepted; partial, non-digit and short values rejected.

Still to do before this task is done:
- Run the SQL Server tests (needs `TRIPMATE_SQLSERVER_TEST_CONNECTION`).
- Fresh-vs-upgrade inventory diff (D05 / C18) on a test DB, not the user's DB.
- Confirm the full schema file and the migration produce the same column definitions.

Definition of done: migration reproducible; diff empty; no data deleted.

## Task 3 — UC-64 read query

Files:
- `src/TripMate.Application/Features/Admin/Payouts/GetDetail/GetPayoutDetailQuery.cs`, `...Handler.cs`, `...Validator.cs` (validates `payoutId` as decimal string).
- `src/TripMate.Application/Features/Admin/Payouts/GetDetail/PayoutDetailDto.cs`.
- Persistence query using `AsNoTracking()`; projection to DTO; no recalculation of amounts.

Tests (red first):
- Unknown ID → not-found failure mapped to 404.
- Known ID returns persisted snapshot amounts unchanged.
- Beneficiary account is masked (per Open Question 3).
- Integration test on SQL Server: `contributingBookings` ordering is deterministic (tie-breaker on `BookingId`).

## Task 4 — UC-73 approve command

Files:
- `Features/Admin/Payouts/Approve/ApprovePayoutCommand.cs`, handler, validator.
- Conditional update: `UPDATE ... WHERE PayoutId = @id AND Status = 'PendingReview'` (or equivalent with row-version check); affected rows 0 → 409 (or 404 if the row does not exist).
- Writes audit record in the same transaction.

Tests:
- Happy path: status and reviewer fields persisted; audit row exists.
- Second approve → 409, no state change, no second audit row.
- D01: force audit insert to fail → status change rolled back.
- I02: two concurrent approves with separate DbContexts behind a barrier → exactly one 200, one 409.

## Task 5 — UC-74 reject command

Files:
- `Features/Admin/Payouts/Reject/RejectPayoutCommand.cs`, handler, validator (reason trim, non-empty, max length).
- Same conditional update and transaction as Task 4.

Tests:
- Reason empty / whitespace / too long / null → 400 with `errors.reason`.
- Happy path stores reason and reviewer.
- Concurrent approve + reject → exactly one succeeds.
- Tour Operator can read `rejectionReason` through the existing TO endpoint (only if the TO payout endpoint exists on the baseline; otherwise defer and record as dependency).

## Task 6 — Controller and authorization

Files:
- `src/TripMate.Api/Controllers/V1/AdminPayoutsController.cs`: three actions, `[Authorize(Roles = Staff)]` or equivalent policy; no DbContext access; map `Result` through `HandleFailure`.
- OpenAPI: add the three operations, DTOs, and error responses. Use `nameof` for filter keys (AGENTS.md §4.1).

Tests:
- Auth matrix per spec: no token → 401; Administrator / Tour Operator / Traveler → 403; Staff → allowed.
- Integration test through the real JWT path (not a fake login).
- OpenAPI contract test: route, method, request body, and error statuses match the controller.

## Task 7 — Audit

- Verify `BR-130` audit entry for detail access contains actor, payout ID, action, and UTC timestamp.
- Test: audit row present after a successful GET; absent after 403.

## Task 8 — Verification and PR

- Run `dotnet format TripMate.slnx --verify-no-changes --no-restore`, `dotnet build`, `dotnet test`, all with exit codes.
- Report pass / fail / skip counts with the SQL-dependent tests listed separately (TEAM_ENGINEERING_RULES §20: a skipped SQL test is not a pass).
- Fill `.github/pull_request_template.md`: list the three endpoints, the migration, and known limitations.
- Do not open the PR or push until the developer chooses (AGENTS.md §1.2 step 6).

## Task 9 — Cross-repo follow-up (not in this branch)

- `Capstone_FE`: Payout Request Review, Approve and Reject confirmation screens (SRS §3.1.2.2 items 39–41) against the contract above. Separate spec required.

## Risks

- The dependency on the unmerged `feature/linhnv-view-payout-records` branch is the largest risk. Its `Payout` entity and migrations change the shape this plan relies on.
- The actor conflict (Open Question 1) changes Task 5 and the authorization in Task 6.
