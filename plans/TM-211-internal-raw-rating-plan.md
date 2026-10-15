# TM-211 internal raw rating implementation plan

User approved on 2026-10-15. Base: `9e369bb3dd039c92d1664aeb81d89fedeb5ed38a`. Branch: `feature/tm-211-internal-raw-rating`, in a separate worktree. Primary user files are preserved.

1. Verify the clean baseline with restore and the existing SQL aggregate suite. Read the current repository rules and approved scope before changes.
2. RED: add missing SQL tests in `TripReviewAggregateSqlServerTests` for the absent `QueryTourRatings` API, raw/display separation, R11 thresholds, R12 global count/pages/command count, empty/null and typed booking namespaces. Record the expected missing-API compilation failure before production code.
3. GREEN: extract the identical Tour source query once, reuse it in `ReadTourAsync`, then add grouped SQL and a scoped left join. Keep the existing integer count and post-materialization display helper. Run new provider cases and all existing Tour/POI aggregate regressions.
4. Review specification compliance and code quality using fresh review agents as required by AGENTS. Resolve actionable findings and verify the latest tests.
5. Run Release build, scoped formatting, provider-backed focused tests, Redis and full regression. Enable the existing real E5/Qdrant test only against isolated local services. Report optional Cloudinary skips explicitly. Keep logs, SQL output and TRX outside Git.
6. Review the final diff, verify unchanged public/schema/POI files and repository status, then commit only intended files locally with the Codex co-author trailer after checks pass.

No push, PR, merge, Jira mutation, deployment, production indexing or production SQL is authorized. Public rounding, DTO placement, POI compatibility and rollout remain pending. No full-corpus semantic or public V2 acceptance claim follows from this slice.
