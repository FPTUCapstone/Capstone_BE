# UC-10 issue #121: cold-start WeightedUtility regeneration

## Scope

Change only the regeneration score normalization for candidates with no preference signal.
Preserve the WeightedUtility objective, tier ordering, and Lexicographic behavior.

## Steps

1. Add a failing `ItineraryVersionServiceTests` case with no traveler profile, one previous
   60-minute POI, one unseen 60-minute POI, and 480 available minutes. Require the unseen POI
   first and both POIs in the resulting itinerary.
2. Document the neutral cold-start score and its travel-cost calibration in the UC-10 spec and
   ADR-001. Map cold-start regeneration tiers to `[0.5, 1]`; keep the prior `[0,1]` rule when a
   preference signal exists.
3. Run the regression, Lexicographic, WeightedUtility golden, benchmark, and Application unit
   tests. Verify formatting and discard generated timing/snapshot changes.

## Verification

`dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --no-restore`

`dotnet format TripMate.slnx --verify-no-changes --no-restore --include src/TripMate.Application/Features/Itineraries/Common/ItineraryVersionService.cs tests/TripMate.Application.UnitTests/Features/Itineraries/ItineraryVersionServiceTests.cs`
