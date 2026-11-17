# CI #785 — SQL Server test cleanup deadlock recovery

Approved scope: the user requested implementation and testing after the proposed
bounded retry in `SqlServerTestDatabase.DisposeAsync`.

- Retry only SQL Server deadlock victim error 1205 from the cleanup batch.
- At most three total executions, with 200ms and 500ms delays before retries.
- Keep the existing database-name guard and `IF DB_ID(...)` idempotency check.
- Mark cleanup complete only after successful execution. Exhausted deadlocks and
  every other SQL error propagate; callers can retry disposal after recovery.
- Keep the explicit `TRIPMATE_SQLSERVER_TEST_PRESERVE=1` behavior, but do not use it
  in cleanup regression tests or the CI-equivalent SQL suite.
- No application retry, password/MSG05 change, new dependency, or global test serialization.

Validation: a regression exercises `DisposeAsync` with a real SQL 1205 produced by
two transactions on probe rows inside its newly created temporary database. This
tests recovery at the cleanup boundary, not the original CI DDL deadlock graph.
Cover transient recovery, bounded exhaustion, non-deadlock propagation and repeated
disposal after success. Run the failing password case/class and the complete
`Category=SqlServer` Release suite with actual temporary-database cleanup enabled.
