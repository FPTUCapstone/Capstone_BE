# UC-52 POI catalogue - Implementation plan

Spec: `specs/UC-52-poi-catalogue-spec.md`.

## Task 1 - Application query (test first)
- Test: `tests/TripMate.Application.UnitTests/Features/PointsOfInterest/Catalogue/GetPoiCatalogueQueryHandlerTests.cs`
  (sorted lists, empty lists, projects id and name only).
- Code: `src/TripMate.Application/Features/PointsOfInterest/Catalogue/`
  `GetPoiCatalogueQuery.cs`, `GetPoiCatalogueQueryHandler.cs`, `PoiCatalogueDto.cs`.
- Verify: `dotnet test tests/TripMate.Application.UnitTests --filter GetPoiCatalogue`.

## Task 2 - API endpoint (test first)
- Test: `tests/TripMate.Api.IntegrationTests/PointsOfInterest/PoiCatalogueEndpointTests.cs`
  (Staff 200, empty lists, 403 for non-Staff roles, 401 without token).
- Code: `[HttpGet("catalogue")]` in `src/TripMate.Api/Controllers/V1/PointsOfInterestController.cs`.
- Verify: `dotnet test tests/TripMate.Api.IntegrationTests --filter PoiCatalogue`.

## Task 3 - Contract and regression
- Check the OpenAPI tests still pass and the new route appears.
- Verify: full `dotnet test TripMate.slnx --filter "Category!=SqlServer&Category!=Redis"`
  and `dotnet format --verify-no-changes` on the changed files.

Definition of done: all tests green, no changes outside the files above, no commit or push until
the developer asks.
