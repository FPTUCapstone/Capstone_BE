# Weather packing tips (backend) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans (inline) to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax.

**Goal:** `GET /api/v1/itineraries/{id}/weather-tips` returns one day-level list of what to bring, from the Tomorrow.io hourly forecast and fixed rules, or `{ "weatherTips": null }` when the forecast is not available for every visit stop.

**Architecture:** A pure rules class turns hourly forecasts into a summary and tips. A new `IDayForecastProvider` (Infrastructure, Tomorrow.io, own cache) supplies the hourly series. A MediatR handler checks access, groups stops by 0.1-degree cells, applies a timeout, and maps every failure to `null`. No AI, no database change.

**Tech Stack:** .NET 10, MediatR, `Result<T>`, xUnit + FluentAssertions, `TestDbContext` (in-memory).

**Spec:** `specs/UC-10-weather-packing-tips-spec.md` (approved 2026-11-24).

## Global Constraints
- Database-First: no migration, nothing stored. `.editorconfig` has `insert_final_newline = false`: strip the final newline the Write tool adds.
- Controllers hold no logic; failures go through `HandleFailure`. The endpoint itself never fails because of the weather service (W3).
- `Capstone_AI` and the explanation prompt are not touched.
- Thresholds (constants in `WeatherTipRules`): storm = thunderstorm or rain >= 4 mm/h; rain = probability >= 40 % or rain >= 0.2 mm/h; heat = felt temperature >= 33; uv >= 6; cool = temperature <= 20; wind >= 10 m/s. At most 5 tips, Warning first, then table order.
- Texts (Vietnamese, exact): storm `Có dông hoặc mưa to, cân nhắc đổi sang điểm trong nhà`; rain `Mang ô hoặc áo mưa`; heat `Mang đủ nước uống và mũ nón`; uv `Mang kem chống nắng và kính râm`; cool `Mang áo khoác mỏng`; wind `Gió khá mạnh, chú ý khi ra biển hoặc điểm trên cao`. Codes `weather_tip.storm|rain|heat|uv|cool|wind`.
- Time zone: fixed offset +07:00, `timeZoneId` `Asia/Ho_Chi_Minh` (Vietnam has no DST; avoids a tz-database dependency). My call.
- Config: `WeatherTips:Enabled=false`, `WeatherTips:TimeoutSeconds=3` (1-10); needs `Tomorrow:Enabled` and key.

## Review Focus
- A visit stop whose hour is missing from the series (beyond 5 days, or a gap): whole answer `null`, not partial tips. Task 4 test `Handle_WhenOneStopHasNoForecastHour_ReturnsNull`.
- Boundary values (exactly 4, 0.2, 40, 33, 6, 20, 10): Task 1 tests per threshold.
- Stop that ends exactly on an hour edge (14:00-15:00) must not pull in the 15:00 hour. Task 1 test `HoursCovering_StopEndingOnTheHour_ExcludesNextHour`.
- Midnight-crossing plan: one list, `date` = local date of the first stop. Task 4 test.
- Another traveler / missing itinerary: the access service's 404 passes through. Task 4 test.
- A provider returning an hour with a missing field must not be filled with zero. Task 2 test.

---

### Task 1: Rules and hourly model (pure)

**Files:**
- Create: `src/TripMate.Application/Features/Itineraries/WeatherTips/HourlyForecast.cs`
- Create: `src/TripMate.Application/Features/Itineraries/WeatherTips/WeatherTipRules.cs`
- Create: `src/TripMate.Application/Features/Itineraries/WeatherTips/ItineraryWeatherTipsResponse.cs`
- Test: `tests/TripMate.Application.UnitTests/Features/Itineraries/WeatherTipRulesTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record HourlyForecast(DateTimeOffset StartUtc, double TemperatureC, double ApparentTemperatureC, double RainProbabilityPercent, double RainMmPerHour, double UvIndex, double WindSpeedMps, bool Thunderstorm);`
  - `public sealed record ItineraryWeatherTipsResponse(WeatherTipsDto? WeatherTips);`
  - `public sealed record WeatherTipsDto(string Date, string TimeZoneId, DateTimeOffset ForecastAtUtc, WeatherTipsSummaryDto Summary, IReadOnlyList<WeatherTipDto> Tips);`
  - `public sealed record WeatherTipsSummaryDto(int MinTempC, int MaxTempC, int MaxRainProbabilityPercent, int MaxUvIndex);`
  - `public sealed record WeatherTipDto(string Code, string Severity, string Text);`
  - `public static class WeatherTipRules { public static (WeatherTipsSummaryDto Summary, IReadOnlyList<WeatherTipDto> Tips) Evaluate(IReadOnlyCollection<HourlyForecast> hours); public static IReadOnlyList<DateTimeOffset> HourStartsCovering(DateTimeOffset arrivalUtc, DateTimeOffset departureUtc); }`
  - `HourStartsCovering` returns every whole-hour UTC start `H` with `H < max(departure, arrival + 1 tick)` and `H + 1h > arrival`; a stop that ends exactly on the hour excludes the next hour; a zero-length stop returns its own hour.

- [ ] **Step 1: Write failing tests** in `WeatherTipRulesTests.cs`: one `[Theory]` per tip with the value just below and exactly at the threshold (`Evaluate_RainProbability40_AddsRainTip`, `..39..._NoRainTip`, same for 0.2 mm/h, 4 mm/h, felt 33/32.9, uv 6/5.9, temperature 20/20.1, wind 10/9.9); `Evaluate_Thunderstorm_AddsStormWithWarning`; `Evaluate_OrdersWarningFirst_AndCapsAtFiveTips` (all six conditions -> 5 tips, first is storm, wind dropped); `Evaluate_SummaryRoundsMinMaxTemperatureAndTakesMaxima` (temps 21.4/30.6 -> 21/31, probability 59.6 -> 60, uv 7.5 -> 8); `Evaluate_CalmDay_ReturnsEmptyTips`; `HoursCovering_StopEndingOnTheHour_ExcludesNextHour` (14:00-15:00 -> [14:00]); `HoursCovering_StopCrossingHours_ListsEach` (13:30-15:10 -> 13,14,15); `HoursCovering_ZeroLengthStop_ReturnsItsHour`.
- [ ] **Step 2:** Run `dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~WeatherTipRulesTests"`. Expected: FAIL (types missing).
- [ ] **Step 3:** Implement the types and `WeatherTipRules` with the thresholds/texts in Global Constraints. Severity strings `Warning` / `Info`.
- [ ] **Step 4:** Same command. Expected: PASS.
- [ ] **Step 5:** Commit `feat(itineraries): add weather tip rules for a day (UC-10)`.

### Task 2: Day forecast provider (Tomorrow.io)

**Files:**
- Create: `src/TripMate.Application/Features/Itineraries/WeatherTips/IDayForecastProvider.cs`
- Create: `src/TripMate.Infrastructure/Weather/TomorrowDayForecastProvider.cs`
- Test: `tests/TripMate.Infrastructure.UnitTests/Weather/TomorrowDayForecastProviderTests.cs`

**Interfaces:**
- Consumes: `HourlyForecast` (Task 1), `TomorrowWeatherOptions` (existing), `IMemoryCache`.
- Produces: `public interface IDayForecastProvider { Task<IReadOnlyList<HourlyForecast>?> GetHourlyAsync(decimal latitude, decimal longitude, CancellationToken cancellationToken); }` — `null` when `Tomorrow:Enabled` is false or the response has no usable `timelines.hourly`; throws `HttpRequestException` on a non-success status (the handler maps it). `internal sealed class TomorrowDayForecastProvider(HttpClient, IMemoryCache, IOptions<TomorrowWeatherOptions>) : IDayForecastProvider`.

- [ ] **Step 1: Write failing tests** (copy the `StubHandler`/`CreateProvider` style of `TomorrowTripWeatherProviderTests`): `GetHourly_ParsesAllFields` (one hour with `temperature`, `temperatureApparent`, `precipitationProbability`, `rainIntensity`, `uvIndex`, `windSpeed`, `weatherCode 8000` -> matching `HourlyForecast`, `Thunderstorm` true); `GetHourly_UsesForecastEndpoint_HourlyMetric_AndKeepsKeyOutOfUrl` (path `/v4/weather/forecast`, query has `timesteps=1h` and `units=metric`, header `apikey`); `GetHourly_DropsAnHourMissingAField` (hour without `uvIndex` is not returned); `GetHourly_WhenDisabled_ReturnsNullWithoutCalling`; `GetHourly_CachesPerLocation` (second call does not hit the handler); `GetHourly_NonSuccessStatus_Throws`.
- [ ] **Step 2:** `dotnet test tests/TripMate.Infrastructure.UnitTests --filter "FullyQualifiedName~TomorrowDayForecastProviderTests"` -> FAIL.
- [ ] **Step 3:** Implement. Mirror `TomorrowTripWeatherProvider` (same request building and `apikey` header); cache key `tomorrow-day:{lat}:{lon}` (invariant culture) for 30 minutes (constant `DayCacheDuration`), results ordered by `StartUtc`. Empty usable list returns an empty list (the handler treats it as no coverage).
- [ ] **Step 4:** Same command -> PASS.
- [ ] **Step 5:** Commit `feat(weather): read the hourly day forecast from Tomorrow.io (UC-10)`.

### Task 3: Options and wiring

**Files:**
- Create: `src/TripMate.Application/Features/Itineraries/WeatherTips/WeatherTipsOptions.cs`
- Modify: `src/TripMate.Infrastructure/DependencyInjection.cs` (next to the `TomorrowWeatherOptions` block, ~line 114-127)
- Modify: `src/TripMate.Api/appsettings.json`, `.env.example`
- Test: `tests/TripMate.Infrastructure.UnitTests/Weather/WeatherTipsOptionsTests.cs` (only if a similar options-validation test pattern exists; otherwise cover via Task 4)

**Interfaces:**
- Produces: `public sealed class WeatherTipsOptions { public const string SectionName = "WeatherTips"; public bool Enabled { get; init; } public int TimeoutSeconds { get; init; } = 3; }`; DI: options bound + `.Validate(o => o.TimeoutSeconds is >= 1 and <= 10, ...)` + `ValidateOnStart()`; `services.AddHttpClient<IDayForecastProvider, TomorrowDayForecastProvider>(...)` with the same base address / 10 s client timeout as the existing provider.

- [ ] **Step 1:** Add the options class, DI lines, `"WeatherTips": { "Enabled": false, "TimeoutSeconds": 3 }` in `appsettings.json`, and `WeatherTips__Enabled=false` / `WeatherTips__TimeoutSeconds=3` with a comment in `.env.example`.
- [ ] **Step 2:** `dotnet build` of the solution. Expected: 0 errors.
- [ ] **Step 3:** Commit `feat(itineraries): wire weather tips options and forecast provider (UC-10)`.

### Task 4: Query and handler

**Files:**
- Create: `src/TripMate.Application/Features/Itineraries/WeatherTips/GetItineraryWeatherTipsQuery.cs`
- Create: `src/TripMate.Application/Features/Itineraries/WeatherTips/GetItineraryWeatherTipsQueryHandler.cs`
- Test: `tests/TripMate.Application.UnitTests/Features/Itineraries/GetItineraryWeatherTipsQueryHandlerTests.cs`

**Interfaces:**
- Consumes: `IItineraryAccessService.ResolveCurrentAsync(id, userId, trackCurrent: false, ct)` (returns `ItineraryAccess.CurrentItinerary`), `IApplicationDbContext`, `IDateTimeProvider`, `IDayForecastProvider`, `IOptions<WeatherTipsOptions>`, `ILogger<>`, `WeatherTipRules`.
- Produces: `public sealed record GetItineraryWeatherTipsQuery(long ItineraryId, long TravelerUserId) : IRequest<Result<ItineraryWeatherTipsResponse>>;` and the handler. Algorithm: access failure -> pass the failure through (W4); `Enabled=false` -> `{null}` (`disabled`); load the itinerary's visit items with `PointOfInterest` (AsNoTracking) that have both times; none -> `{null}` (`no-coordinates`); all departures <= now -> `{null}` (`past`); group by `Math.Round(lat, 1)` / `Math.Round(lon, 1)`, more than 3 groups -> `{null}` (`too-many-locations`); one provider call per group in parallel under a linked token cancelled after `TimeoutSeconds`; any stop without every covering hour in its group's series -> `{null}` (`out-of-range`); provider exception -> `{null}` (`provider-error`), timeout -> `{null}` (`timeout`), caller cancellation rethrown; otherwise `WeatherTipRules.Evaluate` over all covering hours, `date` = +07:00 local date of the earliest arrival, `forecastAtUtc` = `IDateTimeProvider.UtcNow`, outcome `success` or `no-tips`. Logs one line with `ItineraryId` and the outcome only (no place names, no tip text).

- [ ] **Step 1: Write failing tests** with `TestDbContext`, a hand-written `IItineraryAccessService` stub and a `FakeDayForecastProvider` (series per cell, optional exception/delay), fixed clock: `Handle_ReturnsTipsForTheStopHours`; `Handle_WhenOneStopHasNoForecastHour_ReturnsNull`; `Handle_WhenForecastDisabled_ReturnsNullAndDoesNotCallProvider`; `Handle_WhenProviderThrows_ReturnsNull`; `Handle_WhenProviderIsSlower_ThanTimeout_ReturnsNull` (TimeoutSeconds 1, provider waits on the token); `Handle_WhenEveryStopAlreadyEnded_ReturnsNullWithoutCallingProvider`; `Handle_WhenMoreThanThreeLocationCells_ReturnsNull`; `Handle_StopsInTheSameCell_CallProviderOnce`; `Handle_RestItemsAndStopsWithoutTimes_AreIgnored`; `Handle_PlanCrossingMidnight_UsesLocalDateOfFirstStop` (first stop 23:30 local); `Handle_OtherTravelersItinerary_PassesThroughNotFound`; `Handle_CallerCancellation_Propagates`.
- [ ] **Step 2:** `dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~GetItineraryWeatherTipsQueryHandlerTests"` -> FAIL.
- [ ] **Step 3:** Implement the query and handler as described.
- [ ] **Step 4:** Same command -> PASS.
- [ ] **Step 5:** Commit `feat(itineraries): add weather tips query for an itinerary day (UC-10)`.

### Task 5: Endpoint

**Files:**
- Modify: `src/TripMate.Api/Controllers/V1/ItinerariesController.cs` (after `Get`, ~line 70)
- Modify: `src/TripMate.Api/TripMate.Api.http` (one request sample)

**Interfaces:**
- Consumes: `GetItineraryWeatherTipsQuery`.
- Produces: `[HttpGet("{itineraryId:long}/weather-tips")] Task<IActionResult> GetWeatherTips(long itineraryId, CancellationToken ct)` with the same attributes as `Get` (200 `ItineraryWeatherTipsResponse`, 401, 403, 404), 401 when there is no current user, `Ok(result.Value)` or `HandleFailure(result)`.

- [ ] **Step 1:** Add the action, the `.http` sample, and a `ProducesResponseType` list identical to `Get`.
- [ ] **Step 2:** `dotnet build` of the solution -> 0 errors. If an integration-test file asserts the controller's route list, update it; check with `grep -rn "itineraries" tests/TripMate.Api.IntegrationTests -l`.
- [ ] **Step 3:** Commit `feat(itineraries): expose GET itineraries/{id}/weather-tips (UC-10)`.

### Task 6: Verification

- [ ] **Step 1:** `dotnet build` of the solution, then `dotnet test tests/TripMate.Application.UnitTests` and `tests/TripMate.Infrastructure.UnitTests`. Expected: all pass, same count as baseline plus the new tests.
- [ ] **Step 2:** If Golden snapshot files changed, `git checkout -- tests/TripMate.Application.UnitTests/**/Golden` (BE gotcha). `git status` shows only the files of this plan.
- [ ] **Step 3:** Manual check with a real key is NOT run here (production key not confirmed); note it in the hand-off.

## Later stages (own specs after this one, same order)
- **Web (`Capstone_FE`)**: card at the top of the itinerary detail, hidden on `null`/error/loading; calls the endpoint after the detail loads.
- **Mobile (`Capstone_Mobile`)**: same card on the itinerary detail.
- **Rollout**: check Railway `Tomorrow__Enabled`/key and plan limits, then set `WeatherTips__Enabled=true` (needs the user's go; develop auto-deploys, no migration needed).