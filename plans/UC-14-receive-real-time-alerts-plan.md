# UC-14 Receive Real-Time Alerts — Backend Implementation Plan

**Status:** Approved by the user on 2026-10-22.
**Specification:** [`../specs/UC-14-receive-real-time-alerts-spec.md`](../specs/UC-14-receive-real-time-alerts-spec.md)

## Dependency order

`B0 → B1 → B2 → B3 → B4 → B5 → B6 → B7`

## Tasks

### B0 — Audit and baseline

Inventory current Incident/Notification writers, UC-13 progress semantics, notification enum
storage, FCM bootstrap and SQL test facilities. Record clean build and test counts. Verify the One
Call 4.0 timeline and alert-detail payload without exposing the API key.

### B1 — Additive persistence

Add Incident source/affected item/client event fields and `Notification.read_at`; create filtered
unique indexes, delivery-work and device-token tables; update EF mappings and an idempotent SQL
migration. Preserve legacy Incident writers with nullable fields.

### B2 — Alert application contract

Implement transactional Incident/Notification/delivery creation, list and acknowledgement
handlers, message-code response mapping and route-deviation report ordering. Add focused tests for
replay after resolution, mismatch, target state, cooldown and read idempotency.

### B3 — Detection scan

Implement the scoped scanner and 10-minute hosted loop for schedule delay, POI closure and weather.
Resolve only from successful source data or UC-13 progress. Test duplicate scans, provider failure,
progress resolution and legacy isolation.

### B4 — OpenWeather 4.0

Implement a typed HttpClient behind `ITripWeatherProvider`, 10-minute coordinate/time-bucket cache,
rain/thunder parsing and official alert detail. Keep it disabled until server configuration supplies
the key and rollout flags. Test with stored JSON fixtures and no network calls.

### B5 — Advisory API

Return moderate-rain `Info` advisories for pending outdoor stops without persistence. Test threshold,
indoor filtering, ownership and zero Incident/Notification writes.

### B6 — Device and push delivery

Implement authenticated installation register/remove endpoints. Atomically claim eligible work with
a two-minute lease, send to active device tokens, retry after 15/30 minutes up to three attempts,
and preserve terminal `Read`. Test device transfer, no-token stop, expired claims, resolved/session
stops and concurrent claims.

### B7 — Verification and handoff

Run format verification, build, unit, SQL-gated integration and API integration suites. Review the
diff against repository standards and this spec. Document required environment settings,
migration order, rollout flags and accepted limits. Mobile implementation remains a separate task.
