# Specification: TM-65 [UC-19] View Group Members

**Feature**: View Group Members
**Jira Ticket**: TM-65
**Use Case**: UC-19
**Platforms**: ASP.NET Core API and Flutter Mobile
**Status**: Implementation in progress; Report 3 alignment pending

## Goal

Allow an active Traveler in a travel group to view the current active members
and the membership information permitted by the group privacy rules.

## Scope

UC-19 is read-only. It does not create invitations, join a group, remove a
member, leave a group, change the Group Host, or change location-sharing
settings. Those actions remain in UC-18, UC-23, UC-20, UC-21, and UC-22.

## Authorization and Privacy

1. The caller must be an authenticated Traveler.
2. The caller must have an active `GroupMember` record for the requested group.
3. Only active members are returned.
4. The response exposes only: member ID, display name, optional avatar URL,
   Host flag, joined timestamp, and location-sharing enabled state.
5. The response must not expose email addresses, telephone numbers, precise
   locations, credentials, inactive/removed member data, or internal audit data.
6. A group containing active members must have exactly one Host. If persisted
   data violates this invariant, the API logs the inconsistency and fails
   safely with a generic service error. This read-only endpoint does not
   perform BR-49 Host succession; repair belongs to the Host-succession flow.

## API Contract

### `GET /api/v1/travel-groups/{groupId}/members`

Authentication: bearer token for a Traveler.

Success (`200 OK`):

```json
{
  "groupId": 42,
  "groupName": "Da Nang Weekend",
  "itineraryId": 10,
  "memberCount": 2,
  "members": [
    {
      "memberId": 101,
      "displayName": "Khanh Phan",
      "avatarUrl": null,
      "isHost": true,
      "joinedAtUtc": "2026-09-21T09:00:00Z",
      "locationSharingEnabled": false
    }
  ]
}
```

Failures:

- `401` when no valid authenticated session is supplied.
- `403` with the standard permission failure when the authenticated Traveler is
  not an active member of the group.
- `404` when the group does not exist.
- `500` with the standard safe service failure when membership data cannot be
  retrieved or violates the Host invariant.

## Backend Design

Use a query vertical slice named `GetTravelGroupMembers` under
`Features/TravelGroups`. The handler obtains the current Traveler from the
authenticated request, loads the group and active memberships, validates
membership and the single-Host invariant, then projects the response directly
from persisted membership and user profile data. It performs no writes and has
no idempotency requirement.

The controller exposes the endpoint in the existing Travel Groups controller
and returns standard API problem/failure responses. EF query projection must be
read-only and use UTC conversion consistently for `joinedAtUtc`.

## Mobile Design

The group details screen links to a dedicated Members page. A Cubit loads the
member list through the Domain repository abstraction and renders loading,
success, empty, permission-denied, not-found, and generic-error states.

The page displays the group name, associated itinerary ID, and member count
followed by member rows with an avatar fallback, display name, Host badge,
joined date, and a non-sensitive location-sharing status. The Host-only actions
shown in the surrounding group screen are not implemented or changed by this
use case.

Report 3 currently mentions group status and immediate BR-49 succession in
UC-19. The current TravelGroups table and API contract have no group-status
field, and this read-only endpoint must not mutate Host membership. The team
must reconcile those requirements in the authoritative SRS before claiming
full UC-19 acceptance; no status value is fabricated by this implementation.

## Acceptance Criteria

1. An active Host can view all active members and exactly one Host badge.
2. An active non-Host member can view the same permitted list but receives no
   authority from UC-19 to manage membership.
3. A Traveler outside the group cannot retrieve any membership data.
4. Removed/inactive memberships are absent from the response and the UI.
5. A missing group is distinguishable from access denial in the mobile UI.
6. The list remains usable with no avatar, long display names, and a small
   mobile screen.
7. The feature does not modify any travel group or membership record.

## Test Evidence Required

- Backend unit and SQL Server integration tests for authorization, active-member
  filtering, single-Host validation, and UTC response fields.
- Mobile repository, Cubit, and widget tests for success, empty, 403, 404, and
  generic failure states.
- Existing project quality gates run on the final branch head.
