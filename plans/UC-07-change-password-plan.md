# UC-07 Change Password — Backend Implementation Plan

**Status:** DRAFT — execute only after UC-07 specification approval.  
**Authoritative specification:** `specs/UC-07-change-password-spec.md`

## Preconditions

1. Resolve and record the Staff actor conflict in the authoritative SRS / scope matrix.
2. Confirm the exact authentication scheme and safe Web response/cookie adapter after inspecting `AuthController`, `WebRefreshCookie`, token services, and mobile auth transport.
3. Confirm the existing audit-event convention for credential changes. No password, hash, or token may become audit data.
4. Baseline the branch with `dotnet build TripMate.slnx`, relevant auth tests, and `git diff --check`.

## Task 1 — Establish auth and token-revocation baseline

**Read:** login, web-login, refresh, sign-out, UC-06 confirm-reset handlers; `IApplicationDbContext`; JWT/password services; audit infrastructure.  
**Add RED tests:** the existing session issuance/revocation behavior needed by UC-07, including the one-replacement-session invariant.  
**Done when:** the exact reusable token/cookie path is documented before implementation starts.

**Verify:** targeted existing Authentication unit and integration tests.

## Task 2 — Define application contracts and validation

**Files:** new vertical slice under `src/TripMate.Application/Features/Authentication/ChangePassword/`; existing error-code constants only if a missing stable code is approved.  
**RED tests:** required current/new password, canonical policy failures, and same-as-current password.  
**Implement:** request/result models, command, FluentValidation, and a validator that reuses the existing password-policy logic rather than duplicating rules.

**Verify:** filtered Application unit tests.

## Task 3 — Implement secure command handler

**Files:** ChangePassword handler and existing interfaces/services needed for token issuance.  
**RED tests:** success, wrong current password, no local password, restricted account, and no mutation on each failure.  
**Implement:** authenticated-subject lookup with tracking, hasher verification, distinct-password check, hash replacement, `UpdatedAtUtc`, and `Result` failures.

**Verify:** handler tests and `dotnet build TripMate.slnx`.

## Task 4 — Make password and session mutation atomic

**Files:** handler plus existing token/JWT abstractions; no schema file.  
**RED tests:**
- Success: password hash updated → all old refresh tokens revoked → exactly one new token persisted → new credentials returned.
- Revoke failure: refresh-token revoke throws → entire transaction rolls back → old hash and tokens unchanged → 5xx/`MSG127` returned.
- Insert failure: new refresh-token insert throws → entire transaction rolls back → old hash and tokens unchanged → 5xx/`MSG127` returned.
- Validation failure (wrong current password, weak new password): **no transaction starts** → old hash and tokens untouched → 400/`MSG17` or 400/`MSG05` returned.
- Commit failure: transaction commits fail → rollback → old hash and tokens unchanged → 5xx/`MSG127` returned.

**Implement:** one existing database transaction with spec §6 semantics: (1) verify current + validate new before mutation; (2) update hash; (3) revoke all old tokens; (4) insert exactly one replacement; (5) commit before returning. Return credentials only after successful commit.

**Verify:** persistence model and SQL Server integration tests covering success, revoke-failure, insert-failure, and commit-failure scenarios.

## Task 5 — Add authenticated API adapters

**Files:** `AuthController` plus request/response DTO files following current controller conventions.  
**RED tests:**
- 401: missing/invalid/expired access token.
- 403: authenticated but no local password available.
- 400/`MSG01`: missing `currentPassword` or `newPassword`.
- 400/`MSG05`: new password violates canonical policy or equals current input.
- 400/`MSG17`: current password is incorrect (hash mismatch).
- 200: success with new access + refresh tokens; Web variant sets existing HttpOnly refresh cookie and does not serialize refresh token.
- 5xx/`MSG127`: transaction/DB/token-service failure (only this cause maps to `MSG127`; not business-logic failures).

**Implement:** thin shared/mobile and Web endpoints that construct the same command from authenticated subject. The Web endpoint must use the current `WebRefreshCookie` helper. Route the error response through the existing `HandleFailure` pattern mapping `MSG01`, `MSG05`, `MSG17`, `MSG127` correctly per spec §5.

**Verify:** API integration and OpenAPI tests covering all error paths and success path.

## Task 6 — Audit and secret-safety verification

**RED tests:** audit data contains subject/action/timestamp only; logs and error responses contain no submitted secrets.  
**Implement:** minimal compliant audit event, or document why existing global audit middleware already covers this action.

**Verify:** audit/integration tests and source inspection.

## Task 7 — Security and regression suite

Add tests for:
- Cross-role self-only authorization (no user can change another's password).
- Old refresh tokens rejected after successful change.
- Replacement-session usability (new tokens work for subsequent requests).
- Complete transaction rollback on any failure (hash, revoke, insert, commit).
- No password, hash, access token, or refresh token in logs, errors, or audit.

Run:

```powershell
dotnet build TripMate.slnx
dotnet test TripMate.slnx
dotnet format TripMate.slnx --verify-no-changes
git diff --check
```

## Delivery gate

Review checklist before merge:
- ✓ All 7 tasks pass focused review.
- ✓ Error mapping matches spec §5: `MSG01`, `MSG05`, `MSG17` for business logic; `MSG127` only for infrastructure failure. Confirm mismatch is `MSG06`, client-side only.
- ✓ Transaction atomicity confirmed: credentials returned only after successful commit; rollback on any failure.
- ✓ No secrets in logs, errors, or audit.
- ✓ Full suite passes green.

Do not commit, push, or create a PR until every task passes focused review and full validation is green. Record unavailable SQL Server integration explicitly; it does not waive required CI.

