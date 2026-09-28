# Specification: TM-57 [UC-11] View Suggested Itinerary

**Feature**: View, accept, regenerate, and adjust a generated itinerary
**Jira Ticket**: TM-57
**Use Case**: UC-11
**Branch**: `feature/khanhpq-view-suggested-itinerary`
**Repository**: `Capstone_BE`
**Status**: Proposed — awaiting developer approval

## Objective

Allow a Traveler to open the current stored version of an itinerary created by
UC-10, inspect its ordered schedule, and—when that Traveler owns the
itinerary—accept it, regenerate it, or create a manually adjusted version.
Viewing is read-only. A retry of a mutating operation must not create a second
version.

## Scope

- Read the latest version associated with a requested itinerary ID.
- Allow the owner to accept a draft, regenerate from the original scheduling
  request, or reorder/remove visit items and create a new draft version.
- Allow an active member of a Travel Group linked to that itinerary series to
  view the current version, but never mutate it.
- Preserve every historical version as an immutable row; the newest
  `version` for a scheduling request is the current version.
- Return POI/category details, derived travel minutes, estimated visit cost,
  and an unavailable marker when a referenced POI is no longer Active.
- Add idempotent operations for regeneration and manual adjustment.

## Explicitly Out of Scope

- Interactive map rendering, route polylines, and turn-by-turn navigation
  (UC-13).
- Offline download (UC-16), POI browsing/addition (UC-12), group management,
  and a Next.js web UI.
- Changing the UC-10 creation contract or its existing generation behavior.
- Editing POI fields, adding new POIs to an itinerary, multi-day planning,
  or deleting historical itinerary versions.

## Access Rules

- The itinerary owner may read and manage its current version.
- A Traveler with `GroupMemberStatus.Active` in a `TravelGroup` linked to any
  version of the same `SchedulingRequest` may read the current version.
- A group member is read-only, even when the Group Host is a different user.
- A Traveler with no qualifying ownership or active membership receives 403.
- A missing itinerary receives 404. The API must not reveal whether an
  inaccessible itinerary exists.

## Version and State Rules

- UC-10 creates version `1` with status `Draft`.
- `POST .../accept` transitions the current owner-owned `Draft` to `Active`.
  Repeating accept on the already current `Active` version succeeds and
  returns the same detail; no additional version is created.
- Regenerate and manual adjustment each create exactly one successor with
  `version = current.version + 1` and status `Draft`. The prior version is
  never changed.
- Regeneration uses the source `SchedulingRequest` criteria and the existing
  constraint-aware generation service.
- Manual adjustment accepts an ordered, distinct non-empty list of visit POI
  IDs drawn from the current version. The supplied order is preserved; removed
  visits are absent from the successor. Rest items are recalculated by the
  backend and are not directly editable.
- Before persisting a successor, the backend validates POI availability,
  opening hours, route duration, budget, trip duration, end point, and rest
  policy. An infeasible adjustment returns 422 and retains the current version.

## Database-First Design

`planning.Itineraries.version` already exists in the canonical schema and will
be mapped to the domain entity. No EF migration is permitted.

Add an idempotency table through an idempotent SQL script and the canonical
schema snapshot:

`planning.ItineraryVersionOperations`

| Column | Purpose |
| --- | --- |
| `operation_id` | Identity primary key |
| `traveler_user_id` | Owner performing the operation |
| `source_itinerary_id` | Version from which the operation began |
| `operation_type` | `Regenerate` or `Adjust` |
| `idempotency_key` | Client operation key |
| `request_hash` | Normalized payload fingerprint |
| `result_itinerary_id` | The successor version after success |
| `created_at` | UTC operation timestamp |

Enforce a unique key on `(traveler_user_id, idempotency_key)`. Reuse with the
same normalized request replays the stored successor; reuse with a different
operation, source version, or adjustment payload returns 409.

## HTTP Contract

All endpoints require an authenticated Traveler JWT.

| Method and route | Owner | Active group member | Result |
| --- | --- | --- | --- |
| `GET /api/v1/itineraries/{itineraryId}` | Read | Read | Current version detail |
| `POST /api/v1/itineraries/{itineraryId}/accept` | Allowed | 403 | Current version becomes Active |
| `POST /api/v1/itineraries/{itineraryId}/regenerate` | Allowed + `Idempotency-Key` | 403 | New draft version |
| `PUT /api/v1/itineraries/{itineraryId}/items` | Allowed + `Idempotency-Key` | 403 | New draft version |

`PUT .../items` body:

```json
{
  "orderedVisitPoiIds": [101, 205, 307]
}
```

The detail response contains at minimum: `itineraryId`,
`schedulingRequestId`, `title`, `version`, `status`, `validFrom`, `validTo`,
`canManage`, `totalEstimatedCost`, `totalDurationMinutes`, and chronological
items. Every item includes its kind, POI ID/name/category when available,
planned arrival/departure, derived travel duration from the previous item,
estimated cost, mandatory marker, recommendation reason, and `isUnavailable`.

Expected failures use the project `Result`/RFC-7807 contract:

- 400: malformed route ID, missing/malformed idempotency header, or invalid
  adjustment body.
- 403: no ownership or active group membership; group member attempting an
  owner action.
- 404: itinerary does not exist.
- 409: idempotency key reused with a different operation payload.
- 422: current version is not valid for the requested action, submitted POIs
  are not exactly a valid subset of the current visits, or revalidation is
  infeasible.
- 500: unexpected persistence/provider failure, emitted as a sanitized
  problem response and with no partial successor version.

## Architecture

- Add vertical slices under `Features/Itineraries`: `GetDetail`, `Accept`,
  `Regenerate`, and `AdjustItems`, each with a command/query, validator where
  input exists, handler, response DTO, and focused error codes.
- Keep controllers limited to binding, current-user lookup, MediatR dispatch,
  and standard `HandleFailure` mapping.
- Factor access resolution and current-version resolution into focused
  Application helpers used by all four slices; do not add a generic repository.
- Reuse UC-10's `ItineraryGenerationService` for regeneration. Add a focused
  fixed-order generation path for adjustment so requested visit order is never
  silently permuted or expanded with optional visits.
- Persist the predecessor/version operation/new itinerary/items in one
  transaction. UTC timestamps remain mapped with `AsUtcDateTime2()`.

## Acceptance Criteria

1. The owner can fetch the latest version using any itinerary ID in its series.
2. An active group member can fetch that same latest version but cannot accept,
   regenerate, or adjust it.
3. Unknown IDs return 404; inaccessible IDs return 403 with no detail payload.
4. The response marks a referenced inactive POI unavailable without mutating
   stored data and offers enough metadata for Mobile to offer regeneration.
5. Accepting a draft makes only the current version Active; repeated accept is
   safe and creates no version.
6. Regeneration and successful adjustment append one draft version and retain
   the predecessor and its ordered items unchanged.
7. Retry of the same regeneration/adjustment key returns the same successor;
   a changed payload with that key returns 409.
8. An infeasible adjustment or persistence failure leaves no partial version,
   items, or idempotency operation pointing to a nonexistent result.
9. The response derives per-leg travel minutes from scheduled timestamps and
   does not call the route provider during a read.

## Verification

- Domain/unit tests: version transition, fixed-order preservation, read access,
  inactive POI marking, adjustment validation, and idempotency replay/mismatch.
- API endpoint/OpenAPI tests: authentication, authorization, 404/403/409/422,
  required headers, and response contract.
- SQL Server integration tests: transaction rollback, version persistence,
  same-key concurrency, and operation-table constraints.
- Final quality gates: `dotnet format TripMate.slnx --verify-no-changes`,
  `dotnet build TripMate.slnx`, and `dotnet test TripMate.slnx`.
