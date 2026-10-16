# UC-27 Backend Specification — Book Tour

**Platform:** ASP.NET Core .NET 10, EF Core, SQL Server
**Status:** Draft — contract proposal; implementation starts only after approval
**Sources:** SRS §3.5.4, `database/tripmate_schema_v7.sql`

## 1. Scope

An authenticated active Traveler creates a booking for one scheduled departure. UC-27 owns quote and creation of a `PendingPayment` booking. UC-28 owns payment initiation; UC-29 owns ticket issuance.

The server is the sole authority for price, voucher eligibility, capacity, expiry, booking code, and status. Clients never submit or calculate monetary values.

## 2. Persistence boundary

UC-27 **reuses existing tables**. It must not create MySQL-style duplicate booking tables.

| Purpose | Existing SQL Server table | UC-27 use |
|---|---|---|
| Departure and capacity | `commerce.TourSchedules` | Read and atomically update `reserved_capacity` |
| Tour and base price | `commerce.Tours` | Read approved tour and `base_price` |
| Booking | `commerce.Bookings` | Create `PendingPayment` record |
| Named travelers | `commerce.BookingParticipants` | Create one row per participant |
| Voucher | `commerce.Vouchers`, `commerce.VoucherApplicableTours`, `commerce.VoucherRedemptions` | Validate and record redemption using existing rules |

The existing participant model supports `fullName`, optional `phoneNumber`, `email`, `participantType`, document type/number and nationality. **Date of birth is not accepted by this contract** until SRS and a separate SQL Server migration explicitly add it. Contact information is represented by the lead participant for this release; no separate contact table is introduced.

## 3. Public contract

All endpoints require a bearer token and the Traveler role. IDs are JSON strings because database IDs are `BIGINT` and JavaScript must not lose precision.

### 3.1 Quote

```http
POST /api/v1/traveler/tour-bookings/quote
Content-Type: application/json
```

```json
{
  "tourScheduleId": "123",
  "participantCounts": { "adult": 1, "child": 1, "infant": 0 },
  "voucherCode": "SUMMER2026"
}
```

The server verifies the scheduled departure, the approved tour, availability and voucher eligibility, then returns a non-reserving snapshot:

```json
{
  "tourId": "45",
  "tourScheduleId": "123",
  "currency": "VND",
  "participantCounts": { "adult": 1, "child": 1, "infant": 0 },
  "priceBreakdown": [
    { "participantType": "Adult", "quantity": 1, "unitPrice": 1000000, "lineAmount": 1000000 },
    { "participantType": "Child", "quantity": 1, "unitPrice": 700000, "lineAmount": 700000 },
    { "participantType": "Infant", "quantity": 0, "unitPrice": 1000000, "lineAmount": 0 }
  ],
  "subtotalAmount": 1700000,
  "discountAmount": 200000,
  "totalAmount": 1500000,
  "remainingSlots": 5,
  "appliedVoucher": { "code": "SUMMER2026" }
}
```

The quote is informational only: it creates no booking and reserves no capacity. Price and availability are re-evaluated on creation.

### 3.1.1 Effective price policy

For each participant type, the Backend applies this deterministic policy:

1. `Adult`: `TourSchedule.AdultPrice ?? Tour.BasePrice`.
2. `Child`: `TourSchedule.ChildPrice ?? effectiveAdultPrice`.
3. `Infant`: `TourSchedule.InfantPrice ?? effectiveAdultPrice`.


ull` for Child/Infant means “no price override”, never “not permitted”. A future age or allowed-participant-type rule must be modelled explicitly; it must not overload a nullable price. The total before vouchers is the sum of all `quantity × unitPrice` lines.

### 3.2 Create booking

```http
POST /api/v1/traveler/tour-bookings
Idempotency-Key: 550e8400-e29b-41d4-a716-446655440000
Content-Type: application/json
```

```json
{
  "tourScheduleId": "123",
  "participants": [
    {
      "fullName": "Nguyen Van A",
      "participantType": "Adult",
      "phoneNumber": "+84912345678",
      "email": "a@example.com",
      "idDocumentType": "CCCD",
      "idDocumentNumber": "012345678901",
      "nationality": "VNM"
    }
  ],
  "voucherCode": "SUMMER2026"
}
```

`participants.length` is authoritative; a separate client-supplied count is not accepted. Every participant must provide `participantType` (`Adult`, `Child`, or `Infant`). The first participant is the booking contact. Document numbers are PII and must use the project encryption policy before persistence.

Success returns `201 Created` and a booking snapshot:

```json
{
  "bookingId": "456",
  "bookingCode": "BK-20261017-00001",
  "status": "PendingPayment",
  "tourScheduleId": "123",
  "currency": "VND",
  "subtotalAmount": 2000000,
  "discountAmount": 200000,
  "totalAmount": 1800000,
  "paymentExpiresAt": "2026-10-17T10:30:00Z",
  "createdAt": "2026-10-17T10:15:00Z"
}
```

On a repeated key with the identical normalized request, return the original snapshot with `200 OK`. A reused key with a different request returns `409 Conflict`. Missing or invalid `Idempotency-Key` returns the project’s standard validation response (`400`).

## 4. Validation and failure contract

The endpoint must use the existing `ApiResponse` / `ProblemDetails` conventions. Exact feature error-code constants and SRS-message mapping are implementation tasks; no new arbitrary string codes become public until added to the common error mapping.

| Situation | HTTP |
|---|---:|
| Authentication missing/expired | 401 |
| Caller is not an active Traveler | 403 |
| Request/header/participant shape invalid | 400 |
| Schedule or approved tour does not exist | 404 |
| Schedule cancelled, completed, past its `registration_deadline_at`, capacity unavailable, voucher ineligible, or unpaid-booking policy rejected | 409 or 422, finalized with the common error contract |
| Same idempotency key, different payload | 409 |
| Persistence or transaction failure | 500 / standard `MSG127` |

The product team must approve the maximum concurrent unpaid bookings and the payment-expiry duration before implementation. They remain configuration values, not invented business constants.

## 5. Transactional capacity and idempotency

Creation runs in one SQL Server transaction:

1. Validate and acquire/insert the idempotency record scoped to `(traveler_user_id, idempotency_key)`.
2. Lock the `commerce.TourSchedules` row using the chosen SQL Server concurrency mechanism.
3. Re-read schedule/tour/voucher state, verify `registration_deadline_at` has not passed, derive counts from participant types, resolve each effective price, and check `total_capacity - reserved_capacity >= participant count`.
4. Create `commerce.Bookings` with `PendingPayment`, server-calculated amounts and `payment_expires_at`.
5. Create `commerce.BookingParticipants`, update `reserved_capacity`, persist voucher redemption if applicable, and persist the idempotent response.
6. Commit once. Any failure rolls back every change.

Pending-payment capacity is a **soft reservation**. UC-28 confirms payment; cancellation/expiry processing releases the same number of seats in its own transaction. Availability cache may be used for display only and never for a create decision.

## 6. Required dependency from UC-26

UC-27 requires a public Traveler-safe tour-detail/departure contract to choose a `tourId` and `tourScheduleId`. Its exact endpoint is owned by UC-26. Until it exists, clients may use a labelled development fixture only; they cannot create a real booking from that fixture.

## 7. Approval gates

- [ ] Product confirms required participant fields and contact model.
- [ ] Product approves the effective-price policy: Adult schedule price then BasePrice; Child/Infant override then effective Adult price.
- [ ] Product confirms unpaid-booking limit and payment expiry.
- [ ] Team approves the public tour-detail/departure contract from UC-26.
- [ ] Team approves SQL Server migration for an idempotency table only if no reusable project mechanism exists.
- [ ] Backend, Web and Mobile approve the same OpenAPI contract and error mapping.
