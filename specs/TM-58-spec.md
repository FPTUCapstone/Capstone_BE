# TM-58 — Explore Points of Interest Backend Specification

Status: **IMPLEMENTATION COMPLETE AND VERIFIED — all 9 tasks delivered on 2026-09-13**

Jira: [TM-58](https://tripmate-capstone.atlassian.net/browse/TM-58)

Use case: UC-12 — Explore Points of Interest

Branch: `feature/datmnt-explore-pois`

Delivery order: Backend API first. Responsive Web, Mobile integration, UI review, and end-to-end
UAT remain later completion gates.

Canonical approved sources are maintained in the Docs repository:

- `Capstone_Docs/requirements/mvp/explore-points-of-interest-mvp.md`;
- `Capstone_Docs/api/contracts/poi-exploration-api.md`.

This colocated file is the backend delivery specification. If it differs from an approved Docs
contract, Docs is authoritative and this file must be reconciled before implementation.

## 1. Evidence and current state

The scope was derived from the following current sources:

1. Jira TM-58:
   - actor: Guest / Traveler;
   - scope: explore and view detailed POI information;
   - label: `scope-3-catalog`.
2. Report 3 SRS section 3.3.3:
   - browse, search, filter, and inspect Active POIs;
   - paginated list and map markers;
   - list and map screens plus POI details on responsive Web and Mobile;
   - search by name, category, distance, and opening-now state;
   - details include category, description, address, coordinates, opening hours, photos, and
     average rating.
3. Report 3 screens UC-12a and UC-12b / S050 and S051.
4. TM-98 provides the core POI aggregate, category, tags, opening hours, SQL v7 mappings, and
   Administrator create endpoint. [PR #8](https://github.com/FPTUCapstone/Capstone_BE/pull/8) was
   merged into `develop` at commit `e6d64ec`.
5. The TM-58 branch was fast-forwarded to `e6d64ec` on 2026-09-12. The reconciled baseline builds
   with zero errors and zero warnings. All 123 tests that do not require the external SQL Server
   connection pass; the three SQL Server integration tests are discovered and skip only when
   `TRIPMATE_SQLSERVER_TEST_CONNECTION` is absent.
6. TM-58 will reuse the merged TM-98 entities, mappings, error plumbing, and test infrastructure.
   It must not recreate or fork those shared types.

The available evidence identifies TM-58 as the UC-12 backend task. Duplicate-ticket verification
must be repeated in Jira before final delivery rather than frozen as a permanent claim in this
specification.

## 2. Approved source-conflict resolutions

The sources are not fully consistent. The developer approved the following D1-D6 resolutions on
2026-09-12. These decisions are part of the TM-58 contract and must remain synchronized with Docs,
BE, FE, and Mobile.

### 2.1 Actor and authorization

- Jira, the project coverage matrix, and the public landing-page handoff say Guest / Traveler.
- The detailed Report 3 UC-12 section names only Traveler.

**Approved decision (D1):** both list and detail endpoints are public and use `[AllowAnonymous]`.
Authenticated Travelers receive the same catalogue response in this slice. Traveler-only actions
such as adding a POI to an itinerary remain separately authorized by UC-11.

### 2.2 Selected area

Report 3 refers to a selected area, but SQL v7 has coordinates and address only; it has no stable
area identifier, city, district, or polygon.

**Approved decision (D2):** TM-58 does not invent an area model. A client supplies an origin coordinate
and optional maximum distance to define the explored area. Without an origin/distance filter, the
API searches all Active POIs.

### 2.3 Opening-now timezone

POIs have weekly opening hours but no timezone column.

**Approved decision (D3):** evaluate `openNow=true` in `Asia/Ho_Chi_Minh`, matching the current Central
Vietnam product scope. Day `0` is Sunday. A missing day entry or `isClosed=true` means the POI is
not open. Open intervals use `openTime <= currentTime < closeTime`. Overnight hours remain outside
the approved POI model. List and detail responses always return a non-null `isOpenNow` boolean
calculated by this rule; the `openNow=true` query parameter only filters the list to POIs whose
calculated value is true.

### 2.4 Common pagination, sorting, and calculated values

Report 3 references CR-01 but does not define concrete page limits or a default sort.

**Approved decisions (D5-D6):** `page=1`, `pageSize=20`, maximum `pageSize=100`, and deterministic default
sorting by POI name then POI ID. Supported explicit sorts are:

- `name`: name ascending, then POI ID ascending;
- `distance`: unrounded distance ascending, then name and POI ID ascending; origin is required;
- `rating`: rated POIs first, average rating descending, review count descending, then name and POI
  ID ascending.

Distance filters use the unrounded calculated value and the response rounds kilometres to two
decimal places. Average rating is rounded to one decimal place.

### 2.5 Empty results and locked message conflicts

The UC-12 text assigns MSG33 to no results and MSG34 to a missing/inactive POI. The application
message catalogue assigns those codes to unrelated itinerary outcomes.

**Approved decision:** the backend does not emit those conflicting UI messages. An empty search
returns `200 OK` with an empty page. A missing or inactive detail returns `404` with
`Poi.NotFound`. FE copy remains a later UI/content decision.

### 2.6 Shared response standard

- Backend `AGENTS.md`, the existing API pipeline, and the current TM-98 specification require typed
  success DTOs plus RFC 7807 `ProblemDetails` / `ValidationProblemDetails`.
- The shared Engineering Quality Checklist and Team Engineering Rules show the synthetic
  `{ success, statusCode, message, data, errors }` envelope.

**Approved decision (D4):** retain typed success DTOs and RFC 7807 for TM-58 so the endpoint follows
the backend architecture and does not introduce a second response shape. The canonical decision is
recorded in `Capstone_Docs/api/contracts/api-response-standard.md` and must be used consistently by
BE, FE, and Mobile.

## 3. Approved backend scope for this increment

TM-58 will provide two read-only endpoints:

1. `GET /api/v1/pois` — search, filter, sort, and paginate Active POIs.
2. `GET /api/v1/pois/{id}` — retrieve full core-catalog details for one Active POI.

The response will support both list rendering and map-marker rendering. No write, catalogue state
change, or audit entry occurs during browsing.

## 4. Explicitly excluded scope

- Responsive Next.js pages and components.
- Flutter screens and integration.
- Adding a POI to an itinerary; that command belongs to UC-11.
- Booking or viewing a commercial service; that continuation belongs to UC-30.
- Commercial provider contact data and bookable-service details. Their public contract is not
  defined by Jira TM-58 and was also excluded from the approved TM-98 core aggregate.
- Favorite/bookmark writes.
- POI create, update, or deactivate behavior.
- New area/city/district tables, spatial polygons, or an external map/geocoding API.
- EF Core migrations or SQL schema changes.

The excluded FE/Mobile and commercial-service capabilities remain visible follow-up scope. TM-58
must not be reported as full end-to-end completion after only this backend increment.

## 5. List endpoint contract

### 5.1 Request

`GET /api/v1/pois`

Authorization: anonymous or authenticated.

Query parameters:

| Parameter | Type | Default | Rule |
| --- | --- | --- | --- |
| `search` | string? | null | Trimmed; maximum 200 characters; case-insensitive contains match on POI name. |
| `categoryId` | int? | null | Must be greater than zero when supplied. An unknown ID produces an empty page. |
| `originLatitude` | decimal? | null | Must be supplied together with `originLongitude`; range `[-90, 90]`. |
| `originLongitude` | decimal? | null | Must be supplied together with `originLatitude`; range `[-180, 180]`. |
| `maxDistanceKm` | decimal? | null | Must be greater than zero and requires both origin coordinates. |
| `openNow` | bool | false | When true, applies the approved Vietnam-time opening-hours rule. |
| `sort` | string | `name` | One of `name`, `distance`, or `rating`; `distance` requires origin coordinates. |
| `page` | int | 1 | Must be greater than zero. |
| `pageSize` | int | 20 | Must be between 1 and 100. |

Coordinates used for distance calculations are normalized to six decimal places. Distance is
calculated in kilometres from the supplied origin to the POI coordinates. The implementation must
use a database-translatable query/projection and must not load the complete POI catalogue into
memory before filtering or pagination.

### 5.2 Successful response

`200 OK`

```json
{
  "page": 1,
  "pageSize": 20,
  "totalCount": 1,
  "totalPages": 1,
  "items": [
    {
      "id": 42,
      "name": "My Khe Beach",
      "categoryId": 3,
      "categoryName": "Beach",
      "latitude": 16.061000,
      "longitude": 108.246000,
      "address": "Vo Nguyen Giap, Da Nang",
      "indoorOutdoor": "Outdoor",
      "averageVisitDurationMinutes": 90,
      "hasShelter": false,
      "averageRating": 4.6,
      "reviewCount": 1284,
      "thumbnailUrl": "https://example.invalid/poi/42/cover.jpg",
      "distanceKm": 2.4,
      "isOpenNow": true
    }
  ]
}
```

Rules:

- Only `Active` POIs are returned.
- `distanceKm` is null when origin coordinates are absent.
- `isOpenNow` is always a non-null boolean calculated in `Asia/Ho_Chi_Minh`; `openNow=true` applies
  the same calculation as a list filter.
- `averageRating` is the average `social.Reviews.rating` for `target_type='POI'`; it is null when
  no review exists. `reviewCount` is zero in that case. The response rounds the average to one
  decimal place.
- `thumbnailUrl` is the first POI photo ordered by `sort_order`, then `photo_id`; it is null when
  no photo exists.
- Distance filtering and ordering use the unrounded value; `distanceKm` is rounded to two decimal
  places only in the response.
- The list is read-only and uses no EF tracking.

## 6. Detail endpoint contract

### 6.1 Request

`GET /api/v1/pois/{id}`

Authorization: anonymous or authenticated.

`id` must be a positive integer.

### 6.2 Successful response

`200 OK`

The detail DTO contains:

- POI ID, name, description, status, category ID, and category name;
- latitude, longitude, and address;
- indoor/outdoor type, shelter flag, and average visit duration;
- scenic score and photo rating stored on the POI;
- calculated average review rating and review count;
- opening hours ordered Sunday `0` through Saturday `6`;
- photos ordered by `sort_order`, then photo ID, containing URL and optional caption;
- tags ordered by name, containing tag ID and tag name;
- current `isOpenNow` state calculated using the approved Vietnam-time rule;
- created and updated UTC timestamps.

Creator identity and unrelated user/account data are not exposed on the public detail response.

### 6.3 Missing or inactive POI

`404 Not Found`, RFC 7807:

```json
{
  "status": 404,
  "title": "The requested point of interest was not found.",
  "errorCode": "Poi.NotFound"
}
```

Inactive POIs are intentionally indistinguishable from nonexistent POIs to public callers.

## 7. Validation and failure behavior

| Condition | HTTP | Error behavior |
| --- | --- | --- |
| Invalid page, page size, category ID, coordinates, distance, search length, or sort | 400 | Standard RFC 7807 `ValidationProblemDetails`. |
| Incomplete origin-coordinate pair | 400 | Field validation identifying the missing/mismatched coordinates. |
| Distance filter/sort without origin | 400 | Standard validation failure. |
| Unknown category filter | 200 | Empty paginated result. |
| No matching Active POI | 200 | Empty paginated result with `totalCount=0`. |
| Detail ID missing or Inactive | 404 | `Poi.NotFound`. |
| Unexpected database/system failure | 500 | Existing generic ProblemDetails middleware response; no internal details disclosed. |

Location permission is a client concern. If permission is denied, FE/Mobile omits the origin and
distance parameters; the backend does not receive or evaluate a device-permission state.

## 8. Persistence and mapping

No SQL schema change is approved. TM-58 reads existing SQL v7 tables:

- `catalog.POIs`;
- `catalog.POICategories`;
- `catalog.POIOpeningHours`;
- `catalog.POIPhotos`;
- `catalog.Tags` and `catalog.POITagMap`;
- `social.Reviews` for average rating and review count.

Merged TM-98 provides the POI, category, opening-hours, and tag aggregate mappings. TM-58 may add
the minimum read mappings/entities required for `POIPhotos` and the relevant `Reviews` projection.
It must not introduce a generic repository or map unrelated booking/review behavior.

TM-58 must consume the reconciled TM-98 types from `develop`. It must not re-create POI entities,
DbSets, audit types, converters, error plumbing, or test infrastructure on this branch. If the
final TM-98 contract changes any field used below, this specification and the Docs API contract
must be updated before TM-58 tests are written.

All list/detail queries use `AsNoTracking`, select only response fields, pass the cancellation
token, and apply filtering and pagination in SQL.

## 9. Acceptance criteria

1. Guest callers can list and view Active POIs without a JWT.
2. Authenticated Travelers can use the same read endpoints.
3. Inactive POIs never appear in list results and return the same 404 as missing POIs in detail.
4. Name, category, distance, and opening-now filters follow the approved rules.
5. Invalid query combinations return RFC 7807 validation errors.
6. Pagination returns stable ordering, total count, page metadata, and at most the requested page
   size.
7. List items include the data needed for list cards and map markers.
8. Detail includes the core POI data, photos, opening hours, tags, and rating summary.
9. Browsing creates no audit row and changes no data.
10. No schema or EF migration is added.

## 10. Required verification

Application unit tests must cover:

- list-query validation;
- detail-query validation;
- Active-only filtering;
- name/category filters;
- distance calculation, filter, and ordering boundary cases;
- opening-now evaluation, including Sunday, closed day, missing day, and exact open/close bounds;
- pagination and deterministic ordering;
- rating, photo, and tag projections;
- missing/inactive detail behavior.

API integration tests must prove:

- anonymous list and detail access;
- authenticated Traveler access;
- invalid filters return 400 ProblemDetails;
- empty list returns 200 with page metadata;
- inactive/missing detail returns 404 `Poi.NotFound`;
- response JSON and pagination fields match the approved contract.

Infrastructure model tests must verify any new SQL v7 mappings. The full solution build and test
suite must remain green.

## 11. Checklist compliance gate

| Checklist area | Current TM-58 state | Required before implementation/merge |
| --- | --- | --- |
| Spec and API contract | D1-D6 are approved in BE and Docs. | Keep implementation and OpenAPI synchronized with the approved contract. |
| Naming and schema | Approved JSON is camelCase and SQL mapping remains snake_case. | Add OpenAPI contract tests against the final DTOs. |
| Architecture | Approved Controller -> MediatR/Application -> DbContext abstraction flow. | Reuse merged TM-98 entities/mappings; no controller query logic or generic repository. |
| Error handling | Typed DTO plus RFC 7807 is recorded as the canonical response standard. | Test validation, empty, not-found, and generic 500 runtime bodies. |
| Database performance | SQL-side projection, filtering, aggregation, ordering, and paging are required. | Prove SQL Server translation and no unbounded/N+1 loading. |
| Zero regression | Reconciled baseline: build 0 warnings/errors; 123 non-SQL tests pass and 3 SQL tests require the configured connection. | Run all baseline and new tests, including SQL Server, before each implementation commit. |
| Security and Git | Public output excludes creator/account data; branch has no TM-58 implementation. | Check diff/status, packages, generated files, and secrets before every commit. |
| Dependency | TM-98 is merged at `e6d64ec` and the TM-58 branch includes it. | Reuse the merged aggregate and keep the branch synchronized with `develop`. |

## 12. Approval gate

D1-D6 were explicitly approved on 2026-09-12. The Docs requirement, API contract, and shared
response standard record the same decisions. TM-98 is merged, this branch contains merge commit
`e6d64ec`, and the non-SQL baseline is green. This approval authorizes preparation of the atomic
implementation plan but not production-code execution.

Writing failing implementation tests and production code remains gated on:

1. review and explicit approval of `plans/TM-58-plan.md`;
2. configuration and execution of the SQL Server baseline so no inherited SQL test is skipped;
3. continued agreement between this specification and the canonical Docs contracts.

## 13. Delivery and verification evidence

Implementation and full verification completed on 2026-09-13 across all 9 planned tasks:

1. **Test suite results:** 230 tests total across the solution, **230 passed, 0 failed, 0 skipped** (with `TRIPMATE_SQLSERVER_TEST_CONNECTION` configured against `tripmate-uc52-e2e`).
   - `TripMate.Infrastructure.UnitTests`: 4 passed, 0 failed, 0 skipped.
   - `TripMate.Application.UnitTests`: 170 passed, 0 failed, 0 skipped.
   - `TripMate.Api.IntegrationTests`: 56 passed, 0 failed, 0 skipped (including 9 SQL Server integration tests).
2. **Package vulnerability check:** `dotnet list package --vulnerable --include-transitive` reported 0 vulnerabilities across all 7 projects.
3. **Database query bounds on SQL Server:**
   - List query: exactly 2 queries (1 `COUNT(*)` + 1 bounded `SELECT` items), 0 N+1 queries.
   - Detail query: fixed 3 queries (main POI + split queries for Photos and Tags), 0 N+1 queries.
   - Vietnamese collation: verified case-insensitive search under `Vietnamese_100_CI_AS`.
   - Read-only integrity: POIs, tags, photos, reviews, and audit rows remained unchanged before and after query execution.
   - Cancellation token: async cancellation observed and propagated.
4. **Code style & hygiene:** `dotnet format --verify-no-changes` passed with zero errors; no secrets, migrations, or temporary files staged.
