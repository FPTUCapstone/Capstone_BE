# Public Weather Forecast — Spec

Status: decisions agreed with developer on 2026-11-26 (grilling session Q1–Q7).

## Scope
One read-only, anonymous endpoint that backs the FE `/weather` page with live Tomorrow.io data.
Out of scope: personal "my upcoming tours / weather warnings" endpoint (next iteration), geocoding.

## Endpoint
`GET /api/v1/weather/forecast?lat={decimal}&lon={decimal}` — `[AllowAnonymous]`.

| Rule | Value |
|---|---|
| Coordinates | Vietnam bounding box: lat 6–24, lon 102–118, else 400 |
| Rounding | lat/lon rounded to 2 decimals (~1.1 km) before cache and upstream call |
| Rate limit | 30 requests / minute / IP → 429 ProblemDetails |
| Cache | `IMemoryCache`, 30 minutes per rounded cell |
| Upstream | one call: `v4/weather/forecast?timesteps=1h,1d&units=metric`, header `apikey` |
| API key | dedicated `PublicWeather:ApiKey` (no webhook); separate from `Tomorrow` and `TomorrowIo` |
| Failure | disabled, upstream non-2xx (incl. 429), timeout or unusable payload → 503 `public_weather.unavailable` |
| Time zone | `Asia/Ho_Chi_Minh` (+07:00, no DST) |

## Response (matches FE `WeatherForecastResponseDto`)
- `source` = `"api"`, `provider` = `"Tomorrow.io"`, `timeZoneId`, `fetchedAtUtc`.
- `location`: `latitude`, `longitude` (rounded), `timeZoneId`. Name is supplied by FE (Mapbox).
- `current`: the hourly point covering "now" (fallback: first future hour). Forecast, not an observation.
- `hourly[]`: next 24 hours from the current hour. Fields: startUtc, temperatureC, apparentTemperatureC,
  rainProbabilityPercent, rainMmPerHour, uvIndex, windSpeedMps, thunderstorm (weatherCode 8000), weatherCode,
  humidityPercent, cloudCoverPercent, visibilityKm.
- `daily[]`: Tomorrow daily points (day starts 06:00 local). `date` = local `yyyy-MM-dd`, min/max temp,
  maxApparentTempC, maxRainProbabilityPercent, maxRainMmPerHour, totalRainMm, maxUvIndex, maxWindSpeedMps,
  thunderstorm, weatherCode (= weatherCodeMax), humidityAvgPercent, sunriseTime/sunsetTime as local `HH:mm`.
- A point missing any field is dropped, never filled in.
