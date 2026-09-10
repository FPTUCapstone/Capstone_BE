# TM-98 - Create POI Backend Specification

Status: **APPROVED - checklist-aligned revision 2026-09-09**

Jira: [TM-98](https://tripmate-capstone.atlassian.net/browse/TM-98)

Use case: UC-52 - Create Point of Interest

Current delivery phase: Backend PR review; FE integration and UAT follow after the backend contract
is merged and available on `develop`.

## 1. Source of truth and rule precedence

### 1.1 Business and API sources

The business behavior and API contract use the following sources in order:

1. Jira TM-98: an Administrator adds a new POI to the centralized catalog.
2. Report 3 SRS, section 3.9.3.1 (UC-52): actor, validation, flow, and postconditions.
3. The approved decisions recorded in this specification.
4. `database/tripmate_schema_v7.sql`: database-first physical schema.
5. `Report3_Screens_All.html`: visual reference only.

The HTML screen does not define business rules, database behavior, or the final API contract.

### 1.2 Engineering and review sources

Implementation and review must follow:

1. `TEAM_ENGINEERING_RULES.docx` for the shared team workflow and Definition of Done.
2. `Dev_and_CrossReview_Checklist.pdf` for developer self-review and peer-review gates.
3. `AGENTS.md` for repository-specific backend architecture, database-first, Result pattern,
   testing, Git, and security rules.

The shared documents define the common quality gates. `AGENTS.md` and this approved specification
define how those gates are implemented in the backend repository. If wording conflicts, apply the
team precedence rule: Security, approved architecture, API contract, requirement, then personal
implementation preference. Do not infer a new business rule to resolve a conflict.

For this backend, "Unified API Response" means one consistent HTTP contract:

- successful requests return the endpoint's typed response DTO;
- expected application failures use `Result` or `Result<T>` internally and are mapped by
  `HandleFailure` to RFC 7807 `ProblemDetails`;
- validation failures use RFC 7807 `ValidationProblemDetails`; the generated OpenAPI `400`
  response references that schema and exposes `errors` as JSON `camelCase` field paths mapped to
  message arrays, including nested paths such as `openingHours[0].dayOfWeek`;
- unexpected failures are handled centrally and return RFC 7807 `ProblemDetails`.

The synthetic `{ success, statusCode, message, data, errors }` envelope from the cross-repository
example is not used in this backend. Changing that decision requires a separately approved API
contract and coordinated FE/Mobile update.

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

### Included in this backend phase

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

## 4. Database-first model

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

TM-98 maps `POICategories`, `POIs`, `POIOpeningHours`, `Tags`, `POITagMap`, and `AuditLogs` through
EF Core configurations that match SQL schema v7. `POIPhotos` is intentionally not mapped in this
slice because the photo-storage contract is not approved.

No EF Core migration is permitted. The only SQL-script change in TM-98 is documentation that
clarifies `scenic_score` and `photo_rating` remain null when a POI is created; there is no schema
or data migration.

## 5. Approved HTTP contract

Approved route:

```http
POST /api/v1/admin/pois
Authorization: Bearer <administrator-access-token>
Content-Type: application/json
```

Request fields:

| Field | Type | Required | Rule |
| --- | --- | --- | --- |
| `name` | string | Yes | Trimmed; max 200 characters |
| `categoryId` | integer | Yes | Must reference an existing POI category |
| `latitude` | decimal | Yes | -90 through 90; stored at 6 decimal places |
| `longitude` | decimal | Yes | -180 through 180; stored at 6 decimal places |
| `address` | string | No | Trimmed; max 400 characters |
| `description` | string | No | Trimmed; max 2,000 characters |
| `indoorOutdoor` | enum | No | `Indoor`, `Outdoor`, or `Mixed`; default `Outdoor` |
| `averageVisitDurationMinutes` | integer | No | Positive; database default is 60 |
| `hasShelter` | boolean | No | Default false |
| `openingHours` | array | No | At most one row for each day 0–6 |
| `tagIds` | integer array | No | Every ID must reference an existing tag |
| `confirmDuplicate` | boolean | No | Default false; allows explicit confirmation after a duplicate warning |

`scenicScore`, `photoRating`, commercial-service data, contact information, and photos are not
accepted by the TM-98 request.

Success response:

- `201 Created`
- No `Location` header is emitted in TM-98 because no approved, addressable POI read endpoint
  exists yet. The response must not advertise the unresolved `/api/v1/admin/pois/{id}` URI.
- A POI DTO containing its generated ID, persisted values, `Active` status, creator ID,
  UTC timestamps, opening hours, and tag IDs.
- JSON field names use `camelCase`, including keys inside `ValidationProblemDetails.errors`.

When the POI detail endpoint is approved and implemented, it must receive a named route. The
create endpoint may then use `CreatedAtRoute(...)`, and an integration test must follow the
returned `Location` and retrieve the same POI. The create slice must not construct that URI by
concatenating route strings.

Error behavior:

| Condition | HTTP status | Contract |
| --- | --- | --- |
| Missing/invalid JWT | 401 | Authentication middleware |
| Authenticated user is not an Active Administrator | 403 | `Poi.AdminAccessRequired` |
| Invalid request fields/coordinates/opening hours | 400 | RFC 7807 `ValidationProblemDetails` |
| Category or approved child reference does not exist | 404 | `Poi.ReferenceNotFound` |
| Duplicate requires confirmation | 409 | `Poi.PossibleDuplicate` |
| Duplicate explicitly confirmed | 201 | Create proceeds after the approved confirmation rule |
| Unexpected persistence failure | 500 | RFC 7807 `ProblemDetails`; no partial data persisted |

For `HandleFailure` responses, `errorCode` is included as a ProblemDetails extension. Duplicate
conflicts also include `existingPoiId`. The controller must not contain business rules or access
the database directly.

Role-based authorization failures occur before the controller executes. The API therefore uses
endpoint metadata and a shared authorization result handler to return the same RFC 7807
`Poi.AdminAccessRequired` contract for an authenticated non-Administrator. Endpoints without that
metadata and unauthenticated requests retain ASP.NET Core's default behavior.

## 6. Backend processing flow

The request follows the backend's Clean Architecture and vertical-slice flow:

```text
HTTP POST /api/v1/admin/pois
    -> ASP.NET authentication and Administrator role authorization
    -> PointsOfInterestController model binding
    -> MediatR validation pipeline
    -> CreatePoiCommandHandler
       -> confirm the current user is an Active Administrator
       -> load and validate category and tag references
       -> normalize name and coordinates
       -> check the approved duplicate rule
       -> create and validate the POI aggregate in Domain
       -> execute POI, child-record, and audit writes in one transaction
    -> Result<PoiResponseDto>
    -> 201 DTO without a premature Location header, or HandleFailure ProblemDetails
```

Layer responsibilities are fixed as follows:

- API: model binding, authentication/authorization attributes, endpoint-specific forbidden
  ProblemDetails mapping, MediatR dispatch, and HTTP mapping.
- Application: orchestration, reference checks, duplicate detection, transaction boundary request,
  response mapping, and expected `Result<T>` failures.
- Domain: POI invariants, normalization, defaults, opening-hours rules, relationship behavior, and
  audit-log construction.
- Infrastructure: EF Core configurations, SQL Server execution strategy, physical transaction,
  persistence, and database-first mappings.
- Database: referential integrity, check constraints, defaults, indexes, and durable storage.

The handler depends on `IApplicationDbContext`, not the concrete Infrastructure DbContext. A generic
repository is not introduced because the backend architecture explicitly uses the application
DbContext abstraction as its persistence port.

## 7. Approved opening-hours rules

When opening hours are supplied, each item uses:

```text
dayOfWeek: integer 0..6
openTime: time or null
closeTime: time or null
isClosed: boolean
```

Day `0` is Sunday, matching .NET `DayOfWeek`. Missing days mean no hours supplied. A closed day
must have both times null. An open day requires both times and `openTime < closeTime`; overnight
ranges are rejected in this slice.

## 8. Transaction and audit behavior

The approved aggregate and audit log are committed in one physical database transaction. The
handler performs two ordered saves inside that transaction:

1. Add and save the POI aggregate so SQL Server generates the POI ID.
2. Serialize the persisted response, add the audit entry with that generated ID, and save it.
3. Commit only after both saves succeed.

New parent/child relationships use navigation properties so EF Core can propagate generated keys.
If validation, either save, or commit fails, the transaction is not committed and no partial POI
aggregate may remain. The SQL Server execution strategy and transaction receive the request
cancellation token.

Physical atomicity is verified against the canonical v7 schema in an isolated disposable SQL
Server database. The failure test allows the first POI/child save to reach SQL Server, rejects the
second audit save with a test-only database constraint, then reads through a new DbContext and
requires every transactional table count to match its pre-request baseline. It does not assume that
rolled-back SQL Server identity values are reused.

The audit entry records:

- actor Administrator ID;
- action type `POI_CREATE`;
- affected entity `POI`;
- generated POI ID;
- created data excluding credentials or secrets;
- request IP address remains null because the current Application contract does not expose it.

`after_data` stores the persisted POI aggregate as JSON and excludes credentials, tokens, and
other secret values.

## 9. Test acceptance criteria

At minimum, implementation will not be accepted until tests verify:

1. A valid command creates one Active POI, returns its generated ID, and does not publish a
   `Location` header until a real read route exists.
2. Name, category, latitude, and longitude are required.
3. Name, address, and description length limits are evaluated after trimming; whitespace-only
   optional text is stored as null.
4. Latitude and longitude outside their valid ranges are rejected.
5. A missing category is rejected without inserting data.
6. Duplicate detection returns the approved conflict result.
7. Duplicate confirmation follows the approved rule.
8. Invalid opening hours are rejected according to the approved rules.
9. Approved child references are validated and persisted atomically.
10. The creator is the current Administrator.
11. An audit record is created in the same successful transaction.
12. Persistence failure leaves no partial POI aggregate.
13. The endpoint returns 401 for unauthenticated requests and RFC 7807
    `Poi.AdminAccessRequired` with 403 for authenticated users lacking the required role or account
    status.
14. Existing authentication tests remain green.
15. The cancellation token is forwarded through the SQL Server execution strategy, transaction
    start, operation, and commit.
16. A failed transaction operation does not commit.

## 10. Dependencies and approved decisions

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

Amendment approved on 2026-09-09: the `201 Created` response omits `Location` while this
create-only slice has no approved read endpoint. Once a detail endpoint exists, its named route
becomes the single source used by `CreatedAtRoute(...)`. The integration test verifies the
intentional absence of `Location` until that read contract is implemented.

## 11. Shared checklist mapping for the backend

Every applicable item must pass before the reviewer approves the PR.

| Shared checklist area | TM-98 backend requirement |
| --- | --- |
| Spec and API contract | Route, method, request, response, status codes, field types, and errors match this approved specification. |
| Naming and schema | HTTP JSON uses `camelCase`; EF Core maps the approved SQL `snake_case` columns and schema names. |
| Clean Architecture | Controller contains no business or persistence logic; Application coordinates the use case; Domain owns invariants; Infrastructure owns EF Core and transactions. |
| Reuse and DRY | Audit identifiers, POI length limits, defaults, and the controller route have a single code source of truth. |
| Error handling | Validation, authentication, authorization, missing references, duplicates, and unexpected failures follow the contract in section 5. |
| Database and performance | No query runs inside a per-item loop; only required references are loaded; existing SQL indexes support category, coordinates, and audit lookups. |
| Build and tests | Full solution build has 0 errors and 0 warnings; all unit/API tests and the explicitly configured SQL Server integration run pass. A missing SQL test connection is reported as skipped, never as SQL verification. |
| Formatting | `dotnet format --verify-no-changes` passes for every C# file changed by the PR. |
| Security | No real secret, connection string, `.env`, private key, token, or credential is committed. Audit JSON excludes secrets. |
| Git hygiene | The feature branch contains only TM-98 changes and no `bin`, `obj`, IDE, cache, or temporary artifacts. |
| Zero regression | Existing authentication behavior and tests remain green; no existing API contract is unintentionally changed. |
| Documentation | This specification, SQL documentation, PR description, and implementation describe the same behavior. |
| Cross-review | Review findings are fixed, pushed, rechecked, and resolved before approval and merge. |

The FE/Mobile-specific Loading, Empty, Error, Toast, and Retry checks are not applicable to this
backend-only PR. They become required in the later FE integration and UAT phase.

## 12. Definition of Done

The TM-98 backend PR is ready to merge only when all items below are true:

- The approved backend scope and acceptance criteria are implemented.
- API and database mappings match this specification and SQL schema v7.
- Clean Architecture and the vertical-slice flow in section 6 are respected.
- Expected failures use `Result` or `Result<T>` and RFC 7807 HTTP mappings.
- The create response does not advertise a URI that the application cannot resolve.
- POI, opening hours, tag mappings, and audit data are atomic.
- Relevant tests are present and the full test suite passes.
- Build completes with 0 errors and 0 warnings.
- Formatter verification passes for all files changed by the PR.
- Debug code, unused experimental code, secrets, and generated artifacts are absent.
- Swagger/HTTP and real SQL Server smoke-test evidence is recorded in the PR.
- Developer self-review is complete.
- At least one cross-reviewer has rechecked the latest commit and approved it.
- No blocking comment or Request Changes review remains unresolved.
- The PR is merged into `develop` without conflict.

Passing the backend Definition of Done completes the TM-98 backend slice. It does not by itself mark
the complete UC-52 product flow as done; FE integration and end-to-end UAT remain later gates.

## 13. Delivery and verification record

- `feature/datmnt-create-poi` was created from `develop` for this task.
- The original clean baseline passed 19 tests before implementation.
- `plans/TM-98-plan.md` was approved on 2026-09-08 and executed using Red -> Green -> Refactor.
- The checklist-aligned implementation currently passes 97 tests across Application,
  Infrastructure, API contract, and SQL Server integration coverage when the test connection is
  configured; the two SQL Server tests are explicitly skipped when it is absent.
- The full solution currently builds with 0 errors and 0 warnings.
- Formatter verification passes for all C# files changed by the PR.
- Security, debug-code, generated-artifact, and Git diff checks pass.
- The branch is based on the current `origin/develop` history without a merge conflict.

Remote delivery state is tracked in PR #8 rather than frozen in this specification. Before merge,
the PR description must match the latest verification evidence and the latest commit must receive
reviewer recheck and approval.
