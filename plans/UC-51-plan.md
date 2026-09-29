# UC-51 Backend Plan — Reject Tour Operator Application

## Objective

Synchronize the existing UC-51 implementation with current `develop` and deliver the approved rejection contract without changing adjacent use cases.

## Tasks

1. Resolve the `develop` merge by retaining current shared infrastructure and reapplying the UC-51 command, handler, controller response and tests.
2. Align rejection reason validation with the existing 500-character `OperatorProfiles.rejection_reason` column and expose the bound as a domain constant.
3. Record successful rejection through the current immutable `AuditLog` factory pattern and register the rejection action with failure auditing.
4. Preserve the existing UC-51 state transition, reviewer metadata, submitted-document rejection, pending notification and optimistic-concurrency behavior.
5. Map required/too-long reason failures to 422, stale state to 409, missing application to 404 and forbidden caller to 403.
6. Add development seed data for PendingApproval, Approved and Rejected operator applications using idempotent SQL guarded for development/test use.
7. Run targeted unit tests, solution build, full tests and diff checks; record any SQL-only verification dependency honestly.
8. Prove the real SQL Server decision boundary with two barrier-synchronized contexts: reject–reject and reject–approve must produce one `200`, one `409`, exactly one successful decision audit, and exactly one matching notification. The losing request retains its separately committed failure audit under the approved audit policy. Inject a database failure after an `OperatorProfiles` update and verify every business-state, document, notification, and success-audit change rolls back from a fresh context; the independently recorded failure audit must remain.

## Definition of Done

- Spec, schema bound, handler, controller, audit records, notification and tests agree.
- Merge conflicts are fully resolved and no current `develop` code is discarded.
- Local validation passes on the exact branch HEAD.
- Development seed data is repeatable and contains no real credentials or secrets.

## Working-tree verification — 2026-09-28

This evidence covers the resolved merge working tree and is not yet commit-SHA or remote-CI evidence.

- `dotnet format TripMate.slnx --verify-no-changes --no-restore`: pass.
- `dotnet build TripMate.slnx --no-restore`: pass, 0 warnings and 0 errors.
- UC-51 endpoint integration suite: 8 passed, 0 failed, 0 skipped.
- Full solution test run without the SQL connection variable: 1,058 passed, 0 failed, 106 skipped; the skips are SQL Server and optional Cloudinary tests.
- Full API integration run with the Docker SQL Server enabled: 397 passed and 3 existing log-file tests failed because parallel tests contended for `logs/uc04-s11.log`; each of those 3 tests passed when rerun separately.
- The UC-51 seed ran successfully twice against local `TripMateDb` and returned the same PendingApproval, Approved and Rejected fixtures on both runs.
- `git diff --cached origin/develop --check`: pass for the UC-51 branch delta. The merged `develop` parent already contains unrelated trailing whitespace in `specs/TM-69-spec.md`, so a raw merge-index check also reports that pre-existing line.

After the merge resolution is committed, rerun the formatter, build and tests on the resulting HEAD and record the pushed SHA plus remote CI result before marking the PR merge-ready.

## P2 SQL remediation evidence — 2026-09-29

- Added `RejectTourOperatorApplicationSqlServerTests` with real SQL Server cases for concurrent reject–reject, concurrent reject–approve, and trigger-injected rollback after an `OperatorProfiles` update.
- Extended `TripMateApiFactory` so SQL-backed HTTP tests can attach the shared save barrier interceptor; the production registration remains unchanged.
- Provider/schema: SQL Server 2022 Docker on `localhost:14330`; an isolated database is created from the repository's current schema for each SQL test through `SqlServerTestDatabase`.
- Targeted SQL result: `RejectTourOperatorApplicationSqlServerTests` — 3 passed, 0 failed, 0 skipped. Both concurrency cases produced one `200`, one `409`, one successful decision audit, one matching notification, and one independent failure audit for the losing request. The trigger-injected persistence failure returned `500`, rolled back User/profile/document/notification/success-audit changes, and retained only the independent failure audit.
- CI-equivalent Release SQL suite after assigning the new class to the shared `TripMateApiFactory` collection: 126 passed, 0 failed, 0 skipped. This prevents parallel `WebApplicationFactory` host-start races with other SQL integration classes.
- `dotnet format TripMate.slnx --verify-no-changes --no-restore`: pass.
- `dotnet build TripMate.slnx --no-restore`: pass, 0 warnings and 0 errors.
- Full solution with the SQL connection enabled: Infrastructure 134 passed/1 optional Cloudinary skip; Application 666 passed; API 403 passed and 3 shared-log-file tests failed because they contended for `logs/uc04-s11.log`. Each of those same 3 tests passed when rerun separately. No SQL test was skipped.
- Manual smoke: Admin HttpOnly-cookie login → PendingApproval detail → reject → authoritative reload completed successfully against the Docker SQL database and UC-51 seed.
- Required after commit: rerun the targeted SQL tests on the resulting HEAD, push, then update PR #33 with the exact final SHA and remote CI result.
