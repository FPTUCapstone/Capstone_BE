# PR #61 Staff foundation remediation plan

Authority: [Staff foundation specification](../specs/STAFF-role-foundation-spec.md). Product/BA approval remains pending; scope is the user's explicitly requested P1–P3 remediation of the existing branch.

1. Baseline: restore and run full Release tests on clean PR HEAD; run the exact formatter. Audit authentication response text, existing specs/plans and V2 conflicts. Record baseline failures separately from behavior changes.
2. P2: document actor ownership, role matrix, auth/Firebase boundaries, policies, database-first migration, compatibility and Web/Mobile limits in the linked spec. No approval inference and no authorization expansion.
3. P3 RED: extend GoogleAuth and VerifyEmail unit theories to both Staff and Administrator, assert inclusive copy, unchanged error codes/status precedence and no mutation/session. Run focused tests before changing handlers.
4. P3 GREEN: change only the two human response literals and their explanatory comment. Run focused tests. No DTO/status/code/policy changes.
5. P1: run repository formatter to repair diagnosed whitespace; inspect diff to ensure formatting-only edits. Verify exact whole-solution command and changed C# scope, including final-newline rules from .editorconfig.
6. Verification: Release build; complete unit/integration regression; SQL Server 2022 migration and full SQL category without skips; Redis category without skips; full CI equivalent. Where services are unavailable locally, require exact-head GitHub CI evidence before readiness.
7. Two-pass independent review: spec compliance, then code quality. Resolve actionable findings. Commit normally on the existing PR branch, push without rewriting history, update PR description with source/approval state, affected files and exact evidence. No merge.

Definition of done: P1/P2/P3 addressed, required checks green on final code, exact commit and evidence reported, `READY_FOR_RE_REVIEW` with Product/BA approval explicitly pending.
