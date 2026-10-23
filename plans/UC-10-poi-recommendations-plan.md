# Implementation Plan: [UC-10] Pre-Generation Personalized POI Recommendations

| Item | Details |
| :--- | :--- |
| **Ticket ID** | UNASSIGNED |
| **Specification** | `specs/UC-10-poi-recommendations-spec.md` |
| **Scope** | UC-10 — Pre-Generation Personalized POI Recommendations |
| **Status** | FROZEN PLAN — BACKEND IMPLEMENTED AND AUDITED |

---

## Task Breakdown & Development Sequence

### Phase 1: Application Layer DTOs, Query & Validator

#### Task 1: Create Request, Query & Response DTOs
- **Files to Add:**
  - `src/TripMate.Application/Features/Personalization/Recommendations/GetPoiRecommendationsQuery.cs`
  - `src/TripMate.Application/Features/Personalization/Recommendations/PoiRecommendationResultDto.cs`
  - `src/TripMate.Application/Features/Personalization/Recommendations/RecommendedPoiItemDto.cs`
- **Details:**
  - Define `GetPoiRecommendationsQuery(long TravelerUserId, decimal ExplorationLatitude, decimal ExplorationLongitude, int SearchRadiusKm, int? Limit)` returning `Result<PoiRecommendationResultDto>`.
  - Define `PoiRecommendationResultDto(IReadOnlyCollection<RecommendedPoiItemDto> Items, int TotalAvailable)`.
  - Define `RecommendedPoiItemDto(long PoiId, string Name, string CategoryName, string? ThumbnailUrl, decimal DistanceKm, decimal? EstimatedVisitCost, decimal? AverageRating, string? RecommendationReason)`. `AverageRating` (`decimal?`) represents the public traveler review average rating from `social.Reviews` (`TargetType == Review.TargetTypePoi`), rounded to 1 decimal place, or `null` if no reviews exist for that POI. `ScenicScore` and `PhotoRating` remain internal ranking quality signals and are not exposed as user review ratings.

#### Task 2: Implement FluentValidation Rules
- **Files to Add:**
  - `src/TripMate.Application/Features/Personalization/Recommendations/GetPoiRecommendationsQueryValidator.cs`
- **Details:**
  - `ExplorationLatitude`: `InclusiveBetween(-90m, 90m)`
  - `ExplorationLongitude`: `InclusiveBetween(-180m, 180m)`
  - `SearchRadiusKm`: `InclusiveBetween(1, 50)`
  - `Limit`: `InclusiveBetween(1, 20)` when `HasValue`

---

### Phase 2: Application Query Handler Implementation

#### Task 3: Implement `GetPoiRecommendationsQueryHandler`
- **Files to Add:**
  - `src/TripMate.Application/Features/Personalization/Recommendations/GetPoiRecommendationsQueryHandler.cs`
- **Details:**
  - Inject `IApplicationDbContext`, `PersonalBehaviorFeatureAggregator`, and `IOptions<PersonalizationRankingOptions>` following repository DI conventions.
  - Instantiate `PersonalBehaviorAffinityScorer` (denominator-smoothed heuristic behavior affinity with `PriorWeight = 2.0`) and `PersonalizationBaseScorer` using injected options.
  - Read `TravelerProfile.InterestTagsJson` for `TravelerUserId` and parse preference tokens using `TravelerPreferenceScoring.ParsePreferenceTokens` (Query 1).
  - Query active, planning-ready POIs within bounding box and exact equirectangular distance $\le \text{searchRadiusKm}$ (Query 2). Recommender guarantees static eligibility only (`Active`, `planning-ready`, `radius`); final date/time schedule, duration, budget, transport, rest, and mandatory feasibility remain strictly authoritative to the deterministic scheduling engine during `POST /api/v1/scheduling-requests`.
  - Execute batch behavior aggregation using `_behaviorAggregator.AggregateAsync` (Query 3 & 4: at most 2 queries).
  - Score candidates in memory via `PersonalizationFeatureBuilder.Build` and `_baseScorer.Score`.
  - Apply deterministic 6-level tie-break sorting:
    1. `BaseScore DESC`
    2. `ScenicScore DESC` (NULL $\rightarrow \text{decimal.MinValue}$)
    3. `PhotoRating DESC` (NULL $\rightarrow \text{decimal.MinValue}$)
    4. `DistanceKm ASC`
    5. `EstimatedVisitCost ASC` (NULL $\rightarrow \text{decimal.MaxValue}$)
    6. `PoiId ASC`
  - Generate deterministic `recommendationReason` string based on specified feature precedence.
  - Fetch primary thumbnails and review average ratings via batched lookup on `catalog.POIPhotos` and `social.Reviews` for candidate POI IDs (Query 5). Worst-case normal non-empty execution is at most 5 batched read-only DB queries.
  - Return `Result.Success(new PoiRecommendationResultDto(items, totalAvailable))`.

---

### Phase 3: WebAPI Controller & OpenApi Mapping

#### Task 4: Create API Request Model & Controller Endpoint
- **Files to Add:**
  - `src/TripMate.Api/Controllers/V1/Requests/GetPoiRecommendationsApiRequest.cs`
  - `src/TripMate.Api/Controllers/V1/PoiRecommendationsController.cs`
- **Details:**
  - Controller attribute: `[Authorize(Roles = nameof(UserRole.Traveler))]`, `[Route("api/v1/poi-recommendations")]`.
  - Endpoint: `[HttpPost]` returning `200 OK` on success, `400 Bad Request` on validation failure, `401 Unauthorized`, `403 Forbidden`.

---

### Phase 4: Automated Testing Suite

#### Task 5: Unit Tests for Query & Handler
- **Files to Add:**
  - `tests/TripMate.Application.UnitTests/Features/Personalization/Recommendations/GetPoiRecommendationsQueryValidatorTests.cs`
  - `tests/TripMate.Application.UnitTests/Features/Personalization/Recommendations/GetPoiRecommendationsQueryHandlerTests.cs`
- **Test Scenarios:**
  - Cold-start traveler with zero history / preferences.
  - Category and tag preference ordering boosts.
  - Direct behavior feedback (`Like` boost, `Dislike` demotion, `Skip` weak demotion).
  - Category behavior fallback ($\ge 2$ interacted POIs).
  - Null quality score neutral handling ($0.5$).
  - Spatial radius filter enforcement.
  - Deterministic tie-breaking sequence.
  - Recommendation reason string generation.

#### Task 6: Integration Tests for WebAPI Endpoint
- **Files to Add:**
  - `tests/TripMate.Api.IntegrationTests/Personalization/PoiRecommendationsEndpointTests.cs`
  - `tests/TripMate.Api.IntegrationTests/Personalization/PoiRecommendationsSqlServerTests.cs`
- **Test Scenarios:**
  - Unauthenticated request returns `401`.
  - Non-traveler role returns `403`.
  - Validation failure returns `400`.
  - Zero eligible candidate POIs within radius returns `200 OK` with `{"totalAvailable": 0, "items": []}`.
  - Asserts zero DB writes occur during execution.

---

## Verification Plan

### Automated Verification
```bash
# Verify build clean
dotnet build

# Execute full test suite including personalization unit & integration tests
dotnet test --filter "FullyQualifiedName~Personalization|FullyQualifiedName~PoiRecommendations"
```
