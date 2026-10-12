# UC-65 Confirm Payout Settlement Backend Specification

## Scope

UC-65 is the **Administrator-only settlement scope** of the shared payout module. It lists requests eligible for settlement with `ApprovedForSettlement` as the default and permits exactly one business action: authorize payout processing.

## Settlement query contract

`GET /api/v1/admin/payout-settlements` is the Administrator-only read query. The server fixes its data scope to `ApprovedForSettlement`; the request has no status or role selector. It supports the same safe `keyword`, overlap-period, `pageNumber`, and `pageSize` filtering semantics as the Staff review list and returns persisted snapshot amounts with filtered `totalCount` and `totalAmount`.

The response does not expose Staff review actions, reviewer identity, bank account data, or gateway references.

## Authorization and transition

Only an Administrator may authorize `ApprovedForSettlement → SettlementProcessing`. The command validates the current status and persists the status transition plus a durable payout-work item atomically. Staff cannot invoke this endpoint, and an Administrator cannot approve, reject, or bypass Staff review.

The background payout-processing worker consumes the durable work item, invokes the configured payment/banking gateway, and transitions `SettlementProcessing → Settled` or `SettlementFailed`. It retries only transient failures within the configured bound. Gateway outcome handling is not an Administrator action.

## Data boundaries

UC-65 reads persisted payout snapshots. It never recalculates gross, commission, or net amount. The list/details may expose only Administrator-authorized settlement data; bank data and gateway references require explicit least-privilege review before inclusion.

## Required tests

- Administrator may authorize only `ApprovedForSettlement`.
- Staff and other roles receive `403`.
- Wrong current status is rejected without work creation.
- Command transaction persists `SettlementProcessing` and work item together.
- Worker success/final failure makes only valid terminal transitions.
