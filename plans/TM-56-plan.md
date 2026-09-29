# TM-56 Create Scheduling Request Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use
> `superpowers:executing-plans` to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a Traveler generate, save, and view one feasible one-day Da Nang
itinerary with road-network estimates, verified POIs, a practical rest-break
policy, and duplicate-safe submission.

**Architecture:** The Backend owns generation. A request is validated and
persisted atomically with a generated `Draft` itinerary and typed itinerary
items. `IRouteDurationProvider` is an application port with an ORS HTTP
adapter; tests inject a deterministic fake. Mobile remains a feature-first
client: page -> Cubit -> repository -> Dio client, without route/business logic
in widgets.

**Tech Stack:** .NET 10, ASP.NET Core, MediatR, FluentValidation, EF Core SQL
Server mappings, database-first idempotent SQL, xUnit; Flutter, flutter_bloc,
Dio, go_router, uuid.

**Spec:** `specs/TM-56-spec.md` and `specs/TM-56-traveler-research.md`

## Global Constraints

- Database-first only: create idempotent SQL in `database/migrations`; never
  create an EF migration.
- All `DateTimeOffset` values mapped to SQL Server `DATETIME2` use
  `.AsUtcDateTime2()`.
- New backend work follows Domain <- Application <- Infrastructure <- API and
  returns `Result`/`Result<T>` for expected business outcomes.
- The ORS key is read only from server configuration/environment and is never
  committed or shipped in Flutter.
- Haversine can prefilter candidates only; all displayed leg durations come
  from the route provider.
- The API generates one itinerary per request. Idempotency replays return the
  original result and do not create a second itinerary.
- UI copy is English. Do not introduce an Administrator Flutter feature.
- Do not commit, push, or open a PR without a separate developer instruction.

## File Structure

### Backend

- `database/migrations/20260914_add_scheduling_request_generation.sql` —
  idempotent additive schema and constraints.
- `database/seeds/20260914_seed_danang_planning_pois.sql` — idempotent curated
  POIs, their categories/opening hours, source URLs, and verification dates.
- `src/TripMate.Domain/Entities/SchedulingRequest.cs` — request state,
  idempotency payload fingerprint, and generation result relationship.
- `src/TripMate.Domain/Entities/ItineraryItem.cs` — visit/rest invariant and
  ordered itinerary item.
- `src/TripMate.Domain/Entities/Itinerary.cs` — CSP-generated factory and
  item/request navigation.
- `src/TripMate.Domain/Entities/PointOfInterest.cs` — planning metadata,
  including cost/source verification.
- `src/TripMate.Domain/Entities/TravelerProfile.cs` — saved interest-tag
  preferences used as optional ranking input.
- `src/TripMate.Application/Features/Scheduling/*` — query/command DTOs,
  validator, generator, application ports, handler, and error contracts.
- `src/TripMate.Infrastructure/Routing/OpenRouteServiceRouteDurationProvider.cs`
  — ORS V2 Matrix adapter and configuration.
- `src/TripMate.Infrastructure/Persistence/Configurations/*` and
  `ApplicationDbContext.cs` — schema mappings and DbSets.
- `src/TripMate.Api/Controllers/V1/SchedulingRequestsController.cs` and
  `PointsOfInterestSearchController.cs` — Traveler-only HTTP binding/status
  mapping.
- `tests/...` — domain, application, endpoint, SQL Server integration, and
  OpenAPI coverage.

### Mobile

- `lib/features/traveler/domain/entities/itinerary_generation.dart` — pure
  request/result/stop data used by UI.
- `lib/features/traveler/domain/repositories/itinerary_repository.dart` —
  repository contract.
- `lib/features/traveler/data/models/*itinerary*.dart` and
  `data/repositories/itinerary_repository_impl.dart` — API serialization and
  failure mapping.
- `lib/features/traveler/presentation/cubit/create_itinerary_*` — form state,
  UUID retry behavior, and result/failure transitions.
- `lib/features/traveler/presentation/pages/create_itinerary_page.dart` and
  `itinerary_result_page.dart` — responsive English form/result UI.
- `lib/app/router/app_routes.dart`, `app_router.dart`, and
  `core/di/service_locator.dart` — route and dependency registration.
- `test/features/traveler/...` — model, repository, Cubit, and widget tests.

---

### Task 1: Establish the generation schema and planning domain

**Files:**
- Create: `database/migrations/20260914_add_scheduling_request_generation.sql`
- Create: `src/TripMate.Domain/Entities/SchedulingRequest.cs`
- Create: `src/TripMate.Domain/Entities/ItineraryItem.cs`
- Create: `src/TripMate.Domain/Enums/ItineraryItemKind.cs`
- Create: `src/TripMate.Domain/Enums/RestPreference.cs`
- Create: `src/TripMate.Domain/Enums/SchedulingRequestStatus.cs`
- Modify: `src/TripMate.Domain/Entities/Itinerary.cs`
- Modify: `src/TripMate.Domain/Entities/PointOfInterest.cs`
- Modify: `src/TripMate.Infrastructure/Persistence/Configurations/ItineraryConfiguration.cs`
- Create: `src/TripMate.Infrastructure/Persistence/Configurations/ItineraryItemConfiguration.cs`
- Create: `src/TripMate.Infrastructure/Persistence/Configurations/SchedulingRequestConfiguration.cs`
- Modify: `src/TripMate.Infrastructure/Persistence/Configurations/PointOfInterestConfiguration.cs`
- Modify: `src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs`
- Modify: `src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs`
- Modify: `tests/TripMate.Application.UnitTests/TestUtilities/TestDbContext.cs`
- Modify: `tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs`
- Test: `tests/TripMate.Application.UnitTests/Domain/SchedulingRequestTests.cs`
- Test: `tests/TripMate.Application.UnitTests/Domain/ItineraryItemTests.cs`
- Test: `tests/TripMate.Infrastructure.UnitTests/Persistence/SchedulingGenerationPersistenceModelTests.cs`

**Interfaces:**
- Produces `SchedulingRequest.Create(...)`, `SchedulingRequest.Complete(...)`,
  `SchedulingRequest.FailInfeasible(...)`, and a `RequestHash` immutable value.
- Produces `Itinerary.CreateCspGenerated(...)` and
  `Itinerary.AddItem(ItineraryItem item)`.
- Produces `ItineraryItem.CreateVisit(...)` and
  `ItineraryItem.CreateRest(...)`; a rest item may have no POI, while a visit
  item must have one.

- [ ] **Step 1: Write failing domain tests**

```csharp
[Fact]
public void CreateRest_WithoutPoi_IsAllowed()
{
    var item = ItineraryItem.CreateRest(2, arrival, departure, 30);
    Assert.Equal(ItineraryItemKind.Rest, item.Kind);
    Assert.Null(item.PointOfInterestId);
}

[Fact]
public void CreateVisit_WithoutPoi_Throws()
{
    Assert.Throws<ArgumentOutOfRangeException>(() =>
        ItineraryItem.CreateVisit(1, 0, arrival, departure, 60, false, null));
}
```

- [ ] **Step 2: Run the focused tests and confirm RED**

Run: `dotnet test TripMate.slnx --filter "FullyQualifiedName~SchedulingRequestTests|FullyQualifiedName~ItineraryItemTests"`

Expected: compile/test failure because the new types and factories do not yet
exist.

- [ ] **Step 3: Add the idempotent SQL migration**

The script must:

```sql
-- Add request time-zone, operation key/hash, transport/end/rest fields and
-- failure code to planning.SchedulingRequests; add a unique Traveler/key index.
-- Add source_url, verified_at_utc and estimated_visit_cost to catalog.POIs.
-- Add item_kind to planning.ItineraryItems and make poi_id nullable only for
-- Rest items; add a CHECK that Visit requires poi_id.
-- Preserve all existing data and use IF COL_LENGTH / IF NOT EXISTS guards.
```

It must add no EF migration and must use `DATETIME2` for UTC properties.

- [ ] **Step 4: Implement minimal entities/mappings/DbSets**

Use private setters and factories to enforce status, positive sequence/duration,
valid coordinates, required `Visit` POI, and `Rest` semantics. Map all UTC
columns with `.AsUtcDateTime2()`. Map same-transaction relationships through
navigations rather than scalar keys copied before `SaveChangesAsync`.

- [ ] **Step 5: Run focused domain and persistence tests and confirm GREEN**

Run: `dotnet test TripMate.slnx --filter "FullyQualifiedName~SchedulingRequestTests|FullyQualifiedName~ItineraryItemTests|FullyQualifiedName~SchedulingGenerationPersistenceModelTests"`

Expected: PASS.

- [ ] **Step 6: Review task boundary**

Confirm SQL is idempotent, no unrelated schema changed, and all test DbContexts
implement the expanded application context before advancing.

### Task 2: Seed planning-ready Da Nang POIs and expose Traveler search

**Files:**
- Create: `database/seeds/20260914_seed_danang_planning_pois.sql`
- Create: `src/TripMate.Application/Features/PointsOfInterest/Search/SearchPointsOfInterestQuery.cs`
- Create: `src/TripMate.Application/Features/PointsOfInterest/Search/SearchPointsOfInterestQueryHandler.cs`
- Create: `src/TripMate.Application/Features/PointsOfInterest/Search/SearchPointsOfInterestQueryValidator.cs`
- Create: `src/TripMate.Application/Features/PointsOfInterest/Search/SelectablePoiDto.cs`
- Create: `src/TripMate.Api/Controllers/V1/PointsOfInterestSearchController.cs`
- Test: `tests/TripMate.Application.UnitTests/Features/PointsOfInterest/Search/SearchPointsOfInterestQueryHandlerTests.cs`
- Test: `tests/TripMate.Api.IntegrationTests/PointsOfInterest/SearchPointsOfInterestEndpointTests.cs`
- Test: `tests/TripMate.Api.IntegrationTests/PointsOfInterest/SearchPointsOfInterestOpenApiTests.cs`

**Interfaces:**
- Produces `GET /api/v1/points-of-interest/search?query=&latitude=&longitude=&radiusKm=&page=&pageSize=`.
- Returns only active, planning-ready fields:
  `id`, `name`, `address`, `latitude`, `longitude`, `averageVisitDurationMinutes`,
  `estimatedVisitCost`, `openingHoursKnown`, `hasShelter`, and category name.

- [ ] **Step 1: Write the failing search tests**

```csharp
[Fact]
public async Task Handle_ExcludesInactiveAndPlanningIncompletePois()
{
    var result = await handler.Handle(queryNearCentre, CancellationToken.None);
    Assert.All(result.Value.Items, item => Assert.True(item.OpeningHoursKnown));
    Assert.DoesNotContain(result.Value.Items, item => item.Name == "Inactive POI");
}

[Fact]
public async Task Get_AsTraveler_ReturnsOnlySelectableContract()
{
    var response = await client.GetAsync("/api/v1/points-of-interest/search?latitude=16.05&longitude=108.20&radiusKm=10&page=1&pageSize=20");
    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
}
```

- [ ] **Step 2: Run focused tests and confirm RED**

Run: `dotnet test TripMate.slnx --filter "FullyQualifiedName~SearchPointsOfInterest"`

Expected: failure because query/controller do not exist.

- [ ] **Step 3: Implement the read model and controller**

Use `AsNoTracking`, calculate distance only for filtering/sorting, cap `pageSize`
at 50, and require an authenticated Traveler. Do not reuse the Administrator
controller or expose descriptions/audit/admin-only data.

- [ ] **Step 4: Write and validate the seed script**

Seed all approved Da Nang regions with only planning-ready POIs. Every inserted
row must have a source URL and UTC verification date; use the Da Nang Tourism
portal for venue hours/prices where available and OpenStreetMap attribution for
coordinates/categories. Use idempotent `MERGE`/`NOT EXISTS` keys based on
normalized name and coordinates. The script must not scrape or embed Google
Maps/Places content.

- [ ] **Step 5: Run focused tests and execute the seed script against local SQL Server**

Run: `dotnet test TripMate.slnx --filter "FullyQualifiedName~SearchPointsOfInterest"`

Run the repository's documented SQL migration/seed workflow against the Docker
database, then query that every auto-selectable seed row has coordinate, hours,
duration, source, verification time, and either a known cost or null cost.

- [ ] **Step 6: Review task boundary**

Confirm the seed has no copied photos/reviews, has visible OSM attribution where
displayed, and incomplete POIs cannot enter generation.

### Task 3: Implement the routing port and feasibility-first generator

**Files:**
- Create: `src/TripMate.Application/Features/Scheduling/Common/IRouteDurationProvider.cs`
- Create: `src/TripMate.Application/Features/Scheduling/Common/RouteDuration.cs`
- Create: `src/TripMate.Application/Features/Scheduling/Common/ItineraryGenerationService.cs`
- Create: `src/TripMate.Application/Features/Scheduling/Common/GenerationInput.cs`
- Create: `src/TripMate.Infrastructure/Routing/OpenRouteServiceOptions.cs`
- Create: `src/TripMate.Infrastructure/Routing/OpenRouteServiceRouteDurationProvider.cs`
- Modify: `src/TripMate.Infrastructure/DependencyInjection.cs`
- Modify: `src/TripMate.Api/appsettings.json`
- Modify: `.env.example`
- Test: `tests/TripMate.Application.UnitTests/Features/Scheduling/ItineraryGenerationServiceTests.cs`
- Test: `tests/TripMate.Infrastructure.UnitTests/Routing/OpenRouteServiceRouteDurationProviderTests.cs`

**Interfaces:**

```csharp
public interface IRouteDurationProvider
{
    Task<RouteDurationMatrix> GetMatrixAsync(
        IReadOnlyList<RoutePoint> points,
        TransportMode transportMode,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write failing generator tests**

```csharp
[Fact]
public async Task Generate_IncludesAllMandatoryPois_AndReachesEndInTime()
{
    var result = await service.GenerateAsync(inputWithTwoMandatoryPois, CancellationToken.None);
    Assert.True(result.IsSuccess);
    Assert.Equal(new long[] { 12, 28 }, result.Value.VisitPoiIds.Where(id => id is 12 or 28));
}

[Fact]
public async Task Generate_AutoRest_InsertsRestAfterLongContinuousSchedule()
{
    var result = await service.GenerateAsync(fiveHourInputWithAutoRest, CancellationToken.None);
    Assert.Contains(result.Value.Items, item => item.Kind == ItineraryItemKind.Rest);
}
```

- [ ] **Step 2: Run focused tests and confirm RED**

Run: `dotnet test TripMate.slnx --filter "FullyQualifiedName~ItineraryGenerationServiceTests"`

Expected: failure because the port/service are absent.

- [ ] **Step 3: Implement the deterministic generator**

Prefilter by Haversine only, request one route matrix, find the minimum feasible
mandatory sequence (at most six), then greedily add eligible optional stops.
Treat start/end time, opening hours, mandatory POIs, end/return point, and
budget as hard constraints. Read saved Traveler interest tags when available and
use them as a deterministic soft ranking signal together with scenic/photo
scores, route time, and cost. Treat these preferences as ranking only.
Reserve per-leg/final buffers, insert rest according to the approved policy,
including after optional stops, and return a typed infeasible reason instead of a
partial plan.

- [ ] **Step 4: Implement the ORS adapter and configuration**

Use an injected `HttpClient`, `OpenRouteService:BaseUrl`, and
`OpenRouteService:ApiKey`. Map `Walking` to `foot-walking`, `Car` and
`Motorbike` to `driving-car`; reject unsupported `PublicTransit` as infeasible
for this MVP. Parse only matrix distance/duration fields. Convert provider
timeout/4xx/5xx to an unexpected provider failure; never return Haversine as a
customer-facing duration.

- [ ] **Step 5: Run focused tests and confirm GREEN**

Run: `dotnet test TripMate.slnx --filter "FullyQualifiedName~ItineraryGenerationServiceTests|FullyQualifiedName~OpenRouteServiceRouteDurationProviderTests"`

Expected: PASS.

- [ ] **Step 6: Review task boundary**

Verify the key is absent from tracked configuration, a provider outage does not
produce an itinerary, and all displayed travel legs originate from the port.

### Task 4: Create the scheduling request API with idempotency

**Files:**
- Create: `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommand.cs`
- Create: `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandValidator.cs`
- Create: `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs`
- Create: `src/TripMate.Application/Features/Scheduling/Common/SchedulingResponseDto.cs`
- Create: `src/TripMate.Application/Features/Scheduling/Common/SchedulingErrorCodes.cs`
- Create: `src/TripMate.Application/Features/Scheduling/Common/ISchedulingRequestLock.cs`
- Create: `src/TripMate.Infrastructure/Persistence/SqlServerSchedulingRequestLock.cs`
- Create: `src/TripMate.Api/Controllers/V1/SchedulingRequestsController.cs`
- Modify: `src/TripMate.Infrastructure/DependencyInjection.cs`
- Modify: `src/TripMate.Api/OpenApi/*` only if required to make the header and
  error contract visible.
- Test: `tests/TripMate.Application.UnitTests/Features/Scheduling/Create/CreateSchedulingRequestCommandValidatorTests.cs`
- Test: `tests/TripMate.Application.UnitTests/Features/Scheduling/Create/CreateSchedulingRequestCommandHandlerTests.cs`
- Test: `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestEndpointTests.cs`
- Test: `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestOpenApiTests.cs`

**Interfaces:**

```http
POST /api/v1/scheduling-requests
Idempotency-Key: <UUID>
```

The endpoint binds the header explicitly with
`[FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey` so Swagger shows
it. It returns 201 with the original result for an equal-key/equal-payload
replay, 409 for a key/payload mismatch, 422 for feasibility, and standard
401/403 for role failure.

- [ ] **Step 1: Write failing command/endpoint tests**

```csharp
[Fact]
public async Task Post_SameKeyAndPayload_ReplaysOriginalItinerary()
{
    var first = await SendValidRequest(key);
    var second = await SendValidRequest(key);
    Assert.Equal(first.ItineraryId, second.ItineraryId);
}
```

- [ ] **Step 2: Run focused tests and confirm RED**

Run: `dotnet test TripMate.slnx --filter "FullyQualifiedName~CreateSchedulingRequest"`

Expected: failure because command/endpoint are absent.

- [ ] **Step 3: Implement validation, lock, and handler**

Validate time-zone, local same-day duration, coordinates, distinct max-six
mandatory POIs, exact end choice, 60-720 minute duration, 1-50km radius,
positive budget, enum values, and UUID header. Normalize and hash the complete
payload. Under a SQL Server application lock derived from Traveler and the
operation key, replay equal requests, reject mismatched requests, generate, and
persist request + CSP draft + item graph in a serializable transaction.
Persist an infeasible request for stable 422 replay; roll back unexpected
failures without consuming the operation key.

- [ ] **Step 4: Implement explicit controller/OpenAPI binding**

Authorize the Traveler role, pass the authenticated user identity only through
the existing current-user service, map `Result` through `HandleFailure`, and
return `Created` for a new or replayed successful itinerary. Do not expose
solver/provider internals in error copy.

- [ ] **Step 5: Run focused tests and confirm GREEN**

Run: `dotnet test TripMate.slnx --filter "FullyQualifiedName~CreateSchedulingRequest"`

Expected: PASS.

- [ ] **Step 6: Review task boundary**

Confirm same operation cannot create duplicates, a payload mismatch is not
silently replayed, and failed provider calls are not persisted as success.

### Task 5: Prove SQL Server atomicity, concurrency, and route-error behavior

**Files:**
- Create: `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestSqlServerTests.cs`
- Modify: `tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs`
- Test: `tests/TripMate.Api.IntegrationTests/Scheduling/CreateSchedulingRequestSqlServerTests.cs`

- [ ] **Step 1: Write failing SQL Server integration tests**

```csharp
[Fact]
public async Task Post_ConcurrentEqualOperations_PersistsOneRequestOneDraftAndOneItemSet()
{
    var responses = await Task.WhenAll(SendValidRequest(key), SendValidRequest(key));
    Assert.Single(responses.Select(response => response.ItineraryId).Distinct());
    Assert.Equal(1, await CountSchedulingRequests(key));
    Assert.Equal(1, await CountItinerariesForOperation(key));
}

[Fact]
public async Task Post_WhenPersistenceFails_RollsBackRequestItineraryAndItems()
{
    await AssertFailureFromRealConstraintViolation();
    Assert.Equal(0, await CountIncompleteGeneratedGraphs());
}
```

- [ ] **Step 2: Run the SQL Server test filter and confirm RED**

Run: `dotnet test TripMate.slnx --filter "FullyQualifiedName~CreateSchedulingRequestSqlServerTests"`

Expected: failure until the endpoint/mapping/transaction exist.

- [ ] **Step 3: Add only the test seams required for a real provider failure**

Replace ORS through DI in test hosting with a deterministic route provider;
induce a real database constraint/provider failure after graph tracking starts,
not a fake early `SaveChanges` exception.

- [ ] **Step 4: Run SQL Server integration tests and confirm GREEN**

Run: `dotnet test TripMate.slnx --filter "FullyQualifiedName~CreateSchedulingRequestSqlServerTests"`

Expected: PASS against the Docker SQL Server connection string configured for
integration tests.

- [ ] **Step 5: Review task boundary**

Inspect persisted rows after every test: no partial group of request/itinerary/
items exists after a failed operation, and concurrent same-key calls return the
same ID without HTTP 500.

### Task 6: Build Mobile domain/data support with retry-safe repository calls

**Files:**
- Create: `lib/features/traveler/domain/entities/itinerary_generation.dart`
- Create: `lib/features/traveler/domain/repositories/itinerary_repository.dart`
- Create: `lib/features/traveler/data/models/selectable_poi_model.dart`
- Create: `lib/features/traveler/data/models/itinerary_generation_request_model.dart`
- Create: `lib/features/traveler/data/models/itinerary_generation_result_model.dart`
- Create: `lib/features/traveler/data/repositories/itinerary_repository_impl.dart`
- Modify: `lib/core/di/service_locator.dart`
- Modify: `lib/core/error/failures.dart`
- Modify: `lib/core/error/error_mapper.dart`
- Test: `test/features/traveler/data/models/itinerary_generation_result_model_test.dart`
- Test: `test/features/traveler/data/repositories/itinerary_repository_impl_test.dart`

**Interfaces:**

```dart
abstract interface class ItineraryRepository {
  Future<List<SelectablePoi>> searchPois(PoiSearch query);
  Future<ItineraryGenerationResult> createItinerary(
    ItineraryGenerationRequest request,
    String idempotencyKey,
  );
}
```

- [ ] **Step 1: Write failing model/repository tests**

```dart
test('createItinerary sends the operation key and parses a Rest item', () async {
  final result = await repository.createItinerary(request, 'operation-key');
  expect(result.items.any((item) => item.kind == ItineraryItemKind.rest), isTrue);
});
```

- [ ] **Step 2: Run focused tests and confirm RED**

Run: `flutter test test/features/traveler/data`

Expected: compile/test failure because new domain/data types are absent.

- [ ] **Step 3: Implement models, repository, and typed failures**

Send only the approved contract, include `Idempotency-Key`, parse 201/replay as
the same result, and map 422 and 409 to actionable typed failures.
Retain the original form request on errors; do not leak raw Dio messages.

- [ ] **Step 4: Run focused tests and confirm GREEN**

Run: `flutter test test/features/traveler/data`

Expected: PASS.

- [ ] **Step 5: Review task boundary**

Confirm no ORS or map-provider key appears in Flutter and no raw HTTP error is
presented to a Traveler.

### Task 7: Build the itinerary-generation Cubit and form/result UI

**Files:**
- Create: `lib/features/traveler/presentation/cubit/create_itinerary_cubit.dart`
- Create: `lib/features/traveler/presentation/cubit/create_itinerary_state.dart`
- Create: `lib/features/traveler/presentation/pages/create_itinerary_page.dart`
- Create: `lib/features/traveler/presentation/pages/itinerary_result_page.dart`
- Modify: `lib/app/router/app_routes.dart`
- Modify: `lib/app/router/app_router.dart`
- Modify: `lib/features/traveler/presentation/pages/traveler_shell_page.dart`
- Test: `test/features/traveler/presentation/cubit/create_itinerary_cubit_test.dart`
- Test: `test/features/traveler/presentation/pages/create_itinerary_page_test.dart`
- Test: `test/features/traveler/presentation/pages/itinerary_result_page_test.dart`

**Interfaces:**
- Route: `/traveler/itineraries/create`; result receives a typed generated
  itinerary in `GoRouter.extra`, never serializes it into the URL.
- Cubit states: `initial`, `searchingPois`, `submitting`, `success`,
  `validationFailure`, `infeasible`, and `failure`.

- [ ] **Step 1: Write failing Cubit/widget tests**

```dart
blocTest<CreateItineraryCubit, CreateItineraryState>(
  'keeps form values and shows actionable copy for an infeasible request',
  build: () => CreateItineraryCubit(repository: infeasibleRepository),
  act: (cubit) => cubit.submit(validRequest),
  expect: () => [
    isA<CreateItineraryState>().having((s) => s.status, 'status', ItineraryStatus.submitting),
    isA<CreateItineraryState>().having((s) => s.status, 'status', ItineraryStatus.infeasible),
  ],
);
```

- [ ] **Step 2: Run focused tests and confirm RED**

Run: `flutter test test/features/traveler/presentation/cubit/create_itinerary_cubit_test.dart test/features/traveler/presentation/pages/create_itinerary_page_test.dart`

Expected: compile/test failure because UI/Cubit are absent.

- [ ] **Step 3: Implement the Cubit first**

Validate required form input locally; generate one UUID per submit attempt and
reuse it only for retrying the same unchanged attempt. Search POIs through the
repository. Preserve input on 422/system failure. Map error codes to English
copy, including provider-unavailable and constraint advice.

- [ ] **Step 4: Implement responsive pages and routing**

Use existing Material 3/shared widgets. The form has start, exploration centre,
end/return, date/time, duration, transport, radius, budget, mandatory POIs, and
rest preference. The result shows ordered arrival/departure times, `Visit` vs
`Suggested break`, mandatory marker, total estimated cost/time/free time, and
“Food and drinks are not included” for rest. Add a visible `Create itinerary`
entry from Traveler Trips; retain keyboard/small-screen scrolling behavior.

- [ ] **Step 5: Run focused tests and confirm GREEN**

Run: `flutter test test/features/traveler/presentation/cubit/create_itinerary_cubit_test.dart test/features/traveler/presentation/pages/create_itinerary_page_test.dart test/features/traveler/presentation/pages/itinerary_result_page_test.dart`

Expected: PASS.

- [ ] **Step 6: Review task boundary**

Verify all customer-facing strings are English, loading blocks duplicate taps,
no map UI was introduced, and the screen handles loading/empty/error/permission
fallback states.

### Task 8: Run end-to-end quality gates and review final heads

**Files:**
- Modify only files required by fixes found during verification.
- Test: all existing backend and Mobile test projects.

- [ ] **Step 1: Run Backend format/build/test**

Run:

```bash
dotnet format TripMate.slnx --verify-no-changes
dotnet build TripMate.slnx
dotnet test TripMate.slnx
```

- [ ] **Step 2: Run Mobile format/analyze/test/build**

Run:

```bash
flutter pub get
dart format --set-exit-if-changed .
flutter analyze
flutter test
flutter build apk --debug
```

- [ ] **Step 3: Perform local integration smoke checks**

Use the Docker SQL Server and a configured ORS key to verify: POI search, one
valid generation, same-key replay, changed-payload 409, infeasible 422, and an
`Auto` rest break on a long itinerary.

- [ ] **Step 4: Two-pass self-review**

First pass: compare every spec acceptance criterion to a test/implementation.
Second pass: inspect dependencies, secrets, SQL idempotency, UTC mappings,
unrelated diffs, hardcoded values, user-visible English copy, and all provider
failure paths. Classify and fix Critical findings before handoff.

## Plan Self-Review

| Requirement | Covered by |
| --- | --- |
| SQL database-first, UTC mappings, atomic persistence | Tasks 1, 4, 5 |
| Verified POIs over all Da Nang and searchable Mobile selection | Task 2 |
| ORS routing with no paid/mobile key and no Haversine display | Task 3 |
| Hard constraints, budget, opening hours, start/end, rest policy | Tasks 3, 4, 7 |
| Idempotency replay/mismatch and concurrency | Tasks 4, 5 |
| Traveler API/OpenAPI/security/error contract | Tasks 2, 4 |
| Mobile form, English states, retry, result rendering | Tasks 6, 7 |
| Final validation and manual smoke tests | Task 8 |

Placeholder scan: no `TODO`/`TBD` implementation placeholders are used. The
only runtime secret omitted by design is the ORS API key; it is intentionally
provided through untracked local configuration.
