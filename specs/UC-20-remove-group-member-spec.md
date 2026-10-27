# UC-20 Remove Group Member — Backend specification

**Status:** Backend contract and implementation plan approved by the user on 2026-10-27; implementation is on `feature/uc20-remove-group-member`.
**Companion:** [`../plans/UC-20-remove-group-member-plan.md`](../plans/UC-20-remove-group-member-plan.md), [`../../.scratch/uc20/spec.md`](../../.scratch/uc20/spec.md), and [`../../.scratch/uc20/plan.md`](../../.scratch/uc20/plan.md).
**Sources:** Report 3 V2 §3.4.4, BR-45/48/51; Report 4 UC-20 design; `specs/UC-21-leave-travel-group-spec.md` for shared lifecycle and `InApp` conventions; root ADR-0003 and ADR-0004.

## Boundary

An authenticated Traveler who is the active Group Host removes one other active member from an Active, self-organized Travel Group. The Host and group remain active. The target membership becomes `Removed`, records `left_at`, disables location sharing, and immediately loses rights that depend on that group membership. This operation does not change itinerary ownership, Booking, payment, tickets, or tour-customer groups. A removed Traveler may rejoin later with a valid UC-23 invitation; removal is not a permanent ban.

## Removal API contract

- `DELETE /api/v1/travel-groups/{groupId}/members/{memberUserId}` with Traveler authorization, no body, and a nonempty UUID `Idempotency-Key` header. Determine the acting Host from the authenticated session; never accept an actor ID from the request.
- First success: HTTP `200` with `{ groupId, memberUserId, memberName, removedAtUtc }`. `memberName` is the target's permitted display name captured for Mobile MSG60. The response describes the committed removal.
- Persist a successful operation keyed by `(host_user_id, idempotency_key)`, including its group, target, and exact response fields. An exact retry returns the original `200` response, even if the target later rejoins. Reusing a key with a different group or target returns `409` without mutation. Replay creates no second removal, `AuditLog`, or notification.
- Invalid/nonpositive IDs, missing/invalid key, or self-removal: `400`. Unauthenticated: `401`. Wrong role or caller who is not the active Host: `403`. Group absent: `404`. Target not Active, target is the Host, or group Closed: `409`. Inconsistent single-Host state or unexpected persistence failure: server error and internal log, with no committed mutation. Use the existing `Result<T>`, `HandleFailure`, and `ProblemDetails` conventions; add only the needed travel-group error codes.

## Atomic state transition

1. Execute in one serializable transaction. Serialize the actor's operation key, then acquire the existing transaction-scoped group lifecycle lock (`TripMate:GroupInvitation:{groupId}`); preserve the lock order used by join, invitation, and UC-21 leave flows. Check a recorded exact replay before rejecting current membership or group state. For a new operation, require an Active group, an active Host actor, a distinct active non-Host target, and the single-Host invariant.
2. Mark the target `Removed`, set `LeftAtUtc`, and set `LocationSharingEnabled` to false. Do not alter the Host or close the group. Persist the operation outcome, one `AuditLog` identifying actor, target, group, and removal time, and one `InApp` notification for the removed Traveler in the same transaction. If any required write fails, roll back all changes.
3. End active navigation sessions whose access depends exclusively on the removed membership, using a distinct system access-revoked reason. Preserve sessions for which the Traveler retains independent access, such as itinerary ownership. Every member-only group, shared-location, itinerary, and navigation read or mutation must check current access. A request that checks access after the removal commits is denied. A request authorized before the removal commits may finish; the Backend cannot recall a response already in progress. Serialize concurrent navigation progress and removal so progress checked after removal is denied. If immediate revocation for new requests cannot be established, roll back removal per ADR-0004.
4. No rejoin or invitation state is changed by removal. A later valid invitation can reactivate the `Removed` membership under UC-23; an old idempotency key still replays the historical removal response without removing that new membership.

## Notification and inbox contract

- Add or reuse `InApp` in the `dbo.Notifications.channel` constraint and `NotificationChannel` enum. Persist exactly one recipient-owned removal event, for example `Type = TravelGroupMemberRemoved`, `RelatedEntityType = TravelGroup`, `RelatedEntityId = groupId`, `UserId = removedUserId`, `Status = Pending`. The title/body may include the group name captured at removal time; they must not include member lists, itinerary details, shared location, or a link that bypasses membership authorization. Notification persistence failure aborts removal. Later read/display failure does not undo a committed removal.
- Share the UC-21 notification API: authenticated recipient-only `GET /api/v1/notifications?unreadOnly=true&limit=20&beforeId={optional}`. Results are descending by notification ID; `unreadOnly` defaults to false, `limit` defaults to 20 and accepts 1–50, `beforeId` is exclusive, and `nextBeforeId` is null at the end. Response: `{ items: [{ id, type, title, body, relatedEntityType, relatedEntityId, createdAtUtc, isRead }], nextBeforeId }`. Never accept a caller-supplied recipient ID.
- `POST /api/v1/notifications/{notificationId}/read` marks only the authenticated recipient's `InApp` item read, is safe to retry, returns `204`, and returns `404` for absent or other-user items. Invalid pagination returns `400`; both endpoints require authentication. Related entity metadata does not grant group access. The Backend guarantees storage and retrievability, while Mobile owns display.
- Use a single shared schema/API implementation if UC-21 is implemented first or concurrently. UC-20 does not add push, email, SMS, or a delivery worker.

## Persistence and deployment

- Provide idempotent database-first SQL migration(s) for the durable removal-operation record and any `InApp` channel/constraint change not already delivered by UC-21. Apply schema changes before deploying application code that maps them. Existing groups and notifications must be preserved.
- Keep UTC timestamp mapping consistent with the existing `.AsUtcDateTime2()` convention. Store enough of the successful result to replay it exactly after a later rejoin or group state change.

## Acceptance checks

- Host removes one active non-Host member; Host stays Host; membership, end time, location-sharing flag, operation record, audit entry, and recipient notification commit together.
- Self/Host target, stale/inactive target, absent/Closed group, non-Host, unauthenticated, bad IDs/key, and inconsistent Host state follow the specified HTTP outcomes without unintended writes.
- Exact retry after removal or rejoin returns the original response and no duplicate side effects; reused key with another target is rejected. Concurrent removal, join, invitation, leave, and navigation paths preserve membership/access invariants.
- Access checks performed after commit deny group-only reads and navigation; group-dependent active sessions end; independently authorized sessions continue. Failure to revoke or persist the notification/audit/operation rolls back removal.
- Notification reads are recipient-isolated, paginated, and retry-safe when marked read. No notification content or related-entity link exposes protected group details.

## Approval boundary

The cross-service decisions, this Backend contract, and the companion implementation plan were approved before production implementation.
