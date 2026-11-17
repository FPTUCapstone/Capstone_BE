# CI #785 — implementation and validation plan

1. Add the `DisposeAsync` regression in `SqlServerTestDatabaseCleanupTests.cs`;
   verify real error 1205 escapes the unchanged cleanup (red).
2. Add the bounded, deadlock-only retry directly in `SqlServerTestDatabase.cs`;
   rerun the regression and cover exhaustion/non-deadlock errors.
3. Verify formatting with the full CI formatter. Run the ChangePassword endpoint
   tests and complete Release SQL category without PRESERVE, report pass/fail/skip.
4. Obtain the required two-stage review (spec then quality). Keep unrelated seed
   changes intact; do not push. Record evidence and remaining limits in workspace logs.

The user authorized this plan by requesting the previously described fix and tests.
Only fresh isolated test databases may be dropped by the harness; existing E2E data
and retained databases are outside the cleanup scope.
