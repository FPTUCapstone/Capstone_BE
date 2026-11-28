# Railway contract PDF implementation plan

1. Verify baseline renderer and workflow tests; inspect Railway-compatible sandbox/worker capabilities and local configuration key names without exposing credentials. Select the execution model that satisfies the approved isolation requirements.
2. Add failing tests for the chosen renderer boundary and deployment configuration. Implement only the required image, adapter/configuration and worker wiring. Preserve fail-closed behavior and existing timeouts and anchor checks.
3. Build the image and run a real DOCX-to-PDF smoke test with Vietnamese text and signature anchor; exercise missing-tool and timeout behavior. Run affected Infrastructure/Application tests and formatting checks. Report any environment limitations explicitly.
4. Write deployment/runbook instructions including persistent private storage, exact configuration, resource limits and retry of pending/failed previews. Review against the spec, then review implementation quality. Present verified changes; do not push or deploy unprompted.

Definition of done: real container smoke test and affected tests pass, isolation is evidenced, runbook is reproducible, and no approval/signing guard is bypassed.

## Execution evidence

Diagnostic follow-up: write failing native diagnostic tests for ABI 1/2, supported ABI 3 and ENOSYS/EOPNOTSUPP/EPERM; implement distinct safe messages, run native tests and image smoke, update CI/runbook, review, commit and push to the existing BE branch. Approved by the user on 2026-11-28. Actual Railway errno remains unknown until redeployment.

Diagnostic verification: baseline 9 worker tests passed. Native harness failed before the diagnostic function existed, then all 3 diagnostic tests (with ABI/errno subcases) passed in Docker. Full Vietnamese-PDF/auth/timeout smoke passed. Disabling Landlock syscalls produced the expected exit 1 with `result=-1 errno=38 (Function not implemented)` in startup output. Production image contains no diagnostic test artifacts. No .NET, FE, database, or sandbox-policy changes; actual Railway diagnosis is still pending its new log.

- User approved spec and plan on 2026-11-28.
- Baseline renderer tests: 25 passed; opt-in Docker integration test skipped.
- Dedicated private service selected after isolation review; local Docker adapter preserved.
- New remote adapter tests written red before implementation. Python protocol tests also written before the service.
- Infrastructure suite including the real remote adapter against the new service: 1,468 passed, 4 unrelated/opt-in provider tests skipped.
- Application contract tests: 85 passed.
- Linux worker protocol and output-symlink boundary: 9 passed.
- Actual DOCX conversion produced a 12,845-byte Vietnamese PDF with signature anchor. Negative input, missing engine and timeout cleanup checked.
- Independent spec review passed; quality review identified orphan-process reaping, fixed with tini and a stricter descendant-disappearance test. Final review found no remaining blocking issues.
- Final init-enabled smoke passed: real Vietnamese PDF, anchor, auth, invalid input, missing engine, timeout and complete descendant reaping. A host with Landlock syscalls deliberately disabled exited with the expected startup error instead of serving requests.
- Final renderer .NET suite against the actual init-enabled image: 44 passed, 1 optional legacy Docker test skipped. Final Linux worker tests: 9 passed. C# formatter verification and git whitespace checks passed.
- Production kernel self-test, Railway private connectivity and real preview remain required after deployment; no production config/data changed. No commits, pushes or deployment were performed.
