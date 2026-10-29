# UC-14 Receive Real-Time Alerts â€” Tomorrow.io Backend Revision

## Status and source

**Approved by user on 2026-10-28 for UC-14 implementation.** The previously approved specification is preserved at
[`../../Capstone_BE/specs/UC-14-receive-real-time-alerts-spec.md`](../../Capstone_BE/specs/UC-14-receive-real-time-alerts-spec.md).
Companion proposed [Mobile specification](./mobile-spec.md) and [implementation plan](./plan.md).
Implementation is authorized; commit and push are not authorized.

## Purpose and boundary

UC-14 lets the authenticated Traveler receive alerts that can affect an open UC-13 navigation
session. Backend owns detection for weather, schedule delay and POI closure; persistence,
deduplication, resolution, read state, device registration and FCM delivery. Mobile owns GPS and
route-deviation detection and reports only the transition event, never coordinates.

UC-14 exposes an existing UC-15 proposal when one is already valid. It does not generate, apply or
mutate an itinerary. Traffic and general safety incidents are outside this release because the
current system has no verified data source for them.

## Alert sources and timing

Only sessions that are open, unexpired and in `Navigating` or `Exploring` are monitored.

- A background scan checks schedule delay, POI closure and weather every configurable 10 minutes.
- Route deviation arrives immediately from Mobile when the device has connectivity.
- Incident, Notification and delivery work are committed atomically before any push attempt.
- The persisted Alerts list is authoritative when FCM is late or unavailable.
- The 10-minute interval is a scan target, not a push delivery guarantee.

## Detection and resolution rules

| Type | Create when | `source_key` | Resolve when |
| --- | --- | --- | --- |
| `ScheduleDelay` | The next `Pending` item is over the configurable 30-minute threshold after `PlannedArrivalUtc`, with neither `ReachedAtUtc` nor `SkippedAtUtc`. | `delay:{itemId}` | Item is reached or skipped, or the session ends. |
| `POIClosure` | A `Pending` POI is explicitly unavailable, or verified opening hours and a trusted timezone prove it closed at the planned visit time. Missing data means unknown. | `closure:{itemId}` | Source confirms availability for the visit, item is reached/skipped, or session ends. |
| `WeatherAdvisory` (`Info`) | Rain is 2.5 to below 10 mm/hour at an `Outdoor` remaining stop due within one hour. | `tomorrow:rain-moderate:{itemId}:{plannedArrivalUtc}` | Forecast falls outside this band, item is reached/skipped, or session ends. Provider failure never resolves it. |
| `SevereWeather` (`Warning`) | Within three hours of planned arrival: rain is at least 10 mm/hour at an `Outdoor` remaining stop, rain is at least 50 mm/hour at any remaining stop, a thunderstorm is forecast at a remaining stop, or a verified qualifying official event affects a remaining stop at its planned time. | `tomorrow:{condition-or-eventId}:{itemId}:{plannedArrivalUtc}`; different rain bands have different condition keys. | Alert expires/no longer overlaps, successful forecast falls below its band, item is reached/skipped, or session ends. Provider failure never resolves it. |
| `RouteDeviation` | Mobile reports a new off-route transition for a snapshot target in `Pending` or `Skipped`. | `deviation:{targetItemId}` | Target is reached after detection; is skipped after detection if it was Pending; or session ends. |

UC-13 currently records arrival after Traveler confirmation. Delay text must therefore mean â€œarrival
was not recorded on timeâ€; Backend must not claim the Traveler is physically late or derive ETA from
GPS it does not have. The next item may already be late after the current item resolves and may create
an alert on the next scan.

### Weather contract

Backend uses Tomorrow.io's **metric hourly forecast** at remaining POI coordinates and
`PlannedArrivalUtc`, never Traveler GPS. The tested key returned 120 hourly entries containing
`rainIntensity`, `precipitationProbability` and `weatherCode`; finer resolution has not been
verified for this account. Select the forecast hour containing planned arrival, reject missing or
stale values, and read `rainIntensity` directly in mm/hour. `precipitationProbability` means chance
of some precipitation, not chance of rain exceeding a warning threshold; display it only as
context. `weatherCode=8000` is thunderstorm. A city-center observation does not represent every
POI in that city.

If `PlannedArrivalUtc` has already passed and the next stop is still `Pending`, do not treat the old
forecast hour as the Traveler's current arrival prediction. Query Tomorrow.io realtime data whose
provider timestamp is no more than **10 minutes old** and not in the future for that POI only:
current heavy rain or thunderstorm may create a warning labeled **current
conditions at the destination**, with its provider timestamp, but no stale-time moderate-rain push.
Without fresh realtime data, show no new weather push and retain any existing unresolved warning
until a successful check can resolve it. Backend must not infer the Traveler's actual ETA or GPS.
Mobile receives a weather basis (`ForecastAtPlannedTime`, `CurrentAtDestination` or `OfficialEvent`) with the affected
POI and provider time, so forecast and current-condition copy cannot be confused.

The POI catalog currently defaults `IndoorOutdoor` to `Outdoor` on creation. Audit that metadata
before weather pushes are enabled; require explicit, reviewed classification on new/active POIs.
An unreviewed POI must not be treated as verified Outdoor for moderate/heavy rain-only push rules.
Record a readiness issue and show neutral weather information only if trustworthy coordinates/time
exist; thunderstorm/official safety warnings still follow their own verified point/time rules.

| Condition | Backend result |
| --- | --- |
| Rain below 2.5 mm/hour | No UC-14 rain output. |
| Rain from 2.5 to below 10 mm/hour at an `Outdoor` POI, arrival more than one and at most three hours away | Transient `Info` advisory only; no Incident, Notification or push. |
| Rain from 2.5 to below 10 mm/hour at an `Outdoor` POI, arrival within one hour | Persisted `WeatherAdvisory` with `Info`, one preparation Notification/push; at most one moderate-rain preparation push per session in any three-hour window. Additional moderate-rain conditions remain in-app only. |
| Rain from 10 to below 50 mm/hour at an `Outdoor` POI, arrival within three hours | Persisted `SevereWeather` with `Warning`, push eligible; advise postpone/skip the outdoor stop and show an existing valid UC-15 proposal if available. |
| Rain at least 50 mm/hour at any remaining POI, arrival within three hours | Prominent persisted `SevereWeather` warning and one push. |
| Thunderstorm at any remaining POI, arrival within three hours | Persisted `SevereWeather` warning and one push. |
| Verified official severe-weather event at the remaining POI's planned time | `Moderate`, `Severe`, `Extreme` or explicit safety directive creates a persisted warning/push when arrival is within three hours. `Minor` has no UC-14 output in this release. Unknown severity is an operational review-needed result, not a safe result or Traveler push. |
| Next `Pending` POI's planned arrival is past, but fresh realtime data shows at least 10 mm/hour rain at a reviewed Outdoor POI, at least 50 mm/hour rain at any POI, or a thunderstorm | Persisted `Warning` labeled â€œcurrently at the destinationâ€; never claim this is the Traveler's arrival forecast. No moderate-rain preparation push from an overdue planned time. |

Forecasts more than three hours before a visit do not create a persisted UC-14 weather alert or
push. For the same POI and planned time, crossing from moderate rain to at least 10
mm/hour, or from below 50 to at least 50 mm/hour, resolves the lower-band alert and creates one
new higher-priority warning. This escalation may push despite the moderate-rain cooldown. A repeat
scan in the same band updates the existing condition without a new push.

For Tomorrow.io government-issued Severe Weather Events, require a stable event ID, issuing source,
time and affected geometry. The checked `GET /v4/events` route returned HTTP 200 but zero events for
the tested Vietnam locations; it did not prove a usable Vietnamese event payload. Keep this event
path disabled until representative coverage and fields are verified. Do not invent an identity from
title/time/area. Custom threshold Insights are forecast conditions, not official authority alerts.
Keep the API key in Backend configuration/secrets only.

Official-event coverage for Vietnam and forecast fields/request limits are separate feature gates.
Failure to verify official events disables only that path; forecast rain/thunderstorm can run after
its contract is verified. Cache provider responses by coordinates and time bucket for at most one
10-minute scan interval. An API failure or missing forecast hour is `Unknown`, never proof of clear
weather, and cannot resolve an open weather incident.

The tested Da Nang point response at 2026-10-28T06:45:00Z was `rainIntensity=9.23` mm/hour and
`weatherCode=4001`. It does not establish conditions at another POI or at a future visit time.
The tested hourly forecast returned 120 entries; flood/thunderstorm event queries returned zero
events, which does not establish official-event coverage for Vietnam.

## Severity and message contract

TripMate assigns `Info` to transient/persisted moderate-rain preparation and `Warning` to
`SevereWeather`, `ScheduleDelay`, `RouteDeviation` and `POIClosure`. A Tomorrow.io official event's
source severity helps decide whether a weather warning is created; it is not automatically the UI
severity.

Do not add an Incident severity column. Derive severity from incident type and weather source key.
Backend returns `messageCode` plus structured data; Mobile localizes presentation text.

| Type | `messageCode` | Structured fields |
| --- | --- | --- |
| Transient or persisted `WeatherAdvisory` | `trip_alert.weather_advisory` | affected item, forecast time, rain intensity in mm/hour; `incidentId` only when persisted |
| Weather incident | `trip_alert.severe_weather` | affected item/area, weather basis and provider time, intensity or official source event |
| Delay | `trip_alert.schedule_delay` | affected item, planned arrival |
| Deviation | `trip_alert.route_deviation` | target item |
| POI closure | `trip_alert.poi_closure` | affected item/POI, planned visit time |

`description` is nullable provider/source context after content filtering. It is not canonical UI
copy. Push payload contains `incidentId`, `sessionId`, `type` and navigation metadata only; it must
not expose trip detail in lock-screen text.

## REST API

All endpoints require the `Traveler` role. The authenticated user, never a route ID alone, controls
authorization. Follow existing CQRS, `Result<T>`/`HandleFailure`, validation and error conventions.

### List alerts

```http
GET /api/v1/navigation-sessions/{sessionId}/alerts
```

Return newest first, including resolved alerts. Each item contains `incidentId`, `type`, `severity`,
`messageCode`, `description?`, structured reason/affected-item data, `detectedAtUtc`,
`resolvedAtUtc?`, `isRead` and `reroutingProposalId?`. An owned session with no alerts returns `200`
and an empty list.
For weather alerts, include `weatherBasis = ForecastAtPlannedTime | CurrentAtDestination | OfficialEvent` and
`weatherAtUtc` from the provider. These fields are nullable for non-weather alert types. The Mobile
client must not infer a forecast/current distinction from free-text description.
Include `canSkipAffectedStop` for an active severe-weather alert only when the affected POI is
currently classified as reviewed `Outdoor`; Mobile also checks that the affected item remains
`Pending` and asks the Traveler to confirm. The flag does not perform a skip.
Include `weatherCondition = ModerateRain | HeavyRain | ExtremeRain | Thunderstorm` when the
detector knows the condition, so Mobile does not label a thunderstorm as heavy rain.

### Read transient weather advisories

```http
GET /api/v1/navigation-sessions/{sessionId}/weather-advisories
```

The session must still be open. Return current Outdoor moderate-rain advisories for a visit within
three hours that **have not become a persisted `WeatherAdvisory`** (because the visit is more than
one hour away or the preparation-push cooldown suppressed persistence). Use
`advisoryKey = rain-moderate:{itemId}:{plannedArrivalUtc}`, `severity = Info`, `messageCode`,
`affectedItemId`, `forecastAtUtc` and `rainIntensityMmPerHour`. These transient advisories have no
read state and never appear in alert history. A persisted `WeatherAdvisory` appears through the
normal Alerts endpoint and must not be returned here as a duplicate banner.

### Acknowledge an alert

```http
PUT /api/v1/navigation-sessions/{sessionId}/alerts/{incidentId}/acknowledgement
```

Return `204` on first call, replay, and a resolved alert. Set `Status = Read` and `read_at` once.
Return `404` for a legacy/non-TripAlert incident, a mismatched session or another user. Dismissing a
banner does not call this endpoint.

### Report route deviation

```http
POST /api/v1/navigation-sessions/{sessionId}/deviations
Content-Type: application/json

{
  "eventId": "7f663104-83a1-4384-b673-f586cb975f15",
  "observedAtUtc": "2026-10-22T04:00:00Z",
  "targetItemId": 123
}
```

The payload never accepts coordinates, client text or client severity. After ownership and shape
validation, execute in this order:

1. Find a previously created incident by `(sessionId, eventId)` and return it even if the session has
   ended. The same ID with a different target returns `409`.
2. For a new ID, require an open session and a target in its snapshot. `Pending` and `Skipped` are
   allowed; `Reached` is rejected.
3. If that target already has an open deviation incident, return it without a new push.
4. Enforce a configurable five-minute cooldown per session. Return `429` with `Retry-After` when
   blocked.
5. Otherwise create the Incident, Notification and delivery work in one transaction.

Lock the session across steps 3â€“5 so concurrent requests for different targets cannot both pass the
cooldown. Only event IDs that actually created an incident have long-term replay guarantees.
Validate `observedAtUtc`, but do not store it or compare it on replay.

### Register a push device

```http
PUT /api/v1/me/push-devices/{installationId}
DELETE /api/v1/me/push-devices/{installationId}
```

`PUT` associates the authenticated user, installation UUID and current FCM token. Token rotation is
idempotent. If the same installation changes accounts, transfer ownership atomically. `DELETE` is
idempotent and used before logout. Never return or log the raw token.

## Persistence

Use one explicit, rerunnable SQL migration. Do not edit the baseline schema and do not use EF
migrations.

### Existing tables

`trip.Incidents` already has `session_id`, incident type, description, detection/resolution times and
weather link. Add these nullable columns while preserving legacy writers:

| Column | Purpose |
| --- | --- |
| `affected_itinerary_item_id` | Identify the affected session snapshot item; composite FK to `trip.TripSessionItems`. |
| `source_key` | Stable server-computed source identity for open-condition deduplication. |
| `client_event_id` | Replay identity for deviation events that created an incident. |
| `weather_basis` | Weather source context: planned forecast, current destination or official event. Null for non-weather incidents. |
| `weather_at_utc` | Provider forecast/current/event timestamp shown to Travelers. Null for non-weather incidents. |
| `rain_intensity_mm_per_hour` | Structured rain rate for weather alert detail. Null when unavailable or for a non-rain event. |

The existing SQL `incident_type` check constraint allows four values. Extend that constraint to
allow `WeatherAdvisory` while retaining all current values, and add a matching domain constant.
This is necessary for a persisted moderate-rain preparation push; do not encode moderate rain as
`SevereWeather`. The migration must be idempotent and preserve existing Incident rows.

Add filtered unique indexes for `(session_id, incident_type, source_key)` while unresolved and
`(session_id, client_event_id)` when the client event is non-null. Keeping fields nullable preserves
legacy writers and seed data; every UC-14 creation path must explicitly set the proper source key.

Add nullable `read_at` to `dbo.Notifications`. Add a filtered unique index on
`(user_id, related_entity_id)` where `type = 'TripAlert'` and
`related_entity_type = 'Incident'`. Do not alter other notification writers.

Do not change `trip.WeatherEvents`: it is an existing UC-59 read model whose required severity does
not match this provider contract. UC-14 stores provider identity in Incident `source_key` and leaves
`weather_event_id` null.

### New UC-14 tables

- `trip.AlertDeliveryWork`: one row per push-eligible TripAlert Notification, including a persisted
  moderate-rain `Info` alert; unique notification
  ID, attempt count, next-attempt time, claim token and claim expiry.
- `dbo.PushDeviceTokens`: installation UUID, current user, protected FCM token, unique token hash,
  created/updated timestamps.

## Notification delivery

Create Incident, Notification and `AlertDeliveryWork` in the same transaction. A worker may claim a
row only when all conditions hold: Notification is `Pending` or retryable `Failed`; next attempt is
due; fewer than three attempts have been claimed; no live lease exists; Incident is unresolved; and
session is open. Check for at least one valid device token before claim. No token stops work without
incrementing attempts.

Each successful claim increments the attempt count and holds a two-minute lease. Retry after at
least 15 minutes, then 30 minutes, with three total attempts. Terminal conditions set the next
attempt to null. An expired lease may be reclaimed within the attempt limit.

Only the matching claim token may record a result. All sender and lease-sweep updates are conditional
so `Read` can never move back to `Pending`, `Sent` or `Failed`. `Sent` means FCM accepted at least one
current token. Invalid tokens are deactivated. Multiple-device per-token delivery history is outside
this release.

The unavoidable crash window after FCM acceptance and before result persistence can duplicate a
push. Payload identity lets Mobile deduplicate by `incidentId`. A token registered after work became
terminal does not trigger delivery of old alerts.

## Acceptance criteria

- Concurrent scans for one condition create one Incident, one TripAlert Notification and one work
  item.
- Weather boundaries cover below/exactly 2.5, 10 and 50 mm/hour, Outdoor versus Indoor, and arrivals
  just outside/inside one and three hours. Moderate rain at an imminent Outdoor stop creates at most
  one persisted `Info` preparation alert per session in a three-hour window; a heavier band creates
  a `Warning` and escalates once even after that preparation push. Heavy rain at an Indoor POI below
  50 mm/hour alone does not create a weather incident. Provider failure creates no false alert and
  resolves no existing incident.
- A verified official event at the correct POI/time warns according to source severity; missing
  stable identity, source, area or time keeps the official-event path gated and cannot be treated as
  safe. Repeated scans and event updates do not spam pushes.
- Concurrent deviation requests honor replay, open-target deduplication and session-wide cooldown;
  Backend never receives coordinates.
- Alert list and acknowledgement enforce ownership; acknowledgement after resolve succeeds; legacy
  incidents are absent.
- No-token delivery consumes zero attempts. Retry, worker crash, expired lease, session end,
  incident resolution and acknowledgement races obey the terminal rules; `Read` remains final.
- OpenAPI documents nullable fields, response examples, error codes and `Retry-After` before Mobile
  integration.

## Accepted limitations

- A forgotten manual arrival confirmation can cause a delay alert.
- A second deviation to the same target while its incident remains open creates no second push.
- A material weather band escalation (moderate to heavy or heavy to at least 50 mm/hour) may produce
  one new push; a repeated scan within the same band does not.
- FCM delivery is best effort; the Alerts list is the recovery path.
- Hourly forecast at a POI can miss a short local shower, and the 10-minute scan target is not an
  instant safety guarantee. The app must show forecast/provider time and never promise continuous
  monitoring.
- `Outdoor` alone does not establish activity-specific safety for boating, swimming or mountain
  routes. UC-14 weather warnings are guidance; verified activity-specific hazard rules need their
  own source and approved thresholds. Moderate-rain preparation push volume needs a user pilot.
- UC-14 adds no traffic/safety provider, GPS history, offline/GPS-disabled local warning, Web/Admin UI
  or automatic reroute.

## Source notes for this revision

- [WMO precipitation intensity table](https://www.weather.gov/media/epz/mesonet/CWOP-WMO8.pdf): rain bands below 2.5, 2.5 to below 10, 10 to below 50 and at least 50 mm/hour. The one-hour/three-hour notification windows and three-hour preparation-push limit are proposed TripMate UX policy, not meteorological standards.
- [Tomorrow.io forecast API](https://docs.tomorrow.io/reference/weather-forecast), [data-layer definitions](https://docs.tomorrow.io/reference/data-layers-core), [weather codes](https://docs.tomorrow.io/reference/data-layers-weather-codes), [Events API](https://docs.tomorrow.io/reference/events-overview) and [Vietnam coverage note](https://support.tomorrow.io/hc/en-us/articles/38449155149460-Severe-Weather-Events-Global-Coverage). API access alone does not verify live official-event coverage.
