# TM-76 / UC-30 — Commercial Services Backend Plan

> Execution gate: Implement only after this plan is reviewed and an execution mode is selected. The approved contract is `specs/TM-76-spec.md`.

## Objective

Deliver the public, read-only commercial-service catalog for Vehicle, Hotel, and Restaurant services. It exposes only `Available` services from `Active` providers, has no booking/click side effects, and discloses the offer data needed for an informed future booking decision.

## Constraints

- Follow Controller → MediatR query/validator/handler → `IApplicationDbContext`.
- Use an idempotent SQL migration plus the canonical `database/tripmate_schema_v7.sql`; no EF migration.
- Public endpoints are anonymous; logged-in Travelers get the same response.
- Filtering, count, projection, order and pagination remain in SQL with `AsNoTracking()`.
- Never expose API endpoint, commission, booking, or click-log data.
- Only VND is supported. Attributes are allow-listed, never raw JSON.

## Contract

- `GET /api/v1/commercial-services?category=&search=&page=1&pageSize=20`
- `GET /api/v1/commercial-services/{id}`
- Invalid input → RFC-7807 400. Empty list → 200. A missing, unavailable, or inactive-provider service → 404 `CommercialService.NotFound`.

## Tasks

### 1. Specify behavior with failing Application tests

**Files:**
- Create `tests/TripMate.Application.UnitTests/Features/CommercialServices/Explore/ExploreCommercialServicesQueryValidatorTests.cs`
- Create `tests/TripMate.Application.UnitTests/Features/CommercialServices/Explore/ExploreCommercialServicesQueryHandlerTests.cs`
- Create `tests/TripMate.Application.UnitTests/Features/CommercialServices/Detail/GetCommercialServiceDetailQueryHandlerTests.cs`

**Steps:**
1. Cover valid categories, trimmed 200-character search, and page/page-size limits.
2. Cover visibility (active provider + available service), category/provider-name search, deterministic sort, empty result, and detail concealment.
3. Assert disclosure data and safe omission of unknown/malformed attributes.

### 2. Map the existing commercial schema

**Files:**
- Create `src/TripMate.Domain/Entities/ServiceProvider.cs`
- Create `src/TripMate.Domain/Entities/CommercialService.cs`
- Create `src/TripMate.Infrastructure/Persistence/Configurations/ServiceProviderConfiguration.cs`
- Create `src/TripMate.Infrastructure/Persistence/Configurations/CommercialServiceConfiguration.cs`
- Modify `src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs`
- Modify `src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs`

**Steps:**
1. Explicitly map `commercial.ServiceProviders` and `commercial.Services`, their FK, decimal precision, enum/status strings and JSON attributes.
2. Add only the two required DbSets. Do not map/write booking or click-log features.
3. Map `last_updated_at` through `AsUtcDateTime2()`.

### 3. Add the migration, fresh schema parity, and development catalog

**Files:**
- Create `database/migrations/20261003_add_commercial_service_disclosures.sql`
- Modify `database/tripmate_schema_v7.sql`
- Modify the seed path consumed by `database/Dockerfile.seeded` / `database/seed-image.sh`
- Create `tests/TripMate.Api.IntegrationTests/CommercialServices/CommercialServiceMigrationSqlServerTests.cs`

**Steps:**
1. Add `currency_code`, `price_includes_tax`, `refundable_deposit_amount`, `cover_image_url`, `fulfilment_location_label`, `pickup_or_arrival_instructions`, `cancellation_policy_summary`, and `last_updated_at`.
2. Make upgrade rerunnable: add/backfill legacy rows, then enforce required VND/UTC/default/check contracts. Check non-negative monetary values and text lengths.
3. Add canonical-schema versus upgraded-schema inventory tests, including fresh, old baseline, second migration run, and wrong-shape failure.
4. Seed one plausible active/available local-development service per category without overwriting user-managed rows.

### 4. Implement list and detail vertical slices

**Files:**
- Create `src/TripMate.Application/Features/CommercialServices/Common/CommercialServiceDtos.cs`
- Create `src/TripMate.Application/Features/CommercialServices/Common/CommercialServiceConstants.cs`
- Create `src/TripMate.Application/Features/CommercialServices/Common/CommercialServiceAttributeSanitizer.cs`
- Create `src/TripMate.Application/Features/CommercialServices/Explore/ExploreCommercialServicesQuery.cs`
- Create `src/TripMate.Application/Features/CommercialServices/Explore/ExploreCommercialServicesQueryValidator.cs`
- Create `src/TripMate.Application/Features/CommercialServices/Explore/ExploreCommercialServicesQueryHandler.cs`
- Create `src/TripMate.Application/Features/CommercialServices/Detail/GetCommercialServiceDetailQuery.cs`
- Create `src/TripMate.Application/Features/CommercialServices/Detail/GetCommercialServiceDetailQueryValidator.cs`
- Create `src/TripMate.Application/Features/CommercialServices/Detail/GetCommercialServiceDetailQueryHandler.cs`

**Steps:**
1. Validate then filter in SQL by visibility, category and service/provider search.
2. Project public DTOs only; order category → name → ID; calculate page metadata with `CountAsync`, `Skip`, and `Take`.
3. Retain only valid allow-listed attributes by category: Vehicle (transmission, seats, licenseRequired, luggageCapacity); Hotel (roomType, beds, checkInTime, checkOutTime); Restaurant (cuisine, servingSize, reservationType).
4. Detail returns approved provider contact/disclosure fields only and has the same visibility predicate as list.

### 5. Add public endpoints and integration tests

**Files:**
- Create `src/TripMate.Api/Controllers/V1/PublicCommercialServicesController.cs`
- Create `tests/TripMate.Api.IntegrationTests/CommercialServices/ExploreCommercialServicesEndpointTests.cs`
- Create `tests/TripMate.Api.IntegrationTests/CommercialServices/GetCommercialServiceDetailEndpointTests.cs`
- Create `tests/TripMate.Api.IntegrationTests/CommercialServices/CommercialServicesSqlServerTests.cs`

**Steps:**
1. Use `[AllowAnonymous]`, typed OpenAPI result/error metadata and existing failure mapping.
2. Test guest/Traveler compatibility, filter/search/paging, 400 input, empty result, 404 concealment, excluded internals and read-only/no-write behavior.
3. Exercise mappings, query translation, migration, seed and pagination on real SQL Server.

### 6. Verify and hand off

```powershell
dotnet format TripMate.slnx --verify-no-changes --no-restore
dotnet build TripMate.slnx --no-restore --configuration Release
dotnet test TripMate.slnx -c Release --no-build
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --no-build --filter "Category=SqlServer"
git diff --check
```

## Review focus

- Visibility is identical in list/detail.
- Migration works from a legacy baseline and matches fresh schema.
- The API has no information leak and no booking/click-log write.
- SQL does not materialize the whole catalog before paging.
