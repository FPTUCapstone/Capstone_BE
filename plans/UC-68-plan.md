# UC-68 Submit Post-Trip Complaint — Backend Implementation Plan

## Approved cross-UC policy

Use `TourSchedule.EndAtUtc`; plain SRS "days" are calendar days; and capture
`RefundableAmount` at submission as `max(0, VerifiedPaidAmount - total refund
amount already committed for the booking)`. The immutable snapshot is shared by
UC-69 and UC-75.

## Task 1 — Baseline and tests for domain rules

- Inspect the current booking/payment ownership readers, private-media storage
  coordinator, durable cleanup journal, audit log and notification/outbox
  conventions on `develop`.
- Add failing domain/application tests for category/detail validation,
  eligible booking status/window, duplicate booking and exact evidence limits.
- Add refundable-amount snapshot tests using the approved cross-UC policy.
- Add complaint status/category/action enums and pure domain factories/methods
  without framework dependencies.

**DoD:** Tests describe each SRS rule and the selected time policy; no upload,
HTTP or EF concern leaks into Domain.

## Task 2 — Database-first persistence

- Add idempotent SQL migration for `commerce.PostTripComplaints` and
  `commerce.ComplaintEvidence`, including FKs, unique booking constraint,
  checks, indexes, UTC mappings and row-version.
- Add the UC-69/UC-75-reserved fields and nullable complaint-refund linkage
  required by the approved shared schema, without implementing their commands.
- Add entity configuration, DbSets and `IApplicationDbContext` members.
- Add fresh-schema, upgrade-schema and re-run SQL Server tests.

**DoD:** The schema supports one private complaint/evidence set per booking and
can be applied repeatedly without data loss or duplicate constraints.

## Task 3 — Private evidence upload coordination

- Define feature-specific application ports/DTOs for streamed evidence intake,
  validated storage reservation and durable cleanup compensation.
- Reuse the approved private storage/cleanup infrastructure; do not use a
  public URL or a bespoke untracked cleanup worker.
- Implement server-side file-signature/MIME/size/count/aggregate validation and
  map SRS errors MSG159/MSG160 safely.
- Add unit and infrastructure tests for valid JPG/PNG/WebP/PDF, spoofed type,
  per-file/aggregate/count overage, storage failure and cleanup retry.

**DoD:** Validated files remain private, failures are compensatable, and no
binary or permanent location is exposed in logs or responses.

## Task 4 — Submit command, idempotency and side effects

- Implement the Traveler submission command/validator/handler under
  `Features/Traveler/PostTripComplaints`.
- In one serializable transaction, load ownership and booking eligibility,
  prevent duplicate creation, persist the complaint/evidence metadata/audit
  record, and queue the operator notification after commit.
- Populate the immutable refundable-amount snapshot using the approved
  cross-UC policy.
- Implement idempotency-key replay/conflict semantics and translate MSG157,
  MSG158, MSG159, MSG160, MSG161, MSG126 and MSG127 through the current
  catalogue/error conventions.
- Add handler tests for ownership, status/window boundary, concurrent duplicate,
  identical/conflicting replay, persistence rollback and exactly-once outbox.

**DoD:** One logical submit has one complaint/audit/notification outcome and
does not call UC-42, UC-67 or any payment gateway.

## Task 5 — API and contract tests

- Add the authenticated Traveler safe submit-context read and multipart endpoint and request limits, with
  `Idempotency-Key` binding and no controller business logic.
- Document OpenAPI success/error examples and ensure generated contract never
  exposes opaque storage keys or permanent evidence locations.
- Add API integration tests for authentication, foreign booking non-disclosure,
  validation, multipart limits, duplicate replay/conflict and storage failure.

**DoD:** Endpoint, status mapping and safe payloads match the approved spec.

## Task 6 — End-to-end verification

- Run focused domain/application, private-storage/infrastructure, API and SQL
  Server migration tests, then the applicable solution gate.
- Manually verify: valid submit -> private evidence + awaiting-operator state +
  notification; expired/duplicate/invalid evidence -> no mutation; storage
  failure -> no accessible complaint/evidence; replay -> original response.
- Review the implementation against UC-69 integration: it consumes the same
  complaint entity/deadline/evidence model but is not implemented in this task.

**DoD:** Every spec acceptance criterion passes, no known storage, ownership,
migration, audit or cross-UC security blocker remains, and results are recorded
in the pull request.

## Task 7 — Approved evidence amendment and Traveler complaint access

- Add a new idempotent migration (do not alter historical UC-68 migration) for
  `evidence_amendment_used_at`.
- Implement a one-time, idempotent append-evidence command for an own
  `AwaitingOperatorResponse` complaint before its original submit deadline;
  enforce aggregate file/byte limits and leave reason/detail/deadlines immutable.
- Record an audit event and one operator notification on successful append.
- Add own-complaint list and authorised evidence-delivery endpoints. Cloudinary
  uses short-lived private links; Development streams private local storage.
- Add focused handler, API authorisation/idempotency and SQL migration tests.

**DoD:** A Traveler has a dedicated complaint entry point, can view private
evidence, and can append evidence exactly once without changing original terms.
