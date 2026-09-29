# PR #17 — UC-69 remediation evidence

Local working-tree assessment, 2026-09-18. Nothing in this document asserts that these changes have been committed, pushed or published to the PR.

## Scope

The assigned implementation is UC-69. The existing branch also contains UC-68 relative to review base `3796e29b678e78eb0322d4adce20c83e588784cb`. Search behavior, list pagination and list HTTP test work were not added to this remediation. The user's pre-existing date-range validator fix was preserved.

## Findings

| Finding | Evidence / current status |
|---|---|
| SEC-01 | Existing fixes retained: Program and JwtTokenService reject missing signing configuration; no production static fallback. |
| SEC-02 | Tracked appsettings working copies contain no signing key/connection string. README now explains mandatory external configuration and startup failure. History/remote state is not rewritten. |
| ARCH-01 | AuditLog empty constructor is private; setters remain private; creation checks required names, schema lengths and positive optional IDs. Tests cover rejected invalid creation. |
| SEC-03 | Both detail payloads pass through response-only recursive masking. Tests cover credential aliases, nested arrays/objects, serialized JSON, field-change records, malformed/scalar/deep payloads, null, and unchanged persisted values. |
| SOURCE-01 | Locked MSG126 wording retained. BE/FE docs no longer assign MSG129 to 404; MSG132 remains a proposal, not catalog approval. |
| SOURCE-02 | **Pending Lead/PO decision.** No invented Result/Client Platform/Affected Module or schema migration. Optional reason in JSON does not guarantee capture for all actions. |
| TEST-01 | UC-69 has 10 HTTP tests using actual JWT bearer middleware: no/invalid/expired/wrong-signature/wrong-issuer token, both non-admin roles, missing entry, admin/system actor, masking and persistence immutability. EF InMemory backs these HTTP tests; they are not SQL Server verification. UC-68 HTTP tests remain outside this assignment. |
| SCOPE-01 | UC-51 documents removed from the diff against review base. The three operator error mappings are retained: GetOperatorApplicationDetailQueryHandler (UC-49) uses NotFound; ApproveOperatorApplicationCommandHandler (UC-50) uses Forbidden/NotFound/NotPending. Deleting them would change existing status mappings to generic 400. Lead should confirm dependency ownership/scope. |
| SCOPE-02 | Unrelated migration newline removed; migration has no remaining diff against review base. |
| CONTRACT-01 | Existing UC-68 validator fix retained; not claimed HTTP-verified by the new UC-69 tests. |
| PERF-01 / DB-01 | UC-68 only; remain for its owner. No list search or pagination behavior changed. |
| HYGIENE-01 | Local PR description prepared below. Remote PR body has not been changed or verified. |

## Security boundaries and review

Masking is based on credential field names and field-change descriptors. It is not a general detector of a credential embedded under an arbitrary unrelated name or in ordinary prose. Writers must continue to exclude credentials under SRS BR-03; this read-boundary defense does not replace that obligation. Null is preserved; invalid/unsupported payloads are withheld rather than returned raw. No historical audit row is edited.

Two-pass self-review completed for the changed domain/read boundary and HTTP tests: spec/security checks followed by maintainability/regression checks. Additional aliases (`cardCvv`, `cardSecurityCode`, named change records) were reproduced as failing tests before fixing. Independent subagent review was attempted but unavailable due to the agent usage limit; no independent review pass is claimed.

## Verification

- Baseline before remediation: 280 application + 3 infrastructure + 131 HTTP tests passed; 16 SQL Server tests skipped.
- `dotnet build TripMate.slnx --no-restore --verbosity quiet`: passed, 0 warnings/errors.
- `dotnet test TripMate.slnx --no-build --no-restore --verbosity quiet`: 308 application + 3 infrastructure + 141 HTTP tests passed (**452 total**); 16 SQL Server tests skipped because `TRIPMATE_SQLSERVER_TEST_CONNECTION` is not configured.
- New tests: 28 application security/domain cases and 10 UC-69 HTTP cases. Initial 21 security/domain cases failed before implementation; 3 added alias cases failed before the follow-up fix.
- `git diff --check`: passed in BE and FE. FE changes are documentation only; no FE runtime/build validation is claimed.
- No manual browser or real SQL Server verification in this remediation.

## Proposed PR description (copy after reviewing final scope)

### Summary

Adds Administrator audit-log detail retrieval at `GET /api/v1/admin/audit-logs/{id}`, with actor/System context, UTC and Vietnam time, and masked before/after payloads. Sensitive values are withheld in the read response while persisted audit entries remain unchanged. The current branch also includes the UC-68 list endpoint; its ownership/base alignment requires confirmation before this is described as a UC-69-only PR.

The remediation closes the public empty AuditLog constructor, validates creation metadata, preserves fail-fast external JWT/database configuration, and removes unrelated UC-51 documents/migration whitespace.

### Related work

Leader review: PR #17, Jira TM-114/TM-115 referenced by that review. Scope of this remediation: UC-69.

### Validation

- [x] Solution build: 0 warnings/errors.
- [x] Available automated suite: 452 passed.
- [ ] SQL Server suite: 16 skipped; test connection not configured.
- [ ] Independent cross-review: still required.
- Database changes: none.
- Browser/manual endpoint testing: not performed; HTTP pipeline tested automatically with JWT and EF InMemory.

### Open decisions

SOURCE-02 needs Lead/PO approval for the SRS/schema discrepancy; MSG132 needs catalog approval. UC-68 PERF-01/DB-01 and list HTTP coverage remain with its owner. Shared UC-49/50 error mappings remain because existing handlers depend on them. This is not a claim of full merge readiness.
