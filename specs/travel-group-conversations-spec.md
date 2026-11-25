# Travel-group conversation list

Approved scope: the user's explicit implementation contract, on the current feature branch.

- Add GET `/api/v1/travel-groups/conversations` in a separate controller, authorized for Traveler or TourOperator. Caller ID comes exclusively from authentication.
- Return `{ items: [{ groupId, groupName, itineraryId, itineraryTitle, itinerarySourceType, hostUserId, memberCount, status, lastMessage, lastMessageAtUtc, unreadCount }], nextBeforeGroupId }` without an envelope. Project itinerarySourceType directly from the existing itinerary SourceType (user's follow-up contract).
- Query existing SQL-backed travel groups, itineraries, memberships and group messages only. Require the caller's active membership; include Active and Closed groups. Count only active memberships. Preserve nullable itinerary titles and nullable last-message fields. Status is the canonical enum name.
- Use descending group IDs and an exclusive positive `beforeGroupId`. Default limit 50, maximum 100; reject limits outside 1–100 and nonpositive supplied cursors with HTTP 400. Fetch one extra row to determine a next cursor; return null when exhausted.
- Last message uses greatest message ID, consistent with existing chat message ordering. Groups without messages have both preview fields null.
- No writes, runtime fixtures, schema/entity changes, or changes to other chat endpoints. Preserve the unrelated untracked coupon test. No Git history or remote operations.

Accepted test seams: MediatR handler for membership isolation, projection and paging; HTTP endpoint for roles, trusted caller identity, binding and response shape; isolated SQL Server handler test for provider translation and UTC mapping when available.

Approved unread follow-up: add `unreadCount: int` to each conversation. Count existing group messages in SQL where message ID exceeds the current active member's `LastReadChatMessageId ?? 0` and sender differs from the current user. Keep existing group cursor behavior. The third worker owns the member property/mapping and read endpoint; do not edit those files.
