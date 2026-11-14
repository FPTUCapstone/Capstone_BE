# TM-101 implementation and verification plan

1. Preserve four checkouts, stashes and worktrees; fetch official develop; isolate Backend, Web and Docs. Record baseline commands and outcomes before implementation.
2. Backend domain/persistence/CQRS/API: test first; RouteSegment entity/configuration, SQL migration and guarded rollback, DbContext, Staff route controller, handlers/options/list, Result mappings, audit constants. Verify domain/application/API/SQL tests and OpenAPI contracts.
3. Web: test first; Staff page authorization, sealed-session same-origin BFF, API parser and canonical mode mapping, accessible creation form and server list, Mapbox preview and explicit errors. Verify targeted tests, full npm test, lint, typecheck, build and browser flow.
4. Consumers: inspect planner/navigation/Mobile; add narrowly scoped Active route lookup/metric integration without solver rewrites or historical version changes. Verify Active/inactive selection and newly created route availability with integration tests.
5. Independent spec-compliance and code-quality reviews per repository AGENTS instructions; resolve critical findings. Run required checks and SQL migration/idempotency/rollback/failure tests.
6. Docs and corrected Jira description/criteria. Final delivery report distinguishes PASS/FAIL/NOT_VERIFIED/BLOCKED and publication status. Use isolated publication branches and Draft PRs under the publication authorization. Merge and deployment require separate approval.

Exact files may be refined after architecture exploration; document refinements. The user's master prompt authorizes autonomous implementation of defined scope; only substantive missing business decisions are escalated.
