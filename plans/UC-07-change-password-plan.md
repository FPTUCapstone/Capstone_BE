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
**RED tests:** all old refresh tokens revoked; exactly one new replacement token; rollback keeps old hash and tokens unchanged.  
**Implement:** one existing database transaction that updates the password, revokes prior refresh rows, creates replacement refresh token with the required navigation/property conventions, and commits before returning credentials.

**Verify:** persistence model and SQL Server integration tests where configured.

## Task 5 — Add authenticated API adapters

**Files:** `AuthController` plus request/response DTO files following current controller conventions.  
**RED tests:** missing/invalid auth gets 401; Web success sets existing HttpOnly refresh cookie and does not serialize a refresh token; invalid credential maps to `MSG17`; policy/same password maps to `MSG03`.

**Implement:** thin shared/mobile and Web endpoints that construct the same command from authenticated subject. The Web endpoint must use the current `WebRefreshCookie` helper.

**Verify:** API integration and OpenAPI tests.

## Task 6 — Audit and secret-safety verification

**RED tests:** audit data contains subject/action/timestamp only; logs and error responses contain no submitted secrets.  
**Implement:** minimal compliant audit event, or document why existing global audit middleware already covers this action.

**Verify:** audit/integration tests and source inspection.

## Task 7 — Security and regression suite

Add tests for cross-role self-only authorization, old refresh rejection after success, replacement-session usability, and transaction rollback.

Run:

```powershell
dotnet build TripMate.slnx
dotnet test TripMate.slnx
dotnet format TripMate.slnx --verify-no-changes
git diff --check
```

## Delivery gate

Do not commit, push, or create a PR until every task passes focused review and full validation is green. Record unavailable SQL Server integration explicitly; it does not waive required CI.

