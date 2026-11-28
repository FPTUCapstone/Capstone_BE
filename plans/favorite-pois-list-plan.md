# List Favorite POIs (Backend Plan)

## Task 1: Application Query, DTOs, Validator, and Unit Tests (TDD)
- **Files:**
  - `tests/TripMate.Application.UnitTests/Features/RecommendationFeedback/ListFavoritePoisQueryHandlerTests.cs` (write failing tests first)
  - `src/TripMate.Application/Features/RecommendationFeedback/Favorites/FavoritePoiItemDto.cs`
  - `src/TripMate.Application/Features/RecommendationFeedback/Favorites/PagedFavoritePoisResponse.cs`
  - `src/TripMate.Application/Features/RecommendationFeedback/Favorites/ListFavoritePoisQuery.cs`
  - `src/TripMate.Application/Features/RecommendationFeedback/Favorites/ListFavoritePoisQueryValidator.cs`
  - `src/TripMate.Application/Features/RecommendationFeedback/Favorites/ListFavoritePoisQueryHandler.cs`
- **Verify:** `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter "FullyQualifiedName~FavoritePoi"`

## Task 2: Controller Endpoint & Integration Tests (TDD)
- **Files:**
  - `tests/TripMate.Api.IntegrationTests/RecommendationFeedback/PoiFavoriteEndpointTests.cs` (add failing HTTP tests first for auth, role guard, pagination, validation, active-only, ordering, and non-interference with `/api/v1/pois/{id}`)
  - `src/TripMate.Api/Controllers/V1/PoiFavoritesController.cs` (add `[HttpGet("/api/v1/pois/favorites")]`)
- **Verify:** `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter "FullyQualifiedName~PoiFavoriteEndpointTests"`

## Task 3: Full Verification & Formatter Gate
- Build solution in Release (`dotnet build TripMate.slnx -c Release --no-restore`)
- Run unit tests & integration tests
- Run `dotnet format TripMate.slnx --verify-no-changes --no-restore --include <changed .cs files>`
