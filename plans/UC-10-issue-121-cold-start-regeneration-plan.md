# UC-10 issue #121: cold-start WeightedUtility regeneration

## Scope

Change only the regeneration score normalization for WeightedUtility candidates, with and
without a preference signal.
Preserve the WeightedUtility objective, tier ordering, and Lexicographic behavior.

## Steps

1. Add a failing `ItineraryVersionServiceTests` case with no traveler profile, one previous
   60-minute POI, one unseen 60-minute POI, and 480 available minutes. Require the unseen POI
   first and both POIs in the resulting itinerary.
2. Add reviewer regression cases with a matching `nature` or `culture` preference, proving that
   the previous POI can fill the schedule in both cases.
3. Document the neutral regeneration score and its travel-cost calibration in the UC-10 spec and
   ADR-001. Map all regeneration tiers that need normalization to `[0.5, 1]`.
4. Run the regression, Lexicographic, WeightedUtility golden, benchmark, and Application unit
   tests. Verify formatting and discard generated timing/snapshot changes.

## Verification

`dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --no-restore`

`dotnet format TripMate.slnx --verify-no-changes --no-restore --include src/TripMate.Application/Features/Itineraries/Common/ItineraryVersionService.cs tests/TripMate.Application.UnitTests/Features/Itineraries/ItineraryVersionServiceTests.cs`
