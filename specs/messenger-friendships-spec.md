# Messenger friendships

User-authorized scope: persistent friendships only; existing Active-user search and DirectChat stay open independently of friendship. No commit or push.

Authenticated GET `/api/v1/friendships` returns an array of `{ userId, displayName, avatarUrl, status, requestedByUserId }` for the caller's Pending/Accepted pairs with Active peers. Status is the string `Pending` or `Accepted`.

PUT `/api/v1/friendships/{otherUserId}` accepts `{ action }` with exact lowercase `request`, `accept`, `decline`, `cancel`, `remove`; success is 204. Actor and target must be Active. Invalid/self IDs or action: 400; inactive actor or unauthorized transition: 403; missing/inactive target: 404; absent relationship, wrong state, or exhausted contention: 409.

Request creates a canonical unique ordered pair; repeats (including the opposite participant) leave existing Pending/Accepted rows unchanged. Crossed requests never implicitly accept. Only the recipient can accept/decline, only requester can cancel; either participant can remove an Accepted friendship. Recipient accept is repeat safe. Decline/cancel/remove delete the row; repeats after deletion return 409. New requests after deletion are allowed.

Serializable transactions encompass active-account authorization, pair reads and writes. Deadlocks, optimistic conflicts and identified pair unique violations retry with fresh tracking, then safely return 409. SQL constraints enforce canonical uniqueness, requester membership and allowed statuses. No EF migrations or runtime schema changes.

Writes use the injected `IDateTimeProvider`. At most three attempts run, with cancellation-aware 25ms/50ms delays between retries. Migration replay checks required column types/nullability/precision and rejects an existing pair index unless it is enabled, unique, unfiltered and keyed exactly by lower/upper user IDs. GET sorting occurs before DTO construction so the query translates on SQL Server.

Verification: application state/auth tests, endpoint contract/auth tests, opt-in real SQL concurrency and migration tests, scoped DirectChat regression tests. Update every context implementation.
