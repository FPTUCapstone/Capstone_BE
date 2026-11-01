# UC-65 — Confirm Payout Settlement (Backend Plan)

## Status

**Approved 2026-11-01** by the developer. Implements the approved `specs/UC-65-spec.md`. Nothing here pushes, opens a PR, or touches Azure beyond the read-only checks already done.

Developer clarifications recorded with the approval:
- NFC normalization before Trim + `OrdinalIgnoreCase` in `BankBinResolver` is kept.
- Azure UAT stays untouched for now. At Task 13 only read-only checks and a migration dry run inside a transaction with `ROLLBACK` are run, and the results are reported first. Committing the migration and seeding one sample payout on UAT are confirmed separately by the developer.
- The developer starts Docker Desktop for the `[SqlServerFact]` tests (Tasks 1, 7, 13).
- Task 9 `x-signature`: HMAC-SHA256, lowercase hex, key = payout `ChecksumKey`, over the request-body properties sorted alphabetically as `key1=value1&key2=value2…`. Reuse the helper style of the existing `PayOsPaymentGateway`; if the exact field order/format for the `/v1/payouts` payload cannot be confirmed, stop and ask the developer before finalizing Task 9.

## Preconditions

- Branch `feature/uc65-confirm-payout-settlement`, created from `develop` at `628d606c`.
- Task 0 must be green before any other task. Never start on a broken baseline.
- No live PayOS call is ever made by a test, a script in this plan, or CI. `Payment__Payout__Provider` defaults to `Mock`; `LiveTransfersEnabled` defaults to `false`.
- SQL Server integration tests (`[SqlServerFact]`) use the local docker-compose SQL Server through `TRIPMATE_SQLSERVER_TEST_CONNECTION` (they create and drop throw-away databases). Migration and data checks on real data go to Azure UAT only, read-only first, then a single-transaction dry run with `ROLLBACK`, and `COMMIT` only after explicit developer confirmation.
- Docker Desktop was not running at the time of writing; start it before Task 1 and Task 13.
- Reference only (do not merge): Linh's branch `origin/feature/linhnv-confirm-payout-settlement` has an Administrator list query and tests written against a base 483 commits behind. Read with `git show origin/feature/linhnv-confirm-payout-settlement:<path>` for `GetSettlementPayoutsQuery*`, `PayoutSettlementEndpointTests`, and `GetSettlementPayoutsQueryTests`; re-write on `develop`.

## Conventions for every task

Red → Green → Refactor, one task at a time, a failing test first. After each task: run its verification command, then a short review against the spec (Critical findings block the next task). Result pattern with `HandleFailure`, no business logic in controllers, no generic repository, no EF migrations, SQL scripts idempotent. Comments follow the surrounding code's density. Commits are made only when the developer asks.

Proposed layout:

| Layer | Path |
|---|---|
| Domain | `Entities/Bank.cs`, `Entities/PayoutSettlementWorkItem.cs`, additions to `Entities/Payout.cs`, `Common/AuditActionTypes.cs` |
| Application | `Features/Admin/PayoutSettlements/{Common,GetList,GetDetail,Authorize,Processing}/` |
| Infrastructure | `Services/Payment/PayoutDisbursement/{MockDisbursementGateway,PayOsPayoutGateway,PayoutDisbursementOptions}.cs`, `Services/PayoutSettlementBackgroundService.cs`, EF configurations |
| Api | `Controllers/V1/AdminPayoutSettlementsController.cs` |
| Database | `database/migrations/20261101_uc65_payout_settlement_processing.sql` |
| Docs | `docs/UC-65-api-contract.md`, `.env.example` |

## Task 0 — Baseline

- `dotnet build TripMate.slnx` and the unit suites (`TripMate.Application.UnitTests`, `TripMate.Infrastructure.UnitTests`) on the clean branch.
- Record the baseline result (counts, any pre-existing failures) in the working notes. A pre-existing failure is reported, not fixed here.

**Verify:** `dotnet build TripMate.slnx -c Debug` then `dotnet test` per unit project.
**Done when:** build succeeds and the baseline is recorded.

## Task 1 — Database script and persistence model

Files: the migration, `Bank.cs`, `PayoutSettlementWorkItem.cs` (structure only), `Payout.BankBin`, EF configurations (`BankConfiguration`, `PayoutSettlementWorkItemConfiguration`, `PayoutConfiguration` update), `ApplicationDbContext`, `IApplicationDbContext`, `TestDbContext`, `TripMateApiFactory` if it lists sets.

Script content (idempotent, guarded by `IF NOT EXISTS`/`COL_LENGTH`):

- `payment.Banks`: `bank_bin CHAR(6)` PK with a digits-only check, `short_name NVARCHAR(50)` unique, `official_name NVARCHAR(200)` unique, `created_at`.
- Seed exactly the 18 rows listed in the spec (BIN, short name, Vietnamese official name as supplied by the team). Re-running changes nothing.
- `payment.Payouts.bank_bin CHAR(6) NULL` with a 6-digit check. No backfill.
- `payment.PayoutSettlementWorkItems` (columns per spec), FK to `payment.Payouts`, **unique `payout_id`**, check on `state`, index `(state, next_attempt_at)`, `row_version`.
- No change to `CK_Payouts_Status` or `CK_Payouts_LifecycleState`.

Tests (RED first):
- `PayoutPersistenceModelTests` additions: mapping names, nullable `BankBin`, unique work item per payout.
- `[SqlServerFact]` test: script applies twice without error; seed rows exact; duplicate `payout_id` rejected; a `SettlementProcessing → Settled/SettlementFailed` row with attempt count ≥ 1 satisfies `CK_Payouts_LifecycleState`, and one with attempt count 0 is rejected.

**Verify:** `dotnet test tests/TripMate.Infrastructure.UnitTests --filter "FullyQualifiedName~Payout"` and the SQL Server test project filter for the new test class (local docker).
**Done when:** script is idempotent on a clean database and on the existing schema; model tests pass.

## Task 2 — Domain: payout and work-item state machines

`Payout` additions (return `bool` like `Approve`/`Reject`, throw only on programmer errors):

- `AuthorizeSettlement(adminId, nowUtc, bankBin)`: only from `ApprovedForSettlement` → `SettlementProcessing`; sets `SettlementAuthorizedBy/AtUtc` and `BankBin`.
- `RecordGatewayAttempt(nowUtc)`: `GatewayAttemptCount++`, `LastGatewayAttemptAtUtc = now` (only while `SettlementProcessing`).
- `SetGatewayReference(reference)`.
- `MarkSettled(nowUtc)`: requires `SettlementProcessing`, a gateway reference, attempt count ≥ 1.
- `MarkSettlementFailed(reasonCode, nowUtc)`: requires `SettlementProcessing`, attempt count ≥ 1, reason within `GatewayFailureReasonMaxLength`.

`PayoutSettlementWorkItem`: `Create`, `TryClaim(now, lease)`, `RecordSubmitted(reference, now, deadline)`, `ScheduleRetry(code, next)`, `SchedulePoll(next)`, `Complete(now)`, `RequireReconciliation(code)`, `RecoverExpiredLease(now, lease, maxAttempts)`. States `Pending → Submitting → Submitted → Completed | ManualReconciliation`; terminal states accept no further transitions.

Tests: every legal and illegal transition; no transition out of `Settled`/`SettlementFailed`/`Completed`/`ManualReconciliation`; `ManualReconciliation` never changes the payout; lease recovery.

**Verify:** `dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~PayoutSettlement"` (domain tests live next to existing entity tests).
**Done when:** transitions are exhaustively covered.

## Task 3 — Bank BIN resolver

`IBankBinResolver` / `BankBinResolver` (Application, pure, takes the catalogue rows): stored BIN must be in the catalogue; otherwise exact match on `BankName` — NFC normalize, trim, `OrdinalIgnoreCase`, short or official name, exactly one match. Returns a `Result` with `payout.bank_bin_unresolved`.

Tests (every case in the spec): stored valid/unknown BIN, exact short and official name, case difference, surrounding whitespace, NFC vs NFD, accent-stripped rejected, inner-space difference rejected, typo/prefix/substring rejected, duplicate match rejected, blank/null rejected.

**Verify:** `dotnet test … --filter "FullyQualifiedName~BankBinResolver"`.
**Done when:** no code path performs partial or similarity matching.

## Task 4 — Shared contracts

`PayoutSettlementErrorCodes` (`not_found`, `not_approved_for_settlement`, `beneficiary_incomplete`, `amount_invalid`, `bank_bin_unresolved`, `invalid_status`, `forbidden`), DTOs (queue item, detail, authorize response `202`), `AuditActionTypes` additions (`AuthorizePayoutSettlement`, `ViewPayoutSettlementDetails`, `PayoutSettled`, `PayoutSettlementFailed`, `PayoutReconciliationRequired`), notification type constants (`PayoutSettled`, `PayoutSettlementFailed`).

**Verify:** build.
**Done when:** constants compile and are referenced by later tasks only.

## Task 5 — Administrator queue (GET)

`GetPayoutSettlementsQuery` + validator + handler, controller `GET`. Same filters as the Staff list; allow-list `ApprovedForSettlement` (default), `SettlementProcessing`, `Settled`, `SettlementFailed`; anything else → `400 payout.invalid_status`; role check in handler; persisted snapshot amounts; `totalCount`/`totalAmount` over the filtered set; deterministic order; no bank data, reviewer identity, or gateway reference in items.

Tests: Administrator allowed; Staff, TourOperator, Traveler `403`; anonymous `401`; each allowed status; `PendingReview`/`Rejected`/`Cancelled` rejected; keyword, period overlap, paging, empty page; **and the reverse direction**: Administrator is still `403` on `GET /api/v1/admin/payouts`, Staff is `403` on the settlement route. OpenAPI test lists the new route and its status codes.

**Verify:** `dotnet test … --filter "FullyQualifiedName~PayoutSettlements.GetList|PayoutSettlementEndpoint"`.
**Done when:** authorization matrix tests pass in both directions.

## Task 6 — Administrator detail (GET)

`GetPayoutSettlementDetailQuery` + handler + controller `GET {payoutId}`. Returns the snapshot, masked beneficiary, `bankBinResolved`, authorization info, and for processing states the safe outcome summary (`gatewayState`, `failureCode`, `submittedAtUtc`, `reconciliationRequired`). Writes one `ViewPayoutSettlementDetails` audit row for an existing payout (none for not-found), `Cache-Control: no-store`.

Tests: success, `404`, role matrix, audit row written once, response contains no `AccountNumberProtected`, no plaintext account number, no gateway credentials (serialize the response and assert).

**Verify:** `dotnet test … --filter "FullyQualifiedName~PayoutSettlements.GetDetail"`.
**Done when:** leakage assertions pass.

## Task 7 — Authorize command (POST → 202)

`AuthorizePayoutSettlementCommand` + handler + controller. One atomic `SaveChanges` guarded by the `Status` concurrency token and the unique work item (same pattern as UC-73; no explicit serializable transaction is needed because every write is in that single save): role check → load tracked → status check → payable checks (beneficiary complete, positive whole-VND net, BIN resolved) → `AuthorizeSettlement` → insert one `Pending` work item → audit row → one `SaveChanges`. Concurrency token conflict re-reads status untracked and returns `409` with `currentStatus` (UC-73 pattern). The handler never touches the gateway.

Tests: success (status, `SettlementAuthorizedBy/AtUtc`, `BankBin` persisted, one work item `Pending`, one audit row, `202` body); wrong status `409`; not found; Staff `403`; each `422` leaves nothing written; double submit / concurrent authorize → one success, one `409`, one work item; save failure rolls back everything; fractional or zero net rejected; legacy payout with `BankName` only resolved by exact match, non-matching rejected.

**Verify:** `dotnet test … --filter "FullyQualifiedName~AuthorizePayoutSettlement"` plus a `[SqlServerFact]` concurrency test.
**Done when:** atomicity and the single-winner behaviour are proven against SQL Server.

## Task 8 — Gateway port, Mock, configuration

- Port `IPayoutDisbursementGateway` with normalized results: `SubmitAsync` (Accepted | DefinitiveRejection | Transient | IdempotencyKeyExists), `GetStatusAsync` (Processing | Succeeded | Failed | Cancelled | Unknown), `FindByReferenceAsync` (Found | NotFound | LookupFailed).
- `PayoutDisbursementOptions` (`Payment:Payout:*`, `PayoutSettlement:*`) with validators: positive durations/sizes; `Provider ∈ {Mock, PayOs}`; `PayOs` requires `LiveTransfersEnabled = true` and non-empty `ClientId/ApiKey/ChecksumKey`, else startup fails; the collection `Payment:PayOs:*` values are never read as a fallback.
- `MockDisbursementGateway` (default): deterministic accepted → `SUCCEEDED`.
- DI registration in `DependencyInjection.cs`: provider selection, options validation on start.
- `.env.example`: names only, empty values.

Tests: default is Mock; `PayOs` without switch fails; `PayOs` without each credential fails; setting only `Payment:PayOs:*` does not enable payout; Mock behaviour.

**Verify:** `dotnet test tests/TripMate.Infrastructure.UnitTests --filter "FullyQualifiedName~PayoutDisbursement"`.
**Done when:** no configuration can select the live adapter by accident.

## Task 9 — PayOS payout adapter

`PayOsPayoutGateway` (typed `HttpClient`, base URL from options): `POST /v1/payouts`, `GET /v1/payouts/{id}`, `GET /v1/payouts?referenceId=`; headers `x-client-id`, `x-api-key`, `x-signature` (create), `x-idempotency-key`. Mapping per the spec's confirmed contract: `PROCESSING`/`SUCCEEDED`/`FAILED`/`CANCELLED`, unknown → processing + warning; HTTP 4xx other than 408/429 or `code != "00"` → definitive rejection (insufficient balance mapped to its own safe code); 408/429/5xx/timeouts/network → transient; the "key exists" error → `IdempotencyKeyExists`. `description` is built ASCII-only, ≤ 25 chars; `referenceId = PayoutCode`. Credentials, signature, account number, and raw bodies never reach logs or exception messages.

Risk R1: the exact `x-signature` construction for payouts must be taken from the official PayOS documentation/SDK when implementing, not guessed; the adapter is tested with a fake `HttpMessageHandler` and, if the docs publish one, a known-good signature vector. If the construction cannot be confirmed, stop and ask before continuing.

Tests (fake handler only, never the network): request shape and headers, description truncation/ASCII, each response mapping, unknown state, empty lookup vs failed lookup, secrets absent from logs and exceptions.

**Verify:** `dotnet test … --filter "FullyQualifiedName~PayOsPayoutGateway"`.
**Done when:** every mapping row of the spec has a test, and no test can reach the network.

## Task 10 — Settlement processor (application service)

`PayoutSettlementProcessor` modelled on `RefundProcessor`: recover expired leases → claim due items (serializable) → gateway call outside the transaction → record outcome (serializable). Implements the Submit and Poll tables, the 24 h deadline, backoff schedule, `MaxSubmitAttempts = 3`, lookup-by-reference resolution, operator in-app notification and audit rows on terminal outcomes. The account number is decrypted via `IPiiProtector.Unprotect` only inside the submit call. Worker events use a null actor.

Tests (fake gateway and fake clock): accepted → polling → `SUCCEEDED` → `Settled` (+ `GatewayReference`, attempt count, `LastGatewayAttemptAtUtc`, notification, audit); definitive rejection → `SettlementFailed`, no retry; transient then success; transient ×3 then lookup found / not found (`gateway_unavailable`) / lookup failed (`ManualReconciliation`); idempotency-key-exists → lookup; `FAILED`, `CANCELLED`; unknown state stays processing; backoff intervals increase and cap; deadline → `ManualReconciliation`, payout stays `SettlementProcessing`, no new submission, audit row, `reconciliationRequired`; lease recovery re-attempts without a duplicate submit; two concurrent sweeps never both submit the same item; operator payout history shows the settled time; failure of the notification does not roll back the payout.

**Verify:** `dotnet test … --filter "FullyQualifiedName~PayoutSettlementProcessor"`.
**Done when:** the spec's acceptance criteria 5–7 and 9–10 are covered by tests.

## Task 11 — Hosted service wiring

`PayoutSettlementBackgroundService` (scope per sweep, error-tolerant loop like `RefundProcessingBackgroundService`), registered only when the payout options validate; Mock provider keeps it active so UAT can run the whole flow without money.

Tests: sweep loop invokes the processor, survives an exception, honours cancellation.

**Verify:** `dotnet test tests/TripMate.Infrastructure.UnitTests --filter "FullyQualifiedName~PayoutSettlementBackgroundService"`.
**Done when:** the service starts under the default (Mock) configuration in the API integration factory.

## Task 12 — API contract document

`docs/UC-65-api-contract.md` for the Web team (FE-first): routes, query parameters, response shapes, error codes with HTTP statuses, status/outcome vocabulary, polling guidance for the UI (refresh until `Settled`/`SettlementFailed`, show `reconciliationRequired`), and the fact that authorize returns `202` while the final result arrives asynchronously.

**Verify:** contract matches the OpenAPI test output.
**Done when:** every endpoint and error code in the spec appears in the document.

## Task 13 — Full verification

1. `dotnet build TripMate.slnx` with no new warnings in changed files.
2. All unit suites and the new `[SqlServerFact]` tests against the local docker SQL Server.
3. Re-run the migration script twice on a copy of the current schema.
4. **Azure UAT (after the developer confirms):** read-only checks of `payment.Banks`/`bank_bin` presence → apply the script inside one transaction with `ROLLBACK` as a dry run → report. `COMMIT` only on explicit confirmation. Note for the developer: the existing UAT payouts carry no beneficiary data, so UAT of the flow needs a payout seeded with a catalogue bank; that seed is prepared separately and approved before running.
5. Confirm by search that no secret, account number, or `.env` file is staged, and that no test references the live adapter.

**Done when:** all checks pass or any failure is reported with its output.

## Task 14 — Finishing the branch

Do not merge, push, or open a PR unprompted. Present the options (merge, open PR, keep, discard) and act only on the developer's choice. The Web side (Administrator settlement screen replacing the "Pending Backend Settlement Service" view) gets its own FE spec afterwards.

## Dependencies and order

`0 → 1 → 2 → 3 → 4`, then `5`, `6`, `7` (each needs 1–4), `8 → 9`, `10` (needs 2, 8; uses 9 only through the port), `11`, `12`, `13`, `14`. Tasks 5/6 and 8/9 can proceed in parallel branches of work but are integrated one at a time.

## Risks

| Risk | Handling |
|---|---|
| PayOS payout `x-signature` construction unknown | Task 9 stops and asks if it cannot be confirmed from official docs/SDK. |
| Contract points (states, idempotency behaviour, empty lookup) confirmed by the team, not by public docs | Adapter is defensive; unknown values stay `PROCESSING`; the first supervised live run validates them. |
| Docker not running here | Start it for Tasks 1, 7, 13; otherwise SQL Server tests cannot run. |
| UAT payouts have no beneficiary data | Authorize returns `422` there by design; seed separately. |
| Existing `SettlementProcessing` UAT payout has no work item | The worker only processes items it owns; that seed row stays as is. Document in Task 13. |
| Operator history shows settled time from `LastGatewayAttemptAtUtc` | Worker sets it on the successful poll; covered by a Task 10 test. |
