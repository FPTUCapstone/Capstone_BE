# Output format

```
## PR Review: <repo> <PR/branch> (<Jira ID / UC>)
Verdict: APPROVE | CHANGES REQUESTED | COMMENT
Checks run: <lint/typecheck/build/test — result, or "not run">
Counts: Critical N · High N · Medium N · Low N

### Findings (most severe first)
1. [Critical|High|Medium|Low] <short title> — `path:line` (checklist: Cxx)
   Problem: <one sentence>
   Scenario: <input/state -> wrong result>
   Suggested fix: <one or two sentences>

### Not verified / limitations
- <e.g. no Jira context, tests not run, BE endpoint pending (FE-first MVP)>
```

If there are no findings, say so plainly and list what was checked.
