# TM-79 Backend smoke test

## Backend remediation closure (2026-10-09)

This procedure now covers the implemented Backend R1–R7 contract: typed
commerce/service identity, ServiceBooking-to-POI routes, local content-policy
outcomes, Option A schema and canonical Tour/POI aggregate behavior. It does
not claim Mobile/Web or full TM-78-owned UC-32 completion.

Last verified: 2026-10-09

This handoff covers the current Backend runtime. Mobile/Web remain frozen;
standalone itinerary-only review and `poiRatings[]` as a canonical POI review
remain deferred. Deployed legacy-data rollout readiness still requires its
separate authorized operational sample.

## 1. Prerequisites and safety boundary

- Checkout the TM-79 branch and restore/build the .NET 10 solution.
- Use an isolated SQL Server whose login may create and drop disposable
  `TripMate_Test_*` databases. Never point the test variable at production,
  shared Azure SQL, or a database containing team business data.
- The verified local target is container `tripmate-tm70-sql`, bound only as
  `127.0.0.1:14331 -> 1433`.
- Keep SQL and Cloudinary credentials process-only or in an ignored `.env`.
  Never commit them or paste them into this document.
- Cloudinary is optional for the deterministic HTTP/SQL suite. The opt-in
  provider smoke requires development-only `Cloudinary__CloudName`,
  `Cloudinary__ApiKey`, and `Cloudinary__ApiSecret` values.

Verify the exact local SQL target before running tests:

```powershell
docker start tripmate-tm70-sql
docker port tripmate-tm70-sql 1433/tcp
Test-NetConnection 127.0.0.1 -Port 14331
```

Expected binding: `127.0.0.1:14331`. Do not continue if the resolved target is
not the isolated local container.

Set the connection only for the current PowerShell process without displaying
the password:

```powershell
$passwordLine = docker inspect -f '{{range .Config.Env}}{{println .}}{{end}}' tripmate-tm70-sql |
  Where-Object { $_ -like 'MSSQL_SA_PASSWORD=*' } |
  Select-Object -First 1
if (-not $passwordLine) { throw 'Missing isolated SQL password configuration.' }
$password = $passwordLine.Substring('MSSQL_SA_PASSWORD='.Length)
$env:TRIPMATE_SQLSERVER_TEST_CONNECTION =
  "Server=127.0.0.1,14331;Database=master;User Id=sa;Password=$password;TrustServerCertificate=True;Encrypt=False"
$password = $null
$passwordLine = $null
```

## 2. Reproducible canonical HTTP/SQL smoke

The handoff test uses signed JWT authentication, the real ASP.NET HTTP route,
an isolated SQL database, a deterministic review-media storage substitute, and
new request/DbContext scopes between create, recovery read, edit, final read,
and aggregate verification:

```powershell
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj `
  -c Release --no-restore `
  --filter "FullyQualifiedName~Handoff_CreateGetEditGetAndAggregate" `
  --logger "console;verbosity=minimal"
```

Expected: 1 passed, 0 failed, 0 skipped, exit code 0. The test proves:

1. initial `GET /api/v1/bookings/{bookingId}/review` returns `none`;
2. multipart `POST` creates one canonical review and one adopted media link;
3. a second POST returns 409 `trip_review.duplicate`;
4. GET from a new host/request context returns the single review, public-safe
   author name, normalized values, version, immutable deadline, and persisted
   HTTPS media URL;
5. JSON PUT edits only the four editable values plus rowversion;
6. a new GET shows the new version and edited values while C4, media,
   `createdAtUtc`, and `editDeadlineUtc` remain unchanged; and
7. new aggregate contexts report average 5.0/count 1 after create and average
   3.0/count 1 after edit, proving the edit changes average without adding a
   contributor.

Run the typed service route, schema and aggregate coverage against the same
isolated SQL target:

```powershell
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj `
  -c Release --no-restore `
  --filter "FullyQualifiedName~ServiceTripReviewEndpointSqlServerTests|FullyQualifiedName~TripReviewTypedParentMigrationTests|FullyQualifiedName~TripReviewAggregateSqlServerTests" `
  --logger "console;verbosity=minimal"
```

This proves that `serviceBookingId` is emitted without `bookingId`,
`ReviewableRecordRef` and subject are exact `{kind,id}` objects, null
`Services.poi_id` is unsupported, rejected/unavailable text publishes nothing,
typed parent namespaces have separate constraints/uniqueness, and canonical
Tour/POI contributions win over same-booking legacy overlap with internal
warnings rather than double counting.

Representative sanitized POST metadata:

```json
{
  "overallRating": 5,
  "title": "Title",
  "content": "Content",
  "poiRatings": [],
  "routePacing": null,
  "cspRating": null,
  "publishDisplayName": false
}
```

Representative sanitized PUT body:

```json
{
  "overallRating": 3,
  "title": "Edited handoff title",
  "content": "Edited handoff content",
  "publishDisplayName": true,
  "version": "<Base64 SQL rowversion returned by GET>"
}
```

## 3. Compact boundary smoke

Run the final TM-79 application and SQL slices:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj `
  -c Release --no-restore `
  --filter "FullyQualifiedName~Features.TripReviews" `
  --logger "console;verbosity=minimal"

dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj `
  -c Release --no-build --no-restore `
  --filter "FullyQualifiedName~TripMate.Api.IntegrationTests.Reviews" `
  --logger "console;verbosity=minimal" `
  --blame-hang-timeout 3m
```

Important expected HTTP outcomes include:

| Case | Expected result |
| --- | --- |
| Valid first POST | 201 with direct `new` review DTO |
| Second POST / lost-response recovery | 409 `trip_review.duplicate`; recover with GET |
| Non-null pacing without route evidence | 400 `trip_review.route_pacing_unavailable` |
| CSP rating without owned CSP provenance | 400 `trip_review.csp_ineligible` |
| Nonempty POI ratings while G-VISITS is open | 400 `trip_review.poi_unavailable` |
| Stale rowversion | 409 `trip_review.stale_version` |
| PUT at or after deadline | 409 `trip_review.edit_expired` |
| Unknown or create-only PUT member | 400 `trip_review.invalid_edit_payload` |
| Owner-context/create detects a same-booking legacy conflict | 409 `trip_review.legacy_conflict` |
| Media provider unavailable | 503 `trip_review.storage_unavailable` |

Public aggregate reads do not return that owner-write conflict. They apply the
canonical-wins rule, exclude the overlapping legacy contribution and emit the
mandatory internal integrity warning.

C4 evidence is deliberately separate:

- route pacing remains unavailable because the current booking context has no
  authoritative booking-attributed route snapshot; null/omitted pacing works;
- an owned `CSPGenerated` itinerary plus owned scheduling request makes the CSP
  capability available and persists a 1-5 score; and
- POI capability stays unavailable with `visitEvidenceUnavailable` while
  G-VISITS is open.

Legacy fixtures prove conservative read/block/conflict behavior in isolation.
They do not prove the shape or rollout safety of deployed legacy data while
G-LEGACY remains open.

## 4. Optional real Cloudinary provider handoff smoke

After setting the isolated SQL variable and three development-only Cloudinary
variables in the current process, run exactly the opt-in handoff smoke:

```powershell
$env:TRIPMATE_CLOUDINARY_HANDOFF_SMOKE = '1'
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj `
  -c Release --no-restore `
  --filter "FullyQualifiedName~Task13CloudinaryHandoffSmokeTests.RealProvider_HttpSqlPersistenceDeliveryAndExactCleanup" `
  --logger "console;verbosity=minimal"
Remove-Item Env:TRIPMATE_CLOUDINARY_HANDOFF_SMOKE
```

Expected: signed-JWT multipart POST succeeds through the real HTTP/Application
pipeline, one review/media/operation is visible from SQL, GET from a new host
returns the same HTTPS media, the delivery URL is readable as an image, and
exact destroy plus repeated probe confirms absence. The test uses harmless
generated 2x2 PNG bytes and exact cleanup in a `finally` block. A pass proves
the development-provider handoff through HTTP -> SQL -> Cloudinary -> delivery
-> cleanup. It is not production-provider or deployed-environment evidence.

The older `CloudinaryReviewMediaSmokeTests` remains a narrower Task 8d
adapter-only check. Do not substitute that adapter smoke for this Task 13
handoff evidence.

## 5. Full regression and cleanup

```powershell
dotnet format TripMate.slnx --no-restore `
  --include src/TripMate.Application/Features/TripReviews/GetContext/GetTripReviewContextQueryHandler.cs `
            tests/TripMate.Api.IntegrationTests/Reviews/TripReviewEndpointTests.cs `
            tests/TripMate.Api.IntegrationTests/Reviews/Task13CloudinaryHandoffSmokeTests.cs `
  --verify-no-changes

dotnet build TripMate.slnx -c Release --no-restore

dotnet test TripMate.slnx -c Release --no-build --no-restore `
  --logger "console;verbosity=minimal" `
  --blame-hang-timeout 3m

git -c safe.directory=D:/CapStone/Capstone_BE_tm79 diff --check
```

Remove the process-only SQL setting when finished:

```powershell
Remove-Item Env:TRIPMATE_SQLSERVER_TEST_CONNECTION -ErrorAction SilentlyContinue
```

Verify that the suite cleaned its disposable databases:

```powershell
docker exec tripmate-tm70-sql bash -lc '/opt/mssql-tools18/bin/sqlcmd -b -V 11 -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -d master -Q "SELECT name FROM sys.databases WHERE name LIKE N''TripMate_Test[_]%'';"'
```

Expected: no rows. Also verify no `.trx`, dump, data-bearing `TestResults`,
test logs, generated media, testhost process, or Cloudinary smoke asset remains.

### Verified 2026-10-09 regression matrix

| Project/gate | Result |
| --- | --- |
| Moderation focus | 324 passed, 0 failed, 0 skipped |
| Application unit tests | 1,295 passed, 0 failed, 0 skipped |
| Infrastructure unit tests | 619 passed, 0 failed, 2 provider skips |
| API integration with SQL | 754 passed, 0 failed, 1 provider skip |
| Release build | 0 warnings, 0 errors |
| Format verification | Exit 0 |
| SQL cleanup | 0 `TripMate_Test_*` databases remain |

## 6. Remaining boundaries

- BR-94 is implemented by deterministic local `tm79-review-text-v1` screening.
  The runtime intentionally requires no OpenAI, Azure Content Safety, external
  moderation provider, moderation API key or policy-service dependency.
- G-VISITS remains relevant only to the deferred `poiRatings[]` child extension;
  canonical ServiceBooking-to-POI review and aggregate behavior are implemented.
- G-LEGACY is open. Isolated fixtures prove safe compatibility behavior, not
  production legacy-data rollout readiness.
- Canonical Tour and POI aggregate semantics are complete. Linked POI-child
  ratings remain outside the canonical POI-review path.
- Mobile, Web and cross-repository UAT are not started by this Backend smoke.
- Full UC-32 Trip History implementation remains TM-78-owned; TM-79 preserves
  only its typed handoff dependency.
- `origin/develop` must be reconciled separately before merge; Task 13 does not
  authorize merge/rebase and does not use shared/Azure data.
