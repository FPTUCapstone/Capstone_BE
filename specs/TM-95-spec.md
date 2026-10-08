# TM-95 / UC-49 — Unlock User Account

## Goal

Allow an authenticated Administrator to restore a legitimately locked, non-administrative
account to the exact account state it held before the lock, without creating duplicate
administrative actions or leaving an old session usable.

## Scope

This change delivers the Backend contract only. UC-47 will later surface the action in a
real user-detail screen. No stand-alone screen that asks an administrator to type an
arbitrary user ID is in scope.

## Authority boundary

- The current persisted role model has only `Administrator`; the proposed System Owner /
  Platform Administrator split is not yet an approved schema/API contract.
- Until that split exists, the API requires `Administrator` and **rejects an Administrator
  target**, including the caller. This prevents a peer administrative account from restoring
  another privileged account.
- The later role split may replace the policy name but must preserve the target-protection
  rule unless the approved access-control model explicitly changes it.

## Public API

`POST /api/v1/admin/users/{userId:long}/unlock`

Required headers:

- `Authorization: Bearer <administrator access token>`
- `Idempotency-Key: <1–128 visible ASCII characters>`

Request body:

```json
{
  "reason": "Identity and account access were verified by support."
}
```

`reason` is required after trimming and is limited to 1,000 characters; it is persisted only
as administrative audit evidence, never returned to the affected user as an API error.

Success response: `200 OK`

```json
{
  "userId": 42,
  "restoredStatus": "Active",
  "unlockedAtUtc": "2026-10-07T08:00:00Z"
}
```

## Domain and persistence contract

`dbo.Users` gains nullable lock metadata:

- `status_before_lock` — an `AccountStatus` value captured when UC-48 locks the account.
- `locked_by_user_id` — the administrator who performed the lock.
- `locked_at_utc` — UTC timestamp of the lock.
- `lock_reason` — nonempty administrative reason, maximum 1,000 characters.

The fields are nullable only to support historical rows. A current lock must contain all four
values. The future UC-48 command is responsible for creating this complete lock record.

`admin.UserUnlockOperations` stores idempotency state, keyed uniquely by
`(administrator_user_id, idempotency_key)`. It retains the request hash and the successful
response needed for a byte-for-byte semantic replay. A reused key with another target or
reason returns a conflict and creates no second audit event.

The SQL migration is idempotent and database-first. It adds column/default/check/FK/index
contracts to both the canonical schema and an explicit migration; it does not make legacy
`Locked` rows safe to unlock by guessing a status.

## Unlock rules

1. The target must exist and must not have role `Administrator`.
2. The target must currently be `Locked`.
3. The lock record must be complete and `status_before_lock` must not be `Locked`.
4. The target status becomes `status_before_lock`; all lock metadata is cleared.
5. All non-revoked refresh tokens for the target are revoked at the same UTC instant. The
   operation issues no replacement token, so the person must authenticate again.
6. The operation writes one successful `AuditLog` with action `UnlockUserAccount`, before/after
   status metadata and the administrator-provided reason.
7. User update, token revocation, audit log, and idempotency result are one transaction. A
   failure rolls back all of them.

## Error contract

| HTTP | Error code | Meaning |
| --- | --- | --- |
| 400 | `user.unlock_request_invalid` | Missing/invalid idempotency key or blank/oversized reason. |
| 401/403 | standard authorization result | Caller is unauthenticated or not an Administrator. |
| 403 | `user.unlock_protected_administrator` | The target is an Administrator, including self. |
| 404 | `user.not_found` | No target user exists. |
| 409 | `user.not_locked` | Target is not currently locked. |
| 409 | `user.lock_recovery_state_missing` | Historical/incomplete lock metadata cannot be safely restored. |
| 409 | `user.unlock_idempotency_key_payload_mismatch` | A key was reused with different request content. |

The same idempotency key and same request after a committed success returns the original
successful response without changing state or adding another audit record.

## Acceptance and verification

- Unit tests cover a valid restoration, protected target, self-target, missing/incomplete lock
  metadata, non-locked target, same-key replay, and same-key payload mismatch.
- HTTP integration tests cover authorization, response/error mapping, and replay behavior.
- SQL Server integration tests prove the schema contracts, atomic rollback when audit/idempotency
  persistence fails, unique idempotency enforcement, and refresh-token revocation.
- `dotnet format`, Release build, unit/integration tests, and database migration tests pass.

## Explicit exclusions

- UC-47 user list/detail UI.
- UC-48 lock UI/endpoint, except for domain/persistence support needed to preserve its future
  lock metadata.
- New roles or permission tables for the proposed System Owner / Platform Administrator model.
- Email/push notification to the user; no approved notification template or delivery rule exists.
