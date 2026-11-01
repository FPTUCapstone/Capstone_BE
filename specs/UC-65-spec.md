# UC-65 Confirm Payout Settlement Backend Specification

## Status

**Approved 2026-11-01** by the developer, including the five decisions and the PayOS contract confirmed below. Written against the SRS use-case catalogue (UC-65, UC-73, BR-105 as updated 2026-11-01). The implementation plan is `plans/UC-65-plan.md`; no implementation or migration has started.

## Authoritative workflow

```text
ApprovedForSettlement --Administrator authorizes (UC-65)--> SettlementProcessing
SettlementProcessing  --worker: PayOS payout SUCCEEDED-->    Settled            (terminal)
SettlementProcessing  --worker: definitive failure-->        SettlementFailed   (active; manual retry is a future UC)
SettlementProcessing  --worker: no result after 24 h-->      stays SettlementProcessing + manual reconciliation alert
```

The Administrator only authorizes. They never transfer funds, enter a transfer reference, or change the amount. Settlement outcomes are written by the payout-processing worker from the gateway result.

## Product decisions recorded for UC-65 (decision of record)

Until the PO corrects the SRS, this spec is the decision of record.

1. **Flow follows the UC catalogue and BR-105, not SRS §3.9.9.3.** §3.9.9.3 still describes the superseded manual flow (bank transfer reference, `Confirmed` status, `Pending` precondition). None of that is implemented. The PO will correct §3.9.9.3 and BR-105.
2. **Gateway is PayOS payout only.** BR-105 and the Batch Payout Disbursement Processor row name VNPay as primary; the team decided on PayOS (2026-11-01). VNPay is out of scope. The gateway sits behind a port so another provider can be added later without changing the workflow.
3. **Polling window is 24 hours** from the first accepted submission, with increasing backoff. After it expires the system stops polling, keeps the status, logs and alerts for manual reconciliation. It never sets `SettlementFailed` on timeout and never creates a second transfer.
4. **Beneficiary bank code is a 6-digit NAPAS/VietQR BIN stored in a new `bank_bin` column.** Legacy rows without a BIN may be mapped from `BankName` only by exact match against the standard catalogue: Unicode-NFC-normalized, trimmed, compared case-insensitively (`OrdinalIgnoreCase`, decided 2026-11-01). No match or missing data blocks the payout immediately; no fuzzy, partial, accent-stripping, or similarity matching, ever.
5. **Mock by default.** Development, Test, and CI use `MockDisbursementGateway`. A real PayOS transfer requires a deliberate configuration change (two switches, below).
6. **Administrator queue and outbox are ported from Linh's branch** `origin/feature/linhnv-confirm-payout-settlement` (base is 483 commits behind `develop`, no PR). Intent and contract are reused; code is re-written on `develop`, including two-way Staff/Administrator authorization tests.

## Endpoints (Administrator-only)

Controller: new `AdminPayoutSettlementsController`, `[Authorize(Roles = "Administrator")]`, route `api/v1/admin/payout-settlements`. The existing Staff-only `AdminPayoutsController` is not changed. Handlers re-check the role (defence in depth, same pattern as UC-73).

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/v1/admin/payout-settlements` | Settlement queue |
| GET | `/api/v1/admin/payout-settlements/{payoutId}` | Settlement detail |
| POST | `/api/v1/admin/payout-settlements/{payoutId}/authorize` | UC-65 action |

### Queue

Same filter semantics as the Staff review list (`keyword`, `periodFrom`/`periodTo` overlap, `pageNumber`, `pageSize` fixed 20, `totalCount`/`totalAmount` over the filtered set) and the same persisted-snapshot amounts. `status` is limited to the **settlement scope**: `ApprovedForSettlement` (default), `SettlementProcessing`, `Settled`, `SettlementFailed`. `PendingReview`, `Rejected`, `Cancelled` are never returned (`400 payout.invalid_status`). The role fixes the scope server-side; a client cannot widen it.

### Detail

Persisted snapshot only: payout code, operator, period, gross, commission, net, status, requested/reviewed/authorized timestamps and actors, masked beneficiary (`bankName`, `accountHolder`, `maskedAccountNumber`, `bankBinResolved` boolean), and for processing states a safe outcome summary (`gatewayState`, `failureCode`, `submittedAtUtc`, `reconciliationRequired`). Never returns `AccountNumberProtected`, a decrypted account number, gateway credentials, or the raw gateway response. Viewing writes an audit row (BR-130 parity with UC-64: the detail exposes beneficiary data).

### Authorize

`POST …/{payoutId}/authorize`, no body. Success: **`202 Accepted`** with `{ payoutId, payoutCode, status: "SettlementProcessing", authorizedAtUtc }` (processing is asynchronous).

In **one atomic save** (a single `SaveChanges`, the same pattern as UC-73) the handler:

1. Requires `UserRole.Administrator` and an active session.
2. Loads the payout tracked; requires `Status == ApprovedForSettlement`.
3. Validates the payout is payable (below).
4. Resolves and persists `bank_bin` (below).
5. Calls the new domain method `Payout.AuthorizeSettlement(adminId, nowUtc)` → `SettlementProcessing`, sets `SettlementAuthorizedBy/AtUtc`.
6. Inserts exactly one `payment.PayoutSettlementWorkItems` row (`Pending`, `next_attempt_at = now`).
7. Inserts an audit row.
8. Saves once. The `Status` concurrency token plus the unique `payout_id` on the work item make a double click or two administrators race to exactly one winner; the loser gets `409`.

The handler never calls the gateway.

**Payable checks (fail-fast, nothing is written):**

- Beneficiary present: `BankName`, `AccountHolderName`, `AccountNumberProtected` non-empty → else `422 payout.beneficiary_incomplete`.
- `NetAmount` is a positive whole number of VND (PayOS `amount` is an integer) → else `422 payout.amount_invalid`. The amount is read from the snapshot and never recalculated.
- BIN resolution succeeds → else `422 payout.bank_bin_unresolved`.

**BIN resolution:**

1. If `Payout.BankBin` is set, it must exist in the catalogue.
2. Else map `BankName` by **exact match**: normalize both sides to Unicode NFC, trim leading/trailing whitespace only, compare with `StringComparison.OrdinalIgnoreCase` against the catalogue's official name or short name. NFC is canonical equivalence of the same text (composed vs. decomposed accents), not approximation; accents are never stripped and inner whitespace is never collapsed. Exactly one catalogue row must match. The comparison is done in the Application layer over the (small) catalogue, not with SQL collation.
3. Zero or more than one match, or a null/blank name → fail.
4. On success persist the BIN on the payout snapshot in the same transaction.

**Errors:** `401` no session; `403` any non-Administrator (Staff included); `404 payout.not_found`; `409 payout.not_approved_for_settlement` with `currentStatus` (also on concurrency conflict, re-read untracked as UC-73 does); `422` as above; `500` without infrastructure detail. Stable codes live in a new `PayoutSettlementErrorCodes`.

## Worker — `PayoutSettlementBackgroundService`

Follows the `RefundProcessingBackgroundService` / `RefundProcessor` pattern: a hosted sweep loop that creates a scope, claims due work items in serializable transactions, performs the gateway call **outside** the transaction, then records the outcome in a second transaction. Lease-based recovery of items left `Submitting` by a crashed instance.

### Work item states

`Pending` → `Submitting` → `Submitted` → `Completed`, or → `ManualReconciliation`.

### Submit (state `Pending`)

Preconditions re-checked: payout still `SettlementProcessing`, BIN present. The account number is decrypted via `IPiiProtector.Unprotect` in memory for this one call and is never logged, persisted in plaintext, or placed in an exception message.

Request to the port: `ReferenceId = PayoutCode`, `Amount = NetAmount`, `Description` = `"TripMate Payout {PayoutCode}"` reduced to ASCII without diacritics and truncated to 25 characters (no PII; the full code is carried by `ReferenceId`), `ToBin`, `ToAccountNumber`, `IdempotencyKey = "tripmate-payout-{payoutId}"` — stable per payout across every retry.

| Gateway result | Action |
|---|---|
| Accepted (payout id returned) | Store `gateway_reference`, `submitted_at`, `poll_deadline_at = submitted_at + 24 h`; state `Submitted`; schedule first poll. |
| Definitive rejection: HTTP 4xx other than 408/429, or response `code != "00"` (validation, invalid account, insufficient payout balance, authentication) | Payout → `SettlementFailed` with a safe `GatewayFailureReason` code (e.g. `gateway_rejected`, `gateway_insufficient_balance`); work item `Completed`; notify operator; no automatic retry. |
| Transient (timeout, network, 5xx, 408, 429) | Increment attempt; retry with backoff while attempts < `MaxSubmitAttempts` (3). |
| Idempotency key already exists | Treat as "maybe created": look up by `referenceId` (below). |
| Transient and attempts exhausted | Look up by `referenceId`: found → treat as Accepted; clean "not found" → `SettlementFailed` (`gateway_unavailable`, nothing was created); lookup itself fails → `ManualReconciliation`. |

`gateway_attempt_count` and `last_gateway_attempt_at` on `payment.Payouts` are incremented on every gateway call, which also satisfies the existing `CK_Payouts_LifecycleState` constraint (`SettlementFailed`/`Settled` require ≥ 1 attempt).

### Poll (state `Submitted`)

`GET` the payout by id. Backoff schedule (configurable): 30 s, 1 m, 2 m, 5 m, 10 m, 30 m, then every 60 m, until `poll_deadline_at`.

| PayOS state | Action |
|---|---|
| `SUCCEEDED` | Payout → `Settled` (`gateway_reference` kept, `last_gateway_attempt_at = now`); work item `Completed`; audit; notify operator. |
| `PROCESSING` | Schedule next poll. |
| `FAILED`, `CANCELLED`, `REJECTED` or `REVERSED` (terminal failure states; `REJECTED`/`REVERSED` confirmed by the PO on 2026-11-01) | Payout → `SettlementFailed` at once (`gateway_failed` / `gateway_cancelled` / `gateway_rejected` / `gateway_reversed`), polling stops, audit and operator notification as for any failure. No 24 hour wait. |
| Any **unknown** state | Treated as `PROCESSING` plus a warning log. An unrecognized value must never be mapped to success or failure. |
| Transient poll error | Next poll per schedule; does not consume submit attempts. |

### Deadline

When `now > poll_deadline_at` and the state is still not terminal: stop polling, state `ManualReconciliation`, payout **stays `SettlementProcessing`**, structured `Error` log with `payoutId`/`gateway_reference` (no PII), audit row `PayoutReconciliationRequired`, and the detail endpoint reports `reconciliationRequired = true`. No `SettlementFailed`, no new transfer, no automatic retry.

### Notifications

On `Settled` and `SettlementFailed` insert an `InApp` `Notification` for the owning operator (types `PayoutSettled`, `PayoutSettlementFailed`, `RelatedEntityType = "Payout"`). The failure body is generic; internal failure codes are not shown. A notification failure never rolls back the payout outcome.

## Gateway port and configuration

New Application port `IPayoutDisbursementGateway` with `SubmitAsync`, `GetStatusAsync`, `FindByReferenceAsync`. It is separate from `IPaymentGateway` (collection/refund). Implementations in Infrastructure: `MockDisbursementGateway` (default; deterministic accepted → `SUCCEEDED`) and `PayOsPayoutGateway` (`POST /v1/payouts`, `GET /v1/payouts/{id}`, `GET /v1/payouts?referenceId=`, headers `x-client-id`, `x-api-key`, `x-signature`, `x-idempotency-key`). Adapter mapping lives only in the adapter; the worker sees normalized results.

**Railway check (2026-11-01, names only):** service `Capstone_BE` has `Payment__Provider` and `Payment__PayOs__ApiKey/ChecksumKey/ClientId/CancelUrl/ReturnUrl` (collection). **There are no payout-specific PayOS variables**, so payout credentials are not yet provisioned and the default stays Mock on Railway.

| Setting (env var) | Default | Notes |
|---|---|---|
| `Payment__Payout__Provider` | `Mock` | `Mock` or `PayOs`. |
| `Payment__Payout__PayOs__LiveTransfersEnabled` | `false` | Second switch. `Provider=PayOs` with this `false` fails startup, so a real transfer cannot happen by a single mistaken variable. |
| `Payment__Payout__PayOs__ClientId` / `ApiKey` / `ChecksumKey` | none | **Separate from `Payment__PayOs__*`; no fallback** to the collection credentials. Required (fail startup) only when `Provider=PayOs`. |
| `Payment__Payout__PayOs__BaseUrl` | `https://api-merchant.payos.vn` | |
| `PayoutSettlement__SweepInterval` / `BatchSize` / `ProcessingLeaseDuration` | 30 s / 20 / 5 min | Validated positive, same style as `RefundProcessingOptions`. |
| `PayoutSettlement__MaxSubmitAttempts` | 3 | |
| `PayoutSettlement__PollDeadline` | 24 h | |
| `PayoutSettlement__PollBackoff` | see above | |

`.env.example` gains the variable names with empty values. No secret is committed. CI and automated tests never select `PayOs`.

## Data and migration decision

New idempotent script `database/migrations/20261101_uc65_payout_settlement_processing.sql` (no EF migrations, AGENTS.md §3.1); run on the Azure UAT database for verification, not a local DB.

- `payment.Banks` (`bank_bin CHAR(6)` PK, `short_name NVARCHAR(50)`, `official_name NVARCHAR(200)`, unique on each name): the standard catalogue, seeded idempotently (`MERGE`/`IF NOT EXISTS`) with exactly the 18 rows the team supplied and verified on 2026-11-01 (Vietcombank 970436, VietinBank 970415, BIDV 970418, Agribank 970405, Techcombank 970407, MBBank 970422, ACB 970416, VPBank 970432, TPBank 970423, Sacombank 970403, HDBank 970437, VIB 970441, SHB 970443, Eximbank 970431, MSB 970426, OCB 970448, LPBank 970449, VietCapitalBank 970454; the full Vietnamese official names are in the migration). A bank not in this list fails fast with `422 payout.bank_bin_unresolved`; adding a bank is a new reviewed seed row, never a code guess.
- **Azure UAT check (2026-11-01, read-only):** `dbo.OperatorProfiles` (8 rows) and `payment.Payouts` (11 rows) contain **no** `bank_name`, holder name, or protected account number, so there is no existing value that could mismatch the catalogue. The one `ApprovedForSettlement` payout therefore has an incomplete beneficiary and authorizing it returns `422 payout.beneficiary_incomplete`. UAT of UC-65 needs a payout with beneficiary data from the catalogue; that seed is test data prepared separately from this migration. The `Banks` table and `bank_bin` column do not exist on Azure yet.
- `payment.Payouts.bank_bin CHAR(6) NULL` + check (exactly 6 digits). Existing rows stay `NULL`; no automatic backfill, so ambiguous data cannot be silently mapped. Resolution happens at authorization.
- `payment.PayoutSettlementWorkItems`: `work_item_id`, `payout_id` (FK, **unique**), `state`, `attempt_count`, `next_attempt_at`, `lease_expires_at`, `gateway_reference`, `submitted_at`, `poll_deadline_at`, `poll_count`, `last_polled_at`, `last_error_code`, `created_at`, `completed_at`, `row_version`. Indexes on `(state, next_attempt_at)`.
- No change to the existing status `CHECK` or lifecycle constraint is needed; the new transitions already satisfy `CK_Payouts_LifecycleState` provided the attempt count is incremented as above.
- `AuditActionTypes`: `AuthorizePayoutSettlement`, `ViewPayoutSettlementDetails`, `PayoutSettled`, `PayoutSettlementFailed`, `PayoutReconciliationRequired`. Worker events use `ActorUserId = null` (the column is nullable).

## Security and data handling

- Staff cannot authorize; Administrator cannot approve/reject (Staff endpoints stay Staff-only). Both directions are tested.
- The decrypted account number exists only inside the worker's single submit call. Logs, audit `before/after` JSON, notifications, API responses, and exceptions never contain it, `AccountNumberProtected`, the API key/checksum, or raw gateway bodies.
- Money is the persisted snapshot. UC-65 never recalculates gross, commission, or net.
- `x-signature` is computed by the adapter from the PayOS checksum key; the key is never logged.

## Acceptance criteria

1. An Administrator can list and open `ApprovedForSettlement` payouts; Staff and other roles get `403`; unauthenticated gets `401`.
2. Authorize moves exactly `ApprovedForSettlement → SettlementProcessing` and creates exactly one work item in one transaction; any failed precondition writes nothing.
3. A repeated or concurrent authorize yields one success and `409`; one work item exists.
4. An unresolvable BIN, incomplete beneficiary, or non-whole-VND amount returns `422` and leaves the payout `ApprovedForSettlement` with no work item. Only canonical exact matching (NFC, trim, case-insensitive) is used; no fuzzy BIN matching occurs anywhere.
5. The worker submits with the stable `referenceId` and idempotency key, never sends twice for the same payout, and never calls the gateway from the request path.
6. `SUCCEEDED` → `Settled`; definitive rejection → `SettlementFailed`; transient errors retry at most 3 times then reconcile by `referenceId`; unknown gateway states are never treated as terminal.
7. After 24 h without a terminal state the payout stays `SettlementProcessing`, polling stops, an alert/log and audit row are written, `reconciliationRequired` is true, and no new transfer is created.
8. The default configuration and every test use `MockDisbursementGateway`; selecting `PayOs` without `LiveTransfersEnabled` or without credentials fails startup.
9. No secret, account number, or raw gateway payload appears in logs, audit, notifications, or responses.
10. The operator is notified in-app on `Settled` and `SettlementFailed`.

## Required tests

- Authorization matrix both ways on all three endpoints (Administrator allowed; Staff, Traveler, TourOperator, anonymous denied), plus Administrator denied on Staff review endpoints and Staff denied on settlement endpoints.
- Authorize: success, wrong status, not found, concurrency/double click, atomicity (status + work item + audit together; rollback leaves nothing).
- BIN resolver: stored BIN valid and unknown, exact match on short and official name, case difference accepted, surrounding whitespace trimmed, NFC/NFD forms of the same name accepted, accent-stripped name rejected, inner-whitespace difference rejected, near-match/typo/prefix rejected, duplicate match rejected, blank or null name rejected.
- Payable checks: incomplete beneficiary, fractional or zero amount.
- Worker with a fake gateway: accepted, definitive rejection, transient then success, transient exhausted + lookup found / not found / lookup failed, idempotency-key-exists, `SUCCEEDED`, unknown state, deadline reached, lease recovery, no double submit.
- Migration test against SQL Server (constraint `CK_Payouts_LifecycleState` accepts the new transitions).
- Configuration validation: Mock default, `PayOs` without switch or credentials fails startup.
- A leakage test asserting no plaintext account number or credential in logs/audit/notification/response.

## PayOS adapter contract (confirmed by the team 2026-11-01)

The public PayOS documentation does not settle these points. The team confirmed the contract below, and the adapter implements exactly this. It remains defensive: anything outside it is treated as still processing, never as success or failure. No automated test ever calls PayOS; there is no sandbox, only production.

1. **States.** Besides `PROCESSING` and `SUCCEEDED`, the explicit failure states are `FAILED` and `CANCELLED`; at creation, HTTP 4xx or a response `code != "00"` is a definitive rejection. Any other state is `PROCESSING` plus a warning log.
2. **No payout webhook.** Polling `GET /v1/payouts/{id}` is the only mechanism.
3. **Idempotency.** `x-idempotency-key` is fixed as `tripmate-payout-{payoutId}`. A "key already exists" error, or any doubt that a previous attempt created the payout, is resolved with `FindByReferenceAsync` (`GET /v1/payouts?referenceId={PayoutCode}`) as in the Submit table.
4. **Limits.** `referenceId` = `PayoutCode` (ASCII letters, digits, hyphen; at most 24 characters here, within the 36–50 limit). `description` ≤ 25 ASCII characters without diacritics or PII, to fit NAPAS 24/7 transfer content.
5. **Lookup.** `GET /v1/payouts?referenceId={PayoutCode}`. An HTTP 200 with empty `data` after the 3 transient attempts are exhausted means nothing was created → `SettlementFailed` (`gateway_unavailable`). If the lookup call itself fails, the work item goes to `ManualReconciliation`.
6. **Sandbox.** Production only. No real transfer in automated tests; a real run is a single supervised, small-value acceptance run by the member holding the payout credentials, after `LiveTransfersEnabled` is deliberately turned on.
7. **Credentials.** The payout channel uses its own `ClientId` / `ApiKey` / `ChecksumKey`, fully separate from the collection channel, with no fallback to `Payment__PayOs__*`.

## Decisions resolved by the developer (2026-11-01)

1. **Queue status filter:** approved. The Administrator may filter the four settlement-scope statuses.
2. **Exact match:** case-insensitive (`OrdinalIgnoreCase`) after trim, plus NFC normalization (canonical equivalence only). Fuzzy matching stays forbidden.
3. **`bank_bin` on `OperatorProfile` and the operator bank-account API:** out of scope; a UC-46 follow-up.
4. **Authorize returns `202 Accepted`** with `{ payoutId, payoutCode, status: "SettlementProcessing", authorizedAtUtc }`.
5. **Insufficient payout-account balance:** a definitive failure (`SettlementFailed`, safe code, operator notified, no automatic retry).

## Non-goals

Manual retry of `SettlementFailed`; automated reconciliation or a reconciliation UI; VNPay or any other gateway; recalculating or editing payout amounts; changes to Staff UC-63/64/73/74 behaviour; batch payouts (`/v1/payouts/batch`); the Web screens (a separate FE spec, FE-first rule applies); operator-profile BIN capture (see decision 3).
