# UC-10 — ExceedsEndTime diagnosis plan

Spec: `specs/UC-10-exceeds-end-time-diagnosis-spec.md`. Branch `fix/phuctv-exceeds-end-time-diagnosis`.

Fixture (zero buffers, 10-minute legs, deadline 240 min): museum 120, pagoda 90, restaurant (FoodAndDrink) 60.
Raw 310, compressed once 243 (museum 90, pagoda 68, restaurant 45), compressed twice 194.

1. **Red** — `ItineraryVersionServiceTests`:
   - `Suggestions_Food_RestaurantNeedingAnExtension_ReportsExceedsEndTimeWithTripOverflow` → reason null today.
   - `CreateAdjustedVersion_WhenCompressionIsNotEnough_ExtendsByExactlyTheOverflow` → ConstraintsInfeasible today.
   - `CreateAdjustedVersion_WhenNoExtensionWithinTheMaximumFits_IsRejected` (guard).
2. **Green** — `ItineraryVersionService.EvaluateStopsAsync`: diagnose the uncompressed `input`; for `ExceedsEndTime`
   set `OverflowMinutes` from the fully compressed order scheduled with `MaxAvailableMinutes` (null if it fails).
3. **Green** — `CreateAdjustedVersionAsync`: Tier 2 override = `request.AvailableMinutes + OverflowMinutes`;
   reject when `OverflowMinutes` is null.
4. Verify: `dotnet build`, Application unit tests, CI `dotnet format --verify-no-changes` on changed files.
