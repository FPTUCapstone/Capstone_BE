# UC-10 issue #117: category aliases, time limits, and POI traits

## Scope

- Treat all `PoiCategoryNames.IsFoodAndDrink` aliases consistently in selectable search, suggestions, planning eligibility, cost kind, and weighted objective inputs.
- Preserve POI `IndoorOutdoor` and tag names when create and itinerary-version flows build `GenerationCandidate` values.
- Make the sunrise refinement limit and early-start cutoff configurable.

## Time policy

- `TimeOfDay:MaxExtraMinutes` is an absolute limit on minutes added by refinement. Zero forbids any extension, including sunrise.
- Ordinary trips allow at most `min(MaxExtraMinutes, NormalExtraMinutes)` extra minutes.
- Trips starting no later than `EarlyTripCutoffMinutes` (local minutes after midnight) and containing a sunrise spot allow at most `min(MaxExtraMinutes, SunriseExtraMinutes)`.
- Defaults are 60, 15, 60, and 420 respectively. These preserve the prior 15-minute ordinary and 60-minute early-sunrise behavior when no override is supplied.
- The three minute limits must be 0..120. The cutoff must be 0..1439.

## Acceptance

- A `Cafe` POI is excluded from the mandatory picker, rejected as a mandatory food stop, offered as a food suggestion, excluded from nonfood suggestions, and reports average-spend cost kind.
- With `MaxExtraMinutes=0`, a longer sunrise order is rejected. Changing the early cutoff changes which trip gets the sunrise allowance.
- Indoor and sunrise-tagged POIs keep those traits in generated candidates.
- Existing weighted solver and scheduling tests pass; required benchmark report matches the changed source hash.
