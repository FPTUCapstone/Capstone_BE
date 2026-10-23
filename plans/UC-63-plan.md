# UC-63 View Payout Records Backend Implementation Plan

## Status and baseline

**Replanned 2026-10-12** against the latest SRS Use Case catalogue and the approved shared-route decision. UC-63 remains the Staff review scope; it does not become an Administrator settlement feature merely because both roles can enter `/admin/payouts`.

## Task 1 — Verify the lifecycle schema UC-63 reads (no migration)

Superseded: the original Task 1 (reset `payment.PayoutItems`/`payment.Payouts`, then re-create the legacy lifecycle constraints) is withdrawn. UC-46's migrations `20261022_uc46_payout_settlement_request.sql` and `20261023_uc46_payout_available_balance.sql` already deliver everything it proposed, including `Cancelled`, `payout_code`, `period_count`, the multi-month breakdown table, and the per-operator active-payout index. Re-running the reset would delete UC-46 data; re-creating the old constraints would drop `Cancelled` and collide with the new indexes.

1. Confirm the UC-46 migrations are applied on the target database and that EF mapping (`PayoutConfiguration`) agrees with the schema.
2. Persistence tests: the Staff-visible columns (`payout_code`, `period_start`/`period_end`, `period_count`, `net_amount`, `status`, `requested_at`, `rejection_reason`, `reviewed_by`/`reviewed_at`) read back as mapped.
3. UC-63 adds no migration and no reset.

Exit: UC-63 reads the canonical schema as-is; no migration file is added by this plan.

## Task 2 — Server-enforced Staff review scope

- Keep or rename the existing review query as a **Staff-only** handler. Its default and allowed statuses are `PendingReview`, `ApprovedForSettlement`, and `Rejected`.
- Do not accept a role/scope selector from URL, body, or client claims. Resolve scope only from the authenticated server principal.
- Apply controller and handler defense-in-depth authorization for the endpoint.
- The Administrator settlement query (UC-65) is **not** built in this task. See Non-goals — it gets its own endpoint, handler, and plan when UC-65 is started.

Tests first: Staff allowed, Administrator denied, Traveler and Tour Operator denied.

## Task 3 — Read-only Staff list

- Apply keyword, allowed status, overlap period, deterministic ordering, then pagination.
- Keyword matches the persisted `Payout.PayoutCode` (never a code derived from the id) or operator company name.
- Return the persisted `payoutCode`, `periodStart`/`periodEnd`, and `periodCount` (multi-month available-balance payouts), plus persisted snapshot amounts only.
- `totalCount`/`totalAmount` describe the whole filtered result set, not only the current page.
- Do not expose bank details, gateway data, reviewer identity, authorization, or execution-state fields.
- Keep `AsNoTracking`; no `SaveChanges` or audit write occurs in the list handler.

Tests first: default queue, explicit allowed status, denied execution status, filter composition, empty page, stable ordering, summary semantics, and no mutation.

## Task 4 — HTTP and OpenAPI

- Document the Staff review endpoint's own authorization scope. The shared FE route (`/admin/payouts`) also reaching an Administrator settlement scope is a UC-65 concern, not documented here as if built.
- Update ProblemDetails/OpenAPI and integration tests for `401`, allowed `200`, wrong-role `403`, invalid request `400`, and safe empty `200`.

## Task 5 — Verification

Run formatter, build, focused unit/API/SQL tests, then full repository checks. Review the exact branch head before PR creation.

## Non-goals

UC-64 detail/review, UC-73 approval, UC-74 rejection, payment-gateway integration implementation, and manual-retry UI are separate work. UC-64/73/74 are implemented on their own branch and are not deliverables of this plan. Cancellation (UC-64 scope) and any `Cancelled` display are not added here.

## Decisions recorded

- Actor: Staff (SRS catalogue). The older SRS section 3.9.9.1 says Administrator; the SRS must be corrected by the PO, and the UC-63 spec is the decision of record until then.
- `Cancelled` is excluded from the Staff review queue.
- Summary counters are `totalCount`/`totalAmount` over the filtered set; "Total Requested"/"Total Confirmed" are dropped.

**UC-65 (Administrator settlement endpoint, its authorization scope, the `ApprovedForSettlement` → `SettlementProcessing` transition, the payout worker/outbox handoff, and the bidirectional Staff/Administrator settlement tests) is also a non-goal of this plan.** An earlier draft of this plan described that work — an Administrator-only settlement query, a documented settlement authorization scope, and a dedicated "Payout worker handoff contract" task — as if it were part of UC-63's delivery; none of it is implemented in this codebase. UC-65's own plan will be written when that use case is actually started.