# TM-64 Invite Group Members Implementation Plan

**Goal:** Deliver an idempotent, Host-only invitation flow with explicit regeneration.

**Architecture:** The API uses Commands for both write-capable operations, a
database-first operation ledger for idempotency, and SQL Server application
locks per idempotency operation and group. Flutter calls the two POST operations through the existing
repository/Cubit layers and never treats a local action as successful before
the platform or server operation completes.

**Spec:** `specs/TM-64-spec.md`

## Task 1: Synchronize the branch and restore the test baseline

- Merge current `develop` into both UC-18 branches and resolve only UC-18
  conflicts.
- Keep one `TestDbContext.OnModelCreating` override that applies infrastructure
  configurations.
- Run the current BE and Mobile test suites before behavioral changes.

## Task 2: Invitation domain and database contract

- Add named constants for 8-character code length, 30-day expiry, unlimited
  technical uses, bounded collision attempts, and operation action names.
- Encapsulate `GroupInvitation` creation and expiration in domain methods.
- Add an idempotent SQL migration and EF mapping for invitation operations,
  including the unique `(traveler_user_id, idempotency_key)` index.
- Add tests for invalid operation construction and timestamp mapping.

## Task 3: Atomic invitation commands

- Replace the write-capable Query with GetOrCreate and Regenerate Commands.
- Add a SQL Server application-lock abstraction keyed by group ID and execute
  each command within a serializable transaction.
- Add replay, payload-mismatch, concurrent, collision, rollback, and
  authorization tests before implementing each corresponding behavior.

## Task 4: HTTP contract and API tests

- Bind and document `Idempotency-Key` from the header for both POST routes.
- Map 401, 403, 404, malformed key, replay mismatch, and 200 responses using
  raw success DTOs plus ProblemDetails.
- Add OpenAPI and endpoint integration coverage.

## Task 5: Mobile invitation behavior

- Update models, repository, Cubit, routes, and UI for the two POST actions
  and operation keys.
- Reject whitespace-only group names; await clipboard success before MSG55.
- Add confirmation and in-flight disabling for Regenerate.
- Upgrade Android Gradle Plugin to the minimum supported by `share_plus` and
  verify an Android debug build.

## Task 6: Final cross-repository quality gate

- Run all mandatory BE and Flutter checks on final branch heads.
- Update both PR descriptions, attach UI evidence, and summarize no-quota and
  regeneration behavior for reviewers.
