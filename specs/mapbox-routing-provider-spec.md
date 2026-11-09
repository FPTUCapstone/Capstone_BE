# Specification: Mapbox route duration provider (replaces OpenRouteService)

**Status**: v0.2, implemented (approval relayed by the developer through the parent session: "ok sửa đi"). Defaults D1-D7 kept.
**Repo / base**: `Capstone_BE`, worktree `Capstone_BE_MAPBOXROUTE`, branch `feature/phuctv-be-mapbox-routing`, cut from `origin/develop` @ `31948186`.
**Relates to**: UC-10 (itinerary generation), planning edit / version flows, Web and Mobile route drawing (already Mapbox Directions).
**Out of scope**: storing route geometry, Web and Mobile changes, UC-14, any API contract or DB change.

## 1. Problem

Web and Mobile draw routes with Mapbox Directions, but the BE computes `TravelDurationToNextMinutes` and
`TravelDistanceToNextMeters` with OpenRouteService (ORS). Different road graphs give different minutes and metres, so
the numbers shown next to a drawn route do not match the route. Decision: ORS is dropped and the BE uses Mapbox for
everything route-related.

## 2. Current state (verified on `origin/develop`)

- `IRouteDurationProvider.GetMatrixAsync(points, transportMode, ct)` returns a `RouteDurationMatrix`
  (minutes, optional metres, optional "unreachable" flags). One implementation: `OpenRouteServiceRouteDurationProvider`.
- ORS behaviour to preserve: POST `v2/matrix/{profile}` asking for `duration` + `distance`;
  `foot-walking` for Walking, `driving-car` for Car and Motorbike; motorbike minutes = seconds x `MotorbikeDurationFactor`
  (default 0.9, validated 0.5-1.5 at startup); `PublicTransit` throws `NotSupportedException`; seconds rounded **up** to
  minutes, metres rounded to nearest; `null` cell = unreachable (off-diagonal only); negative value, wrong matrix shape or
  malformed JSON = provider failure; distances are optional (matrix without them still works, no km shown).
- Failure mapping: timeout (`OperationCanceledException` not caused by the caller, `TimeoutException`) ->
  `RouteDurationProviderException(Timeout)`; `HttpRequestException` (incl. non-2xx), bad shape, bad JSON ->
  `RouteDurationProviderException(Unavailable)`; caller cancellation and unexpected exceptions pass through. The handler
  turns these into `planning.routing_provider_unavailable` (503) and logs `ex.ProviderName`.
- Missing API key -> `InvalidOperationException` when called (app still starts without a key).
- DI: `DependencyInjection.cs` ~178-181 (options + validator) and ~376-386 (typed `HttpClient`, `Timeout = 20 s`,
  `IRouteDurationProvider` scoped).
- Matrix size: `SchedulingGeneration:MaxMatrixCandidates` = 40 (candidates **including** mandatory POIs) + start + end
  => **up to 42 points** per matrix by default. The option has no upper bound in its validator.
  `LegModeRouteDurationProvider` adds one more matrix call per distinct per-leg transport mode;
  `MemoizingRouteDurationProvider` de-duplicates identical calls inside one request.
- The user's local `.env` already holds `MAPBOXROUTING__ACCESSTOKEN` and `MAPBOXROUTING__BASEURL=https://api.mapbox.com/`;
  no code reads them yet.

## 3. Mapbox Matrix API facts (official docs, https://docs.mapbox.com/api/navigation/matrix/)

| Item | Value |
|---|---|
| Request | `GET https://api.mapbox.com/directions-matrix/v1/mapbox/{profile}/{lng,lat;lng,lat;...}?annotations=duration,distance&access_token=...` (the docs do not describe POST) |
| Profiles | `driving`, `walking`, `cycling`, `driving-traffic` |
| **Max coordinates per request** | **25** for `driving`, `walking`, `cycling`; **10** for `driving-traffic` |
| **Rate limit** | **60 requests/minute** for `driving`/`walking`/`cycling` (30 for `driving-traffic`) |
| Elements | billed per origin-destination pair; max 625 per request (25 x 25) |
| Response | `code`, `durations` (seconds), `distances` (metres), row-major, `null` = no route. HTTP 200 for `Ok` and `NoRoute`; 401 bad/missing token; 403 forbidden; 404 profile; 422 `InvalidInput`; >=500 server error |
| Auth | token in the `access_token` query parameter only (no `Authorization` header) |

Your memory was right: 25 coordinates per request and a request-rate limit (60/min, **per token, shared by every user
of the BE**). I did not find 429/413 or max-URL-length in the doc page; 429 is handled as a generic non-2xx.

## 4. Design

### 4.1 Types (namespace `TripMate.Infrastructure.Routing`)

- `MapboxRoutingOptions` (section **`MapboxRouting`**):
  `BaseUrl` (default `https://api.mapbox.com/`), `AccessToken` (default empty), `MotorbikeDurationFactor` (default 0.9,
  range 0.5-1.5, same constants as today). The names bind from `MAPBOXROUTING__ACCESSTOKEN`, `MAPBOXROUTING__BASEURL`
  and `MAPBOXROUTING__MOTORBIKEDURATIONFACTOR`.
- `MapboxRoutingOptionsValidator` (`ValidateOnStart`): motorbike factor in range (as today) **and** `BaseUrl` is an
  absolute http(s) URI (today a bad URL only explodes lazily in the `HttpClient` factory). Empty token stays allowed at
  startup so the app and tests boot without a key.
- `MapboxRouteDurationProvider : IRouteDurationProvider` (typed `HttpClient`, `Timeout` stays
  `SchedulingReservationOptions.RouteProviderTimeout`). Provider name constant `"Mapbox"`.
- Deleted: the three `OpenRouteService*` classes in `src/TripMate.Infrastructure/Routing/`.

### 4.2 Profile mapping

Walking -> `walking`; Car and Motorbike -> `driving` (Mapbox has no motorbike profile; minutes x `MotorbikeDurationFactor`,
as with ORS); PublicTransit -> `NotSupportedException` (unchanged). Plain `driving`, not `driving-traffic`
(10-coordinate cap, 30 req/min, and Web/Mobile use `mapbox/driving`, so results line up).

### 4.3 Single request (N <= 25 points)

`GET directions-matrix/v1/mapbox/{profile}/{coords}?annotations=duration,distance&access_token={token}`; coordinates
formatted `lng,lat` with `CultureInfo.InvariantCulture`, up to 6 decimals; token URL-escaped.
Response handling identical to ORS: shape check (`durations` N x N, `distances` optional but if present must be N x N),
`null` off-diagonal = unreachable, negative = invalid, seconds x factor / 60 rounded up, metres rounded.
Mapbox-specific: `code` must be `Ok`; `NoRoute` is accepted only when the matrix is well-formed (all cells `null`
=> all pairs unreachable, same as ORS `null`); any other `code` => `Unavailable`.

### 4.4 Chunking (N > 25 points)

One call cannot hold more than 25 coordinates, and a matrix needs every pair (and the matrix is **directed**).
Design (no sources/destinations parameters needed):

1. `B = ceil(N / 12)` blocks, split as evenly as possible (block size <= 12).
2. For every unordered block pair `(i, j)`, one request whose coordinate list is `block i ++ block j` (<= 24 coordinates,
   <= 576 elements, under both limits) and `annotations=duration,distance`. The N' x N' response contains the four
   sub-blocks `ii`, `ij`, `ji`, `jj`; copy `ij` and `ji` (and fill diagonal blocks from the first pair that contains
   them) into the full matrix using an index map.
3. Requests run in parallel (`Task.WhenAll`), so wall time stays about one request (still within the 20 s
   `HttpClient` timeout). The first failure fails the whole matrix with the same exception mapping as 4.3; the other
   requests are cancelled.
4. N <= 25 uses the single request of 4.3.

**API calls per matrix**: `B*(B-1)/2` for `B >= 2`, else 1.

| Points N | Blocks B | Calls |
|---|---|---|
| 2-25 | 1 | 1 |
| 26-36 | 3 | 3 |
| **37-42 (default worst case)** | **4** | **6** |
| 43-48 | 4 | 6 |
| 49-60 | 5 | 10 |

Billed elements are about 1.6x the 1764 needed for N=42 (diagonal blocks are recomputed), accepted to keep the call count
low (a "directed block" variant needs 16 calls for N=42 and bills fewer elements; rejected because the 60 req/min cap is
the tighter limit).

### 4.5 Failure behaviour (same as ORS)

Same exceptions and kinds as section 2. 401/403/404/422/429/5xx all become `Unavailable` (HTTP status stays on the inner
`HttpRequestException`). `ProviderName` changes from `"OpenRouteService"` to `"Mapbox"` (only visible in the existing
log property `Provider` and in test doubles).

### 4.6 DI and config

- `DependencyInjection.cs`: replace the ORS options/validator/`HttpClient`/`IRouteDurationProvider` registrations with the
  Mapbox ones; `BaseAddress` = `MapboxRouting:BaseUrl`.
- `src/TripMate.Api/appsettings.json`: replace the `OpenRouteService` section with
  `"MapboxRouting": { "BaseUrl": "https://api.mapbox.com/", "AccessToken": "" }`.
- `.env.example`: `MAPBOXROUTING__ACCESSTOKEN=replace-with-your-mapbox-token`, `MAPBOXROUTING__BASEURL=https://api.mapbox.com/`,
  `MAPBOXROUTING__MOTORBIKEDURATIONFACTOR=0.9` (ORS lines removed).
- `docker-compose.yml`: `MAPBOXROUTING__ACCESSTOKEN: ${MAPBOXROUTING__ACCESSTOKEN:?... must be set in .env}` and
  `MAPBOXROUTING__BASEURL: ${MAPBOXROUTING__BASEURL:-https://api.mapbox.com/}` replace the two ORS lines.
- Docs: `docs/operations/uc10-tripmate-ai-measurement-runbook.md` (lines ~117-118 key requirement, ~341 troubleshooting row).
  `README.md` has no ORS mention (re-grep at implementation). `SolverComparisonReport.cs:510` ("no OpenRouteService") is deliberately left as is: the generator output is compared with the
  committed benchmark reports (`SolverComparisonReportTests`), which keep the historical wording.
- **Not edited**: historical records (`docs/benchmarks/*` generated reports, old `specs/`/`plans/`/`docs/superpowers`
  files, `graphify-out/`), because they describe what was true at the time.

### 4.7 Token safety

The token travels in the query string, so it could leak through logs. Rules: never include the request URI in our own
exceptions or log messages (the thrown messages stay generic). Verified during implementation: the `IHttpClientFactory`
logging of .NET 10 already redacts the query string, so no `appsettings.json` log override was needed; a test
(`TheRealHttpClientPipeline_NeverLogsTheAccessToken`) captures every log message and scope of a real call through the DI
pipeline at `Trace` level and asserts the token value never appears.

## 5. Risks

| # | Risk | Mitigation / decision needed |
|---|---|---|
| R1 | **25-coordinate limit**: default worst case is 42 points | Chunking (4.4): 1-6 calls per matrix, parallel. Alternative with zero chunking: set `SchedulingGeneration:MaxMatrixCandidates=23` (<= 25 points => always 1 call) but fewer candidate POIs per generation (quality trade-off). Default in this spec: **chunk, keep 40**. |
| R2 | **60 requests/min per token, shared by all users**. A 42-point generation costs 6 calls (+6 per extra per-leg transport mode); ~10 worst-case generations/min saturate the token. Typical trips with <= 25 points cost 1 call | 429 -> `Unavailable` -> existing 503 `planning.routing_provider_unavailable`, no retry in v1 (YAGNI; Memoizing provider already removes duplicate calls). If demos need burst headroom: lower `MaxMatrixCandidates`, or add retry/limiter later. Mapbox may allow a higher limit on request. |
| R3 | Billing: Matrix is billed per element, and chunking bills ~1.6x the needed pairs | Check the free tier of the account before the demo; numbers are not in the doc page I read. |
| R4 | Numbers change versus ORS (different graph) for all new generations; stored itineraries are untouched | Expected and intended. Golden tests use synthetic matrices and are unaffected. |
| R5 | Walking/driving may return `null` for points far from any road or for very long walking legs (Mapbox limit not documented on the Matrix page) | `null` -> unreachable (existing behaviour: generation drops such places). Verify with the real Da Nang/Hue/Hoi An POIs in the manual check (section 6). |
| R6 | A token with **URL restrictions** (common for web `pk.` tokens) rejects server calls (no Referer) with 403 | Use a dedicated BE token without URL restrictions (section 7). |
| R7 | Token in query string can leak into logs | Section 4.7. |
| R8 | Matrix durations vs Directions durations for the same pair might differ slightly | Manual check on 2-3 legs against Directions (section 6); accepted if within rounding. |
| R9 | Deploy order: with no token the first routing call throws `InvalidOperationException` (same as ORS with no key) | Set the Railway token **before** deploying (section 7). |

## 6. Tests (TDD, written first)

`tests/TripMate.Infrastructure.UnitTests/Routing/`
- `MapboxRouteDurationProviderTests` - the 13 existing ORS cases ported (timeout, caller cancel, network error, 5xx, 4xx,
  `TimeoutException`, null cells -> unreachable, bad shape, malformed JSON, unexpected exception not swallowed,
  rounded metres, missing distances, missing metres for null pair) plus:
  - request is `GET`, path `directions-matrix/v1/mapbox/{walking|driving}/lng,lat;...`, `annotations=duration,distance`,
    `access_token` present, invariant-culture coordinates;
  - Car and Motorbike -> `driving`, Walking -> `walking`, PublicTransit -> `NotSupportedException`; motorbike factor applied;
  - `code: NoRoute` well-formed -> all unreachable; unknown `code` -> `Unavailable`; empty token -> `InvalidOperationException`;
  - **chunking**: N=25 -> 1 request; N=26 -> 3; N=42 -> 6; every request <= 25 coordinates and <= 625 elements; a stub that
    answers each request from a ground-truth directed function (asymmetric, with a few unreachable pairs) must reproduce the
    exact N x N minutes, metres and unreachable flags; one failing chunk -> controlled exception (Timeout/Unavailable kinds kept);
    caller cancellation still propagates;
  - exception messages never contain the token.
- `MapboxRoutingOptionsValidatorTests` - factor range (ported) + `BaseUrl` must be absolute http(s).
- Config binding test: `MAPBOXROUTING__ACCESSTOKEN` / `__BASEURL` environment-style keys bind to `MapboxRoutingOptions`.
- DI/logging test: provider resolves through `AddInfrastructure`-style registration and logs do not contain the token (4.7).

`tests/TripMate.Application.UnitTests` and `Api.IntegrationTests`: literal `"OpenRouteService"` in provider-failure fixtures ->
`"Mapbox"`. Run the Application unit tests last and `git checkout` the golden JSON files they rewrite.

Manual check (needs the real token, not automated, token never printed): generate one Da Nang itinerary on a local BE and
compare 2-3 legs' minutes/metres against the same pairs from Mapbox Directions (`mapbox/driving`, `mapbox/walking`).

## 7. What you must do outside the code

1. **Mapbox token for the BE**: create a dedicated access token (e.g. `tripmate-be-routing`) with the default public scopes
   (Directions/Matrix) and **no URL restrictions**; do not reuse the Web/Mobile token. Optionally ask Mapbox for a higher
   Matrix rate limit if you expect many concurrent generations.
2. **Railway (BE service)**, before deploying this change:
   - add `MapboxRouting__AccessToken` (secret). Optional: `MapboxRouting__BaseUrl` (default is right),
     `MapboxRouting__MotorbikeDurationFactor` (default 0.9);
   - after the deploy works, remove `OpenRouteService__ApiKey`, `OpenRouteService__BaseUrl`,
     `OpenRouteService__MotorbikeDurationFactor` (whichever exist) and revoke the ORS key.
3. **Local `.env`**: already has `MAPBOXROUTING__ACCESSTOKEN` and `MAPBOXROUTING__BASEURL`; delete the old
   `OPENROUTESERVICE__*` lines. Other developers must copy the two names from the new `.env.example`.
4. The same renamed variables apply to any CI/other environment that sets `OPENROUTESERVICE__*` (none found in
   `.github/workflows`).

## 8. Decisions I made by default (tell me if you disagree)

- D1 Chunk and keep `MaxMatrixCandidates=40` (R1). Alternative: cap at 23.
- D2 No retry/limiter on 429 in v1 (R2).
- D3 `PublicTransit` stays `NotSupportedException` (not mapped to driving like Mobile does), to keep BE behaviour unchanged.
- D4 `ProviderName` becomes `"Mapbox"`.
- D5 Empty token is allowed at startup and fails at call time, as ORS did.
- D6 Historical docs/specs/benchmarks keep their ORS wording.
- D7 This spec is committed with the code (`specs/mapbox-routing-provider-spec.md`); plan goes to `plans/mapbox-routing-provider-plan.md`.

## 9. Flow after approval

Clean baseline (`dotnet build`, `dotnet test` on the worktree) -> `plans/mapbox-routing-provider-plan.md` -> wait for
approval -> TDD task by task (options+validator, single request, chunking, DI/config/docs removal) -> review ->
`dotnet build` + `dotnet test` -> one local commit (no push, no AI trailer) + `## Summary` for the PR.
