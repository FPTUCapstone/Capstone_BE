# Travel group host management contract

Approved by the current request. Scope: existing TravelGroups only; preserve the dirty worktree.

- `PUT /api/v1/travel-groups/{groupId}/name`, Traveler role, body `{ "name": "..." }`. Require a positive ID, nonblank name whose trimmed length is at most `TravelGroupConstants.MaxGroupNameLength` (150), an Active group and its Active host membership. Return `200 { groupId, name }` with the trimmed name. Repeating the current name succeeds without another audit event. Persist a changed name and its audit outcome atomically.
- `POST /api/v1/travel-groups/{groupId}/dissolve`, Traveler role, no body or idempotency header. Check the same host authorization and lifecycle, then return `409` with `errorCode: travel_group.dissolution_restricted`. BookedTour explains that history-preserving dissolution without changing booking/itinerary access is unsupported. Other sources explain that closure requires ending memberships and would remove history/shared-snapshot access. No success response or mutation is implemented.
- Both operations acquire `IGroupJoinLock.AcquireGroupLockAsync` inside a serializable transaction, sharing UC21/join/invitation serialization. Retry concurrency/deadlock conflicts up to three attempts; exhausted conflicts use the existing `503` code.
- Before reading any group, both handlers query `Users` for an existing actor with `Status == Active` and `Role == Traveler` inside that transaction. Absent/inactive/locked or wrong-role actors receive `403` using `travel_group.host_permission_required`, regardless of stale JWT claims. MediatR registers assembly-scanned FluentValidation validators via `ValidationBehaviour<,>`, which validates IDs/name before invoking these handlers; the domain rename also guards blank/oversize names.
- After rename commits, send `ConversationChanged { groupId }` through the existing travel-chat recipient query and SignalR hub. No `IGroupChatNotifier` abstraction exists in this checkout. Notification failure is logged without reversing the committed response; reads provide recovery. Rename does not touch the immutable creation receipt's `GroupName`. Existing create replay payload matching remains valid; immutable replay response naming is handled by the independent agent.
- Standard failures: invalid IDs/name `400`, unauthenticated `401`, wrong role/nonhost `403`, absent group `404`, Closed group `409`; inconsistent Active-host lifecycle uses the existing `500` error convention.

## Why dissolution is restricted

`TravelGroup.Close` requires zero Active members. SQL `CK_TravelGroups_Lifecycle` requires Active/null closed_at or Closed/non-null closed_at. Conversation listing, message history and itinerary-share snapshots require Active membership. Marking everybody Left would therefore violate the approved history-retention requirement. Keeping everybody Active would violate `Close`; bypassing it would invent a new lifecycle. UC21 implements individual leave, successor transfer, invitation expiration and access-aware navigation revocation, not a history-preserving bulk dissolve. The current itinerary access resolver also treats scheduling-linked group access separately from manual owner access.

No booking, itinerary, shares, membership, invitations, GPS, chat history or independent ChatGroups are changed by the dissolve endpoint. A future successful dissolve requires an explicit archived-history/access lifecycle; this change does not redesign those contracts.

The FE must display the `409` explanation and keep the group active, rather than locally marking it Closed/read-only. Because dissolution is rejected without mutation, there is no closing GPS version or lifecycle notification. Any future successful close must invalidate GPS sessions under the same lock before commit to reject in-flight publications.

Validation is source inspection and two-pass self-review only. User explicitly prohibited tests, builds, formatting, lint and push.

## Self-review

- Contract pass: checked routes/body/response, trimmed bounds, Traveler authorization, current host membership, shared lock and explicit restricted-dissolve error mapping. `Close` and membership transitions are unchanged; restricted dissolve has no writes, GPS changes or notifications.
- Integration pass: checked assembly-scanned handlers/validators, transaction commit before rename invalidation, recipient filtering, atomic audit persistence, no-op rename retries and immutable create-receipt comparison. Existing dirty error mappings are preserved. Runtime/SQL concurrency behavior remains unverified because executable checks were prohibited. The create-replay response snapshot change remains with the independent agent.
- Follow-up authorization review: added authoritative Active/Traveler database checks to both handlers before group reads. Validation registration and execution order were inspected in `DependencyInjection` and `ValidationBehaviour`; no duplicate handler name validation is required for the controller's `Sender.Send` path.
