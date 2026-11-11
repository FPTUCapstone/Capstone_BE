# UC-70 — Cancel or emergency-stop a commercial departure

Canonical cross-module specification: `../../spec.md`.

## Backend contract

UC-70 affects one owned `TourSchedule`; it never cancels every departure of a Tour.

| Situation | TourSchedule | Booking | TripSession |
| --- | --- | --- | --- |
| Before start | `Cancelled` | `Cancelled` | linked open session `Interrupted` (a session can start before departure) |
| Active trip | `EmergencyStopped` | `Interrupted` | linked open session `Interrupted` |

UC-70 records the decision and its cancellation context only. It has no refund handoff: it creates no
`Refund`, calculates no amount, adds no payment, refund or payout schema, and neither calls nor mutates
those modules. Việc thanh toán và hoàn tiền được xử lý riêng theo chính sách hủy booking áp dụng
(UC-42 owns refunds, UC-71 owns payout).

Only the owning active, approved Tour Operator may preview or confirm the action. Tomorrow.io alerts only
create a `WeatherSafetyReview`; the Operator decides manually and nothing cancels or stops a departure
automatically. TripMate Admin is not a pre-approval
step; Admin/Ops may audit or reconcile a committed decision afterward.

## API

- `GET /api/v1/operator/tours/{tourId}/schedules/{scheduleId}/emergency-impact`
- `POST /api/v1/operator/tours/{tourId}/schedules/{scheduleId}/cancel-before-departure`
- `POST /api/v1/operator/tours/{tourId}/schedules/{scheduleId}/emergency-stop`
- `PUT /api/v1/operator/tours/{tourId}/schedules/{scheduleId}/emergency-evidence`
  expresses the desired evidence state: supplying fields adds or replaces the stored
  evidence; `withdrawEvidence: true` clears it (and must not carry evidence values).
  Both paths stay `OperatorProvided`, never reopen the departure and never change
  Booking, TripSession or Ticket state; `addEvidenceLater` is a commit-time
  audit flag, not a permanent bar to updating evidence.
- `GET /api/v1/traveler/tour-bookings/{bookingId}/emergency-status`
- `POST /api/v1/traveler/tour-bookings/{bookingId}/emergency-status/acknowledge`

Both POST endpoints require `Idempotency-Key` and the latest `impactVersion` returned by
the preview. A changed impact returns `tour_schedule.concurrent_state_changed`.
Push payload copy is generic. The authenticated, active booking owner loads reason, safety
instructions and emergency contact from `emergency-status`.

## Invariants

- At payment confirmation the Backend materializes one immutable BookedTour itinerary
  snapshot per booking (`Itinerary.SourceBookingId = Booking.Id`, `SourceTourId = Tour.Id`,
  `SourceType = BookedTour`, `ValidFromUtc/ValidToUtc = TourSchedule.Start/EndAtUtc`).
  Items follow the template ordered by `DayNo` then `SequenceNo` (flattened 1..N);
  arrival = local departure date + (DayNo − 1) + `PlannedTime` converted with
  `Tour.TimeZoneId`; departure = arrival + `StayDurationMinutes`; every commercial stop is
  mandatory; template edits after payment never change the snapshot. A sellable tour must
  have at least one stop and every stop must carry `PlannedTime` (`tour_booking.itinerary_not_bookable`
  otherwise). Materialization failures (missing times, empty template, unknown timezone,
  invalid/ambiguous DST local times, overlapping or out-of-window stops) keep payment truth
  intact, create no empty itinerary, and are audited as `BOOKING_ITINERARY_MATERIALIZATION_FAILED`
  with a stable, non-PII failure code. Replays and concurrent completions produce exactly one
  snapshot; the unique filtered index is the last line of defense and conflicts answer as replays.
- `trip.TripSessions.completion_reason` accepts the UC-70 emergency stop reasons
  (`WeatherEmergency`, `AuthorityDirective`, `DestinationUnavailable`, `TransportFailure`,
  `SafetyIncident`, `Other`) in addition to the navigation lifecycle reasons.
- One committed operation per schedule and one operation per actor/command ID.
- State, pending notifications and audit are persisted in one serializable transaction.
- That transaction also closes every open `WeatherSafetyReview` of the departure. The `Closed` realtime hint is sent only after commit; a replay closes nothing.
- No FCM or payment gateway call occurs inside that transaction.
- Unused tickets/QR are revoked; used ticket history is preserved.
- Active commercial sessions are linked exactly by `Itinerary.SourceBookingId`; the departure is
  derived through `Booking.TourScheduleId`.
- An open session without that exact link blocks both decisions with `TripSessionLinkUnverified`.
- Late payment and payment reconciliation after an emergency cancellation
  are owned by the Payment module and are outside UC-70.
- Emergency Stop first checks that the departure exists and belongs to the Operator, then the password:
  another Operator gets the same safe 404 whether its password is right or wrong, and no audit row.
- Emergency Stop is limited per Operator (10 attempts in 5 minutes, then HTTP 429), and each wrong password
  leaves a failed audit entry without the password. The limiter is in-process: it is exact with one Backend
  replica only; several replicas need a distributed limiter.
- Only a deadlock victim or a lost race on the decision table's unique keys (one decision per departure,
  one per Operator command) is retried; FK, CHECK and schema failures surface at once.
- Traveler acknowledgment records receipt idempotently; it never infers that the Traveler is safe
  and changes nothing else.
- Operator-supplied evidence is stored as `OperatorProvided`, never presented as independently verified.
  Missing evidence does not block an Emergency Stop; `AuthorityDirective` may be marked for follow-up.
- Active stop requires immediate instructions and an emergency contact.
- Failed/missed push does not reverse the committed decision; persisted state is authoritative.

## Explicitly excluded

- Weather auto-cancel, evidence registry/Admin approval UI, reschedule, SMS/voice and shelter/evacuation routing.
- Refund eligibility, amounts, cases and settlement (UC-42); payout (UC-71); commission.
- Production migration execution, deployment, commit or push.

## Confirmed authentication decision

- Active Emergency Stop requires the owning Operator's current password in the final confirmation.
  Backend verifies it but never persists, logs or includes it in the idempotency hash. Preview does
  not require step-up; an account without a usable password must use password recovery first.

## Weather review (Tomorrow.io)

- `POST /api/v1/webhooks/tomorrow/alerts` verifies `X-Signature: t={timestamp},sig={hex}` where `sig` is
  HMAC-SHA256 of the raw timestamp string with the webhook secret (the body is not signed), within
  `WebhookToleranceSeconds`. 401 bad/missing signature, 400 malformed JSON, 413 too large, 415 not JSON,
  202 stored (a re-delivered body is stored once).
- Only notifications of `TomorrowIo:AlertId` become evidence; other Alerts, `END` (all-clear) and
  Locations TripMate does not monitor are processed as no-ops. Matching uses the Location id, resolved to
  the meeting point or weather-required stop it monitors.
- Monitored points are the meeting point and stops with `IsRequiredForWeather = true` of departures that
  have not ended and start within `LocationMonitoringLookaheadDays`. Each Location is created, linked to
  the Alert, re-linked when missing, and unlinked before it is deleted once it expires. Any provider
  failure ends that cycle's remote calls; the next cycle retries.
- Inbox completion is a compare-and-set on the lease (owner and attempt), so a reclaimed item can be
  completed only by its new owner.

## Deployment prerequisites

- An active Custom Insight and Alert approved on the Tomorrow.io dashboard; its id in `TomorrowIo__AlertId`.
- `TomorrowIo__Enabled=true` with `TomorrowIo__ApiKey` (or `Tomorrow__ApiKey`), `TomorrowIo__WebhookSecret`
  and an https `TomorrowIo__BaseUrl`; startup validation refuses a partial configuration.
- An Alerts webhook on a public HTTPS URL `/api/v1/webhooks/tomorrow/alerts`; its secret in the secret store.
- Linked Locations confirmed with `GET /v4/alerts/{alertId}/locations` after the first location sync.
- FCM credentials for push and `NEXT_PUBLIC_SIGNALR_URL` for the Operator Web hub.
- Migrations applied with `database/apply-schema.sh` (it reruns every migration; all are idempotent).
- Runbook and the real-webhook gate: `docs/UC-70-tomorrow-webhook-runbook.md`.
