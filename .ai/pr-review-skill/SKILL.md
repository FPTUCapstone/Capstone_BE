---
name: pr-review-skill
description: Review a TripMate pull request or local diff (Capstone_BE, Capstone_FE, Capstone_Mobile) against team rules, severity levels and the Dev/CrossReview checklist, and write PR descriptions with the team PR template. Use when asked to "review PR", "review my changes", "cross review", "write PR description" or "create PR" in this workspace.
---
# TripMate PR Review

Read-only review. Never modify code, commit or push unless the user explicitly asks.

## Inputs (ask only if missing)

- Repo: `Capstone_BE` | `Capstone_FE` | `Capstone_Mobile`
- Target: PR number/branch, or "current changes". Base branch is `develop`.
- Jira ID + Use Case (e.g. `TM-98 / UC-52`) and acceptance criteria. Without them, say business-rule checks were limited.

## Procedure

1. Identify the repo and load `references/repo-profiles.md` for that repo.
2. Read the repo's own `AGENTS.md`, `CONTRIBUTING.md`, `docs/CODEBASE_RULES.md` (if present) and `.github/pull_request_template.md`. The team PR template in `references/pr-template.md` is the standard; if the two differ, follow the team template.
3. Get the diff: `git diff develop...HEAD` (or `git diff` for uncommitted work). `gh` may be unauthenticated; fall back to local git.
4. Review changed code in context (open surrounding files, not just hunks). Check, in order: correctness, security, validation, architecture rules, DB consistency (BE), tests, scope creep.
5. Map findings to the checklist (`references/checklist.md`) with code IDs (C01-C63, I/D/U/V) where one applies. Run only the simulation cases relevant to the diff. Never write "no bugs" just because tests are green (C46).
6. Check the PR description against the team template (`references/pr-template.md`, "Review checks against the template") and include any gaps as findings.
7. Report using `references/output-format.md`. Severity rules are in `references/severity.md`.

## Writing a PR description

When asked to write or draft a PR description, use `references/pr-template.md` exactly: 5 headings in order, filled from the real diff, Jira/UC and test results. Do not invent links, test results or screenshots; write `None` / `Not run` where that is the truth. Drafting the text is not permission to open or push the PR.

## Rules

- Report only issues you verified in the code; cite `path:line`. No speculative or style-only nitpicks unless the repo lint rules cover them.
- FE-first MVP: FE screens are built before BE endpoints. Do NOT block on a missing BE endpoint or FE/BE field-name drift. Instead check the FE is honest: no silent fake fallback, demo fixtures gated by an env flag, limitation recorded in PR/docs.
- Scope: flag unrequested features, unrelated reformatting, new global state/UI frameworks/architecture layers without approval.
- Secrets: any committed secret, token, `.env`, or `NEXT_PUBLIC_*` holding a server secret is Critical.
- Do not claim API/auth/persistence is integrated unless the code proves it.
- Answer in the user's language (Vietnamese if they write Vietnamese); keep code identifiers in English.
