# UC-35 production plan

## Task 1 — Complete current Draft creation vertical slice

Harden the existing Draft aggregate, validation, command handler and endpoint
against the approved spec's basic rules. Add missing validator/domain tests and
API endpoint tests. Verify with targeted `dotnet test` and `dotnet build`.

## Task 2 — Database-first authoring schema

Add an idempotent SQL migration and EF mappings for package terms, departure
prices/capacity/deadline, cancellation policies/rule snapshots, create
idempotency operations and audit events. Add mapping/entity tests. Verify SQL
review, targeted tests and build.

## Task 3 — Draft query/update and audit commands

Implement owner-scoped list/detail/update endpoints and application handlers.
Replace child collections atomically, recompute warnings server-side and append
audit events. Add unit/integration tests for ownership, update locking and
warning behavior.

## Task 4 — Lifecycle and review prerequisites

Implement submit-for-review plus protected review/publish transitions, using
existing media data for the primary-image prerequisite. Add transition tests,
authorization tests and API endpoint tests.

## Task 5 — Mobile data/domain/editor state

Replace the current create-only contract with DTOs/mappers, Draft list/detail
and update operations, locally persisted unsent editor state, and Cubits for
list/editor/review. Add repository/Cubit tests.

## Task 6 — Mobile production editor and preview

Build the responsive multi-step editor, server-warning rendering, destination
and POI pickers, terms/policy/departure editors, media handoff, preview and
review submission. Add focused widget tests.

## Task 7 — Verification and review

Run full backend tests and mobile formatter/analyzer/tests when Flutter SDK is
available. Perform spec-compliance and code-quality review; resolve blocking
findings and report environmental limitations precisely.

---

## Amendment A tasks (BE) — pending spec approval

Baseline on branch head `b2592e15`: `dotnet build` 0 errors; Application unit
tests 1960 passed; `dotnet format --verify-no-changes` reports 54 whitespace
errors (the cause of the failing CI `test` job).

## Task A1 — Make CI green
Run `dotnet format` on the solution and commit it as a formatting-only change.
Verify: `dotnet format --verify-no-changes`, `dotnet build`.

## Task A2 — Domain: terms, destinations, policy snapshot
Add `TourDestination.Create`, `Tour` methods for terms/destinations, a
`CancellationPolicyRules` navigation and the single `ApplyCancellationPolicySnapshot`
method (spec A.6-2); extend `CreateDraft`/`UpdateDraft`. Tests first in
`TourTests` (invariants, ordering, limits, whole VND). Verify: domain tests.

## Task A3 — Destination catalogue
`GetDestinationsQuery` + handler + `DestinationsController`; unit and endpoint
tests (anonymous, ordered, no-store). Verify: targeted tests.

## Task A4 — Create/Update accept the new fields
Extend commands, validators, fingerprint (normalize first, never sort ordered
lists) and handlers (destination existence, active policy template, shared
snapshot method, whole VND). Replace-all runs delete → SaveChanges → insert →
SaveChanges inside the serializable transaction (spec A.6-1). Update
`PUT .../cancellation-policy` to use the same snapshot method. Tests first for every
new rule and for the idempotency mismatch. Verify: handler + validator tests.

## Task A5 — Detail, list and submit prerequisites
Extend the detail DTO and add the destination prerequisite in submit-review.
Extras (only if approved): `TourReviewReadinessEvaluator` shared by detail and
submit-review, `canSubmitForReview`/`reviewBlockers`, list filter and paging.
Tests first.
Verify: handler tests.

## Task A6 — Migration seed alignment and SQL Server integration tests
Align the default-policy seed to 25 %; extend the migration and endpoint
integration tests (create → get round trip; update with identical, reordered and
changed destination/itinerary lists; idempotency replay). Write the update test
first: it is the only way to prove the replace-all path (InMemory unit tests
cannot). Needs Docker SQL Server. Verify: `TripMate.Api.IntegrationTests`.

## Task A7 — Verification and review
Full `dotnet build`, all test projects, `dotnet format --verify-no-changes`;
two-pass spec/quality review; update the PR description. No push until the
developer chooses (AGENTS §5.1).

---

## Amendment B tasks (done)

- B1 BE: shared readiness evaluator, detail readiness fields, list `status`/paging (tests first;
  unit + SQL Server endpoint test).
- B2 BE: `timeZoneId`, `highlights`, `travellerRequirements` through domain, commands, validators,
  fingerprint and detail; itinerary warning evaluator (pure, unit tested).
- B3 BE: bounded retry for concurrent duplicate creates (SQL Server concurrency test).
- B4 Web: categories/destinations/policies/POIs from server catalogues, cover-photo manager,
  departure terms, readiness and warnings, paging, Published status.
- B5 Mobile: draft list, resume/edit, photos, preview, warnings, submit for review, read-only view
  for non-editable states.
