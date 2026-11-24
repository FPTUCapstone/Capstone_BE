# UC-69 Respond to Post-Trip Complaint — Backend Implementation Plan

## Definition of done

UC-69 is done only when the Tour Operator can safely respond to an owned complaint, accepted decisions create the existing UC-42 refund workflow exactly once, and every response path is covered by automated tests. No direct refund gateway call is permitted.

## Task 1 — Baseline and domain contracts

- Inspect the merged refund state machine, audit/outbox convention, booking ownership readers and message catalogue.
- Add `PostTripComplaint`, `ComplaintEvidence`, response action/status enums, immutable refundable-amount snapshot, both response deadlines and complaint-to-refund source/link contracts.
- Add DTOs for list, detail, evidence-link metadata, available actions and response result.

**DoD:** Domain transitions have unit tests; no UI or command can supply financial/ownership fields that the server must derive.

## Task 2 — SQL schema migration

- Add idempotent SQL migration for `commerce` complaint/evidence tables, `payment.Refunds` complaint linkage, constraints, FKs, indexes and complaint refund uniqueness.
- Update the database-first schema artifact only according to repository policy.
- Add fresh-schema, upgrade-schema and rerun/idempotence SQL Server tests.

**DoD:** Migration works on a clean database and an existing database without data loss or duplicate constraints.

## Task 3 — Query side

- Implement owned paged list and detail queries under `Features/Operator/PostTripComplaints`.
- Load booking/tour/operator ownership server-side.
- Create time-limited evidence URLs through the storage abstraction; ensure failures do not leak permanent location data.
- Keep queries read-only: derive displayed allowed actions from authoritative
  status/deadline without persisting a transition. The deadline processor in
  Task 4 persists `Expired`/`Declined`; response commands repeat the deadline
  check in their own transaction.

**DoD:** Foreign ownership, expired records, empty lists and evidence authorization have unit and integration coverage.

## Task 4 — Deadline processor

- Implement a scheduled idempotent processor for the seven-day operator deadline and the UC-75 three-day Traveler deadline.
- Make the command handlers repeat deadline checks in their transactions to handle a race with the processor.

**DoD:** Overdue records persist as `Expired` or `Declined`; neither path creates a refund.

## Task 5 — Response command and financial handoff

- Implement one command/handler/validator for `AcceptFullRefund`, `ProposePartialRefund`, and `Reject`.
- Enforce BR-150/151/152, immutable monetary snapshots, optimistic concurrency and idempotency-key semantics.
- Use the shared refund factory/processor to create a single complaint-sourced refund only for full acceptance; do not duplicate UC-42 transition logic.
- Persist response, refund link, audit event and notification outbox entry atomically.

**DoD:** Replays return the original result; conflicting replays fail; refund creation failure rolls back the response; no payment gateway is invoked.

## Task 6 — API and error catalogue mapping

- Add controller endpoints and request validation.
- Map MSG162, MSG163, MSG164, MSG165, MSG126 and MSG127 through the existing API/message catalogue, without reusing codes for unrelated meanings.
- Document OpenAPI request/response/error examples.

**DoD:** Contract tests validate route authorization, status codes, problem payloads, idempotency header and no cross-operator data disclosure.

## Task 7 — End-to-end verification

- Run application/domain unit tests, focused API integration tests and full relevant solution gates.
- Run SQL migration tests against SQL Server.
- Verify: full acceptance -> UC-42 visible refund; partial proposal -> no refund until UC-75; rejection -> no refund; both deadlines persist their terminal state without a user request.
- Record baseline commit, commands and results in the PR.

**DoD:** All acceptance criteria in the spec pass; no known security, migration or authorization blocker remains.
