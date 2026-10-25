# UC-14 Receive Real-Time Alerts — Backend Specification

**Status:** Approved by the user on 2026-10-22.
**Source:** [`../../.scratch/uc14-real-time-alerts/spec.md`](../../.scratch/uc14-real-time-alerts/spec.md)

## Scope

Backend monitors an open UC-13 `TripSession`, persists alerts for its Traveler, exposes the alert
and advisory APIs, and sends private FCM notifications. Mobile detects route deviation and sends
only an event ID, target item and observation time. UC-14 never uploads Traveler GPS and never
applies a UC-15 itinerary adjustment.

The supported incident types are `SevereWeather`, `ScheduleDelay`, `RouteDeviation` and
`POIClosure`. Traffic and general safety events are outside this release.

## Detection contract

Backend scans sessions in `Navigating` or `Exploring` every configurable 10 minutes.

| Type | Create when | Open-incident source key | Resolve when |
| --- | --- | --- | --- |
| Schedule delay | The next pending item is over 30 minutes late. `ReachedAtUtc` remains a manual UC-13 signal. | `delay:{itemId}` | Reached, skipped, or session ended. |
| POI closure | POI is inactive, or known hours plus a trusted itinerary timezone prove it closed at planned arrival. Missing hours/timezone mean unknown. | `closure:{itemId}` | POI recovers, item progresses, or session ends. |
| Severe weather | Official OpenWeather alert overlaps the visit; thunderstorm; or rain is at least 10 mm/hour. | `openweather4:{alertId}` or `weather:{condition}:{itemId}:{plannedArrivalUtc}` | A successful later forecast no longer meets the condition, item progresses, or session ends. Provider failure does not resolve it. |
| Route deviation | UC-13 reports a new off-route transition for a pending or skipped snapshot item. | `deviation:{targetItemId}` | Reached, skipped after detection, or session ended. |

Rain from 2.5 to under 10 mm/hour at an outdoor POI is an `Info` advisory. It is returned from the
advisory endpoint and creates no Incident, Notification or push. All persisted incidents use
TripMate severity `Warning`.

OpenWeather One Call 4.0 is server-only. Use the 15-minute timeline record nearest
`PlannedArrivalUtc`, `rain.1h` as mm/hour, weather IDs 200–299 as thunderstorm, and alert detail by
stable alert ID. If `event` is blank, use the first tag, then a generic label. Official-alert and
forecast paths can be enabled independently during rollout; the API key must come from secrets or
environment configuration and must never be logged or returned.

## Persistence and consistency

`trip.Incidents` gains nullable `affected_itinerary_item_id`, `source_key`, and `client_event_id`.
Existing rows remain legacy history. Filtered unique indexes protect one open source key per
session/type and one persisted client event ID per session.

`dbo.Notifications` gains nullable `read_at`. `Read` is terminal: push completion, retry and lease
sweeps use conditional updates and cannot change it. A filtered unique index permits one
`TripAlert` Notification for each user/Incident.

`trip.AlertDeliveryWork` stores attempt count, due time and a renewable two-minute claim lease.
`dbo.PushDeviceTokens` stores one installation and one SHA-256 token hash uniquely. The raw FCM
token is encrypted at rest. Registering the same installation or token transfers it to the current
authenticated account; logout deactivates it.

Incident, Notification and delivery work are inserted in one transaction before push is attempted.
No active device means terminal `Failed` with zero delivery attempts. A claim is eligible only
when the Notification is pending/failed, due, below three attempts, unclaimed/lease-expired, its
Incident is open, its session is open, and the user has an active device. Failures retry after 15
then 30 minutes. Terminal paths clear `next_attempt_at`. An expired third claim becomes terminal
without overwriting `Read`.

## APIs

- `GET /api/v1/navigation-sessions/{sessionId}/alerts`: owned alerts, newest first, including
  type, severity, message code, optional description, affected item, timestamps, read state and
  optional UC-15 proposal ID.
- `PUT /api/v1/navigation-sessions/{sessionId}/alerts/{incidentId}/acknowledgement`: idempotently
  marks the notification read, including after incident resolution.
- `POST /api/v1/navigation-sessions/{sessionId}/deviations`: body contains `eventId`,
  `observedAtUtc`, `targetItemId`. After ownership, replay a persisted event first, then validate
  session/target, return an existing open target incident, apply a five-minute session cooldown,
  then create. Same persisted event with another target returns 409. Cooldown returns 429 with
  `retryAfterSeconds`.
- `GET /api/v1/navigation-sessions/{sessionId}/weather-advisories`: current moderate-rain
  advisories for pending outdoor items; it persists nothing.
- `PUT /api/v1/me/push-devices/{installationId}` and `DELETE` on the same route register and
  deactivate the authenticated Traveler's FCM installation.

Mobile localizes `messageCode`; Backend does not send localized prose as the contract. Push carries
only routing metadata and generic preview text. The Alerts API remains authoritative after offline
or failed push delivery.

## Accepted first-release limits

- A second deviation to the same target does not push while the first incident remains open.
- Replays are guaranteed only for event IDs that created an incident; cooldown-rejected IDs are not
  stored. Mobile waits for `retryAfterSeconds` and emits a fresh event only for a fresh transition.
- Severity escalation and weather threshold oscillation do not send an extra push for an already
  open source incident.
- Notifications created without an active token are not sent later when a token is registered.
- Official alert coverage for Vietnam must be verified before enabling that path in production.

## Runtime configuration

Set `OpenWeather__ApiKey` in Backend user-secrets or the deployment secret store. Set
`OpenWeather__Enabled=true` only after timeline validation; keep
`OpenWeather__EnableOfficialAlerts=false` until Vietnam official-alert coverage is accepted.
`TripAlerts__ScanInterval` defaults to `00:10:00`. No API key belongs in Mobile or committed JSON.

## Acceptance checks

Tests cover ownership, replay ordering, cooldown and concurrent deduplication; read versus retry;
claim lease recovery and three-attempt cap; no-token, resolved-incident and ended-session stops;
rain boundaries; provider failure; POI missing-data behavior; migration/index shape; device token
transfer; and OpenWeather response fixtures. Existing Backend suites must remain green.
