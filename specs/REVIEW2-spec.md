# REVIEW2 — Mentor feedback remediation (backend spec)

- **Status**: APPROVED by developer 2026-10-16 (cross-repo spec approved the same day).
- **Branch**: `feature/review2-mentor-feedback` from `develop` (caf75c8).
- **Deadline**: Review 2 on 2026-10-18. Unfinished parts ship behind configuration flags.
- **Clients**: Capstone_FE (web) and Capstone_Mobile build against the contract in §3.

## 1. Goal

A traveler gets a feasible itinerary that shows distance, time and transport per leg and honest
prices; can add a restaurant (Tripadvisor rating/reviews), replace / remove any stop and change a
leg's transport while the backend keeps the itinerary feasible.

## 2. Backend decisions

| # | Decision |
|---|---|
| D1 | `costKind` derived from category: `AverageSpend` for `Food & Drink`, otherwise `AdmissionTicket`. Returned on itinerary items, scheduling items and POI detail. `PoiDetailDto` also returns `estimatedVisitCost`. `GetPoiRecommendationsQueryValidator` uses `SchedulingRequest.MinSearchRadiusKm/MaxSearchRadiusKm` (100 km). |
| D2 | New category `Food & Drink`. Seed 10–15 real Da Nang / Hội An restaurants. Generation never auto-schedules `Food & Drink`; it cannot be a mandatory POI (search can exclude it; create validation rejects it). `catalog.POIs.tripadvisor_location_id BIGINT NULL`. Beach planning hours start 04:30; tags "Ngắm bình minh", "Chèo SUP". |
| D3 | ORS matrix requests `duration` + `distance`. Distance persisted per leg (`planning.ItineraryItems.travel_distance_to_next_meters`). Existing column `transport_mode_to_next` mapped. Motorbike = `driving-car` distance, duration × `Routing.MotorbikeDurationFactor` (default 0.9, admin-editable). Old itineraries return `null` distance. |
| D4 | `PUT /itineraries/{id}/items` accepts `stops[{poiId, transportModeToNext?}]` (legacy `orderedVisitPoiIds` kept): add / replace / remove / reorder / per-leg mode. New POIs validated like generation candidates. Infeasible → 422 `itinerary.stop_infeasible` naming the stop. Removing a mandatory POI removes it from `MandatoryPoiIdsJson`. |
| D6 | `GET /itineraries/{id}/suggestions` (Food / Replace), max 5, detour from one ORS call (straight-line fallback), dry-run fit check (`fitsItinerary`, `unfitReason`). Tripadvisor (Terra) ratings/reviews behind `Tripadvisor__Enabled`; review translation via Gemini behind `ReviewTranslation__Enabled`. |
| D10 | (P1) Time-of-day soft rules: 04:30–06:30 beach/sunrise bonus, 06:00–09:30 outdoor/nature bonus, 11:00–14:00 outdoor-without-shelter penalty, 16:30–18:00 beach/scenic bonus; admin-tunable. |

## 3. Contract

### 3.1 `GET /api/v1/itineraries/{id}` (`ItineraryDetailResponse`, also returned by accept / regenerate / adjust)
- `+ transportMode` — trip default (`Walking|Motorbike|Car|PublicTransit`).
- `+ startLocation: { latitude, longitude, address }` — `address` is `null` (not stored today).
- `+ endLocation: { latitude, longitude, address }` — end POI, or the start location when `returnToStart`.
- items: `+ travelDistanceFromPreviousMeters: int?`, `+ transportModeFromPrevious: string?`,
  `+ costKind: "AdmissionTicket"|"AverageSpend"`, `+ address: string?`.

### 3.2 `POST /api/v1/scheduling-requests` items (`SchedulingItemDto`)
- `+ travelDistanceToNextMeters: int?`, `+ transportModeToNext: string?`, `+ costKind`.

### 3.3 `GET /api/v1/points-of-interest/{id}` (`PoiDetailDto`)
- `+ estimatedVisitCost: decimal?`, `+ costKind`.

### 3.4–3.6
`PUT /items` stops, suggestions and external-reviews contracts: see the cross-repo spec
(`.scratch/review2-mentor-feedback/spec.md` §3.4–3.6, copied into the plan of tickets 05–09).

## 4. Data & configuration

- Database-first SQL scripts under `database/migrations/` (idempotent) + `database/tripmate_schema_v7.sql`.
- Seeds under `database/seeds/` with a verify script.
- `.env`: `Tripadvisor__ApiKey` (local only, never committed), `Tripadvisor__BaseUrl`,
  `Tripadvisor__Enabled` (false by default), `Tripadvisor__CacheMinutes`, `Tripadvisor__DailyCallBudget`,
  `ReviewTranslation__Enabled`.
- Tests never call ORS, Tripadvisor or Gemini.

## 5. Error codes (new)

- `itinerary.stop_infeasible` (422) — ticket 05.
- `planning.mandatory_food_not_allowed` (422, same status as `planning.constraints_infeasible`; stored on the failed request and replayed) — ticket 02.

## 6. Acceptance

`dotnet build` and `dotnet test` green (integration tests with SQL Server + Redis when available);
every new field and endpoint covered by tests; migrations rerunnable.
