# Weather packing tips for an itinerary day (backend)

Follows `UC-10-background-explanation-spec.md` and the UC-14 weather work (`TomorrowTripWeatherProvider`).
Status: approved by the developer on 2026-11-24. Decisions: thresholds of section 3 stay as proposed; the file keeps
the `UC-10` name; the Tomorrow.io plan is assumed to be Free (hourly forecast about 5 days). The developer left
open point 8.1 (is `Tomorrow__Enabled=true` set on Railway production?) unanswered, so it is checked before rollout.
The feature is off by default, so it does not block the code. Clients follow in the order Backend, Web, Mobile.

## 1. Why
A traveler who plans 25/11 should see, at the top of that itinerary, what to bring because of the weather that
day ("take an umbrella and sunscreen"). Decided with the developer on 2026-11-24: one list for the whole day (not
one tip per stop), and the AI must never guess the weather.

## 2. Approaches considered
| | Approach | Trade-off |
|---|---|---|
| **1 (recommended)** | The backend fetches the forecast and writes the tips with fixed rules and Vietnamese templates. No AI. | Predictable, instant, testable, no model change. Wording is plain, not "smooth". |
| 2 | Same as 1, then one separate call to Capstone_AI that rewrites the already-decided tips as one friendly sentence. | Smoother text, but needs a second model on the laptop (CPU, slow, tunnel can be down). Can be added later on top of 1 without changing its API. |
| 3 | Put the weather into the existing explanation prompt. | Rejected: the fine-tuned `tripmate-explainer` was trained on the current prompt (retrain needed), and the model could invent facts. |

This spec is approach 1. Approach 2 would be its own spec. `Capstone_AI` and the explanation prompt are not touched.

## 3. Rules
| # | Rule |
|---|---|
| W1 | The tips come from the forecast of the hours the traveler is at each visit stop (planned arrival to departure), at that stop's coordinates. Rest items and stops without coordinates are ignored. |
| W2 | If the forecast is not available for every visit stop, the answer is `null` (no partial tips). Not available means: feature or Tomorrow.io disabled, API key missing, call failed or timed out, a stop time is beyond the hourly forecast the API returned, or the whole plan is already in the past. Nothing is guessed, and nothing from the AI is used. |
| W3 | The endpoint never fails the page: any error becomes `null`, logged with an outcome name (section 6). |
| W4 | Only the traveler who owns the itinerary can read it (same check as itinerary detail). Another user gets the same 404 as detail. |
| W5 | Stops are grouped by coordinates rounded to 0.1 degree (about 11 km). At most 3 groups are looked up per request; if there are more, the answer is `null`. One forecast call per group, cached 30 minutes. |
| W6 | The time zone is the itinerary's (`Asia/Ho_Chi_Minh` today). The forecast hours are matched on UTC instants. |

### Tips (my proposal, thresholds are constants in one class and easy to change)
Each tip appears at most once; the list is ordered by severity then by the table order, at most 5 tips.

| Code | Condition during any visit stop | Vietnamese text | Severity |
|---|---|---|---|
| `weather_tip.storm` | thunderstorm, or rain at least 4 mm/h | Có dông hoặc mưa to, cân nhắc đổi sang điểm trong nhà | Warning |
| `weather_tip.rain` | rain probability at least 40 % or rain at least 0.2 mm/h | Mang ô hoặc áo mưa | Info |
| `weather_tip.heat` | felt temperature at least 33 °C | Mang đủ nước uống và mũ nón | Info |
| `weather_tip.uv` | UV index at least 6 | Mang kem chống nắng và kính râm | Info |
| `weather_tip.cool` | temperature at most 20 °C | Mang áo khoác mỏng | Info |
| `weather_tip.wind` | wind at least 10 m/s | Gió khá mạnh, chú ý khi ra biển hoặc điểm trên cao | Info |

If no rule fires, `tips` is empty and the summary still shows ("good weather").

## 4. API
New endpoint, so the itinerary detail stays fast and does not depend on an outside service:
`GET /api/v1/itineraries/{id}/weather-tips`

```json
{ "weatherTips": {
    "date": "2026-11-25",
    "timeZoneId": "Asia/Ho_Chi_Minh",
    "forecastAtUtc": "2026-11-24T03:00:00Z",
    "summary": { "minTempC": 22, "maxTempC": 31, "maxRainProbabilityPercent": 60, "maxUvIndex": 8 },
    "tips": [ { "code": "weather_tip.rain", "severity": "Info", "text": "Mang ô hoặc áo mưa" } ]
} }
```
When W2 applies: `200` with `{ "weatherTips": null }`. Clients hide the block on `null`, on any error, and
while loading. `date` is the local date of the first visit stop; a plan that crosses midnight still gets one list.
`code` lets a client localize; `text` is Vietnamese.

## 5. Data and configuration
- Tomorrow.io `v4/weather/forecast?timesteps=1h` already returns hourly `temperature`, `temperatureApparent`,
  `precipitationProbability`, `rainIntensity`, `uvIndex`, `windSpeed`, `weatherCode`. `temperature` feeds
  `summary.minTempC` / `maxTempC` and the `weather_tip.cool` rule; `temperatureApparent` feeds `weather_tip.heat`.
  An hour that lacks any of these fields is treated as having no forecast (W2), never filled in. The existing `ITripWeatherProvider` reads only rain and
  thunderstorm and is used by UC-14 alerts, so it stays untouched. A new `IDayForecastProvider` in Infrastructure
  reads the hourly series and shares the `Tomorrow` options (key, base URL).
- New options: `WeatherTips:Enabled` (default `false`) and `WeatherTips:TimeoutSeconds` (default 3, allowed 1-10).
  Needs `Tomorrow:Enabled=true` and the key, as UC-14 does.
- No database change, no migration, nothing stored. The forecast is read when the traveler opens the page, so it
  gets fresher as the date gets closer.
- Cost: at most 3 Tomorrow.io calls per itinerary view, cached 30 minutes per location and day; the free plan's
  daily and hourly call limits are shared with UC-14 and UC-70.

## 6. Scope
- `Features/Itineraries/WeatherTips`: query, handler, rules class (pure function), response.
- `IDayForecastProvider` + `TomorrowDayForecastProvider` + options and validators; `appsettings.json`, `.env.example`.
- Controller action on the itineraries controller.
- Log outcomes (no place name, no tip text): `success`, `no-tips`, `disabled`, `out-of-range`, `provider-error`,
  `timeout`, `too-many-locations`, `no-coordinates`, `past` (every stop already ended; no forecast call is made).
- Tests: rules (one per tip and each boundary), provider parsing with a fake HTTP handler, handler (owner,
  other user, disabled, error, timeout, beyond forecast, past plan, partial coverage gives `null`).
- Web and Mobile follow in their own PRs (Backend, then Web, then Mobile, as UC-10): a card at the top of the
  itinerary, hidden on `null`. Specs for them come after this one is merged.

## 7. Not in scope
Per-stop tips; AI wording (approach 2); changing an itinerary because of the weather (UC-14 alerts and the CSP
own that); weather for dates beyond the forecast.

## 8. Open points for the developer
1. Is `Tomorrow__Enabled=true` with a key already set on Railway production, and which plan is it (call limits, forecast length)? Hours beyond the plan's forecast simply give `null`, so this affects how many dates show the block, not correctness. OPEN: to be checked before rollout.
2. Thresholds in section 3: kept as proposed (decided 2026-11-24).
3. Ticket id: kept the `UC-10` name (decided 2026-11-24).