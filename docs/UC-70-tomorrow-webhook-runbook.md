# UC-70 — Tomorrow.io webhook runbook

For the person enabling Tomorrow.io weather reviews after the UC-70 merge. Every step is manual: the
Backend never creates Insights or Alerts and never changes the Tomorrow.io account. Never paste a secret,
API key or JWT into this file, a ticket, a screenshot or a log.

Tomorrow.io only creates a `WeatherSafetyReview` for the owning Operator. It never cancels, stops or
refunds a departure.

## 1. Tomorrow.io dashboard

1. Create or open the Custom Insight for the weather condition TripMate reviews. Vietnam has no Insight
   Category (Severe Weather Event) coverage confirmed for this account, so use a Custom Insight.
2. Create an Alert on that Insight with `PRIOR`, `START` and `END` notifications and make it active.
3. Copy the Alert id; it becomes `TomorrowIo__AlertId`.
4. Development → Webhooks → Alerts: add the public HTTPS URL
   `https://<staging-host>/api/v1/webhooks/tomorrow/alerts` and turn its status toggle on.
5. Copy the webhook secret straight into the secret store as `TomorrowIo__WebhookSecret`.
6. Check in the dashboard that the plan includes Alerts, Custom Insights and webhooks. A 401/403 from the
   API in step 4 below means a credential or entitlement problem, not a TripMate fault.

## 2. Staging configuration

Set in the staging secret store (values never in git):

| Setting | Value |
| --- | --- |
| `TomorrowIo__Enabled` | `true` |
| `TomorrowIo__ApiKey` | API key (or keep the existing `Tomorrow__ApiKey`; it is used as fallback) |
| `TomorrowIo__WebhookSecret` | the webhook secret from step 1.5 |
| `TomorrowIo__AlertId` | the Alert id from step 1.3 |
| `TomorrowIo__BaseUrl` | `https://api.tomorrow.io` |

Startup fails fast if `Enabled=true` and any of ApiKey, WebhookSecret, AlertId or an https BaseUrl is
missing. FCM credentials and `NEXT_PUBLIC_SIGNALR_URL` (Operator Web) must be configured as for the
other UC-70 notices.

## 3. Database

Run `database/apply-schema.sh` against staging. It reruns every migration; all are idempotent and roll
back on any shape mismatch. Expected UC-70 objects: `commerce.EmergencyTourCancellationOperations`,
`trip.WeatherAlertInboxItems`, `trip.WeatherSafetyReviews` (with `IX_WeatherSafetyReviews_StatusDeadline`),
`trip.WeatherLocationMappings`, and no `trip.WeatherEventEvaluations`.

## 4. Deploy and link Locations

1. Deploy the merged Backend with the settings above. The webhook is live from this point.
2. Create a staging departure with verified meeting coordinates and at least one stop marked
   weather-required, starting within `TomorrowIo__LocationMonitoringLookaheadDays` (default 7).
3. Wait for the location sync (`LocationSyncIntervalHours`, default 6; or restart the API to run it).
4. Confirm the links with the API key from the secret store, sent as a header so it stays out of history:

   ```bash
   curl -s -H "apikey: $TOMORROW_API_KEY" \
     "https://api.tomorrow.io/v4/alerts/$TOMORROW_ALERT_ID/locations"
   ```

   The `data.locations` array must contain every `tomorrow_location_id` of
   `trip.WeatherLocationMappings WHERE is_active = 1`. Open one Location on the Tomorrow.io dashboard and
   check it sits on the departure's meeting point (GeoJSON is longitude, latitude).

## 5. Real webhook gate (staging, before production)

Capture one real delivery. Do not hand-craft it.

1. Wait for the Insight to become truthy at a linked Location. To force one, give the staging departure a
   meeting point (or weather-required stop) where the Insight is truthy and let the location sync create
   and link it: a Location TripMate did not create is ignored by design. Tomorrow.io retries a failed
   delivery twice, a few minutes apart.
2. In the Tomorrow.io Activity page, record the delivery time and HTTP status (no headers, no body).
3. Verify on staging:

   | Check | Expected |
   | --- | --- |
   | API log for the request | `202`, no `Webhook authentication rejected` |
   | `trip.WeatherAlertInboxItems` | one row, `status = 'Processed'`, `processed_at` set, `last_error` NULL |
   | `trip.WeatherEvents` | one row, `provider = 'TomorrowIo'`, `provider_event_id` starting with the Alert id |
   | `trip.WeatherSafetyReviews` | one `PendingAcknowledgement` review for the departure |
   | Operator Web | the review appears live (SignalR) and the Operator receives the push |
   | Departure, bookings, sessions | unchanged: still `Scheduled`, `Confirmed`, not interrupted |
   | `payment.Refunds`, payouts | no new row |

4. If the delivery was rejected with 401, read the `Webhook authentication rejected` reason in the API
   log (it never contains the secret or signature) and compare the header's `t` unit (seconds or
   milliseconds; both are accepted) and the configured secret.

## 6. Production

Only after section 5 passes: repeat sections 1–4 for production with its own webhook URL, secret and,
if separate, Alert. Until then keep `TomorrowIo__Enabled=false` in production.

## Known limits

- The body is not signed by Tomorrow.io; only the timestamp is. Optionally restrict the webhook route to
  the documented sender IP `35.231.166.26` at the proxy.
- A Location deleted on the Tomorrow.io side is not recreated automatically; deactivate its mapping so the
  next sync creates a new one.
- 429 is retried on the next cycle (sync 6 h, reconciliation 15 min); `Retry-After` is not read.

- The Emergency Stop rate limiter is in-process and exact with one Backend replica only.
- An `END` (all-clear) notification never closes a review; the Operator decides.
- `PRIOR`/`START` carry no end time; the event is treated as lasting 24 hours for matching.
