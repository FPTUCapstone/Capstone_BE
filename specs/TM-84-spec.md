# TM-84 / UC-38 — Create Coupon

## 1. Purpose and scope

An active Tour Operator creates a reusable promotion code for one or more of
their own tours. The code defines the discount and eligibility conditions that
the later booking use case will evaluate. This UC creates the coupon only; it
does not apply a coupon, create a booking, charge a payment, or mutate usage
counts.

To let clients select real tours without exposing other operators' data, UC-38
also provides a protected eligible-tour lookup. It returns only caller-owned,
Approved tours needed by this form; it is not a general tour-management
endpoint.

The traveller-facing outcome is predictable checkout later: a code states its
amount, VND cap where applicable, minimum order amount, time window, redemption
limits, and eligible tours before it is accepted.

## 2. Approved product decisions

- Actor: an authenticated user with the `TourOperator` role and an active
  operator profile.
- A coupon applies to one or more explicitly selected tours owned by that
  operator. There is no implicit “all current and future tours” scope.
- A code is global, case-insensitively unique, stored in canonical uppercase,
  and immutable after creation.
- Exactly one discount type is selected:
  - `Percentage`: 1–100 inclusive, with a required VND `maxDiscountAmount`;
  - `Flat`: a positive VND `discountValue`; `maxDiscountAmount` is omitted.
- `minOrderAmount` is non-negative VND. `usageLimit` and
  `usageLimitPerUser` are optional positive integers.
- `validFromUtc` and `validToUtc` are UTC instants and `validToUtc` must be
  strictly later than `validFromUtc`. A code cannot be created with an expired
  end time.
- A newly created coupon has status `Active`, `usedCount = 0`, and cannot be
  combined with another coupon in one future booking. The latter is enforced
  by the future booking/apply-coupon flow, not this UC.

## 3. Persistence

The existing canonical schema is the source of truth:

- `commerce.Vouchers` stores ownership, code, discount, usage and validity.
- `commerce.VoucherApplicableTours` links a voucher to eligible tours.

No schema migration is needed for this UC. The implementation must add the
missing Domain/EF mapping only if these tables are not yet represented in code.
For a single transaction, the voucher-to-tour entities must use navigation
properties so generated database keys are correctly propagated.

## 4. API contract

### `GET /api/v1/operator/coupons/eligible-tours`

Authorization: authenticated active approved `TourOperator` only.

Successful response: `200 OK` with `{ tourId, title, destination, basePrice }`
items, ordered by title then ID. It exposes only the caller's Approved tours.

### `POST /api/v1/operator/coupons`

Authorization: authenticated `TourOperator` only.

Request:

```json
{
  "code": "SUMMER10",
  "discountType": "Percentage",
  "discountValue": 10,
  "maxDiscountAmount": 200000,
  "minOrderAmount": 1000000,
  "usageLimit": 100,
  "usageLimitPerUser": 1,
  "validFromUtc": "2026-10-05T00:00:00Z",
  "validToUtc": "2026-10-31T16:59:59Z",
  "applicableTourIds": [101, 102]
}
```

Successful response: `201 Created`, with `{ couponId, code }`, where `code` is
the normalized canonical code. Clients use this confirmation only; a future
coupon-detail/list use case owns retrieval of the complete coupon record. The
response never exposes booking or redemption records.

Expected failures use `Result`/ProblemDetails:

- `401` unauthenticated; `403` non-operator or inactive operator;
- `400` malformed/invalid request;
- `409 coupon.code_conflict` case-insensitive code collision;
- `404 coupon.tour_not_found` for a selected tour that does not exist;
- `403 coupon.tour_not_owned` if any selected tour is not owned by the caller.

The command is atomic: no voucher is created when any selected tour fails an
ownership/existence check or when the unique code insert conflicts.

## 5. Validation and boundary rules

- Code: 3–30 ASCII letters, digits, or `-`; trim whitespace and uppercase
  using invariant casing. Reject blank, whitespace-only, and unsupported
  characters rather than silently changing user intent.
- `applicableTourIds`: required, non-empty, positive, and duplicate-free.
- All selected tours must be owned by the caller and have `Approved` status;
  draft, pending, rejected, and inactive tours are not eligible for a public
  promotion.
- Decimal money values are VND and have at most two decimal places; values are
  never negative. A flat discount may not exceed the minimum order amount when
  one is supplied.
- Status and `usedCount` are server-owned values, never accepted from clients.

## 6. Architecture

- Domain: `Voucher` aggregate/factory plus `VoucherApplicableTour` association
  enforce intrinsic invariants.
- Application: vertical slice `CreateCoupon` command, validator, handler,
  response and error-code constants. The handler owns authorization-dependent
  ownership checks and the one-transaction persistence boundary.
- Infrastructure: EF mapping for existing commerce tables, including UTC
  `datetime2` mapping and unique-code handling.
- API: a thin operator controller maps `Result` failures through the existing
  standard failure handler.

## 7. Acceptance criteria

1. An active operator can create a percentage coupon for one or more owned,
   approved tours and receives a normalized 201 response.
2. A flat coupon, limits, minimum order and validity window persist exactly.
3. Invalid discount combinations, dates, limits, code format, duplicate tour
   IDs, and empty scope return 400 without writes.
4. A caller cannot create a coupon for another operator’s tour; mixed ownership
   is atomic with no partial coupon.
5. Concurrent/case-variant duplicate codes result in one success and one 409;
   no server error or duplicate row occurs.
6. Domain/application unit tests and SQL Server integration tests prove
   validation, ownership, persistence, uniqueness and rollback behavior.
7. Existing tests remain green and database changes are not introduced unless
   an implementation audit demonstrates that canonical schema is insufficient.

## 8. Explicit exclusions

- Editing, disabling, listing, or deleting coupons;
- entering/applying coupons during booking;
- usage-count mutation, redemption history, cancellation/refund adjustment;
- personalised, first-order, geographic, channel, referral or stackable
  promotions;
- Mobile/Web UI. A client integration follows only after this API contract is
  reviewed and merged.
