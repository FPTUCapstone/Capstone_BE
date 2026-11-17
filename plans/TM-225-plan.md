# TM-225 implementation plan

1. Establish clean develop worktrees, inspect SRS, existing schemas, authentication,
   notification/audit patterns and the team's unmerged provider UI.
2. Add regression-first provisioning domain/application/infrastructure and API work
   for UC-76/77, preserving role boundaries and temporary-credential secrecy.
3. Add regression-first provider-owned request query, decision and outcome lifecycle
   for UC-78–80, concurrency, notifications and overdue processing.
4. Add canonical SQL changes and idempotent upgrade migrations. Verify legacy-data
   compatibility, re-run safety, ownership/unique constraints and SQL concurrency.
5. Integrate existing applicable Web screens and real authenticated transports,
   including first-password-change routing, server role guards and confirmation UI.
6. Run focused and full relevant tests, complete changed-file dotnet format checks,
   Web lint/typecheck/build, self-review and browser verification. Report remaining
   external dependencies accurately. Obtain user instruction before remote delivery.

## Verification on 15 November 2026

- Focused Application tests passed 45 cases. The focused Release API matrix passed
  45 cases with the SQL Server connection configured and zero skipped cases. This
  includes real SQL provisioning concurrency, available-POI ordering/filtering,
  request ownership, deterministic paging, mutation replay, lifecycle processing,
  migration parity and wrong-shape regression coverage.
- Broader Application regression passed 4,798 cases. The ordinary API run passed
  857 cases with 157 skipped while external SQL/Redis configuration was omitted.
  Infrastructure passed 1,151 cases with three external smoke tests skipped.
  Candidate replay passed 13 cases.
- Changed-file dotnet format verification and git diff --check passed. The build
  retains existing package vulnerability warnings and the existing scheduling
  nullable-reference warning.
- Web passed 104 Node tests and 3,780 Vitest tests with five skipped. ESLint,
  TypeScript checking and the production build passed.
- Browser checks used the actual Web, JWT authentication and a disposable SQL
  database. Administrator-created Staff, Staff-created Provider, first-password
  redirects and reload, request confirmation, rejection input/focus/Escape,
  backdrop dismissal and actual outcome recording succeeded. List and detail
  screens were inspected at 390px. Captured provisioning email replaced external
  SMTP delivery during browser checks.
- Browser-only databases and servers were cleaned up. Temporary test artifacts
  remain outside tracked source. The user subsequently authorized commit, push, PR creation and CI verification.
- UC-31 request submission and initial itinerary association remain upstream
  dependencies. Monthly commission statements and the full provider authentication
  lifecycle remain assigned to TM-226 and TM-229.
