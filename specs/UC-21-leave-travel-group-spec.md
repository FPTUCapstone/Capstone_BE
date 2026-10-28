# UC-21 Leave Travel Group — Backend specification

**Status:** Approved for BE implementation on 2026-10-25. Work proceeds on the isolated UC-21 branch under `AGENTS.md`.
**Source:** Report 3 V2 §3.4.5, BR-48/49/51; `docs/adr/0003-explicit-travel-group-lifecycle-state.md`; current group, navigation and notification code.

## Boundary

An authenticated Traveler leaves one self-organized Travel Group through Mobile. This changes group membership, host succession, group closure, invitations and access to group-only data. It does **not** alter the creator-owned itinerary, Booking, payment, ticket, tour departure or tour-customer communication group. This specification does not grant the successor Host permission to edit an itinerary owned by the former Host; that is a separate UC-17/itinerary decision.

## API contract

- `POST /api/v1/travel-groups/{groupId}/leave`, Traveler authorization, no request body.
- Require a nonempty UUID `Idempotency-Key`, consistent with create/join; persist the complete successful outcome. An exact retry returns the original `200` response even after a later rejoin. Reusing the key with a different group returns `409`.
- Success `200`: `{ groupId, membershipStatus: "Left", groupClosed, newHostUserId, leftAtUtc }`; `newHostUserId` is null unless a successor was selected. The response reports committed state, not a client-side prediction.
- Missing/invalid key or nonpositive group ID: `400`. Group not found: `404`. Nonmember/Removed member: `403`. Already Left with a new key, or Closed group: `409`. Inconsistent persisted Host/member state: server error with no mutation and an internal log, not a user-actionable conflict. Unauthenticated: `401`; wrong role: `403`. Use the project's `Result`/`HandleFailure` and `ProblemDetails` conventions; do not invent a response envelope.

## State transition

1. Execute inside one serializable transaction. First serialize the caller's leave key, then acquire the same transaction-scoped `TripMate:GroupInvitation:{groupId}` lock used by join and invitation operations. Invitation-code locks, when needed by another flow, must precede that group lock. Read/update group state only after acquiring it, and regression-test concurrent join, invitation and leave.
2. Check an existing leave-operation replay before rejecting current membership or closure. Otherwise read the group and Active members; reject Closed, nonmember, Removed or Left membership as specified above.
3. Non-Host: set own membership to `Left`, set `left_at`, disable location sharing. Host remains unchanged.
4. Host with remaining Active members: select the member with earliest `joined_at`; resolve an exact timestamp tie by smallest `user_id` (approved deterministic extension to BR-49). Change `host_user_id` and mark old Host `Left` in the same transaction. Disable old Host location sharing.
5. Final Active Host: mark membership `Left`, set lifecycle state `Closed` and `closed_at`, expire all usable invitations. Keep the historical `host_user_id` value because the schema requires it, but it grants no active Host privileges after closure. A Closed group cannot reopen through join or invitation replay.
6. Never change Booking, payment, ticket or itinerary owner. End any active navigation session for which this group membership is the caller's only access right, with an explicit access-revoked reason; preserve sessions the caller can still access independently as owner or through another valid right. This transition must be atomic with the membership change or otherwise prevent any post-leave group-only snapshot/progress disclosure. Mobile clears group-only cached state on success.
7. When a successor Host is chosen, commit exactly one durable `InApp` notification record addressed to that successor in the same transaction as the Host transfer. Later notification reading/display failure must not roll back that committed transfer; retries must not create duplicates. The new Host's current role is read from group state, independently of notification delivery.

## Invitation compatibility

- Follow ADR-0003: `status` is explicitly `Active` or `Closed`, with `closed_at` null exactly when Active. Add both fields by idempotent SQL migration and EF mapping. Migration must audit/backfill existing groups and reject inconsistent rows instead of silently changing a group's business state.
- UC-18 get/create/regenerate must reject closed groups and require the caller to be the Active Host. A replay of an old invitation operation must not present a usable invitation after closure.
- UC-23 must reject closed groups even when a prior invitation code has not expired, including concurrent join/closure. A historical exact join replay may return its recorded outcome but must not reactivate membership.
- `GetTravelGroupMembers` and any group-only location reads must require an Active member of an open group.
- Existing itinerary access for Manual itineraries is a separate UC-17/itinerary defect; UC-21 must not promise Host succession changes itinerary ownership/edit rights. Any read relying on group membership must also exclude Closed groups.

## Navigation and notification integration

- A group-only TripSession must terminate with a distinct system reason such as `GroupAccessRevoked`; it must not be mislabeled as a traveler-completed trip. `Get`, progress and `Complete` must not return its itinerary snapshot to a former member. A session remains available if the same traveler still has an independent right to that itinerary (for example, creator ownership). These checks must be concurrency-safe with leave.
- Extend SQL Server's `CK_TripSessions_CompletionReason` to allow `GroupAccessRevoked` through an idempotent migration before deploying the Backend; the old constraint rejects this write and would roll back the leave transaction.
- Add `InApp` to the `dbo.Notifications.channel` constraint and `NotificationChannel` enum. On succession, insert `Type = TravelGroupHostTransferred`, `RelatedEntityType = TravelGroup`, `RelatedEntityId = groupId`, `UserId = successorUserId`, `Status = Pending`. Keep title/body free of booking, ticket and private member data. The unique leave-operation record prevents a retry from inserting a duplicate. A database write failure aborts the leave transaction; a later read/UI failure does not undo a committed transfer.
- Minimal recipient-only read contract: `GET /api/v1/notifications?unreadOnly=true&limit=20&beforeId={optional}` returns the authenticated user's `InApp` notifications newest first (descending notification ID); `unreadOnly` defaults to false, `limit` defaults to 20 and accepts 1–50, and `beforeId` is an exclusive cursor. Response: `{ items: [{ id, type, title, body, relatedEntityType, relatedEntityId, createdAtUtc, isRead }], nextBeforeId }`, with `nextBeforeId = null` at the end. Never accept a caller-supplied user ID. `POST /api/v1/notifications/{notificationId}/read` marks only that user's `InApp` record `Read`, is safe to retry, returns `204`, and returns `404` for a missing or other-user record. Both endpoints require authentication; invalid pagination returns `400`. Use existing `Result`/`ProblemDetails` conventions. Mobile rendering remains a separate Mobile task.
- This is an in-app inbox contract, not push delivery. Do not add email, SMS, FCM delivery or a new background worker for UC-21. Until Mobile consumes the read API, BE can only report that the notification is stored and retrievable, not that it has been shown to the user.

## Acceptance checks

- Member leave; Host succession and one successor notification; final-Host closure; timestamp tie; inactive/nonmember; replay including rejoin; concurrent leave/join/invitation; stale invitation; group-only read denial; group-only navigation termination without stopping independently accessible sessions; Booking/ticket untouched.
- Apply all UC-21 idempotent SQL migrations before deploying the Backend that maps `status`, `closed_at`, leave operations, the new navigation completion reason and `InApp` notifications.
- Notification reads must be recipient-isolated, paginated and retry-safe when marked read. A delayed/failed Mobile read cannot affect the committed Host role.

## Approved decisions

1. Tie-break: smallest `user_id` after equal `joined_at`.
2. Require `Idempotency-Key` and a durable leave-operation outcome.
3. Use explicit `Active`/`Closed` state plus `closed_at` per ADR-0003; retain historical `host_user_id` without privileges after closure.
4. End only navigation sessions whose access depends exclusively on the departed group membership; preserve independently accessible personal sessions.
5. Create an in-app notification for the successor Host; delivery failure does not roll back the Host transfer.

## Approval boundary

The notification direction is approved: persist an `InApp` event and offer recipient-only read/acknowledge APIs. This BE contract and the companion implementation plan were approved for implementation on 2026-10-25. Mobile display is not included in the BE work.
