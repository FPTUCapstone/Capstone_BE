# UC-49 — Unlock-account notification delivery

## Purpose

When an administrator successfully unlocks an account, TripMate must preserve the unlock
transaction even when delivery fails, and must subsequently attempt to notify the affected
user. This replaces the old UC-49 implementation note that notification was excluded.

## Scope

- Rebase the UC-49 branch onto `develop` and resolve the three concurrent-file conflicts without
  discarding UC-49 behaviour.
- Deliver `AccountUnlocked` notifications through email when the target account has an email
  address.
- Make delivery durable: pending messages survive process restarts and are retried by a hosted
  worker.
- Retain a notification record for accounts without an email address. It represents an in-app
  notification for a future notification centre; the API must not claim that a push/SMS message
  was sent.
- Preserve the existing unlock transaction, idempotency, audit, and refresh-token revocation
  guarantees.

## Explicit exclusions

- Push/SMS transport, device-token registration, and a notification-centre API/UI.
- Retrofitting delivery behaviour for unrelated existing notification types.
- Changing the Administrator-only and protected-administrator rules already defined in
  `specs/TM-95-spec.md`.

## Design

### Durable delivery state

`dbo.Notifications` remains the durable outbox. The UC-49 handler inserts one
`AccountUnlocked` notification within the same database transaction as the account restoration,
audit record, refresh-token revocation, and unlock idempotency operation.

The notification is created as:

- `Email` + `Pending` channel matching canonical admin notification patterns (`ApproveOperatorApplication`, `RejectOperatorApplication`). The email delivery worker queries records with valid target emails (`NULLIF(LTRIM(RTRIM(user.email)), '') IS NOT NULL`) for dispatch and retry. Accounts without a valid email retain the pending Email notification record without false dispatch attempts.

### Email worker

A narrowly-scoped hosted service polls only `AccountUnlocked` email notifications in `Pending`
state. It atomically claims a notification before sending, uses a dedicated application email port
that accepts the persisted title/body, and then:

- marks it `Sent` and sets `SentAtUtc` after confirmed delivery;
- returns it to `Pending` after an unknown/transient delivery result so a later poll retries;
- marks it `Failed` only after a definite rejection.

The worker never participates in the HTTP request transaction. Therefore an SMTP failure cannot
roll back an already-authorized account unlock.

### Retry and duplicate safety

Only one worker may claim a row at a time. The claim must be released if the process stops or the
attempt cannot be completed. A retry must not create another `Notification` row; idempotent
replay of the unlock endpoint also creates no duplicate notification.

## Tests

- Handler: email versus no-email channel selection and one notification per successful unlock.
- SQL Server: rollback leaves no notification; success/replay/concurrent requests leave exactly
  one durable notification.
- Worker: confirmed delivery becomes `Sent`; transient/unknown result is retried; definite
  rejection becomes `Failed`; no-email fallback is not passed to SMTP.
- Rebase result: release build, affected tests, SQL Server migration/integration tests, formatting
  and the full test suite.

## Acceptance criteria

1. A successful UC-49 unlock is durable even when email transport is unavailable.
2. An email-capable account eventually has an email-delivery attempt; successful delivery is
   recorded as `Sent`.
3. A non-email account retains an honest pending in-app notification rather than an incorrect
   email-delivery claim.
4. Replaying the idempotency key does not duplicate notification, audit, token revocation, or
   unlock state.
