# UC-65 Backend API contract — Confirm Payout Settlement

Status: **implemented on `feature/uc65-confirm-payout-settlement` (not merged yet).** Authority:
`specs/UC-65-spec.md` (Approved 2026-11-01) and the UC catalogue of Report 3 (UC-65, UC-73, BR-105). For
Capstone_FE. Web only (`ADMIN_WEB_ONLY`). The older SRS §3.9.9.3 (bank transfer reference, `Confirmed`
status) is superseded and is **not** implemented.

## What changes for the FE

`/admin/payouts/settlement` currently shows `PayoutSettlementUnavailableView` ("Pending Backend Settlement
Service"). Replace it with a queue, a detail and a confirm dialog on the endpoints below.

- The Administrator only **authorizes**. They never type a bank transfer reference and never edit an amount.
- Authorizing returns `202 Accepted` while the payout is `SettlementProcessing`. The transfer runs
  asynchronously in a worker, so the final result (`Settled` or `SettlementFailed`) appears later:
  refresh the queue or the detail until the status leaves `SettlementProcessing`.
- `PAYOUT_STATUSES` in `payoutRecords.ts` lists only `PendingReview`, `ApprovedForSettlement`, `Rejected`.
  Add `SettlementProcessing`, `Settled`, `SettlementFailed` for the Administrator scope.
- The Staff routes stay Staff-only and the settlement routes are Administrator-only. Neither role can use the
  other's endpoints (`403`), so the shared `/admin/payouts` route must send each role to its own API.
  The Web BFF proxy for `/api/admin/payouts` is Staff-only today; settlement needs its own proxy path.

## Auth

`Authorization: Bearer <token>`. Role **`Administrator`**, account `Active`. Staff, TourOperator and Traveler
get `403`; no session gets `401`.

## Endpoints

| Method and path | Use |
| --- | --- |
| `GET /api/v1/admin/payout-settlements` | Settlement queue. |
| `GET /api/v1/admin/payout-settlements/{payoutId}` | Settlement detail. |
| `POST /api/v1/admin/payout-settlements/{payoutId}/authorize` | The UC-65 action. No body. |

### `GET /api/v1/admin/payout-settlements`

Query parameters, all optional:

| Name | Notes |
| --- | --- |
| `status` | `ApprovedForSettlement` (default), `SettlementProcessing`, `Settled`, `SettlementFailed`. Anything else, including `PendingReview`, `Rejected`, `Cancelled`, is `400` `payout.invalid_status`. |
| `keyword` | up to 200 characters; matches the payout code or the operator company name. |
| `periodFrom`, `periodTo` | `YYYY-MM-DD`; a payout is included when its period overlaps the range. `periodFrom` after `periodTo` is `400` `payout.invalid_period_range`. |
| `pageNumber` | default `1` |
| `pageSize` | default `20`, maximum `100` |

Ordering is fixed: newest period first, then operator id, then payout id (descending). The response is the same
shape as the Staff review list (`PayoutsResponseDto`): `pageNumber`, `pageSize`, `totalCount`, `totalPages`,
`totalAmount` (sum of `netAmount` over the whole filtered set) and `items[]` with string ids, `payoutCode`,
`operator { userId, companyName }`, `periodStart`, `periodEnd`, `periodCount`, `grossRevenue`,
`commissionAmount`, `netAmount`, `requestedAtUtc`, `status`. Amounts are persisted snapshots in VND and are
never recalculated. The list carries no bank, reviewer or gateway data.

### `GET /api/v1/admin/payout-settlements/{payoutId}`

`Cache-Control: no-store`. Returns a payout in the settlement scope only; any other payout is `404`
`payout.not_found`. Each successful view writes an audit row (BR-130).

```json
{
  "payoutId": "123",
  "payoutCode": "PO-000123",
  "status": "SettlementProcessing",
  "settlementPeriod": { "startDate": "2026-09-01", "endDate": "2026-09-30" },
  "tourOperator": { "operatorId": "7", "companyName": "Company" },
  "grossRevenue": 12000000.0,
  "commissionRate": 0.1,
  "commissionAmount": 1200000.0,
  "netPayoutAmount": 10800000.0,
  "requestedAtUtc": "2026-10-03T08:15:00+00:00",
  "reviewedAtUtc": "2026-10-04T08:15:00+00:00",
  "reviewedBy": "5",
  "settlementAuthorizedAtUtc": "2026-11-01T09:00:00+00:00",
  "settlementAuthorizedBy": "2",
  "beneficiary": { "bankName": "Vietcombank", "accountHolder": "Nguyen Van A", "maskedAccountNumber": "****1234" },
  "bankBinResolved": true,
  "outcome": {
    "gatewayState": "Submitted",
    "failureCode": null,
    "submittedAtUtc": "2026-11-01T09:00:05+00:00",
    "reconciliationRequired": false
  }
}
```

- `outcome` is `null` while the status is `ApprovedForSettlement`.
- `bankBinResolved` tells the UI whether authorizing will find the bank in the catalogue. When it is `false`,
  show a warning before the dialog: the action would be refused with `422 payout.bank_bin_unresolved`.
- `outcome.gatewayState` is the worker's progress: `NotQueued` (a payout that was never queued),
  `Pending`, `Submitting`, `Submitted`, `Completed`, `ManualReconciliation`.
- `outcome.failureCode` is a safe code for `SettlementFailed`: `gateway_rejected`, `gateway_failed`,
  `gateway_cancelled`, `gateway_reversed`, `gateway_insufficient_balance`, `gateway_invalid_account`, `gateway_unauthorized`,
  `gateway_unavailable`, `beneficiary_incomplete`, `beneficiary_unreadable`. Map them to friendly text; never
  show the raw code to an Operator.
- `reconciliationRequired = true` means the system stopped after 24 hours without a final answer. The payout
  stays `SettlementProcessing` and **no second transfer is ever created automatically**; a person reconciles it
  with the payment provider. Show a clear banner and do not offer a retry.
- The full account number, the protected value and the provider's transfer reference are never returned.

### `POST /api/v1/admin/payout-settlements/{payoutId}/authorize`

No body. Show the confirmation dialog first (CR-05). On success the response is **`202 Accepted`**:

```json
{ "payoutId": "123", "payoutCode": "PO-000123", "status": "SettlementProcessing", "authorizedAtUtc": "2026-11-01T09:00:00+00:00" }
```

The status change and the queued work happen together; the transfer itself is not made during this request.

| HTTP | `errorCode` | When | Suggested UI |
| --- | --- | --- | --- |
| 401 | | no session | sign in |
| 403 | `Payouts.Forbidden` | not an Administrator | access denied |
| 404 | `payout.not_found` | unknown payout | refresh the queue |
| 409 | `payout.not_approved_for_settlement` | already authorized/settled by someone else, or not approved; `currentStatus` in the problem details | refresh and show the current status |
| 422 | `payout.beneficiary_incomplete` | bank name, account holder or account number missing | tell the Administrator to ask the Operator to fix the bank account |
| 422 | `payout.amount_invalid` | the net amount is not a positive whole number of VND | contact the developers; do not edit the amount |
| 422 | `payout.bank_bin_unresolved` | the bank name does not match the standard bank list exactly | ask the Operator to correct the bank name |
| 500 | | unexpected failure | retry later |

Errors follow the project's RFC 7807 `ProblemDetails` with an `errorCode` extension.

## Statuses

`ApprovedForSettlement` → (authorize) → `SettlementProcessing` → `Settled` or `SettlementFailed`.
`SettlementFailed` is final in this release: there is no retry endpoint (a later use case).

The Operator is notified in-app on `Settled` and `SettlementFailed`.

## Not in this contract

Manual retry, a reconciliation screen, and the Staff review endpoints (see UC-63/64/73/74).
