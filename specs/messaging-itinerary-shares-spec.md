# Readonly itinerary snapshot shares

User-authorized scope (2026-11-28): implement the backend in the current dirty messenger-ux-be worktree, including domain, application, API, SQL migration, canonical schema, contract artifact and source review. No parent backend, FE or Mobile changes. No test, build or analyzer execution. This authorization supersedes the repository's approval/baseline/TDD execution gates for this task.

The authoritative machine-readable consumer contract is [messaging-itinerary-shares.openapi.json](messaging-itinerary-shares.openapi.json). HTTP returns bare JSON, not an envelope.

POST `/api/v1/messaging/itinerary-shares` accepts exactly these meaningful fields: `{itineraryId: positive int64, conversationKind: "travel_group" | "chat_group" | "direct", conversationId: positive int64, clientRequestId: nonempty UUID}`. Success is HTTP 200 `{shareId: UUID, messageId: int64}` for both creation and replay. The actor comes exclusively from authentication. No client snapshot is consumed. A new share sends one existing TEXT message with exact original content `tripmate:itinerary-share:{shareId}` (canonical lowercase UUID). No message-type/schema expansion and no external message integration.

GET `/api/v1/messaging/itinerary-shares/{shareId}` returns HTTP 200 `{shareId,title,sharedAtUtc,itineraryVersion,items:[{sequenceNo,name,arrivalUtc,departureUtc,stayDurationMinutes,latitude,longitude,address}]}`. Times are UTC RFC3339; arrival/departure may be null because the source schema permits it. Coordinates are JSON numbers or null; address is string or null. Items follow source sequence then source item ID. Missing POIs use `Rest stop` with null location; empty/whitespace title uses `Shared itinerary`.

Creation requires an Active actor who owns the exact itinerary row (`TravelerUserId`) and can currently send in the real selected conversation kind. Travel groups require Active membership, Traveler/TourOperator role and Active group status. Independent groups require actual `ChatGroupMembers` membership; they never consult travel group membership. Direct conversations require an actual participant and both accounts Active. Numeric IDs from different kinds are never interchangeable. All source/header/POI/item and authorization reads are held within a serializable transaction through both saves, preventing mixed itinerary versions or revocation races.

The persisted snapshot copies only the itinerary title/version and explicitly projected POI names, coordinates, address and planned timeline. It includes no booking/payment/customer data, costs, recommendation reasons, friendly explanations or private notes. There is no dependency on live itinerary/POI data during GET. No cascade from source itinerary or conversation; no update API or entity mutators. EF disallows post-save property modifications and SQL rejects UPDATE through an immutable trigger.

Reads require an authenticated Active current participant using existing history rules: Active travel membership (closed groups remain readable); current independent group membership; both direct participants Active. The original message must still exist in the correct message table/conversation. Outsiders, removed members, missing sources/shares and inaccessible conversation targets receive 404 without snapshot data. Ownership alone does not grant read access after leaving. New current participants can see older shares because existing chat history has no join-time cutoff.

Actor/clientRequestId is globally unique within this boundary. Identical itinerary/kind/conversation payload replays the stored receipt, without a new message/snapshot/event, while current send access is still required. Changed payload returns 409, even when itinerary content has changed since the original send. A replay does not reload a deleted/changed original itinerary. The share UUID is also the nested send handler's clientMessageId; it is separate from the caller's share retry key. SQL unique-index conflicts and deadlocks roll back and retry from durable state; bounded exhaustion returns 409 with same-key retry guidance. Active-account POST denial is 403; malformed input is 400; unauthenticated access is 401. Closed/inaccessible targets return 404. Failed nested sends return 409 and roll back.

The new result-aware serializable transaction commits only successful Results and clears tracking between execution-strategy attempts. It is implemented by the production SQL DbContext. Other providers must explicitly implement this transaction capability; the default fails closed rather than pretending rollback support. Existing general transaction methods retain their behavior.

Notifications happen after commit via the existing travel `MessageReceived` payload/user fanout or independent/direct notifier channels. Notification failure does not undo persistence. Delivery is best effort, with HTTP history as fallback; there is no outbox or exactly-once transport guarantee. An uncertain commit may be recovered as a replay and omit a notification.

## QA cases (not executed)

1. For each kind, owner/authorized Active sender shares successfully; assert exact receipt shape and exact TEXT marker in the existing message table, exactly one share and one message.
2. GET as another current participant; assert the exact whitelist, numeric/null locations and UTC times. Empty title and POI-less rest stops use safe fallbacks. Null source times remain null.
3. Reject nonowner, inactive sender, removed member, closed travel group, outsider, wrong kind with colliding numeric ID, and direct conversation with inactive peer; assert no rows were saved.
4. Unauthenticated requests are 401. Outsider/removed member/inactive reader/unknown UUID GET requests return no data and 404. A former owner who left the conversation has no special bypass. Closed travel history remains readable.
5. Change itinerary title/items/version and POI name/address after send; old GET remains identical. Delete source itinerary; snapshot remains readable while original chat history is visible.
6. Replay identical key before and after itinerary mutations; assert same receipt and one message. Alter each request payload field with the same actor/key; expect 409. Use the same key under another actor; keys remain actor-scoped.
7. Race simultaneous identical and changed-payload requests across SQL connections/processes; assert one durable winner, replay/409, no duplicate messages and no HTTP500 for unique/deadlock races. Exhausted transient contention returns 409.
8. Inject failure between message save and snapshot save and inject a failed Result after nested persistence; neither row may commit. Race source item/version updates, group closure, membership removal and account deactivation with send; verify serializable ordering.
9. Disconnect/cancel SignalR after commit; receipt/history remain durable. Replays do not create a second notification. Verify each existing conversation channel receives its expected event shape only after commit.
10. Apply migration twice on SQL Server; inspect unique actor/key index and UTC datetime2(7). Try UPDATE against every snapshot column; SQL trigger rejects it. Exercise EF post-save modifications; immutable mapping rejects them. Check canonical schema creation matches migration.
11. Seed booking references, costs and unique sensitive note/explanation markers in the source; neither persisted items JSON, HTTP responses nor notification payloads contain them. Fake marker text in an unrelated conversation must not grant GET access.

## Source review and remaining risks

Source review covers whitelist projection, actor ownership, conversation-specific membership, role boundary, live history access, serializable save scope, Result rollback, index race recovery, post-commit notifier placement, immutable mapping and idempotent SQL/canonical parity. No runtime correctness, compilation, migration application, execution plan or test success is claimed. Serializability can increase locking/deadlock contention for large itineraries. SQL Server transaction/concurrency/migration QA remains required. No item-count limit is introduced, matching the supplied contract. Snapshot retention after source deletion requires ordinary database retention administration; there is no new deletion policy/API.

## Changed file inventory for this scope

- `src/TripMate.Api/Controllers/V1/ItinerarySharesController.cs`: exact HTTP boundary and existing post-commit notification channels.
- `src/TripMate.Application/Features/ItineraryShares/ItineraryShareContracts.cs` and `ItineraryShareHandlers.cs`: DTOs, ownership/privacy projection, access checks, nested sends, snapshot reads and replay/concurrency handling.
- `src/TripMate.Domain/Entities/ItineraryShare.cs`: immutable snapshot/receipt entity and conversation constants.
- `src/TripMate.Domain/Entities/GroupMessage.cs`: existing text discriminator constant only; no new message type.
- `src/TripMate.Infrastructure/Persistence/Configurations/ItineraryShareConfiguration.cs`: schema, UTC converter, unique retry key and immutable EF behavior.
- `src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs` and `src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs`: snapshot DbSet and serializable commit-on-success transaction capability; existing general transaction APIs unchanged.
- `src/TripMate.Application/Features/ChatGroups/ChatGroupHandlers.cs` and `src/TripMate.Application/Features/DirectChat/DirectChatHandlers.cs`: let deadlocks escape nested send catch blocks so the outer share transaction can roll back/retry.
- `database/migrations/20261128_add_itinerary_shares.sql` and appended matching block in `database/tripmate_schema_v7.sql`: idempotent SQL, unique index, column compatibility checks and immutable UPDATE trigger.
- `tests/TripMate.Application.UnitTests/Domain/ItineraryShareTests.cs`: unexecuted fallback-title/UTC and invalid-kind test source.
- `tests/TripMate.Application.UnitTests/TestUtilities/TestDbContext.cs`, `tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs`, and `tests/TripMate.Infrastructure.UnitTests/Features/PasswordReset/ConfirmPasswordResetFlowTests.cs`: snapshot DbSet/interface compatibility; manual model key where needed. InMemory doubles intentionally do not claim rollback support for the new transaction capability.
- This spec, `specs/messaging-itinerary-shares.openapi.json`, `plans/messaging-itinerary-shares-plan.md`, and follow-up scope notes in the existing messenger group spec/plan.
