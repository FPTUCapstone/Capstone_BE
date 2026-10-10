# UC-03 Resubmit Tour Operator Application — Backend Implementation Plan

Status: **Revised draft — 2026-10-08**. Follows `specs/UC-03-spec.md`; Web and Mobile share this API. Branch: `feature/linhnv-resubmit-tour-operator-application`.

## Task 0 — Baseline and contract lock

1. Record final baseline SHA, branch status, SDK version, restore/build/test results, SQL Server provider/schema, and any skipped checks.
2. Confirm the BE UC-02 operator-registration dependency is merged into this branch. The current branch must contain its formats, file validator, authenticated Cloudinary storage, signed-download service, durable cleanup reservation/worker, and SQL locking helper before UC-03 implementation starts; do not recreate a second incompatible stack.
3. Add locked catalog/constants for MSG161 and MSG162; reuse MSG01/127/157/158/159.
4. Add `AuditActionTypes.OperatorApplicationResubmit = "ResubmitOperatorApplication"`; do not use magic action strings.

DoD: contract and dependencies are traceable to current code. If BE UC-02 is absent, first rebase/merge the approved UC-02 change; UC-03 implementation does not proceed on an incomplete baseline.

## Task 1 — Query and authorized document projection

Implement `GET /api/v1/operator/application` in the Operator application feature:

1. Query the current user's `User`, `OperatorProfile`, and documents.
2. Count prior successful resubmissions using `ActionType` and numeric `AffectedEntityId`.
3. Generate short-lived signed download URLs only after role/ownership authorization.
4. Return the exact DTO from the spec, including `userStatus`, `approvalStatus`, `reviewedAtUtc`, document status/type/timestamps, URL and expiry. Never expose stored asset references.
5. Cover `401`, `403`, `404`, malformed stored data fallback, and authorized URL projection with tests.

DoD: DTO and OpenAPI match the shared Web/Mobile contract.

## Task 2 — Command, normalization, and validation

Implement the exact PUT multipart command and validator:

1. Normalize text with `Trim()` before required, length, regex, and uniqueness validation.
2. Reuse UC-02 tax code, licence number, and phone rules.
3. Reuse backend file validation for extension, MIME, signature/content, 5 MiB limit, and at most five new current-cycle supporting files.
4. Return MSG01/157/158 through structured problem details. Reserve MSG159 for identifier conflicts and MSG161 for invalid state.
5. Test whitespace boundaries, Unicode names/addresses, exact max lengths after trim, file signature mismatch, empty MIME, valid formats, and count boundaries.

DoD: validator tests pass and client-side validation is not trusted for security.

## Task 3 — Transactional handler and document lifecycle

Implement the handler under a SQL transaction:

1. Lock the current application row or perform an equivalent conditional transition requiring both user/profile states to be `Rejected`.
2. Check uniqueness excluding the current user. Translate known unique-index violations from concurrent writes to `409` MSG159.
3. Create durable cleanup reservations before each authenticated Cloudinary upload and retain opaque asset references.
4. Apply document rules:
   - new licence: retain historical rejected row and add new `Submitted` row;
   - no new licence: reset the latest existing rejected Business License to `Submitted`;
   - no licence at all: fail MSG157;
   - retain old rejected supporting documents and insert new ones as `Submitted`.
5. Update company/contact fields and `User.FullName`.
6. Capture complete before-state, then set both states to `PendingApproval` and clear current `RejectionReason`, `ReviewedBy`, and `ReviewedAtUtc`.
7. Add one audit row with the constant action, `OperatorProfile`, numeric user ID, actor, before-state, and after-state.
8. Save and commit once, then mark cleanup reservations completed. On failure, leave reservations retryable and return a safe error.

DoD: no new document status/schema is required; all successful state changes are atomic and auditable.

## Task 4 — HTTP endpoint and error mapping

1. Add only `PUT /api/v1/operator/application/resubmit`; do not offer an alternate POST route.
2. Apply Tour Operator authorization and multipart request limits before file processing.
3. Map validation to `400`, missing profile to `404`, role/auth to `401/403`, state and uniqueness conflicts to `409`, and infrastructure failures to `503` MSG127.
4. Update Swagger/OpenAPI examples for numeric `userId`, document URL expiry, and MSG162 response.

DoD: HTTP contract is identical for FE and Mobile clients.

## Task 5 — SQL Server integration and failure tests

Use the real SQL Server provider and fresh DbContexts:

1. Happy path with a new licence: verify both states, new Submitted document, retained history, cleared current review fields, and one audit.
2. Happy path without a new licence: verify the latest rejected licence becomes Submitted and can pass UC-50's prerequisite.
3. Duplicate tax/licence, missing licence, wrong role, missing profile, and non-rejected application mappings.
4. Two concurrent resubmits synchronized before persistence: exactly one `200`, one `409`, one audit, and one winning new document set.
5. Fault injection after at least one update: query with a new context and prove rollback of user, profile, documents, and audit.
6. Upload succeeds then DB fails: reservation remains retryable; worker eventually deletes the orphan.
7. Commit succeeds while worker runs: SQL locking/reservation completion prevents deletion of the referenced asset.
8. Signed URL authorization and expiry tests; raw asset reference never appears in response.

DoD: tests prove behavior against SQL Server rather than EF InMemory simulations.

## Task 6 — Final verification and evidence

Run against final HEAD:

```text
dotnet format TripMate.slnx --no-restore --verify-no-changes
dotnet build TripMate.slnx -c Release
dotnet test TripMate.slnx -c Release
git diff --check
```

Record exact SHA, commands, pass/fail/skip, provider/schema, seed data, Cloudinary mode, and SQL concurrency/rollback results. Smoke test both Web and Mobile against the same BE final head. Call out the separate Admin review-list dependency instead of claiming it is delivered by UC-03.
