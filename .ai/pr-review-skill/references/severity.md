# Severity

| Severity | Meaning | Team action |
| --- | --- | --- |
| Critical | Breaks a requirement, security hole, data loss, secret leak, architecture-rule violation | Must fix, blocks merge |
| High | Likely bug/regression in a normal flow, missing authz/validation, broken tests/build | Must fix |
| Medium | Edge-case bug, missing test for new logic, maintainability risk | Fix or explain in PR |
| Low | Minor improvement, naming, small cleanup | Developer judgment |

PR is mergeable only when Critical = 0 and High = 0 and Medium are handled (see OPEN_CODE_REVIEW_GUIDELINE.md §9).
