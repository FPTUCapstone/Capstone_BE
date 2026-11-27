# Traveler refund account plan

1. Add failing focused tests for no default, save/update/encrypted-at-rest/owner isolation/validation, automatic new-case snapshot and immutable old snapshots, manual recipient saves default.
2. Add pure domain entity, EF configuration/DbSet and idempotent SQL. Implement GET/PUT vertical slices and authenticated thin controller with no-store responses.
3. Reuse a scoped helper for default-account persistence/read within existing refund transactions. Thread IPiiProtector through all new-case handoff paths and manual confirmation without changing refund business calculations.
4. Run relevant Application tests and exact-file format; return migration/contract and source files to root for backup/migration/restart/live verification. No remote changes.
