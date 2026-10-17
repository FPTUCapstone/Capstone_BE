# UC-28 — Make Electronic Payment (Backend) Plan

## Preconditions

- Approve `specs/UC-28-make-electronic-payment-be-spec.md`.
- Confirm a clean Backend baseline with focused booking tests.
- Keep PayOS credentials only in User Secrets/environment variables.

## Task 1 — Payment persistence migration and mapping

Add an idempotent SQL migration and update the fresh schema, EF entity and
configuration for payment order code, payment-link reference, expiry, update
timestamp and transaction statuses. Add SQL Server tests for fresh and upgrade
schema parity.

**Done when:** transaction persistence can distinguish a pending, cancelled,
expired and successful payment attempt without secrets.

## Task 2 — Application payment contract and tests

Write failing unit tests for create/reuse eligibility, amount authority,
replacement links and idempotent completion. Add payment DTOs, commands,
queries, validators, persistence port and `IPaymentGateway` abstraction.

**Done when:** the tests specify every decision in the approved spec.

## Task 3 — Mock provider and common completion handler

Implement `MockPaymentGateway`; it returns a configured local checkout URL and
deterministic references. Implement a single transactional completion handler
used by mock and PayOS paths.

**Done when:** success confirms a booking, cancellation preserves pending
booking state, expiry obeys `payment_expires_at`, and duplicates are idempotent.

## Task 4 — HTTP endpoints and authorization

Expose create/reuse, status and mock-outcome endpoints. Map errors through the
existing Result/ProblemDetails convention. Register mock completion only for
`Payment:Provider=Mock`.

**Done when:** an owner can complete the local flow and another user cannot
observe or mutate it.

## Task 5 — PayOS provider and webhook boundary

Add PayOS configuration binding, client implementation, request signing and
webhook signature verification. The webhook invokes the Task 3 handler; no
return URL parameter changes state.

**Done when:** provider tests validate signature/amount handling without live
credentials, and webhook duplicate delivery is safe.

## Task 6 — Verification

Run focused unit tests, SQL Server integration tests, the Backend build and
relevant booking regression tests. Then manually run Mock:

`PendingPayment → create link → success → Confirmed/Paid`.

**Done when:** all checks pass and no source/configuration file contains a
secret.
