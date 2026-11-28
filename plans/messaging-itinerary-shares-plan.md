# Backend itinerary snapshot share implementation plan

Authorized by the explicit user request on 2026-11-28. Work only in `Git/worktrees/messenger-ux-be`, preserving existing dirty messenger work. Scope is the readonly itinerary share contract for travel_group/chat_group/direct. Tests/builds/analyzers and migration execution are prohibited for this task.

1. Read existing message handlers, history authorization, entities, mappings and transaction helpers. Preserve separate conversation kinds and existing TEXT messages.
2. Add immutable snapshot entity, EF mapping and DbSet; add a production serializable Result transaction that rolls back failures without changing existing transaction semantics. Update affected test context declarations for interface compatibility.
3. Add CQRS share/read handlers. Capture only owned itinerary header plus POI/timeline whitelist; reuse nested send handlers in the same transaction. Persist the receipt and handle unique/deadlock races. Recheck current history access on every read.
4. Add authenticated API routes with exact consumer DTOs and post-commit notification through existing channels. Preserve standard ProblemDetails error responses.
5. Add idempotent SQL migration and the identical canonical schema block, including actor/key uniqueness, schema shape checks, UTC datetime2(7), and immutable UPDATE trigger.
6. Publish machine-readable OpenAPI and narrative spec including authorized scope, replay/status semantics, privacy, limitations and QA cases. Add focused test source if useful, but do not execute it.
7. Read changed source for privacy, authorization, transaction, migration and contract consistency. Deliver precise paths and remaining SQL/runtime QA work without pass claims.

Implementation source and artifacts are written. Verification is limited to manual source reading; all execution-based verification remains unperformed by instruction. Definition of delivery: changes are reviewable in this worktree with exact contract and QA checklist; no build/test/analyzer/migration command runs, no commit/push/PR, no parent/FE/Mobile edits.
