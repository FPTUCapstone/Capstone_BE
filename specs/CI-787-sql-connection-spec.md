# CI #787 — bounded connection recovery for SQL test fixtures

The user requested a fix after CI failed while opening a non-pooled SQL connection
during schema/migration bootstrap. Scope is the test fixture, not application SQL retry.

- Retry a connection-open timeout only while the connection remains closed, at most
  three attempts, with 200ms/500ms delays. Preserve cancellation and propagate other errors.
- SQL commands and scripts must never be replayed by connection-open recovery.
- Bootstrap opens one master connection for database creation and one database connection
  shared by the canonical schema and ordered migration scripts. Keep pooling disabled.
- Public script execution retains a connection scoped to that script; no live connection
  is cached on the fixture or retained into cleanup. Keep QUOTED_IDENTIFIER enabled.
- Existing guarded cleanup/deadlock recovery and explicit PRESERVE behavior remain intact.
- No global application retry or global serialization of tests. Prefer reducing connection
  churn before adding scheduling limits without evidence that they are needed.

Validate via the existing fixture API: recover injected pre-open timeouts, bound exhaustion,
propagate cancellation/non-timeout errors, execute prior script batches only once, and verify
the real bootstrap connection count and migration contracts. Fault injection is a policy
test; it is not proof of reproducing CI's resource pressure on the local machine.
