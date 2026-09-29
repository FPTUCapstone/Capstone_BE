# Traveler Reality Check: UC-10 Itinerary Generation

## Decision Context

This review evaluates UC-10 as a one-day, self-guided trip-planning experience
for a Traveler using a mobile application in Vietnam. It tests whether the
proposed feature produces a plan that is useful outside a demo, rather than
merely a technically valid POI sequence.

## Evidence Summary

Tourist itinerary planning is commonly modeled as a constrained route-design
problem: the selected POIs and their order must respect time, budget, opening
hours, start/end locations, travel duration, transport mode, and user
preferences.[^1] A recent systematic review specifically identifies opening
time windows, start/end locations, mandatory visits, transport mode, and
budget as practical extensions of the problem.[^1]

The route data must match the transport mode and departure context. Route
providers support travel mode, departure time, waypoint ordering, travel-time
matrices, and route-duration limits because straight-line distance alone is not
an adequate customer-facing travel-time estimate.[^2][^3] Google also documents
that routing output may depend on platform and mode, which reinforces the need
to label uncertain estimates honestly.[^2]

Research on personalized tourist recommendations includes start/end points,
opening hours, planned visit time, budget, personal interests, rest/meal
considerations, and time cost as factors affecting satisfaction.[^4] The
important product implication is not that UC-10 needs every advanced factor in
its first release; it is that the MVP must distinguish non-negotiable facts
from preferences and uncertainty.

## Traveler Expectations

| Traveler question | Product requirement |
| --- | --- |
| “Can I actually finish this before returning to my hotel?” | Separate exploration area from end location; support return-to-start. Treat the end point and total available time as hard constraints. |
| “Will I arrive while the place is open?” | Require a date/time and time zone. Use verified opening hours as a hard feasibility rule. |
| “Is the travel time believable for my motorbike/walking trip?” | Use road-network route durations for the final selected route. Haversine may prefilter candidates only. |
| “Will I stay within my budget?” | Define budget as per-person POI-visit cost only; show what it excludes and never treat unknown cost as free. |
| “Why did the app select these places?” | Show a short reason for every optional stop and identify mandatory stops. |
| “The plan is too ambitious. What should I change?” | Return a specific infeasibility reason and retain all entered values for adjustment. |
| “I do not want to lose a good plan.” | Persist a successful result as a Draft and show that it is saved. |

## Required Changes to the Current UC-10 Specification

### 1. Road-network routing is a release requirement

The current specification proposes Haversine plus a configured speed as the
initial `IRouteDurationProvider`. That is acceptable only for internal
candidate filtering. It is not sufficient for the final schedule: bridges,
one-way streets, rivers, and transport restrictions make straight-line time
materially misleading.

**Decision:** introduce `IRouteDurationProvider`; production/MVP itinerary
items must receive their final travel durations from a configured road-network
provider. Google Routes is one possible provider because it supports route
matrices, modes, departure context, and waypoint ordering.[^2][^3] The provider
key/configuration remains outside source control. If no route provider is
configured, return MSG127 rather than claim that a Haversine schedule is
accurate.

### 2. Build slack into the plan

An algorithm that fills every available minute creates a fragile and stressful
day. UC-10 should reserve a configurable buffer: a short transition buffer per
leg and a final return buffer before the requested end time. The result should
show both scheduled time and unallocated free/buffer time.

The Traveler's saved pace is a soft preference:

- `Relaxed`: fewer optional POIs and more free time.
- `Moderate`: default balance.
- `Fast`: permits more optional POIs while retaining mandatory buffers.

No pace may override mandatory POIs, opening hours, end point, or budget.

### 3. Make POI data quality visible and enforceable

The generator cannot truthfully promise a plan unless its planning candidates
have coordinates, visit duration, verified opening hours, and a known visit
cost when a budget is supplied. Maintain a planning-ready POI dataset before
demo/release; an initial curated set of active Da Nang POIs is sufficient.

- `estimated_visit_cost = 0` means verified free.
- `estimated_visit_cost = null` means unknown, not free.
- Missing opening hours means the POI is not planning-ready; it is not
  auto-selected and cannot be a mandatory stop.
- The catalog should record when operational details were last verified in a
  later catalog-data task. Until then, the UI must use “estimated” and avoid
  claims of real-time accuracy.

### 4. Keep the form progressive, not bureaucratic

The initial form should expose only: date/time, start, exploration area,
return/end choice, duration, and transport mode. Budget, mandatory POIs, and
pace belong in an **Advanced options** section with sensible profile-derived
defaults. A user who denies location permission must still choose a start POI.

The app needs a Traveler-facing POI search/read endpoint; requiring numeric
coordinates would make the core feature unusable.

### 5. Treat infeasibility as guidance, not an error

`422 planning.constraints_infeasible` should contain a safe, actionable reason
code which Mobile maps to English user copy. Examples:

- `mandatory_pois_do_not_fit_time`
- `mandatory_poi_closed_at_planned_time`
- `mandatory_poi_cost_unknown_for_budget`
- `budget_below_mandatory_poi_cost`
- `end_location_cannot_be_reached_in_time`

The API must not expose solver internals. Mobile keeps form input and offers a
single recommended change such as removing an optional mandatory POI, extending
time, increasing budget, or selecting a closer end location.

### 6. Explain the output and its boundaries

The result screen must show:

- ordered stops and planned arrival/departure;
- travel time between stops, total visit/travel/buffer time, and free time;
- total estimated POI cost and excluded cost categories;
- mandatory-stop marker;
- short recommendation reason for optional POIs; and
- “Saved as draft” confirmation.

UC-13 remains responsible for live navigation. Do not promise traffic, weather,
or turn-by-turn guidance in UC-10. A future integration may open the next stop
in a navigation app; Google Maps URLs support directions with origin,
destination, and waypoints, though platform waypoint limits mean this should
not be represented as complete in-app navigation.[^5]

## Development-Stage Decisions: No Paid Map, Curated Data, and Rest Breaks

### Development routing provider

Use **openrouteservice (ORS) Directions/Matrix V2** behind
`IRouteDurationProvider` for development and the capstone demo. ORS supports
road-network directions and matrices through a server-side API key.[^6] Its
public API has restrictions, so the generator must prefilter candidates, make
one bounded matrix request per operation, and cache that result for the
operation.[^7] The key belongs in local secrets or environment configuration,
never Mobile source or Git.

This means no map-provider purchase is needed now. Haversine is used only to
discard distant candidates before calling ORS; it must never become a displayed
travel duration. If ORS is unavailable, return MSG127 rather than fabricate an
accurate-looking schedule. Public OSRM demo servers are not an application
dependency. `Motorbike` uses a road-driving estimate in this MVP and must be
labeled accordingly, not presented as motorbike-specific navigation.

### Seed-data policy

Create a curated set of about 15-20 planning-ready Da Nang POIs rather than a
bulk scrape. Coordinates and basic categories may come from OpenStreetMap, with
the required visible attribution and ODbL compliance.[^8] Do not copy Google
Maps/Places descriptions, photos, or reviews into the seed data.

Every auto-selectable seed POI needs coordinates, category, realistic visit
duration, known opening hours, estimated visit cost, a `sourceUrl`, and a
`verifiedAtUtc` timestamp. Opening hours and price must come from the official
venue or Da Nang Tourism information; the city tourism portal supplies reference
hours/prices but says they may change, so these facts remain estimates and are
periodically re-checked.[^9]

### Rest-stop policy

Rest time is a relevant itinerary-planning factor alongside opening hours,
visit duration, route conditions, date/time, and weather.[^10] Add a required
`restPreference`: `Auto` (default), `None`, or `Frequent`.

- `Auto`: on trips of at least five hours, target one 30-45 minute break after
  roughly 2.5-3 hours of continuous travel plus visiting.
- `Frequent`: target a break at most every two hours.
- `None`: do not recommend a named venue, but retain the schedule safety
  buffer.

A break is an `ItineraryItem` with `itemKind = Rest`, not an attraction. Prefer
a planning-ready cafe, restaurant, or sheltered rest area near the route and
inside its opening hours. If none exists, reserve the time as “Free/rest time”
instead of inventing a venue. Food/drink is excluded from the current POI-visit
budget unless a verified cost is modeled later.

## Final MVP Boundary

### Must ship with UC-10

1. Date/time, start, exploration centre, end/return, duration, transport,
   rest preference, and hard mandatory-POI feasibility.
2. ORS-backed road-network duration provider for final itinerary times.
3. Verified planning-ready POI data: location, opening hours, duration, cost
   policy, source, and verification timestamp.
4. Budget semantics, time/rest buffer, idempotent persistence, and a Draft
   result.
5. Clear loading, infeasible, system-failure, and GPS-permission fallback
   experiences.

### Deliberately defer

- Traffic-aware re-planning, weather, crowds, public-transit timetables, meal
  reservations, accessibility routing, multi-day planning, maps, and live
  navigation.
- Multiple alternative itineraries and one-tap stop replacement.
- Group preference aggregation; that belongs after the group lifecycle is
  complete.

## Go/No-Go Checklist Before Implementation

- [ ] Product owner accepts openrouteservice as the development/demo routing
      provider and supplies a server-side API key through local secrets/env.
- [ ] A curated set of planning-ready POIs exists for the intended demo area.
- [ ] POI visit-cost entry is approved for the Admin catalog workflow.
- [ ] The returned result explicitly calls all durations/costs estimates and
      labels a driving-profile motorbike estimate honestly.
- [ ] The rest-break policy and its budget boundary are accepted.
- [ ] The UI keeps input after infeasibility and explains the next useful
      adjustment.
- [ ] UC-10 remains a one-day itinerary generator; map/navigation and group
      planning remain separate UCs.

## Recommendation

Proceed with UC-10 only after treating road-network duration and planning-ready
POI data as first-class release dependencies. A polished form without these
two foundations will create plans that look persuasive but fail the Traveler at
the moment they try to follow them.

## Sources

[^1]: Ruiz-Meza, J. et al. “[A systematic literature review for the tourist trip design problem: Extensions, solution techniques and future research lines](https://doi.org/10.1016/j.orp.2022.100228).” *Operations Research Perspectives*, 2022.

[^2]: Google Maps Platform. “[Routes API](https://developers.google.com/maps/documentation/routes).” Accessed September 2026.

[^3]: Google Maps Platform. “[Route Optimization API parameter list](https://developers.google.com/maps/documentation/route-optimization/parameter-list).” Accessed September 2026.

[^4]: Zhang, Y. et al. “[Personalized Tour Itinerary Recommendation Algorithm Based on Tourist Comprehensive Satisfaction](https://www.mdpi.com/2076-3417/14/12/5195).” *Applied Sciences*, 2024.

[^5]: Google Maps Platform. “[Maps URLs: Directions](https://developers.google.com/maps/documentation/urls/get-started).” Accessed September 2026.

[^6]: openrouteservice. “[API interactive examples](https://openrouteservice.org/dev/).” Accessed September 2026.

[^7]: openrouteservice. “[API Restrictions](https://openrouteservice.org/restrictions/).” Accessed September 2026.

[^8]: OpenStreetMap Foundation. “[Licence and Legal FAQ](https://osmfoundation.org/wiki/Licence_and_Legal_FAQ).” Accessed September 2026.

[^9]: Da Nang Tourism Information Portal. “[Updated admission prices for attractions and tourist sites](https://danangfantasticity.com/en/news/plan-your-da-nang-journey-2026-updated-admission-prices-for-attractions-and-tourist-sites).” January 2026.

[^10]: Kwangsawad, T. and M. Chaiwuttisak. “[Time-related factors influencing on an itinerary planning system](https://www.sciencedirect.com/science/article/pii/S1757988016000386).” *Journal of Hospitality and Tourism Technology*, 2016.
