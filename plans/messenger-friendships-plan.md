# Messenger friendships implementation

The user's concrete worker instruction authorizes direct implementation of the accompanying spec.

1. Establish scoped DirectChat baseline; add failing friendship state and authorization tests.
2. Add guarded domain model, MediatR contracts/handlers, EF mapping/context sets, thin authenticated controller and idempotent SQL plus canonical schema.
3. Add endpoint and opt-in SQL migration/concurrency tests. Run scoped application/API tests and compile infrastructure tests to verify all context implementations.
4. Review authorization, state transitions, retries, schema parity and diff scope. Report contract, files, results and any environment limits. Leave changes uncommitted.

Completed verification:
- Application friendship/DirectChat scope: 30 passed.
- API friendship/DirectChat endpoint scope with real SQL Server: 9 passed, none skipped. Includes migration replay, incompatible column/index rejection, real SQL list projection and forced write races.
- Infrastructure password-reset context implementation scope: 2 passed.
- API build: zero warnings/errors. All context implementations compile.
- Live authenticated Web login and 24 HTTP assertions passed, including simultaneous opposite requests returning 204/204 and independent DirectChat access.

Runtime left running at `http://localhost:5058`, using existing `tripmate-sqlserver` on port 14330 and isolated database `TripMate_Messenger_Friends_20261126_2b31df62`. UI demo accounts: `messenger-alice@example.test`, `messenger-bob@example.test`, `messenger-outsider@example.test`. Password stays in the existing `%TEMP%/messenger-qa-credentials.json`; no secrets are committed. Startup script and process/database state are in `%TEMP%/tripmate-messenger-friends-runtime/`. Separate smoke accounts keep UI demo relationships undisturbed. The temporary extra SQL container was removed. Existing databases were not changed.
