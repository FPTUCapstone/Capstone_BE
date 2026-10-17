# REVIEW2 tickets 04–09 — implementation plan

- **Spec**: `specs/REVIEW2-spec.md` (approved 2026-10-16); cross-repo contract `.scratch/review2-mentor-feedback/spec.md` §3.4–3.6
- **Branch**: `feature/review2-mentor-feedback` (after tickets 01–03)
- **Status**: implemented 2026-10-16 (developer asked to implement from the agreed decisions); reviewed, see Process record
- **Verify after every task**: `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` on changed files;
  regenerate the solver benchmark (`--label=review2`) whenever `Scheduling/Common|Csp|Routing` changes.

## Ticket 04 — motorbike duration factor (D3)

Finding: admin UC-57 manages four `SystemConfigs` keys, but the scheduler reads none of them.

**Decision (2026-10-16): A** — options value, not the admin screen.
- **A (recommended, 0.25 d)**: `SchedulingGenerationOptions.MotorbikeDurationFactor` (appsettings / `.env`,
  default 0.9, validated 0.5–1.5). Changed by the team, not from the admin screen.
- **B (≈1 d + web admin UI)**: fifth UC-57 key `Routing.MotorbikeDurationFactor` in `dbo.SystemConfigs`, admin DTO /
  command / validator, web admin form field, and the scheduler reads it per request.

| Task | Change | Test (red first) |
|---|---|---|
| 04.1 | Decorator `MotorbikeAwareRouteDurationProvider` (Application, `Scheduling/Routing`): for `Motorbike` multiplies minutes by the factor (ceiling), keeps metres; other modes untouched. Registered around ORS. | factor applied only to motorbike; metres unchanged; factor 1.0 = identity |
| 04.2 | Factor source per decision A or B | options validation / admin tests |

## Ticket 05 — `PUT /itineraries/{id}/items` with stops (D4, §3.4)

| Task | Change | Test (red first) |
|---|---|---|
| 05.1 | Request `{ stops: [{ poiId, transportModeToNext? }] }` or legacy `{ orderedVisitPoiIds }` (exactly one); command carries `IReadOnlyList<AdjustedStop>`; validator: 1–20 stops, distinct, modes in Walking/Motorbike/Car; idempotency hash includes modes | validator + hash tests; legacy body still works |
| 05.2 | Accept POIs outside the current version: must be `Active`, planning-ready, inside the request radius (same rules as `RelevantPoiQuery`) → else 422 `itinerary.stop_infeasible` reason `Inactive` / `OutsideRadius` / `NoOpeningHours` | handler tests |
| 05.3 | Per-leg matrices: one ORS call per distinct mode (≤3); `PerLegRouteMatrix.Compose(defaultMatrix, legMatrices, orderedStops)` takes each consecutive pair from its leg's matrix, everything else from the trip mode; items get `TransportModeToNext` = requested leg mode | compose tests; adjusted version persists per-leg mode + metres |
| 05.4 | Infeasibility diagnosis: `FixedOrderDiagnosis.Explain(...)` replays the fixed order and returns the first failing stop + reason (`ClosedAtArrival`, `ExceedsEndTime`, `ExceedsBudget`) with `plannedArrival` / `closesAt`; returned as 422 `itinerary.stop_infeasible` with `data` | one test per reason |
| 05.5 | API: controller maps both bodies; problem details carry `data`; OpenAPI updated | integration/OpenAPI tests |

Note: Regenerate rebuilds from the request, so a restaurant the traveler added is not kept by "Tạo lại". The
client warns before regenerating when the itinerary has a restaurant.

## Ticket 06 — removing a mandatory POI updates the request (D4)

| Task | Change | Test (red first) |
|---|---|---|
| 06.1 | `SchedulingRequest.ReplaceMandatoryPoiIds(...)` (domain guard: subset of the previous list); adjust handler drops removed mandatory ids before generating; fixed-order generation no longer rejects a removed mandatory id that is also gone from the request | domain + handler tests; regenerate afterwards does not restore it |

## Ticket 07 — Tripadvisor (Terra) client (D6, §3.6)

| Task | Change | Test (red first) |
|---|---|---|
| 07.1 | `TripadvisorOptions` (`Enabled`=false, `BaseUrl`, `ApiKey`, `CacheMinutes`=60, `DailyCallBudget`=40); typed `HttpClient`, header `X-API-Key`, never logs the key | options binding/validation |
| 07.2 | `ITripadvisorClient`: `GetRatingsAsync(ids)` via `GET /locations?ids=` (one call for many), `GetReviewsAsync(id, size)`; `IMemoryCache` + in-process daily budget (UTC day); disabled/budget exhausted/HTTP error → empty result, never throws to callers | fake-handler tests incl. budget and cache hit |
| 07.3 | `GET /api/v1/points-of-interest/{id}/external-reviews?size=5` (authenticated travelers only, so anonymous traffic cannot burn the quota) | handler + endpoint tests |

## Ticket 08 — suggestions with dry-run fit check (D6, D11-2, §3.5)

| Task | Change | Test (red first) |
|---|---|---|
| 08.1 | `GET /api/v1/itineraries/{id}/suggestions?kind=Food&afterItemId=` / `?kind=Replace&replaceItemId=`; candidates: not in itinerary, active, planning-ready, in radius; Food = `Food & Drink`, Replace = same category | query/validator tests |
| 08.2 | One ORS matrix for current stops + up to 10 nearest candidates; detour minutes/metres; straight-line fallback (`distanceSource`) when ORS fails | detour + fallback tests |
| 08.3 | Dry-run each candidate through 05.4 (no save) → `fitsItinerary`, `unfitReason`, `overflowMinutes`, `overflowVnd`, `estimatedArrival`, `openAtArrival`, `closesAt`; sort fit desc, detour asc, Tripadvisor rating desc; max 5 | ordering/fit tests |
| 08.4 | Attach Tripadvisor rating via 07.2 (one batched call) | rating present/absent tests |

## Ticket 09 — review translation (D6)

**Decision (2026-10-16)**: reuse the Gemini (`gemini-3.5-flash-lite`) configuration of AI ranking. Finding: the local
`.env` `AiRanking__ApiKey` is the TripMate AI service key, not a Gemini key, so `ReviewTranslation__ApiKey` must be
filled with a Gemini key; until then the flag stays off and tests use a fake.

| Task | Change | Test (red first) |
|---|---|---|
| 09.1 | `IReviewTranslator` (Gemini HTTP, strict prompt "translate only"), `ReviewTranslation__Enabled` flag; cache per review id + target | fake-handler tests |
| 09.2 | `GET /api/v1/points-of-interest/{id}/external-reviews/{reviewId}/translation?target=vi` → `{ title, text, machineTranslated: true }`; 503 when disabled | endpoint tests |

**Decision (2026-10-16)**: external reviews require an authenticated traveler.

## Order

04 → 05 → 06 → 07 → 08 → 09, benchmark regenerated after 05 (and 08 if it touches `Scheduling/Common`).

## Process record (2026-10-16)

- Parts of tickets 05–09 were first written before their tests. Redone per AGENTS.md §1.2.4: implementation
  removed → build/tests RED; restored; then 21 behavioural mutations were applied one at a time. 18 were caught;
  the 3 survivors (matrix cap for chosen stops, Gemini non-completed status, CanManage check) got new tests that
  fail on the mutation and pass on the real code. A metadata-passthrough mutation is caught by a SQL test.
- Two-axis review (Standards + Spec, parallel sub-agents) against caf75c8. Dispositions:

| Finding | Severity | Disposition |
|---|---|---|
| Replace suggestions for a mandatory stop always unfit | High | Fixed: each variant keeps only the mandatory stops it contains (test first) |
| Suggestion openAtArrival/arrival taken from a later failing stop | High | Fixed: `unfitPoiId` added, candidate fields only from its own infeasibility (test first) |
| General failure reported as ExceedsEndTime | Medium | Fixed: reason left null |
| Translation only searched the first 5 reviews; per-size cache | Medium | Fixed: one cached page of 10 per location, sliced by size (tests first) |
| Food suggestion on a 20-stop itinerary could not be saved | Medium | Fixed: no food suggestions when full (test first) |
| Duplicated plan-item → entity mapping (3 places) | Medium | Fixed: `GeneratedItineraryItemMapper` |
| `excludeFoodAndDrink` "not wired" | Medium | No change: bound from the query string (`[FromQuery]` record), documented in OpenAPI test |
| Feature flags `Features__PerLegTransport/ExternalReviews` missing | Medium | Dropped from the spec (developer decision 2026-10-16): Tripadvisor__Enabled covers reviews, clients hide unfinished buttons |
| Hash included last stop mode; both bodies accepted | Low | Fixed (SQL + endpoint tests first) |
| Missing Tripadvisor ratings re-fetched every time | Low | Fixed: absent ids cached (test first) |
| Motorbike factor out of range silently ignored | Low | Fixed: options validator + ValidateOnStart (test first) |
| Allowed leg modes inline; duplicated message; redundant wrapper; 00:00 closing | Low | Fixed (refactors) |
| Food & Drink only in seed, not migration | Low | Kept: POI categories are seed data in this repo (all other categories too) |
| Straight-line distance when the suggestion is the first stop | Low | Deferred: needs start→stop leg in plan items |
| Leg override matched by coordinates | Low | Deferred: two stops at identical coordinates are not in the catalog |
| Extra reads inside the serializable adjust transaction | Low | Accepted: two primary-key reads |
| `LinkTripadvisorLocation` only used by tests | Low | Kept: domain guard for the column; seeds write SQL directly |
| Reason strings instead of enum | Low | Kept: repo convention for error/reason codes is string constants |

## Ticket 19 (P1) — time of day (implemented 2026-10-16, option A)

- `Scheduling/TimeOfDay/` (outside the benchmark-hashed solver folders): `TimeOfDayScorer` (windows 04:30–06:30
  beach/sunrise tag +3, 06:00–09:30 outdoor/nature +1, 11:00–14:00 outdoor without shelter −2 / mixed −1,
  16:30–18:00 beach or scenic ≥ 8 +1; scored at the middle of each visit) and `TimeOfDayRefiner` (tries every order
  up to 6 visits, pairwise swaps above; keeps an order only if it scores higher, is valid through
  `GenerateFixedOrderAsync` and ends at most `TimeOfDay:MaxExtraMinutes` (15) later). Template reasons replace
  the technical recommendation reason of matched visits.
- Applied after the solver in create and regenerate (before the "no alternative" check); not in traveler adjustments.
- Matrices are memoized, so refinement adds no routing calls. CSP objective and benchmark unchanged.
- Tests first: scorer windows, refiner (accept ≤ 15 min, reject 60 min, disabled), regenerate and create handler;
  mutations (limit ignored, reasons dropped) are caught.
