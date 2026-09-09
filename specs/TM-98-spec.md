# TM-98 — Create POI (Backend Specification)

Status: **APPROVED — 2026-09-08**

Jira: [TM-98](https://tripmate-capstone.atlassian.net/browse/TM-98)

Use case: UC-52 — Create Point of Interest

Current delivery phase: Backend first; Frontend and UAT follow only after the API contract is approved and implemented.

## 1. Source of truth

The specification uses the sources below in this order:

1. Jira TM-98: an Administrator adds a new POI to the centralized catalog.
2. Report 3 SRS, section 3.9.3.1 (UC-52): detailed actor, validation, flow, and postconditions.
3. `database/tripmate_schema_v7.sql`: current database-first physical schema.
4. `Report3_Screens_All.html`: visual reference only; it does not define business rules or the final API contract.

No application behavior is inferred from the HTML mockup alone.

## 2. Confirmed scope

The backend slice will provide an authenticated Administrator operation that creates one POI.

Confirmed requirements:

- Only a signed-in, Active Administrator may create a POI.
- Mandatory business data is POI name, category, latitude, and longitude.
- Latitude and longitude must be within valid geographic ranges.
- The system checks for an existing Active POI with the same name at the same location.
- A successfully created POI starts with status `Active`.
- Opening-hours data must be validated when it is supplied.
- The creation and all confirmed child records are stored atomically.
- The creation event is written to the immutable audit log in the same transaction.
- A failed validation or failed save must not leave a POI or child records persisted.
- No EF Core migration will be generated; mappings follow SQL schema v7.

## 3. Delivery boundary

### Included in this backend phase after open decisions are approved

- POI domain model and supported child models.
- EF Core database-first mappings for the approved tables.
- CQRS command, validator, handler, result/error codes, and response DTO.
- Administrator-protected HTTP endpoint.
- Duplicate detection according to the approved definition.
- Atomic audit-log creation.
- Unit tests and the applicable API/integration tests supported by the repository.
- OpenAPI-visible request and response contract.

### Not included

- POI list/detail query (TM-58 or a prerequisite read-contract task).
- POI update (TM-99).
- POI removal/deactivation (TM-100).
- Route management (TM-101/TM-102).
- Algorithm parameter configuration (TM-103).
- Frontend implementation in `Capstone_FE`; it follows the completed backend contract.
- Database schema redesign unless an approved requirement cannot be represented by schema v7.

## 4. Current database model

SQL schema v7 currently supports:

- `catalog.POICategories`
- `catalog.POIs`
- `catalog.POIOpeningHours`
- `catalog.POIPhotos` (URL metadata only)
- `catalog.Tags`
- `catalog.POITagMap`
- `dbo.AuditLogs`

`catalog.POIs` currently contains category, name, description, coordinates, address,
indoor/outdoor type, scenic score, photo rating, average visit duration, shelter flag,
status, creator, and UTC timestamps.

The backend currently maps only `Users` and `RefreshTokens`; none of the POI, category,
tag, photo, opening-hours, or audit-log tables is mapped yet.

## 5. Draft HTTP contract

Approved route:

```http
POST /api/v1/admin/pois
Authorization: Bearer <administrator-access-token>
Content-Type: application/json
```

Proposed core request fields:

| Field | Type | Required | Draft rule |
| --- | --- | --- | --- |
| `name` | string | Yes | Trimmed; max 200 characters |
| `categoryId` | integer | Yes | Must reference an existing POI category |
| `latitude` | decimal | Yes | -90 through 90; stored at 6 decimal places |
| `longitude` | decimal | Yes | -180 through 180; stored at 6 decimal places |
| `address` | string | No | Max 400 characters |
| `description` | string | No | Max 2,000 characters |
| `indoorOutdoor` | enum | No | `Indoor`, `Outdoor`, or `Mixed`; default `Outdoor` |
| `averageVisitDurationMinutes` | integer | No | Positive; database default is 60 |
| `hasShelter` | boolean | No | Default false |
| `openingHours` | array | No | At most one row for each day 0–6 |
| `tagIds` | integer array | No | Every ID must reference an existing tag |
| `confirmDuplicate` | boolean | No | Default false; allows explicit confirmation after a duplicate warning |

`scenicScore`, `photoRating`, commercial-service data, contact information, and photos are not
accepted by the TM-98 request.

Draft success response:

- `201 Created`
- A POI DTO containing its generated ID, persisted values, `Active` status, creator ID,
  and UTC timestamps.

Draft error behavior:

| Condition | HTTP status | Error code proposal |
| --- | --- | --- |
| Missing/invalid JWT | 401 | Authentication middleware |
| Authenticated user is not an Active Administrator | 403 | `Poi.AdminAccessRequired` |
| Invalid request fields/coordinates/opening hours | 400 | `Poi.ValidationFailed` |
| Category or approved child reference does not exist | 404 | `Poi.ReferenceNotFound` |
| Duplicate requires confirmation | 409 | `Poi.PossibleDuplicate` |
| Duplicate explicitly confirmed | 201 | Create proceeds after the approved confirmation rule |
| Unexpected persistence failure | 500 | Standard ProblemDetails; no partial data persisted |

Responses use the repository's `Result<T>` and `HandleFailure` conventions; no synthetic
`{ success, data, errors }` envelope will be introduced.

## 6. Opening-hours proposal

If opening hours are included in TM-98, each item will use:

```text
dayOfWeek: integer 0..6
openTime: time or null
closeTime: time or null
isClosed: boolean
```

Day `0` is Sunday, matching .NET `DayOfWeek`. Missing days mean no hours supplied. A closed day
must have both times null. An open day requires both times and `openTime < closeTime`; overnight
ranges are rejected in this slice.

## 7. Transaction and audit behavior

The approved aggregate is created in one `SaveChangesAsync` transaction using navigation
properties for new parent/child records. The audit entry records:

- actor Administrator ID;
- action type `POI_CREATE`;
- affected entity `POI`;
- generated POI ID;
- created data excluding credentials or secrets;
- request IP address remains null because the current Application contract does not expose it.

`after_data` stores the persisted POI aggregate as JSON and excludes credentials, tokens, and
other secret values.

## 8. Test acceptance criteria

At minimum, implementation will not be accepted until tests verify:

1. A valid command creates one Active POI and returns its generated ID.
2. Name, category, latitude, and longitude are required.
3. Latitude and longitude outside their valid ranges are rejected.
4. A missing category is rejected without inserting data.
5. Duplicate detection returns the approved conflict result.
6. Duplicate confirmation follows the approved rule.
7. Invalid opening hours are rejected according to the approved rules.
8. Approved child references are validated and persisted atomically.
9. The creator is the current Administrator.
10. An audit record is created in the same successful transaction.
11. Persistence failure leaves no partial POI aggregate.
12. The endpoint returns 401/403 for unauthenticated/unauthorized requests.
13. Existing authentication tests remain green.

## 9. Dependencies and approved decisions

The developer approved the MVP decision bundle on 2026-09-08:

- TM-27 is treated as superseded for the POI-create API before implementation starts.
- TM-98 creates the core POI aggregate only: POI, opening hours, tag mappings, and audit log.
- The request includes `indoorOutdoor` and `hasShelter`; `scenicScore` and `photoRating` start
  as null and are not administrator inputs in this slice.
- Commercial provider/service creation and contact information are excluded pending resolution
  of the commercial data model.
- Binary image upload and photo rows are excluded until a storage contract is approved.
- Category and tag IDs must already exist. TM-98 does not invent or seed a category/tag catalog.
- A duplicate means a trimmed, case-insensitive name plus exact coordinates after normalization
  to the database's six decimal places.
- `confirmDuplicate: false` returns `409 Poi.PossibleDuplicate` with the existing POI ID;
  `confirmDuplicate: true` permits the Administrator to create it.
- Day `0` is Sunday, matching .NET `DayOfWeek`; missing days mean no hours supplied. A closed day
  has null times, an open day requires both times, and overnight ranges are rejected in this slice.
- Audit action type is `POI_CREATE`; `after_data` contains the persisted POI aggregate without
  credentials, tokens, or unrelated user data. IP address remains null until a shared request-
  context/auditing contract is introduced.

Approval of this bundle intentionally defines a smaller first increment than the full SRS screen.
The excluded commercial and upload capabilities must remain visible follow-up scope and TM-98 must
not be reported as full end-to-end UAT completion until the approved product scope is satisfied.

## 10. Spec gate result

Completed after approval:

1. Created `feature/datmnt-create-poi` from `develop`.
2. Verified the baseline through the .NET 10 SDK container: 19 tests passed.
3. Created `plans/TM-98-plan.md` with atomic Red → Green → Refactor steps.

The plan was explicitly approved on 2026-09-08. The backend slice proceeded under that approval;
Frontend integration and end-to-end UAT remain separate completion gates.
