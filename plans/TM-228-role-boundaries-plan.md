# TM-228 atomic implementation plan

1. Record original SRS provenance and exact rules; inspect current controllers, handlers, auth, audits, schema, Web guards and open TM-225 dependency.
2. Add failing account HTTP tests covering role-scoped reads, hidden targets, required reasons, mutations, refresh revocation, audit and status restoration.
3. Add minimal account CQRS queries/commands, DTOs, controller and error mapping. Add persisted pre-lock status/session version with idempotent SQL and UTC mapping.
4. Test signed-JWT revocation and role/state checks before wiring token validation. Ensure an unlock cannot revive old sessions.
5. Add operational negative tests, then align controller/Application policies and Web guards without changing business rules.
6. Integrate Web account directory/detail with typed API, protected proxy and reason-required actions. Test presentation, denied access and API failures.
7. Format, build, run unit, integration, SQL, OpenAPI and Web checks; independent specification and quality review; publish Draft PRs with unresolved dependencies explicit.
