# UC-14 — Real-time alerts production runbook

For the person turning UC-14 on in production. Never paste an API key, service-account JSON or JWT into
this file, a ticket, a screenshot or a log.

UC-14 detects four things for an open navigation session: schedule delay, POI closure, weather (rain,
thunderstorm) and route deviation (reported by Mobile). It never changes an itinerary.

Two switches decide whether it works, and both fail **silently** if forgotten:

| Without | Result |
| --- | --- |
| `Tomorrow__Enabled=true` + key | No rain or thunderstorm alerts. Delay, closure and deviation still work. |
| Firebase Admin credentials | No push. The in-app Alerts list still works. |

Since this release the Backend logs a startup warning for each missing switch (search the log for
`UC-14 weather alerts are off` and `UC-14 push is unavailable`). No warning means both are set.

## 1. Configuration (secret store, never git)

| Setting | Value |
| --- | --- |
| `Tomorrow__Enabled` | `true` |
| `Tomorrow__ApiKey` | Tomorrow.io API key (startup fails fast if Enabled with no key) |
| `FIREBASE_SERVICE_ACCOUNT_KEY_JSON` or `Firebase__ServiceAccountKeyJson` | the whole Firebase Admin JSON as one secret value (the Backend accepts either name). Use this on a host with no file mount (Railway). Firebase sign-in already needs it, so it is usually set. |
| `GOOGLE_APPLICATION_CREDENTIALS` | alternative: path of a mounted JSON file (`docker-compose.yml` mounts `./secrets/tripmate-firebase-admin.json` to `/run/secrets/tripmate-firebase-admin.json`) |
| `Firebase__ProjectId` | `tripmate-82be3` (already in `docker-compose.yml`) |

The UC-70 settings `TomorrowIo__Enabled`, `TomorrowIo__ApiKey`, `TomorrowIo__AlertId` and
`TomorrowIo__WebhookSecret` are a different feature (Operator weather reviews). They do **not** turn on
UC-14 weather: `Tomorrow__Enabled` defaults to `false` and must be set on its own. `TomorrowIo__ApiKey`
falls back to `Tomorrow__ApiKey`, but not the other way round, so set both keys.

Optional tuning (defaults are in `appsettings.json`): `TripAlerts__ScanInterval` (10 min),
`TripAlerts__ScheduleDelayThreshold` (30 min), `TripAlerts__AdvisoryRainMmPerHour` /
`SevereRainMmPerHour` / `ExtremeRainMmPerHour` (2.5 / 10 / 50, must stay increasing).

The Mobile release build needs its own `google-services.json` for the same Firebase project.

## 2. Database

Apply through the manual **DB migrate (Azure SQL)** workflow on `develop`; do a dry run first.
It applies every file in `database/migrations/` and records it in a ledger. UC-14 needs:

- `20261022_add_uc14_alert_contract.sql`
- `20261028_uc14_tomorrow_weather.sql`

If production already received them by hand, baseline them with `stamp_only` instead of re-running.
Confirm all objects exist (every value must be non-NULL):

```sql
SELECT OBJECT_ID(N'trip.AlertDeliveryWork')                              AS delivery_work,
       OBJECT_ID(N'dbo.PushDeviceTokens')                                AS push_tokens,
       COL_LENGTH(N'trip.Incidents', N'weather_basis')                   AS incident_weather,
       COL_LENGTH(N'catalog.POIs', N'indoor_outdoor_reviewed_at_utc')    AS poi_reviewed;
```

## 3. POI review (decides which POIs get rain alerts)

Rain-only alerts fire only for POIs whose `indoor_outdoor = 'Outdoor'` **and**
`indoor_outdoor_reviewed_at_utc` is set. Thunderstorm and rain >= 50 mm/h alert for any POI. `Mixed`
and `Indoor` POIs never get rain-only alerts.

1. Run `database/reports/uc14_poi_weather_readiness.sql`. It lists active POIs not yet reviewed.
2. A person checks each listed place, fixes `indoor_outdoor` where wrong, then sets
   `indoor_outdoor_reviewed_at_utc = SYSUTCDATETIME()` for that place only.
3. Do **not** bulk-stamp the whole table: the stamp means "a person checked this place".

`database/seeds/20261108_seed_uc14_weather_poi_sample.sql` adds seven reviewed sample POIs for
development and UAT. Do not run it in production.

## 4. Deploy and verify

1. Deploy the Backend with the settings above.
2. Read the startup log: neither UC-14 warning may appear.
3. On a real Android device: sign in, allow notifications, start a navigation session.
4. Check that the device registered (a row with `deactivated_at IS NULL`; never select `protected_token`):

   ```sql
   SELECT COUNT(*) AS active_devices FROM dbo.PushDeviceTokens WHERE deactivated_at IS NULL;
   ```

5. Trigger a delay (do not mark the first stop reached; wait `ScheduleDelayThreshold` + one scan, up to
   about 40 minutes) or report a deviation from the app. Then check the alert and its delivery:

   ```sql
   SELECT TOP 20 i.incident_type, i.source_key, i.detected_at, i.resolved_at, n.status, n.sent_at, n.read_at
   FROM trip.Incidents AS i
   LEFT JOIN dbo.Notifications AS n
       ON n.related_entity_type = N'Incident' AND n.related_entity_id = i.incident_id
   ORDER BY i.incident_id DESC;
   ```

   Expected: the incident exists, the notification becomes sent, the phone shows the push, tapping it
   opens the Alerts list, and acknowledging sets `read_at`.
6. Weather: on a day with rain or thunderstorm at a reviewed Outdoor stop within 3 hours, the session
   gets a `WeatherAdvisory` (2.5 to under 10 mm/h, at most one push per session per 3 hours) or a
   `SevereWeather` incident. This path has been verified only with fixed test data and a live provider
   smoke run on a dry day, so treat the first real rain as the weather acceptance check.

## 5. Turning it off

Set `Tomorrow__Enabled=false` and redeploy to stop weather alerts. Remove the Firebase credential to stop
push. No schema rollback is needed; open incidents stay readable in the Alerts list.

## Known limits

- Scan interval is 10 minutes; a push is not guaranteed within it. The Alerts list is authoritative.
- Push retries: 3 attempts, then the alert stays only in the Alerts list.
- Mobile refreshes alerts on start, on a push hint, on app resume and on reconnect; there is no timer.
- A POI that is open past midnight is treated as closed after its closing time.
- Push tokens are stored with ASP.NET Data Protection. If the key ring is not persisted across restarts or
  shared between instances, tokens stored earlier cannot be read and pushes fail until the device
  registers again. Persist the key ring if the host does not already do so: on Railway, the container
  filesystem is replaced on every deploy unless a volume is attached to the Backend service.
- Tomorrow.io responses are cached in 10-minute buckets per location.
