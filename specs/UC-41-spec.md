# UC-41 — Cancel Tour Booking

## Scope

Provide a single backend cancellation use case for a tour booking. The authenticated
caller is either the booking's Traveler or the Tour Operator that owns the booked
tour. This change does not execute a payment-gateway refund; it creates one durable
pending refund request for the later refund-processing flow.

## API

`POST /api/v1/bookings/{bookingId}/cancellation`

The authenticated actor is derived from the access token. The request contains an
optional reason category and reason detail. An `Idempotency-Key` header is required
for a mutating request; a replay of the same actor, booking, and payload returns the
original response.

## Business rules

- A Traveler may cancel only their own booking. A Tour Operator may cancel only a
  booking for a schedule belonging to one of their tours.
- Only `PendingPayment` and `Confirmed` bookings can be cancelled. A booking with a
  `Used` ticket cannot be cancelled.
- `PendingPayment` cancellation releases reserved capacity and invalidates tickets;
  it creates no refund.
- A Traveler cancelling a `Confirmed` booking receives the amount from the immutable
  policy snapshot recorded for the booking. The initial platform policy is: at least
  7 days before departure 100%; 3 to less than 7 days 70%; 24 to less than 72 hours
  30%; less than 24 hours is ineligible.
- A Tour Operator cancelling a `Confirmed` booking creates a 100% refund request.
  Operator cancellation requires a reason category and detail.
- Cancellation, schedule capacity release, ticket invalidation, cancellation audit
  data, idempotency operation, and pending refund creation are one transaction.
- A successful cancellation is idempotent. Concurrent calls cannot create duplicate
  pending refunds or release capacity twice.

## Error conditions

- `booking.not_found` (404): booking does not exist or is not visible to the caller.
- `booking.cancellation.forbidden` (403): caller is neither its traveler nor tour
  owner.
- `booking.cancellation.invalid_state` (409): status is terminal or a ticket was used.
- `booking.cancellation.window_closed` (422): Traveler is within 24 hours of departure.
- `booking.cancellation.idempotency_payload_mismatch` (409): key is reused with a
  different request payload.

## Out of scope

- Calling payment gateways, payout execution, staff refund approval/retry (UC-42/71).
- Tour-policy authoring UI. This change persists a policy snapshot to make the
  cancellation result stable even if a future policy changes.
