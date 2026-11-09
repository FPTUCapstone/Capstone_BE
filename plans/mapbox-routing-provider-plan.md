# Plan: Mapbox route duration provider (replaces OpenRouteService)

Spec: `specs/mapbox-routing-provider-spec.md` (decisions D1-D7 as written). Base `origin/develop` @ `31948186`.
Baseline: `dotnet build TripMate.slnx` passes (0 errors).

Tasks (TDD, one at a time, `dotnet test` on `TripMate.Infrastructure.UnitTests` after each):

1. **Options + validator** - tests: `MapboxRoutingOptionsValidatorTests` (factor range ported, `BaseUrl` absolute http/https),
   `MapboxRoutingConfigurationTests` (`MAPBOXROUTING__ACCESSTOKEN`/`__BASEURL`/`__MOTORBIKEDURATIONFACTOR` env-style keys bind).
   Code: `MapboxRoutingOptions`, `MapboxRoutingOptionsValidator`.
2. **Provider, single request (N <= 25)** - port the 13 ORS tests into `MapboxRouteDurationProviderTests`
   (GET, `directions-matrix/v1/mapbox/{driving|walking}/...`, `annotations=duration,distance`, token, invariant coordinates,
   `code` handling, PublicTransit, empty token, token not in messages). Code: `MapboxRouteDurationProvider`.
3. **Chunking (N > 25)** - tests: 25 -> 1 request, 26 -> 3, 42 -> 6, limits per request, ground-truth directed matrix is
   reproduced, one failing chunk, cancellation. Code: block split + index-map merge, `Task.WhenAll`.
4. **Wiring and removal** - DI registrations, `appsettings.json`, `.env.example`, `docker-compose.yml`, runbook,
   `SolverComparisonReport` comment, test fixtures `"OpenRouteService"` -> `"Mapbox"`, delete ORS classes/tests;
   DI/logging test that the token never reaches logs.
5. **Verification** - `dotnet build`, `dotnet test` (Application tests last; `git checkout` golden JSON), `dotnet format --verify-no-changes`
   on changed files if the repo uses it, local commit (no push, no AI trailer), `## Summary`.

Review: severity-based self-review between tasks; Critical findings block the next task.
