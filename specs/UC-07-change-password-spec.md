# UC-07 Change Password — Backend Specification

**Status:** DRAFT — awaiting developer approval  
**Authoritative product reference:** SRS §3.2.7 Change Password and the current UC-07 catalogue entry supplied by the product team.

## 1. Purpose

An authenticated local-password user changes their own password by proving knowledge of the current password. The Backend remains the sole authority for password verification, policy enforcement, hashing, token revocation, token issuance, and audit data.

## 2. Scope and actor decision

### In scope

- An authenticated user changes only their own local account password.
- Verify the submitted current password against `dbo.Users.password_hash`.
- Validate the new password with the existing canonical local-password policy.
- Reject a new password equal to the current password.
- Atomically replace the password hash, record the existing user update timestamp, revoke existing refresh-token sessions, and issue a replacement session for the caller.
- Serve Web and Mobile through actor-appropriate transport adapters over one application command.
- Audit the password-change security event without storing a password, password hash, access token, refresh token, or credential-derived value in the audit payload.

### Out of scope

- Password reset, OTP, email/SMS delivery, MFA, account activation, unlocking, role changes, or administrator reset of another account.
- Firebase as a password authority.
- A password-history table, password expiry, password-reuse history, schema migration, or changes to the configured password policy.
- Changing another user's credentials through an administrative route.

### SRS_INTERNAL_CONFLICT — actor ownership

The current UC catalogue supplied for this task lists **Traveler, Tour Operator, Staff, and Administrator**. The detailed SRS text at §3.2.7 lists **Traveler, Tour Operator, and Administrator** only. This specification treats the newer supplied UC catalogue as the intended delivery scope, including Staff, because UC-07 is self-service and applies only to the authenticated subject. The SRS actor list and `WEB_SCOPE_MATRIX.md` must be aligned before merge; no role gains authority to change another user's password.

## 3. Security and domain rules

1. Authentication is mandatory. The subject user ID comes from the validated access token, never from a body field.
2. Only an active account with a non-null local `PasswordHash` can change a password. Google-only accounts do not acquire a local password through UC-07.
3. `currentPassword` and `newPassword` are secret inputs. They must not be logged, placed in an audit payload, returned in an error, or persisted outside the password hash.
4. New-password validation reuses the existing registration/UC-06 policy. The current codebase policy is 8–72 characters, no whitespace, and uppercase, lowercase, digit, and special-character requirements. A policy change is separate work.
5. `newPassword` must differ from `currentPassword`, verified by checking the submitted new value against the existing hash. String comparison alone is not authoritative.
6. Passwords are stored only through the existing `IPasswordHasher` / `IPasswordHasherService`; plaintext is never stored.
7. On success, revoke all active `dbo.RefreshTokens` for the user, then create exactly one newly issued refresh token for the caller in the same database transaction. Existing access tokens expire naturally; the caller receives a new access token.
8. Any failed validation or failed current-password verification leaves password and token rows unchanged.
9. The implementation preserves existing account-status rules. It must not reactivate, unlock, approve, or modify an account.

## 4. Application contract

### Command

`ChangePasswordCommand(CurrentPassword, NewPassword, SubjectUserId, ClientSessionContext)`

`SubjectUserId` and `ClientSessionContext` are assembled by authenticated transport code. They are not client-controlled body values.

### Request DTO

```json
{
  "currentPassword": "CurrentPassword1!",
  "newPassword": "NewPassword2@"
}
```

`confirmNewPassword` is a Web/Mobile-only confirmation field and is never sent to the Backend.

### Success response

The application returns the existing authentication session result: a newly issued access token plus a newly issued refresh token and expiry metadata. The Web adapter writes the raw refresh token only through the existing secure HttpOnly refresh-cookie mechanism and returns the existing safe `WebAuthResponseDto`; it must not expose the refresh token in JSON. The Mobile adapter uses the existing mobile authentication response contract.

### Proposed routes

- `POST /api/v1/auth/change-password` — authenticated Mobile/shared token-response adapter.
- `POST /api/v1/auth/web/change-password` — authenticated Web adapter; appends the replacement HttpOnly refresh cookie and returns the safe Web authentication response.

Both routes delegate to the same command/handler. The exact existing authentication scheme names and OpenAPI response types must be reused after implementation discovery; this does not authorize a second password authority or a legacy response envelope.

## 5. Validation and error mapping

| Condition | HTTP/result | User-safe outcome |
| --- | --- | --- |
| Missing field | 400 validation problem, `MSG01` | Required-field feedback |
| New password violates canonical policy or equals current password | 400 validation problem, `MSG03` | Password policy feedback |
| Current password is incorrect | 400 validation problem, `MSG17` | `Current password does not match our records.` |
| Confirmation mismatch | prevented client-side; Backend does not receive confirmation | `MSG04` on client |
| Unauthenticated, expired, or invalid access token | 401 | Sign in again |
| Authenticated account cannot use a local password | 403 problem code to be named during implementation | Safe local-password-unavailable feedback; no password is created |
| Database/session issuance failure | 5xx, `MSG127` | Generic retry feedback |

Because this is a self-service authenticated operation, `MSG17` for the caller's own current credential is permitted by SRS.

## 6. Transaction and session semantics

Within one existing database transaction:

1. Load the authenticated user with tracking.
2. Verify current password and validate the new password before mutation.
3. Hash and set the new password; set `UpdatedAtUtc` from `IDateTimeProvider`.
4. Revoke every active refresh token for the subject.
5. Generate, hash, and persist one replacement refresh token associated with the authenticated caller/device context.
6. Persist password and session changes atomically.
7. Commit, then return replacement access and refresh tokens.

If the transaction fails, no replacement session is returned. Audit persistence must use the existing project convention without exposing secrets.

## 7. Acceptance criteria

1. Traveler, Tour Operator, Staff, and Administrator can change only their own local password after actor ownership is formally reconciled in SRS.
2. Correct current password and valid distinct new password update the hash and return a renewed current session.
3. Incorrect current password returns `MSG17` and makes no password or token mutation.
4. Invalid or same-as-current new password returns `MSG03` and makes no mutation.
5. All pre-existing refresh sessions are revoked; exactly one replacement session is issued for the successful caller.
6. Invalid/missing authentication is rejected before password processing.
7. Google-only/no-local-password accounts cannot create a password through this endpoint.
8. Logs, ProblemDetails, responses, and audit payloads contain no raw password, hash, access token, or refresh token.
9. Controller remains thin; policy and mutation stay in the application/domain/infrastructure layers.

## 8. Test requirements

- Validator tests for required values and canonical policy.
- Handler tests for success, incorrect current password, same password, no local password, restricted account, transaction failure, and no-secret result/error behavior.
- Persistence/integration tests confirming password replacement, revocation of prior sessions, creation of exactly one replacement session, and rollback on failure.
- API tests for 200, 400/`MSG01`, 400/`MSG03`, 400/`MSG17`, 401, 403, and no refresh token in the Web JSON response.
- Web adapter tests confirming the replacement cookie follows the existing `WebRefreshCookie` convention.

## 9. Definition of done

- No database schema change or EF migration is introduced.
- Approved route contracts are documented in OpenAPI.
- All acceptance and security tests pass.
- `dotnet build TripMate.slnx`, required tests, `dotnet format TripMate.slnx --verify-no-changes`, and `git diff --check` pass.

