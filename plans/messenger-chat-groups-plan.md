# Execution plan
1. Inspect existing handlers/hub/context/runtime; run scoped baseline chat and friendship tests.
2. Write application authorization, validation, retry and cursor tests, verify red. Add domain, EF mappings, SQL migration/canonical schema and context implementers.
3. Add application CQRS, thin endpoints and user-bound GroupChat notifications. Verify application and endpoint tests.
4. SQL concurrency regression: simultaneous creation/message replay, conflicting payloads, atomic graph keys, monotonic readers; migration twice.
5. Review scope/security and implementation; run scoped build and regression suites. Stage publish, update only named isolated QA DB, restart 5058 and smoke contract. Leave all work uncommitted.

Verification so far: baseline endpoint suite 6 passed; final application chat/friendship/travel-chat scope 78 passed (including deterministic bounded-contention/cancellation); endpoint/realtime scope 8 passed; SQL Server scope 12 passed, zero skips; scoped API publish succeeded; runtime smoke 16 passed. Final post-review endpoint/context checks follow. SQL schema migration was applied twice to the isolated runtime DB; SQL tests use independent throwaway DBs in the existing server. Old runtime app directory retained, state.before-chat-groups.json records rollback state. No credentials in tracked files, no commit/push.

Final verification: post-review API/realtime 8/8 passed; infrastructure context/PasswordReset scope 4/4 passed; final independent-group SQL regression rerun 2/2 passed (15 seconds), zero skips. Reviewed spec compliance and implementation/security, git diff --check clean. Scoped publish succeeded with -m:1 -nr:false. Runtime remains PID 5952, port 5058, isolated QA database; no product database modifications. All pending friendship files retained. No AI/Mobile modifications or Git commits/pushes.

## Follow-up execution
1. Inspect model/contracts; communicate exact routes/DTOs; write application tests first and run baseline travel chat.
2. Extend domain/message EF optimistic tokens, immutable original-send snapshots and persistent edit request records; explicit idempotent SQL migration/canonical schema/context implementers.
3. Implement member-first reads, safe edits, vote identities, important page, independent members; thin endpoints and MessageUpdated events. Protect interactions against edit races with bounded retry and revalidation.
4. Verify app/API, forced SQL edit/edit and edit/vote/payment races, migration twice, original-send retries and privacy. Review against spec and architecture.
5. Single-process -m:1 scoped builds/tests only. Stage a new deployment; apply migration to named isolated QA DB, switch5058 with rollback retained, smoke new contracts. No product seeds/database changes, no AI/Mobile edits or push.

Follow-up checks: application scope54 passed, API/realtime scope6 passed, SQL scope11 passed (57s, zero skips). SQL covers edit-vote and edit-payment races, concurrent same-key edits, migration twice, original send replay after edit, and existing chat hardening. Parent review fixes: exact-ID DTO loading (no id+1), Actor FK mapping matches SQL, closed-group important/vote reads preserved. Migration defaults revision0/interaction_revision0/financially_confirmed0; nullable original payload snapshots backfilled only once. Single-process publish/runtime switch in progress; final closed-group/context checks follow.


Parent follow-up: restored existing UC22 foreground location branch into separate GroupLocationsController, domain/entity/configuration and migration, without replacing current member lifecycle or TravelGroupsController. Added current Active-account and Active-group checks, exact closed-group errors and current context implementations. Application groups/friendships scope195 passed; targeted GPS/cards27 passed at that checkpoint; SQL cards/GPS8 passed, zero skips. Additional strict viewed-revision acknowledgement/payment regressions and final formatting/publish are being completed. All DB fixtures isolated, no product seeds.

## Authorized readonly itinerary-share follow-up (2026-11-28)
Implement the explicit new share scope in this dirty worktree under [messaging-itinerary-shares-plan.md](messaging-itinerary-shares-plan.md). Add immutable snapshot persistence plus atomic existing TEXT send, current history authorization, durable idempotency, SQL migration/canonical schema and OpenAPI. Let nested direct/group send deadlocks propagate to the outer transaction recovery. Manual source review only; do not run tests/builds/analyzers or apply SQL. Previous check results above are unrelated to this follow-up.


## Approved leave execution (code only)
1. Preserve dirty BE work; read existing group handlers, transaction helper, notifier, SQL and repository instructions.
2. Add immutable creator/original member snapshot, durable leave receipt, EF mappings/DbSet, group transaction lock; author idempotent SQL and canonical schema without executing.
3. Implement leave command/route and explicit replacement validation; serialize message/member reads, sends and read cursors with leave; bound recognized contention recovery and preserve nested share rollback.
4. Extend membership DTO eligibility and postcommit actor/survivor invalidations. Source-review authorization, retries, creator replay and SQL rerun compatibility. No tests/build/lint/analyzers/migration/browser execution; provide handoff cases and residual risks.


## Approved independent host management execution (code only)
1. Preserve existing dirty checkout; inspect chat entity/handlers, shared group lock, notifier and SQL.
2. Add guarded rename/kick/dissolve, closure checks on send/leave/transfer, immutable creation name and compatible DTO defaults; keep history/memberships on dissolve.
3. Expose exact FE management routes and postcommit notifications; enforce closed snapshot-share sends without changing snapshot history access.
4. Author idempotent SQL and matching canonical schema/configuration. Manually review authorization ordering, replay behavior, transaction serialization and scope. No tests/build/lint or SQL execution; no commit/push/UI changes.
