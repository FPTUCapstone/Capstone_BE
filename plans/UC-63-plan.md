# UC-63 View Payout Records Backend Implementation Plan

## Status and baseline

**Replanned 2026-10-12** against the latest SRS Use Case catalogue and the approved shared-route decision. UC-63 remains the Staff review scope; it does not become an Administrator settlement feature merely because both roles can enter `/admin/payouts`.

## Task 1 — Payout lifecycle schema and local test reset

1. For local/dev/test only, delete `payment.PayoutItems` and then `payment.Payouts`; seed fresh lifecycle fixtures. Do not ship a production data-reset migration.
2. Add a forward migration replacing legacy payout status constraints with `PendingReview`, `ApprovedForSettlement`, `SettlementProcessing`, `Settled`, `SettlementFailed`, and `Rejected`.
3. Add fields for Staff review (`reviewed_by`, `reviewed_at`, `rejection_reason`), Administrator authorization (`settlement_authorized_by`, `settlement_authorized_at`), and gateway outcome (`gateway_reference`, `gateway_failure_reason`, attempt metadata).
4. Add constraints rejecting inconsistent state/field combinations and preserve one active payout per operator and settlement period.

Exit: canonical schema, migration, EF mapping, and persistence tests agree. No legacy-status mapping exists.

## Task 2 — Server-enforced Staff review scope

- Keep or rename the existing review query as a **Staff-only** handler. Its default and allowed statuses are `PendingReview`, `ApprovedForSettlement`, and `Rejected`.
- Do not accept a role/scope selector from URL, body, or client claims. Resolve scope only from the authenticated server principal.
- Apply controller and handler defense-in-depth authorization for the endpoint.
- The Administrator settlement query (UC-65) is **not** built in this task. See Non-goals — it gets its own endpoint, handler, and plan when UC-65 is started.

Tests first: Staff allowed, Administrator denied, Traveler and Tour Operator denied.

## Task 3 — Read-only Staff list

- Apply keyword, allowed status, overlap period, deterministic ordering, then pagination.
- Return persisted snapshot amounts only and `totalCount`/`totalAmount` for the filtered page set.
- Do not expose bank details, gateway data, reviewer identity, authorization, or execution-state fields.
- Keep `AsNoTracking`; no `SaveChanges` or audit write occurs in the list handler.

Tests first: default queue, explicit allowed status, denied execution status, filter composition, empty page, stable ordering, summary semantics, and no mutation.

## Task 4 — HTTP and OpenAPI

- Document the Staff review endpoint's own authorization scope. The shared FE route (`/admin/payouts`) also reaching an Administrator settlement scope is a UC-65 concern, not documented here as if built.
- Update ProblemDetails/OpenAPI and integration tests for `401`, allowed `200`, wrong-role `403`, invalid request `400`, and safe empty `200`.

## Task 5 — Verification

Run formatter, build, focused unit/API/SQL tests, then full repository checks. Review the exact branch head before PR creation.

## Non-goals

UC-64 detail/review, UC-73 approval, UC-74 rejection, payment-gateway integration implementation, and manual-retry UI are separate work.

**UC-65 (Administrator settlement endpoint, its authorization scope, the `ApprovedForSettlement` → `SettlementProcessing` transition, the payout worker/outbox handoff, and the bidirectional Staff/Administrator settlement tests) is also a non-goal of this plan.** An earlier draft of this plan described that work — an Administrator-only settlement query, a documented settlement authorization scope, and a dedicated "Payout worker handoff contract" task — as if it were part of UC-63's delivery; none of it is implemented in this codebase. UC-65's own plan will be written when that use case is actually started.