# Specification: TM-64 [UC-18] Invite Group Members

**Feature**: Invite Group Members
**Jira Ticket**: TM-64
**Use Case**: UC-18
**Branch**: `feature/khanhpq-invite-group-members`
**Target Repositories**: `Capstone_BE`, `Capstone_Mobile`
**Status**: Approved for implementation

## Objective

An authenticated Traveler who is the Group Host can create, view, copy, share,
and explicitly regenerate a QR-backed invitation for one travel group. Creating
or sharing an invitation never creates a membership; UC-23 redeems invitations.

## Business Decisions

- An invitation is usable for 30 days after creation.
- UC-18 has no usage-quota business rule. The existing non-null database
  `max_uses` column is stored as the named technical value `UnlimitedUses` and
  is not displayed or controlled by UC-18.
- Opening Invite Members reuses the group's usable invitation. It never rotates
  a code merely because the screen is opened again.
- Regenerate is an explicit Host action. It immediately expires every usable
  invitation for the group, then creates one replacement invitation.
- At most one usable invitation may exist for a group after either operation
  commits.

## Authorization and Failures

- Both operations require `[Authorize(Roles = Traveler)]`.
- The caller must equal `TravelGroup.HostUserId`; otherwise return `403` with
  `travel_group.host_permission_required` and MSG126.
- A missing group returns `404` with `travel_group.group_not_found`.
- Missing, malformed, or payload-mismatched idempotency keys return `400` or
  `409` ProblemDetails according to the API contract.
- Unique-code exhaustion, persistence errors, and network/server failures map
  to MSG127 without exposing implementation details.

## HTTP Contract

### Get or create the current invitation

`POST /api/v1/travel-groups/{groupId}/invitation`

Required request header: `Idempotency-Key: <UUID>`.

- If a usable invitation exists, return it with `200 OK` and do not create a
  replacement.
- Otherwise atomically create one invitation and return it with `200 OK`.
- Retrying a write operation with the same key returns the invitation produced
  by that operation. Reusing a key for another group or action returns `409`.

### Regenerate an invitation

`POST /api/v1/travel-groups/{groupId}/invitation/regenerate`

Required request header: `Idempotency-Key: <UUID>`.

- Atomically expire the previous usable invitation(s), create one replacement,
  and return it with `200 OK`.
- Retrying the same operation key returns that replacement rather than rotating
  again.

### Success payload

```json
{
  "groupId": 1,
  "groupName": "Da Nang Summer Trip",
  "inviteCode": "TM7X9K2A",
  "qrData": "tripmate://groups/join?code=TM7X9K2A",
  "expiresAt": "2026-10-14T10:30:00Z"
}
```

`qrData` is an opaque deep-link payload for UC-23. The response is a raw DTO on
success and RFC-7807 ProblemDetails on failure; no synthetic envelope is used.

## Persistence and Concurrency

- Add an idempotent database-first SQL script for
  `social.GroupInvitationOperations` with `traveler_user_id`, `group_id`,
  `operation_type`, `idempotency_key`, `invitation_id`, and `created_at`.
- Enforce a unique key on `(traveler_user_id, idempotency_key)`.
- Bind an operation key to its group and action; a mismatch returns `409`.
- Execute check/create or expire/create inside one serializable transaction.
- Acquire a SQL Server transaction-scoped application lock whose resource is
  derived from `groupId` before looking up or changing invitations. This makes
  concurrent requests for one group deterministic.
- Generate an 8-character alphanumeric code using cryptographic randomness.
  Retry boundedly when the database's unique `invite_code` constraint reports a
  collision; return MSG127 only after all attempts fail.

## Mobile Flow

1. Group Host selects **Invite Members**. Mobile creates a UUID operation key,
   disables the initiating control, and posts to the get-or-create endpoint.
2. The loaded screen displays group name, QR data, invitation code, and expiry.
   It formats the timestamp as `dd/MM/yyyy HH:mm` in Asia/Ho_Chi_Minh time.
3. Copy awaits the platform clipboard write before showing MSG55. Share sends
   the QR/deep-link payload.
4. **Regenerate Invitation** asks for confirmation, creates a new UUID key,
   calls the regenerate endpoint, and replaces the displayed invitation only
   after the server succeeds.
5. Authentication failure redirects to sign-in while preserving the target;
   permission and system failures show MSG126 and MSG127 respectively.

## Required Verification

- Backend: full build/test; SQL Server tests for concurrent get-or-create,
  concurrent regenerate, operation-key replay/mismatch, code collision,
  rollback, and HTTP 401/403/404/200 contracts.
- Mobile: `flutter pub get`, formatter, analyzer, unit/widget tests, and debug
  APK build. Tests cover whitespace group names, clipboard failure, loading
  control disabling, regenerate confirmation, and endpoint/header contracts.
