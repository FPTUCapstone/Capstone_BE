# UC-63 View Payout Records Backend Implementation Plan

## Status and baseline

**Replanned 2026-10-12** against the latest SRS Use Case catalogue and the approved shared-route decision. UC-63 remains the Staff review scope; it does not become an Administrator settlement feature merely because both roles can enter `/admin/payouts`.

## Task 1 — Payout lifecycle schema and local test reset

1. For local/dev/test only, delete `payment.PayoutItems` and then `payment.Payouts`; seed fresh lifecycle fixtures. Do not ship a production data-reset migration.
2. Add a forward migration replacing legacy payout status constraints with `PendingReview`, `ApprovedForSettlement`, `SettlementProcessing`, `Settled`, `SettlementFailed`, and `Rejected`.
3. Add fields for Staff review (`reviewed_by`, `reviewed_at`, `rejection_reason`), Administrator authorization (`settlement_authorized_by`, `settlement_authorized_at`), and gateway outcome (`gateway_reference`, `gateway_failure_reason`, attempt metadata).
4. Add constraints rejecting inconsistent state/field combinations and preserve one active payout per operator and settlement period.

Exit: canonical schema, migration, EF mapping, and persistence tests agree. No legacy-status mapping exists.

## Task 2 — Separate server-enforced query scopes

- Keep or rename the existing review query as a **Staff-only** handler. Its default and allowed statuses are `PendingReview`, `ApprovedForSettlement`, and `Rejected`.
- Add a distinct **Administrator-only** settlement query for UC-65. Its default is `ApprovedForSettlement`; exact history/filter fields belong to the UC-65 contract.
- Do not accept a role/scope selector from URL, body, or client claims. Resolve scope only from the authenticated server principal.
- Apply controller and handler defense-in-depth authorization for each endpoint.

Tests first: Staff allowed/Administrator denied for review; Administrator allowed/Staff denied for settlement; Traveler and Tour Operator denied for both.

## Task 3 — Read-only Staff list

- Apply keyword, allowed status, overlap period, deterministic ordering, then pagination.
- Return persisted snapshot amounts only and `totalCount`/`totalAmount` for the filtered page set.
- Do not expose bank details, gateway data, reviewer identity, authorization, or execution-state fields.
- Keep `AsNoTracking`; no `SaveChanges` or audit write occurs in the list handler.

Tests first: default queue, explicit allowed status, denied execution status, filter composition, empty page, stable ordering, summary semantics, and no mutation.

## Task 4 — HTTP and OpenAPI

- Document the Staff review endpoint and Administrator settlement endpoint as separate authorization scopes even if both are reached through a shared FE route.
- Update ProblemDetails/OpenAPI and integration tests for `401`, allowed `200`, wrong-role `403`, invalid request `400`, and safe empty `200`.

## Task 5 — Payout worker handoff contract

- UC-65 changes `ApprovedForSettlement` to `SettlementProcessing` atomically and writes the durable work item/outbox record.
- The payout-processing worker, not the Administrator request, invokes the gateway and makes the terminal transition to `Settled` or `SettlementFailed`.
- Bound retries to transient failures; record attempts and final safe failure reason. Manual retry remains outside this scope.

## Task 6 — Verification

Run formatter, build, focused unit/API/SQL tests, then full repository checks. Review the exact branch head before PR creation.

## Non-goals

UC-64 detail/review, UC-73 approval, UC-74 rejection, payment-gateway integration implementation, and manual-retry UI are separate work.