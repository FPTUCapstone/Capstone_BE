# UC-28 — Make Electronic Payment (Backend)

## Status

Draft for approval. This specification defines the Backend contract for the
Traveler payment flow. Implementation starts only after this specification and
its plan are approved.

## Scope

An authenticated Traveler pays an eligible `commerce.Bookings` record through
the configured gateway. `Mock` is the local provider; `PayOS` is the staging
and production provider. Both use the same application completion handler.

The scope includes payment-link creation, idempotent completion, transaction
persistence, mock completion, PayOS webhook verification, and a canonical
payment-status read.

It excludes refunds, partial payments, installments, cash payment, ticket
issuance, and payment-provider selection.

## Decisions

1. Only `PayOS` is used outside local development. Local development uses
   `Mock` via `Payment:Provider=Mock`.
2. A Traveler cancellation at the gateway does **not** cancel the booking. The
   booking remains `PendingPayment` until its existing `payment_expires_at`.
3. One active payment link exists per booking. A new link is issued only if the
   prior link has been cancelled or expired and the booking is still eligible.
4. The amount is always read from the booking on the server. Clients never
   provide an amount, gateway reference, or success status.
5. A verified gateway callback is authoritative. Return/deep-link parameters
   are only navigation hints and are never used to mark a payment successful.
6. A successful payment received while the booking is still eligible atomically changes the transaction to `Success`,
   `Bookings.payment_status` to `Paid`, and `Bookings.status` to `Confirmed`.
7. Mock completion is enabled only when `Payment:Provider=Mock`; it is not
   registered or accepted in PayOS environments.
8. A verified success received after the booking has expired is persisted as a
   successful transaction, but never reinstates the booking or issues tickets.
   The booking remains `Expired` for reconciliation/refund handling in its
   dedicated use case.

## API contract

All Traveler endpoints require the authenticated booking owner.

Until the UC-27 entry point is available, a development-only Web test entry
may call the standard create/reuse endpoint with an explicit booking ID. It
does not receive an alternate Backend route, seed data or authorization bypass:
the target must be a real, owner-controlled `PendingPayment` booking.

### Create or reuse payment link

`POST /api/v1/traveler/bookings/{bookingId}/payments`

Response `200`:

```json
{
  "paymentId": 42,
  "bookingId": 17,
  "gateway": "Mock",
  "checkoutUrl": "http://localhost:3001/checkout/mock?paymentId=42",
  "status": "Pending",
  "amount": 2500000,
  "currency": "VND",
  "paymentLinkExpiresAtUtc": "2026-10-17T06:15:00Z",
  "bookingPaymentExpiresAtUtc": "2026-10-17T06:20:00Z"
}
```

The response reuses the active pending transaction and its checkout URL. A
second request does not create another transaction.

### Read canonical payment status

`GET /api/v1/traveler/bookings/{bookingId}/payment`

Returns the current transaction summary and booking payment state. This is the
only endpoint used by Web and Mobile after a return/deep link.

### Complete mock checkout

`POST /api/v1/traveler/payments/{paymentId}/mock-outcome`

Body:

```json
{ "outcome": "Success" }
```

Allowed values are `Success`, `Cancelled`, and `Expired`. The route returns
`404` when Mock is disabled and requires the booking owner. It invokes the
same completion application handler as a verified PayOS callback.

### PayOS webhook

`POST /api/v1/payments/payos/webhook`

Anonymous endpoint. It verifies the PayOS HMAC signature before resolving the
merchant order code and invoking the common completion handler. Invalid
signatures return `400`; valid duplicate callbacks return `200` with the
already persisted result.

## Eligibility and outcomes

| Condition | Result |
| --- | --- |
| Booking belongs to another user or does not exist | `404` |
| Booking is not `PendingPayment` or has `Paid` payment status | business conflict |
| `payment_expires_at <= now` before gateway success | mark booking and active transaction `Expired`; no link |
| Active pending payment link | return that same link |
| Cancelled/expired link while booking remains eligible | create one replacement link |
| Gateway amount/currency does not match transaction | reject callback and retain pending state |
| Duplicate verified callback | idempotent `200`, no duplicate state mutation |
| Verified gateway success after booking expiry | persist transaction success; keep booking `Expired`; no ticket or automatic reinstatement |

## Error contract

| Code | Meaning |
| --- | --- |
| `PAYMENT_BOOKING_NOT_ELIGIBLE` | The booking is not `PendingPayment` or is already paid. |
| `PAYMENT_BOOKING_EXPIRED` | The booking payment hold has expired. |
| `PAYMENT_NOT_FOUND` | The payment does not exist or is not owned by the caller. |
| `PAYMENT_GATEWAY_UNAVAILABLE` | The selected provider cannot create/retrieve the payment link. |
| `PAYMENT_MOCK_DISABLED` | A Mock-only action was requested while Mock provider is disabled. |
| `PAYMENT_CALLBACK_INVALID` | A webhook signature, amount, currency or reference is invalid. |

## Persistence

`payment.PaymentTransactions` remains the source of payment-attempt history.
The database migration adds a merchant order code, provider payment-link id,
expiry and update timestamps, and expands the transaction status constraint to
`Pending`, `Success`, `Cancelled`, `Expired`, `Failed`. It allows `Mock` as a
local gateway and creates unique indexes that prevent duplicate order codes and
active gateway references. It also creates a unique filtered index on
`booking_id` where status is `Pending`; the handler catches its duplicate-key
outcome, re-reads the existing pending transaction, and returns that link.

`raw_response` stores a redacted gateway payload for reconciliation; it must
not store API keys or checksum keys.

## Configuration and security

`Payment:Provider` supports `Mock` and `PayOS`. PayOS credentials and URLs are
read from environment variables or User Secrets only. The PayOS client ID, API
key and checksum key are never returned to clients, logged, or committed.

The PayOS provider creates a signed payment request with the server amount and
returns its `checkoutUrl`. The configured PayOS return/cancel URLs are not
trusted state transitions. The webhook is the only production completion path.

## Acceptance criteria

1. A valid Traveler receives one reusable mock checkout URL for an eligible
   booking.
2. Mock `Success` persists one successful transaction and confirms the booking.
3. Mock `Cancelled` keeps the booking pending and permits a replacement link.
4. Mock `Expired` keeps the booking pending only until booking expiry; expired
   bookings cannot create another link.
5. Duplicate completion and webhook deliveries are idempotent.
6. Invalid PayOS signatures, amount mismatches and unauthorized callers never
   change payment or booking state.
7. The same API and response DTO work with Mock and PayOS.
