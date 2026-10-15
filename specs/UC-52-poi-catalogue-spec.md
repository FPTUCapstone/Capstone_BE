# UC-52 POI catalogue (reference data) - Spec

## 1. Context

The Create POI screen (Capstone_FE, UC-52) must offer the Staff user a list of categories and tags to
pick from. TM-98 (approved spec, section 10) requires category and tag IDs to exist already and
explicitly does not include any read endpoint for them, so the screen calls
`GET /api/v1/admin/pois/catalogue`, which the Backend never provided (404 in production). This task
adds that read-only reference-data endpoint. It follows the Staff-only decision of Capstone_BE PR #81.

Developer approval: given in chat on 2026-10-15 ("tôi approve cho bạn làm") for the contract below.

## 2. Scope

Included:
- `GET /api/v1/admin/pois/catalogue`, authorized for Staff only.
- Returns every POI category and every tag (id and name), sorted by name.
- Unit tests, API integration tests, OpenAPI-visible response contract.

Not included:
- Creating, editing, or deleting categories or tags.
- Seeding category or tag data (existing database seeds already provide it).
- Paging, filtering, or caching (the lists are small reference data; YAGNI).
- Any change to `POST /api/v1/admin/pois`.
- Frontend changes (the FE contract already matches this response).

## 3. Contract

Request: `GET /api/v1/admin/pois/catalogue`, no body, no query parameters.

Response `200`:

```json
{
  "categories": [{ "id": 1, "name": "Attraction" }],
  "tags": [{ "id": 4, "name": "Family" }]
}
```

- `categories` and `tags` are always present; they are empty arrays when there is no data.
- Each list is ordered by `name` (database collation, then `id` as tie-breaker) so the order is stable.
- Only `id` and `name` are returned (no category description).

Errors (same behaviour as the other `/api/v1/admin/pois` routes):

| Condition | Status | Body |
|---|---|---|
| No or invalid token | 401 | none |
| Authenticated but not Staff (Administrator, Traveler, TourOperator) | 403 | ProblemDetails with `errorCode` `Poi.AdminAccessRequired` (code kept for contract stability, see PR #81) |

## 4. Rules

- Read-only: no writes, no audit row, no transaction.
- Controller keeps authorization only; the query handler reads through `IApplicationDbContext` with
  `AsNoTracking()` and projects to DTOs (two queries, not one per item).
- No inactive filtering: `POICategories` and `Tags` have no status column.

## 5. Acceptance criteria

1. Staff receives 200 with both lists sorted by name.
2. With no categories or tags, both lists are empty arrays.
3. Administrator, Traveler and TourOperator receive 403 with the existing ProblemDetails.
4. An unauthenticated request receives 401.
5. The OpenAPI document describes the route and the response schema.
6. The existing Create POI behaviour and tests are unchanged.
