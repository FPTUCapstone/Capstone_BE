# TM-101 UC-54 Create Route API

This implementation follows the current official Report 3 §3.9.4.1. The signed-in account must currently be Active and Staff in SQL; the JWT role alone does not grant access. Administrator, Traveler and Tour Operator cannot use these endpoints.

## Contracts

- `GET /api/v1/admin/routes?page=1&pageSize=20`: `items`, `page`, `pageSize`, `totalCount`; stable descending creation time and identifier order. Maximum page size 100 is a transport bound.
- `GET /api/v1/admin/routes/options`: Active POIs (`id`, `name`, `latitude`, `longitude`) and canonical modes `Walking`, `Motorbike`, `Car`, `PublicTransit`.
- `POST /api/v1/admin/routes`: requires an `Idempotency-Key` UUID header and JSON fields `fromPoiId`, `toPoiId`, `distanceKm`, `estimatedDurationMinutes`, `transportMode`, optional `routeNote`.

The success response is a direct `RouteDto`, HTTP 201, including persisted identifier, endpoint names, metrics, Active status, creation time, note and `messageCode: MSG139`. No synthetic response envelope is added. Distance must fit existing SQL `decimal(6,2)` without rounding; duration must be a positive SQL integer. Notes are trimmed, blank becomes null and the maximum stored length is 2,000 characters.

Repeating the same actor and operation UUID is rejected with HTTP 409 and internal error code `Routes.DuplicateSubmission`; no second route or audit is created. Generate one UUID per intended operation and keep it during retries after uncertain network outcomes. A new UUID does not bypass the preserved existing directed Origin + Destination + Transport Mode uniqueness constraint: that conflict is HTTP 409 `Routes.DuplicateRoute`. Neither internal code introduces a new MSG identifier.

Failures use ProblemDetails/ValidationProblemDetails: missing required fields MSG01; unauthorized MSG126 (403); nonexistent, inactive or identical endpoints MSG141; nonpositive metrics MSG143; persistence or audit failure MSG127 (503). Invalid canonical mode, note storage limit and unrepresentable distance use `Routes.InvalidInput` with field details. Route and immutable audit commit in the same serializable transaction. Account and endpoint reads hold locks through commit; bounded deadlock retry revalidates all state and operation identity.

## Owner approvals

On 2026-11-13 the Owner approved an optional Route Note with blank stored as null and a 2,000-character maximum. The current Drive catalog contains MSG139, MSG141 and MSG143. The Owner approved implementation wording MSG139 “Route created successfully.”, MSG141 “Select two different Active points of interest.” and MSG143 “Distance and estimated travel time must be positive values.”

## Database change

`database/migrations/20261113_add_route_creation.sql` is applied automatically by the existing `database/apply-schema.sh` migration glob. It adds lifecycle status, note, creator and operation UUID, preserving legacy fields and rows. Legacy rows backfill Active only when both endpoints are Active. An unsafe legacy endpoint stops the entire migration atomically for Owner review. It does not silently deactivate, delete or repair rows. Existing schema/constraints remain authoritative for supported modes and numeric storage.

The Active endpoint trigger validates direct RouteSegments insert/update writes. EF disables direct SQL OUTPUT for this trigger-bearing table. The existing `AuditLog` private state and absence of update/delete application operations provide the SRS normal-application immutability guarantee; database administrator access is not an application feature.

Rollback lives under `database/migrations/rollback/20261113_add_route_creation.sql`, outside the automatic migration glob. It refuses to remove columns if any new note, actor, operation UUID or non-Active lifecycle data exists. Preserve a backup and use roll-forward recovery for databases with UC-54-created rows.

POI deactivation lifecycle handling belongs to its separately approved use case. Planning and navigation must filter both Active route status and current Active endpoint status; this task does not add a lifecycle management screen or update historical itineraries.
