# UC-70 — Cancel or emergency-stop a commercial departure

Canonical cross-module specification: `../../spec.md`.

## Backend contract

UC-70 affects one owned `TourSchedule`; it never cancels every departure of a Tour.

| Situation | TourSchedule | Booking | TripSession |
| --- | --- | --- | --- |
| Before start | `Cancelled` | `Cancelled` | linked open session `Interrupted` (a session can start before departure) |
| Active trip | `EmergencyStopped` | `Interrupted` | linked open session `Interrupted` |

Code UC-70 is SRS **UC-66 "Handle Weather Warning and Operator Cancellation"** (the SRS renumbered
UC-70 to "Synchronize Offline Trip Data").

UC-70 records the decision and hands paid bookings over to the existing refund pipeline (see
**Refund handoff**). It never calls a payment gateway, never changes payment or payout state and adds no
schema; the refund processor, Admin manual completion (UC-42/UC-67) and payout (UC-71) stay where they are.

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

## Refund handoff (SRS UC-66, BR-136, BR-143, BR-186)

Decided 2026-11-14. Applied inside the same serializable transaction as the decision.

| Booking at decision time | Cancel before departure | Emergency stop (active trip) |
| --- | --- | --- |
| `PaymentStatus = Unpaid` | No refund (BR-136) | No refund |
| Paid, one verified `Payment` transaction equal to `TotalAmount`, no earlier `Refund` | **`Refund` Pending for 100% of `TotalAmount`** | Admin review |
| Paid but payment not verifiable, or an earlier `Refund` exists | Admin review | Admin review |

- Every cancellation reason refunds 100% (BR-136: the Operator cancelled). The refund reason text says
  whether the cause was weather (`WeatherEmergency`, BR-186: refunded at once, TripMate recovers the
  Operator's share later) or another Operator reason (BR-143).
- Each refund gets a `BookingCancellationOperation` (actor `TourOperator`, the deciding Operator) because
  `Refund` requires one. Its `Idempotency-Key` is derived from the UC-70 command id and the booking id, so a
  replay or a retried transaction can never create a second refund.
- `InitiatedBy = TourOperator`. The existing refund processor and Admin manual completion take it from there.
- An emergency stop creates no refund: the unused part of an interrupted trip is reviewed by Admin case by
  case, refunding the services not yet delivered after reasonable costs already incurred (Civil Code 2015,
  Art. 427(2)); no extra compensation for force majeure (Art. 351(2)).
- "Admin review" = one in-app notice (`EmergencyRefundReviewRequired`, related `TourSchedule`) to every
  active Administrator per decision, listing each booking code, paid amount and why it needs review.
  Nothing is sent when no booking needs review.
- The decision audit `afterData.RefundHandoff` lists `SubmittedRefunds` (booking id, amount) and
  `AdminReviewBookingIds`.
- The Operator response adds `refundsSubmitted` (replays recompute it from the derived keys).
- Traveler `emergency-status` adds `refundHandling`: `RefundSubmitted` (a UC-70 refund exists for the
  booking), `UnderReview` (paid booking without one) or `NotApplicable` (unpaid), and `tourId` for
  booking another departure of the same tour.
- Admin Active Trips items add `refundReviewRequired`: the trip was interrupted by an emergency stop and
  its paid booking has no refund yet.

## Weather warnings and escalation

- When a `WeatherSafetyReview` is created, every Traveler holding a `PendingPayment` or `Confirmed` booking
  of that departure gets one in-app notice (`WeatherWarning`, SRS MSG185) with generic copy; no departure,
  booking or session state changes. It is in-app only: a push would need a new FCM contract on Mobile.
- When a review escalates after the Operator's acknowledgement deadline, every active Administrator also
  gets an in-app notice (`WeatherSafetyReviewAdminEscalated`, related `WeatherSafetyReview`).

## Differences from SRS UC-66 (documented decisions)

| SRS UC-66 says | TripMate does | Why |
| --- | --- | --- |
| Weather data from OpenWeatherMap | Tomorrow.io alerts by webhook, plus a reconciliation sweep | Push alerts on monitored Locations; no polling quota |
| Per-booking failure handling, result panel shows "Failed Items" (4.b1) | All bookings of the departure change in one serializable transaction; any failure rolls everything back and the Operator retries the same command | A departure is never half cancelled; idempotent retry is safe |
| Result panel: Bookings Cancelled, Refunds Submitted, Notifications Sent | `affectedBookingCount` and `refundsSubmitted`; push delivery is asynchronous, so no "sent" count | Push is sent after commit by the delivery queue |
| MSG151 push names the tour, date, amount and "has been refunded" | Generic push copy; details load from `emergency-status` after sign-in | Nothing personal on the lock screen; at decision time the refund is submitted, not yet credited |
| Refund "immediately" through UC-67 | The refund request is created immediately; the refund processor submits it, and PayOS refunds are completed by Admin (`complete-manual`) | PayOS has no refund API |
| Rebooking link inside MSG151 | Mobile "Đặt chuyến khác" button from `emergency-status.tourId` | Same outcome without a link in the push |
| MSG185 weather warning to Travelers | In-app notice | A push would need a new FCM contract on Mobile |
| MSG153 when a refund cannot reach the gateway | Raised by the refund processor (UC-67), not by UC-70 | UC-70 never calls a gateway |
| Cancellation only | Also an emergency stop of an active trip, Operator evidence, password step-up for the stop, Traveler acknowledgement, 30-minute review deadline with escalation | Safety during a running tour |

Error copy follows the SRS message table: MSG123 (empty reason), MSG126 (not the owning active approved
Operator), MSG133 (departure already cancelled, stopped or completed).

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
- Refund processing, Operator recovery and debt (UC-42), refund transfer (UC-67), payout (UC-71), commission.
- An Admin screen for the review notices (the Web has no notification inbox yet).
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
