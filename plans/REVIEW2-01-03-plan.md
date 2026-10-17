# REVIEW2 tickets 01–03 — implementation plan

- **Spec**: `specs/REVIEW2-spec.md` (approved 2026-10-16)
- **Branch**: `feature/review2-mentor-feedback`
- **Status**: APPROVED by developer 2026-10-16
- **Verify after every task**: `dotnet build` then `dotnet test` (unit + integration; integration needs
  `TRIPMATE_SQLSERVER_TEST_CONNECTION` / `TRIPMATE_REDIS_TEST_CONNECTION`, otherwise they skip).
- TDD per task: failing test first → minimal code → refactor.

## Ticket 01 — cost kind + POI detail price (D1)

| Task | Change | Files | Test (red first) |
|---|---|---|---|
| 01.1 | `CostKind` enum (`AdmissionTicket`, `AverageSpend`); `PoiCategoryNames.FoodAndDrink = "Food & Drink"`; `CostKindPolicy.FromCategory(string?)` | `Domain/Enums/CostKind.cs`, `Domain/Constants/PoiCategoryNames.cs`, `Application/Common/Pricing/CostKindPolicy.cs` | `CostKindPolicyTests`: food → AverageSpend; other / null → AdmissionTicket; case-insensitive |
| 01.2 | `ItineraryDetailItemDto.CostKind`; mapped in `GetItineraryDetailQueryHandler.ToResponse` | `Itineraries/Common/ItineraryDetailResponse.cs`, `GetDetail/GetItineraryDetailQueryHandler.cs` | detail handler test asserts costKind per item |
| 01.3 | `SchedulingItemDto.CostKind`; mapped where scheduling responses are built (category from loaded POIs) | `Scheduling/Common/SchedulingResponseDto.cs`, `Create/CreateSchedulingRequestCommandHandler.cs` | create-handler test asserts costKind |
| 01.4 | `PoiDetailDto.EstimatedVisitCost` + `CostKind` | `PointsOfInterest/Detail/PoiDetailDto.cs`, `GetPoiDetailQueryHandler.cs` | detail test: cost null / 0 / 60000 returned as-is |
| 01.5 | Recommendations radius uses `SchedulingRequest.MinSearchRadiusKm/MaxSearchRadiusKm` | `Personalization/Recommendations/GetPoiRecommendationsQueryValidator.cs` | validator: 80 km valid, 101 km invalid |

DoD: the three DTOs expose `costKind`; POI detail exposes the price; existing tests green.

## Ticket 03 — leg distance, per-leg mode, start/end location (D3, §3.1–3.2)

| Task | Change | Files | Test (red first) |
|---|---|---|---|
| 03.1 | `RouteDurationMatrix` gains optional metres: `Create(minutes, unreachable, meters)` + `GetMeters(i, j): int?`; existing `Create(minutes)` overloads unchanged | `Scheduling/Common/RouteDuration.cs` | matrix tests: metres returned; null when not supplied; size mismatch throws |
| 03.2 | ORS requests `["duration","distance"]`, parses `distances` (rounded metres, unreachable → null) | `Infrastructure/Routing/OpenRouteServiceRouteDurationProvider.cs` | provider test with fake handler: request body contains both metrics; metres parsed |
| 03.3 | `GeneratedItineraryItem.TravelDistanceToNextMeters`, `TransportModeToNext`; both leg-filling helpers (`ItineraryScheduleEvaluator`, `OptionalRouteOptimizer`) fill metres; invariant validator checks metres match the matrix | `Scheduling/Common/GenerationInput.cs`, `ItineraryScheduleEvaluator.cs`, `OptionalRouteOptimizer.cs`, `GeneratedItineraryInvariantValidator.cs` | generation tests: last item null, others = matrix metres |
| 03.4 | DB: `planning.ItineraryItems.travel_distance_to_next_meters INT NULL` (idempotent script) + schema v7; entity `TravelDistanceToNextMeters`, `TransportModeToNext` (maps existing `transport_mode_to_next`); `ItineraryItem.Create` optional params + guards (metres ≥ 0) | `database/migrations/20261016_add_itinerary_item_travel_distance.sql`, `database/tripmate_schema_v7.sql`, `Domain/Entities/ItineraryItem.cs`, `Infrastructure/Persistence/Configurations/ItineraryItemConfiguration.cs` | domain tests for guards; EF mapping covered by integration tests |
| 03.5 | Persist metres + mode on every write path: create, regenerate, adjust (`ItineraryVersionService` ×2, `CreateSchedulingRequestCommandHandler`) — mode = request transport mode for now (per-leg in ticket 05) | those files | integration: generated items have metres + mode |
| 03.6 | Responses: detail item `TravelDistanceFromPreviousMeters`, `TransportModeFromPrevious`, `Address`; detail response `TransportMode`, `StartLocation`, `EndLocation` (`ToResponse` takes the scheduling request; update its 4 callers); `SchedulingItemDto` `TravelDistanceToNextMeters`, `TransportModeToNext` | `ItineraryDetailResponse.cs`, `GetItineraryDetailQueryHandler.cs`, Accept / Adjust / Regenerate handlers, `SchedulingResponseDto.cs`, create handler | handler tests: fields mapped; old item (null metres) → null |

DoD: a new itinerary returns metres per leg + mode + start/end location; old itineraries return null
metres without errors; migration rerunnable.

## Ticket 02 — Food & Drink, restaurant seed, exclusions (D2)

| Task | Change | Files | Test (red first) |
|---|---|---|---|
| 02.1 | Migration: category `Food & Drink` (idempotent insert), `catalog.POIs.tripadvisor_location_id BIGINT NULL`; entity + EF mapping | `database/migrations/20261016_add_food_category_and_tripadvisor_id.sql`, schema v7, `PointOfInterest.cs`, POI configuration | mapping via integration tests |
| 02.2 | Generation candidate pool excludes `Food & Drink` (create + regenerate + fixed-order loads) | `ItineraryVersionService.cs` (`LoadRelevantActivePoisAsync`), create handler candidate load | generation test: food POI in radius never scheduled |
| 02.3 | Create validation rejects a food POI as mandatory → `planning.mandatory_food_not_allowed` (422); selectable-POI search gains `excludeFoodAndDrink` flag (const on query) | `Create/...CommandHandler.cs` or validator, `SearchSelectablePoisQuery*.cs`, controller | validator/handler test; search test |
| 02.4 | Seed `database/seeds/20261016_seed_danang_restaurants.sql` + `..._verify_...sql`: 10–15 restaurants (name, coords, address, opening hours, average spend, source URL, `tripadvisor_location_id` matched once via Terra search and checked by a human); beaches → planning hours 04:30; tags "Ngắm bình minh", "Chèo SUP" | seeds | verify script asserts counts, hours, ids |

DoD: food POIs exist with prices/ids; never auto-scheduled; not accepted as mandatory.
Note: 02.4 spends ≈ 15–20 Tripadvisor calls (one search per restaurant / demo attraction).

## Order

01.1 → 01.5 → 03.1 → 03.6 → 02.1 → 02.4. Tickets 04–09 get their own plan after this one is approved.
