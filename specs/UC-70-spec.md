# UC-70 — Cancel or emergency-stop a commercial departure

Canonical cross-module specification: `../../spec.md`.

## Backend contract

UC-70 affects one owned `TourSchedule`; it never cancels every departure of a Tour.

| Situation | TourSchedule | Booking | TripSession |
| --- | --- | --- | --- |
| Before start | `Cancelled` | `Cancelled` | linked open session `Interrupted` (a session can start before departure) |
| Active trip | `EmergencyStopped` | `Interrupted` | linked open session `Interrupted` |

UC-70 records the decision and its cancellation context only. Việc thanh toán và hoàn tiền được xử lý
riêng theo chính sách hủy booking áp dụng: UC-42 owns refund eligibility, amounts, cases and settlement,
and UC-71 owns payout, reading booking state on its own schedule. UC-70 creates no `Refund`, calculates
no amount, and neither calls nor mutates either module.

Only the owning active, approved Tour Operator may preview or confirm the action. UC-14/Tomorrow.io/NCHMF
alerts can request review but never invoke these commands automatically. TripMate Admin is not a pre-approval
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
- No FCM or payment gateway call occurs inside that transaction.
- Unused tickets/QR are revoked; used ticket history is preserved.
- Active commercial sessions are linked exactly by `Itinerary.SourceBookingId`; the departure is
  derived through `Booking.TourScheduleId`.
- An open session without that exact link blocks both decisions with `TripSessionLinkUnverified`.
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

## Release blockers still open
- UC-42 has no contract yet that picks up a booking cancelled by UC-70: its worker only processes
  existing `Refund` rows (BLOCKED_UC42_HANDOFF_CONTRACT). UC-70 does not invent one.
- SQL Server migration rerun and rollback tests must pass in the release environment before deploy.
