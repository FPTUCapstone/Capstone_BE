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
| Settlement | Administrator | `ApprovedForSettlement` | UC-65 settlement queue — not implemented by this PR. See Non-goals. |

**UC-63 delivers the Review scope only.** `GET /api/v1/admin/payouts` is Staff-only (`[Authorize(Roles = "Staff")]`); an Administrator request to it is `403`. The Settlement scope's endpoint, its own authorization boundary, and its tests are UC-65 work and do not exist in this codebase yet — they must not be implied as already built by this spec. A client-supplied role or status must never expand the server-selected scope.

## UC-63 review-query contract

Staff may filter only `PendingReview`, `ApprovedForSettlement`, and `Rejected`. Settlement execution states (`SettlementProcessing`, `Settled`, `SettlementFailed`) are outside UC-63. The endpoint never approves, rejects, authorizes, recalculates, or mutates a payout.

| Parameter | Default | Rule |
| --- | --- | --- |
| `keyword` | empty | Trimmed, max 200; matches the persisted `Payout.PayoutCode` (the code UC-46 issued to the Tour Operator) or operator company name. The code is never derived from `payoutId`. |
| `status` | `PendingReview` | One Staff-visible status. |
| `periodFrom` / `periodTo` | empty | ISO `yyyy-MM-dd`; periods overlap when `PeriodStart <= periodTo` and `PeriodEnd >= periodFrom`; from must not be after to. |
| `pageNumber` | 1 | At least 1. |
| `pageSize` | 20 | Fixed at 20 in the Web BFF contract. |

The response keeps string IDs, the persisted `payoutCode`, persisted backend-calculated money amounts, settlement period (`periodStart`/`periodEnd`) and `periodCount` (how many settlement months the request covers — UC-46 aggregates several months into one available-balance payout), requested date, status, and operator company name. `totalCount` and `totalAmount` describe the whole filtered result set, not only the current page. A future dashboard-only `pendingReviewCount` or `pendingReviewAmount` must explicitly be calculated with status fixed to `PendingReview`, independently of the requested status filter.

No operator bank account, gateway reference, reviewer identity, or final-settlement data is exposed by UC-63.

## Authorization and errors

- Missing/invalid session: `401`.
- Review query: any non-Staff role, including Administrator: `403`.
- Settlement query: UC-65 scope, not implemented by this PR (see Non-goals).
- Invalid filter: `400` with a stable safe error code.
- Empty result: successful empty page; the Web presents its locked no-data message.
- Unexpected failure: `500` without infrastructure detail.

## Data and migration decision

UC-46 snapshots the backend-calculated payout amount and its calculation basis when the request is created. List endpoints read those persisted snapshots and never recalculate them.

UC-63 adds **no migration**. The lifecycle schema it reads — `payout_code`, `period_count`, the multi-month snapshot (`PayoutPeriodBreakdowns`), `Cancelled`, and the per-operator active-payout unique index — is delivered by the UC-46 migrations (`20261022_uc46_payout_settlement_request.sql`, `20261023_uc46_payout_available_balance.sql`). Earlier drafts that proposed resetting `payment.PayoutItems`/`payment.Payouts` and re-creating the legacy status constraints are withdrawn: they would destroy UC-46 data and conflict with those migrations. No automatic mapping from legacy `Pending`, `Requested`, `Confirmed`, or `Paid` values is permitted.

## Product decisions recorded for UC-63

- **Actor:** Staff. The SRS use-case catalogue lists UC-63 as Staff; the older detailed section 3.9.9.1 says Administrator. This spec follows the catalogue and the UC-46 → UC-64/73/74 → UC-65 workflow. The SRS itself must be corrected by the PO; until then this spec is the decision of record.
- **`Cancelled` is not in the Staff review queue.** The queue exposes `PendingReview`, `ApprovedForSettlement`, and `Rejected` only. Cancellation handling is part of UC-64 scope, not UC-63, so no Cancelled filter or Cancelled display is added here.
- **Summary counters:** `totalCount` and `totalAmount` over the filtered result set. The older SRS wording "Total Requested" / "Total Confirmed" is not carried over — `Confirmed` is not a canonical UC-46 status.

The database model must contain Staff review fields, Administrator authorization fields, and gateway outcome fields. UC-63 reads those records but does not write them.

## Acceptance criteria

1. A valid Staff session receives the default `PendingReview` review queue; a valid Administrator session is denied (`403`) from this endpoint.
2. Status filtering cannot expose settlement execution states through UC-63.
3. Filters compose, ordering is deterministic, pagination is applied after filtering, and the response is read-only.
4. Monetary values remain persisted snapshots and are never recalculated by the endpoint.
5. Tests cover Staff allowed, Administrator denied, invalid filters, pagination, empty data, and no mutation — all against this one Staff-only review endpoint.

## Non-goals

UC-64 detail/review, UC-73 approval, UC-74 rejection, payout worker/outbox implementation, and payout generation are separate work. UC-64/73/74 are implemented on their own branch and are not deliverables of UC-63; this spec does not claim they are absent from the repository.

**UC-65 (Administrator settlement) is also a non-goal of this PR**, despite earlier drafts of this spec describing its endpoint, authorization boundary, and tests as deliverables. Nothing beyond the workflow diagram's description of UC-65's role (above) is implemented here. The Administrator-settlement endpoint, its authorization scope, and its bidirectional Staff/Administrator tests will be designed and planned when UC-65 itself is started — not drafted in advance inside this document.