# Team PR template

Standard PR description for `Capstone_BE`, `Capstone_FE`, `Capstone_Mobile`. Keep the 5 headings and their order. Replace every `_write here_` placeholder; never leave it in a submitted PR.

```markdown
## 1. Related PRs
- Link related PRs or _No related PRs_
## 2. Attention!
**These are things you need to get done before release to production. Please read it carefully!**
- _write here_
## 3. Summary(short description about PRs's goal)
- _write here_
## 4. Link specs
- _write here_
## 5. Output(Image, video, JSON example)
- _write here_
```

## What goes in each section

1. **Related PRs** - dependency PRs and merge order (e.g. BE endpoint PR that this FE PR waits for), or `No related PRs`.
2. **Attention!** - only release-blocking actions: DB migration/seed to run, new or changed env vars/config (names only, never values), dependency or deploy order, feature flags, manual steps. Write `None` if there are none. Known limitations and prototype/mock behaviour also go here or in Summary (checklist C49, C59).
3. **Summary** - the goal of the PR in a few lines: what changed and why, scoped to one Jira/UC. State plainly what is real integration and what is fixture/mock (C03).
4. **Link specs** - Jira ID, UC, SRS/Figma/OpenAPI/docs links with version (C01, C02).
5. **Output** - evidence: screenshots/recording for visible UI, request/response JSON for API changes, test/build command results with exit code (C34). Sanitize tokens, PII and real connection strings.

## Review checks against the template

- All 5 headings present, no leftover `_write here_`.
- Section 2 lists every migration, config or ordering step the diff actually requires; a diff that adds a migration, env var or new dependency with `None` in section 2 is at least Medium (C19, C49, C52, C60).
- Section 3 matches the diff: no claimed integration the code does not prove; no unrelated work (scope).
- Section 4 has Jira/UC and spec links; missing links limit the business-rule review - say so in "Not verified".
- Section 5 has evidence for the change type (UI -> image/video, API -> JSON). Missing evidence for a visible UI or contract change is Medium; secrets or PII in the evidence is Critical.
- A PR body that uses the older repo `.github` template instead is a Low finding (process); review the code normally.
