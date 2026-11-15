# UC-10 rest stop selection (fix)

- **Status:** Approved by the developer on 2026-11-11.
- **Branch:** `fix/phuctv-uc10-rest-stop-selection`, based on the UC-10 weighted personalization branch at `f22c7aa8`.

## Problem

Local E2E on a copy of the demo database (real Mapbox) gave both itineraries a 30-minute rest stop at Am Phu Cave (Động Âm Phủ). The cave is a 16–24 minute detour.

`ItineraryScheduleEvaluator.IsRestCategory` treated two kinds of POI as rest places:

- food and drink, cafe, restaurant, rest area or rest stop;
- a sheltered beach, natural attraction, park, theme park or water park.

The same predicate (`IsQualifiedRestCandidate`) has two effects:

1. **Rest stop choice.** Food and drink POIs are not in the candidate pool. So the nearest sheltered attraction became the rest stop, with no detour cap.
2. **Visit eligibility.** `OptionalRouteOptimizer`, `RoutingContext` and the weighted inputs exclude rest-qualified POIs from the visits. Ba Na Hills, Am Phu Cave, Than Tai Hot Spring Park, VinWonders, Mikazuki Water Park, Hoa Phu Thanh and Suoi Luong (local data) could never be scheduled as visits.

## Decisions (developer, 2026-11-11)

1. Rest places are food and drink, cafe, coffee shop, restaurant, rest area and rest stop only. Shelter no longer matters. Beaches, natural attractions, parks and theme or water parks are places to visit.
2. A rest stop may add at most **10 minutes** over going straight to the next stop. The detour is previous → rest → next minus previous → next, in matrix minutes.
3. If no rest place qualifies, the plan gets the existing free rest item: no POI, reason `Free/rest time`.

## Behavior

- `IsRestCategory(category)` returns true only for the rest place categories.
- `FindQualifiedRestCandidate` and the invariant validator's `FindExpectedRestCandidate` drop candidates whose detour exceeds `MaxRestDetourMinutes` (10). Both share `RestDetourMinutes`.
- Both objective modes and the heuristic are affected. Plans can now include sheltered attractions as visits. The CSP, pace and budget still decide whether they fit.
- No API, database or client change: Web and Mobile already show free rest items.

## Out of scope (phase B)

- Adding nearby cafes as rest-only candidates, so that rest stops name a real place more often.

## Tests

- `RestStopSelectionTests`:
  - the category table;
  - no rest at a sheltered attraction, with both the heuristic and the CSP;
  - the attraction is scheduled as a visit, with both;
  - free rest beyond a 10-minute detour;
  - a cafe within the cap.
- `Generate_AutoRestNeverUsesAShelteredAttraction_AndStillRests` replaces the test that pinned the old behavior.
- Weighted gate corpus: 0 of 192 plans changed, because the corpus has no sheltered attractions. The solver-comparison report is regenerated for the source hash.

## Verification (2026-11-11)

- CI suite: Application 4715 passed. API integration 828 passed. Infrastructure 1124 passed; one unrelated email worker test passed on rerun, 3 out of 3.
- Solver-comparison report `reststop`: plans identical on 907 scenarios, and the gate passed.
- Weighted gate corpus: 0 of 192 plans changed.
- `dotnet format --verify-no-changes` on the changed files passes on Linux (the CI runner). Windows reports a CRLF-only difference.
- Local E2E (copy of the demo database, Moderate pace, 480 minutes, real Mapbox):

  | Traveler | Before | After |
  |---|---|---|
  | Culture | rest at Am Phu Cave (16-minute leg); 48 travel minutes in total | free rest with no detour; 15 travel minutes |
  | Nature and beach | rest at Am Phu Cave (24-minute leg); 59 travel minutes in total | free rest with no detour; 22 travel minutes |

## Rest reason wording (developer decision, 2026-11-11)

- **Problem:** Web and Mobile show a stop's `recommendationReason` as-is. Visit reasons are Vietnamese since G5 ("Hợp sở thích của bạn", "Gần lộ trình"), but a rest item still showed the solver's English code: "Free/rest time" or "Suggested rest stop".
- **Decision:** show the two rest reasons in Vietnamese: "Nghỉ tự do" and "Điểm nghỉ gợi ý".
- **Change:** `RestStopReasons.ForTraveler` translates the two codes where a plan leaves the solver: when it is saved (`GeneratedItineraryItemMapper`, used by create, regenerate and adjust) and in the fresh create response. The solver, the invariant validator and the explanation input keep the English codes, so no solver source and no golden file changes.
- **Not changed:** itineraries saved before this change keep their English text. After a manual adjust the *visit* reasons are still the solver's English codes ("Mandatory location", "Suggested nearby location"); G5 only rewrites visit reasons of generated plans. Mobile's own label "Suggested break" on the result page is a Mobile string.
- **Tests:** `RestStopReasonsTests` (translation, saved rest item, visit untouched) and `Handle_RestStop_ShowsItsReasonInVietnamese_InTheResponseAndTheSavedItinerary`.
