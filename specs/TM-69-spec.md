# Specification: TM-69 [UC-23] Join Shared Group Trip

**Feature**: Join Shared Group Trip  
**Jira Ticket**: TM-69  
**Use Case**: UC-23  
**Branch**: `feature/khanhpq-join-shared-group-trip`  
**Target Repositories**: `Capstone_BE`, `Capstone_Mobile`  
**Status**: Completed

## Objective

Allow an authenticated Traveler to redeem one valid private group invitation,
create or reactivate exactly one active membership, receive the joined group,
and open that group in the Flutter mobile application. Invitation generation
and regeneration remain UC-18 responsibilities.

## Scope

- A Traveler enters an invitation code or scans a QR payload issued by UC-18.
- The API validates the invitation and atomically creates or reactivates the
  membership.
- The operation is idempotent and safe when the same request is retried or
  submitted concurrently.
- Mobile provides an in-app Join Group entry point, manual-code input, QR
  scanning, clear English states, and navigation to the joined group.

## Explicitly Out of Scope

- Creating, sharing, or regenerating invitations (UC-18).
- Host member management and removal operations (UC-20).
- Invitation quotas, public group discovery, or friend discovery.
- Automatic location-sharing opt-in (UC-22).
- Group closure and Host succession (UC-21).
- OS-level Android App Links and iOS Universal Links. QR codes are scanned
  inside TripMate for this MVP.

## Business Decisions

- The caller must be an authenticated Traveler.
- UC-18 invitation codes are exactly eight uppercase alphanumeric characters.
  The API trims input and normalizes it to uppercase before validation.
- The only accepted QR payload is
  `tripmate://groups/join?code=<INVITATION_CODE>`. The Mobile client extracts
  the `code` parameter and submits the same API request as manual entry.
- A valid invitation is one that exists, has not expired, has remaining
  technical capacity, and belongs to an existing group. UC-18 represents
  unlimited business usage with its technical `int.MaxValue` capacity.
- `Active` membership: reject with MSG57 (409 Conflict) and include `groupId`
  in the problem details metadata so client can route to the existing group.
  It must not create a second row or increment invitation usage.
- `Left` or `Removed` membership: reactivate the existing `(group_id, user_id)`
  row to `Active`, set `JoinedAtUtc` to the rejoin time, clear `LeftAtUtc`,
  and set `LocationSharingEnabled` to `false`. This allows a previously departed
  or removed user with a valid invitation to rejoin per Report 3 while avoiding
  restoring unconsented location sharing.
- A successful new join or rejoin increments `GroupInvitations.UsedCount`
  once. It is audit/technical data only and does not introduce a member quota.
- An active group member receives access to the associated shared itinerary
  (`itineraryId` returned in response) and permitted group information. Group
  membership does not grant itinerary-edit permission or itinerary ownership.
- The current schema has no group lifecycle status. For this UC, group
  availability means that the group record exists and the invitation is usable.
  UC-21 must add and enforce an explicit closed/unavailable state later.

## HTTP Contract

### Join a group

`POST /api/v1/travel-groups/join`

Required request header:

```text
Idempotency-Key: <UUID>
```

Request body:

```json
{
  "invitationCode": "A7K4P2QX"
}
```

Success (`200 OK`):

```json
{
  "groupId": 1,
  "groupName": "Da Nang Summer Trip",
  "itineraryId": 12
}
```

The success payload is a raw DTO. Failures use RFC-7807 `ProblemDetails`; no
synthetic response envelope is introduced.

## Authorization and Failure Contract

| Condition | HTTP status | Error/message behavior |
| --- | --- | --- |
| Missing or malformed `Idempotency-Key` | 400 | ProblemDetails; key must be a non-empty UUID. |
| Missing or malformed invitation code | 400 | Validation failure; Mobile shows MSG01 for an empty code. |
| Unauthenticated caller | 401 | Existing authentication behavior / MSG125 on Mobile. |
| Authenticated non-Traveler | 403 | Existing role authorization behavior / MSG126 on Mobile. |
| Invalid, expired, regenerated, or unavailable invitation | 400 | `travel_group.invitation_unavailable`; Mobile shows MSG56. |
| Already active member | 409 | `travel_group.already_active_member`; returns `groupId` in metadata; Mobile shows MSG57 and navigates to group. |
| Reused operation key with a different normalized code | 409 | `travel_group.idempotency_key_payload_mismatch`. |
| Unexpected persistence or network failure | 5xx | Mobile shows MSG127; no partial membership is retained. |

## Persistence and Concurrency

Database-first SQL is required; EF migrations are forbidden.

Add `social.GroupJoinOperations` with:

- `join_operation_id` primary key;
- `traveler_user_id`, `idempotency_key`, normalized `invitation_code`;
- `invitation_id`, `group_id`, and `created_at`;
- a unique constraint on `(traveler_user_id, idempotency_key)`;
- foreign keys to the Traveler, invitation, and group.

The operation record binds a retry key to its normalized request payload. A
retry after a successful commit returns the same group, even if the invitation
later expires or is regenerated.

The command executes one SQL Server serializable transaction. To prevent
deadlocks with UC-18 invitation regeneration, target group ID is pre-resolved
so application locks are acquired before any database tables are touched:

```text
operation key (Traveler + UUID) -> invitation code -> travel group
```

After acquiring all locks, it re-reads the invitation and membership,
then performs one of: replay, reject, create member, or reactivate member. The
membership update, invitation usage count, and join-operation record are saved
in the same transaction. This prevents duplicate active memberships for retries
and for simultaneous requests using different keys.

## Mobile Flow

1. Traveler opens **Join Group** from the Traveler area.
2. Traveler enters an eight-character code or selects **Scan QR Invitation**.
3. QR scanning is in-app only; unsupported/malformed QR data is treated as an
   invalid invitation and shown with MSG56.
4. Mobile creates one UUID idempotency key per user join attempt, disables both
   submission paths while the request is pending, and keeps that key for retry
   of the same attempt.
5. On success, Mobile displays MSG58 using `groupName`, then opens the exact
   `groupId` route.
6. On 409 already-active member, Mobile extracts `groupId`, shows MSG57, and
   redirects to the group screen.
7. Mobile maps unavailable, auth, and unexpected failures to MSG56,
   MSG125/MSG126, and MSG127 respectively without exposing raw server details.

## Acceptance Criteria

- A valid invitation creates one `Active` membership with a joined timestamp
  and returns the target group.
- A `Left` or `Removed` membership is reactivated to `Active` with
  `LocationSharingEnabled = false` rather than inserted as another member.
- An already `Active` member receives 409 Conflict with `groupId` in metadata,
  sees MSG57, and is routed to their group.
- A regenerated or expired invitation sees MSG56 and creates no membership.
- Two concurrent calls using the same Traveler and idempotency key return the
  same group; database state contains one membership and one join operation.
- Concurrent join attempts for the same Traveler/group with different keys
  cannot create duplicate active membership.
- Reusing an idempotency key with another invitation code returns 409.
- Failed persistence leaves no new/reactivated member, usage increment, or
  operation record.
- Mobile supports manual code and in-app QR entry, handles loading/error states,
  and navigates only after server success.

## Verification Required

- Domain unit tests for membership creation, rejoin, and forbidden transitions.
- Command/validator tests for every rule and failure above.
- SQL Server integration tests for same-key concurrency, different-key
  membership races, transaction rollback, replay, payload mismatch, and
  UC-18 invitation regeneration race.
- Endpoint/OpenAPI tests for request body, required header, 401, 403, 400,
  409, and success contracts.
- Mobile parser, Cubit, repository, and widget tests for manual and QR paths.
- Final Backend and Mobile format/build/test gates required by each repository.
