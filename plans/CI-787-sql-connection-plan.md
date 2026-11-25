# CI #787 — implementation plan

Approved scope follows the user's request to fix and test the reported connection timeout.

1. Regress `ExecuteScriptAsync` pre-open timeout before any SQL batch runs; show red.
2. Add bounded open-only recovery at the fixture's existing ADO connection boundaries.
3. Measure bootstrap opens and show a regression for the master + database connection budget;
   reuse one open connection across canonical schema and migrations without replaying commands.
4. Cover exhaustion, cancellation, non-timeout failures and script execution failure separately.
   Run migration/cleanup regressions, full CI formatter and the complete Release SQL category
   against SQLServer2022 plus Redis8.2 AOF (real cleanup enabled).
5. Two-stage review, record results honestly, preserve unrelated seed changes and local data.
