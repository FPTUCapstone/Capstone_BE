# UC-63 View Payout Records Backend Specification

## Status

**Amended 2026-10-12** against the latest SRS Use Case catalogue and the approved shared-payout-list decision. Older Detailed Functional Requirements payout sections are intentionally excluded because the team will revise them jointly.

## Authoritative workflow

```text
PendingReview --Staff approve (UC-73)--> ApprovedForSettlement
PendingReview --Staff reject (UC-74)--> Rejected
ApprovedForSettlement --Administrator authorize (UC-65)--> SettlementProcessing
SettlementProcessing --payout-processing worker / gateway success--> Settled
SettlementProcessing --payout-processing worker / final failure--> SettlementFailed
```

UC-65 authorizes and initiates payout processing. The asynchronous transition from `SettlementProcessing` to `Settled` or `SettlementFailed` is performed by the payout-processing worker from the configured payment or banking gateway result. The worker applies the approved bounded retry policy for transient failures; manual retry is a future use case.

## Shared route and backend scopes

`/admin/payouts` is a shared internal route, available to **Staff** and **Administrator**. The route itself does not grant either role the other role’s permissions. The authenticated role selects a server-enforced scope:

| Scope | Actor | Default status | Purpose |
| --- | --- | --- | --- |
| Review | Staff | `PendingReview` | UC-63 read-only review queue; UC-64/73/74 follow from this scope. |
| Settlement | Administrator | `ApprovedForSettlement` | UC-65 settlement queue; it never exposes Staff review actions. |

The backend exposes separate authorization boundaries for these scopes. The existing `GET /api/v1/admin/payouts` may remain the Staff-only review query; the Administrator settlement query must use a separately authorized handler/endpoint. A client-supplied role or status must never expand the server-selected scope.

## UC-63 review-query contract

Staff may filter only `PendingReview`, `ApprovedForSettlement`, and `Rejected`. Settlement execution states (`SettlementProcessing`, `Settled`, `SettlementFailed`) are outside UC-63. The endpoint never approves, rejects, authorizes, recalculates, or mutates a payout.

| Parameter | Default | Rule |
| --- | --- | --- |
| `keyword` | empty | Trimmed, max 200; matches derived `PO-{payoutId}` or operator company name. |
| `status` | `PendingReview` | One Staff-visible status. |
| `periodFrom` / `periodTo` | empty | ISO `yyyy-MM-dd`; periods overlap when `PeriodStart <= periodTo` and `PeriodEnd >= periodFrom`; from must not be after to. |
| `pageNumber` | 1 | At least 1. |
| `pageSize` | 20 | Fixed at 20 in the Web BFF contract. |

The response keeps string IDs, persisted backend-calculated money amounts, settlement period, requested date, status, and operator company name. `totalCount` and `totalAmount` always describe the current filtered Staff result. A future dashboard-only `pendingReviewCount` or `pendingReviewAmount` must explicitly be calculated with status fixed to `PendingReview`, independently of the requested status filter.

No operator bank account, gateway reference, reviewer identity, or final-settlement data is exposed by UC-63.

## Authorization and errors

- Missing/invalid session: `401`.
- Review query: any non-Staff role, including Administrator: `403`.
- Settlement query: any non-Administrator role, including Staff: `403`.
- Invalid filter: `400` with a stable safe error code.
- Empty result: successful empty page; the Web presents its locked no-data message.
- Unexpected failure: `500` without infrastructure detail.

## Data and migration decision

UC-46 snapshots the backend-calculated payout amount and its calculation basis when the request is created. List endpoints read those persisted snapshots and never recalculate them.

Current test payout data is disposable. Before applying the lifecycle schema migration in local/dev/test, reset `payment.PayoutItems` and then `payment.Payouts`; seed only the new statuses. No automatic mapping from legacy `Pending`, `Requested`, `Confirmed`, or `Paid` values is permitted and no production reset migration may be shipped.

The database model must contain Staff review fields, Administrator authorization fields, and gateway outcome fields. UC-63 reads those records but does not write them.

## Acceptance criteria

1. A valid Staff session receives the default `PendingReview` review queue; a valid Administrator session never receives Staff scope or review actions.
2. A valid Administrator session resolves only to the independently authorized settlement scope, whose default is `ApprovedForSettlement`.
3. Status filtering cannot expose settlement execution states through UC-63.
4. Filters compose, ordering is deterministic, pagination is applied after filtering, and the response is read-only.
5. Monetary values remain persisted snapshots and are never recalculated by the endpoint.
6. Tests cover Staff allowed, Administrator denied from the review endpoint, Administrator settlement-scope allowed, Staff denied from settlement scope, invalid filters, pagination, empty data, and no mutation.

## Non-goals

UC-64 detail/review, UC-73 approval, UC-74 rejection, UC-65 authorization, payout worker/outbox implementation, and payout generation are separate work.