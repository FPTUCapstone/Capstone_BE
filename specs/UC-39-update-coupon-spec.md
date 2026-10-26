# UC-39 — Update Coupon (backend)

Status: Approved by developer on 2026-10-26; implementation plan approved on the same date.

## Scope

An active, approved Tour Operator updates a coupon they own through the existing operator coupon API. This specification supports the web UC-39 screen. Mobile implementation is outside this delivery.

## Business rules

1. The caller must be an authenticated, active, approved Tour Operator and own the coupon. A foreign or missing coupon must not expose another operator's data.
2. Coupon code and discount type (`Percentage` or `Flat`) cannot change after creation. Coupon name, discount value, percentage cap, minimum order, total and per-user usage limits, validity end, and applicable tours may change. The validity start may change only before that instant has passed.
3. A coupon with recorded uses remains editable. The new terms apply to bookings created after the update. Existing bookings and redemptions retain their stored discount amounts; changing coupon terms does not reset usage.
4. `usageLimit = null` and `usageLimitPerUser = null` mean unlimited. A non-null limit must be a positive integer. A new total limit equal to `usedCount` is valid and prevents another redemption; a lower limit is invalid. Per-user limit changes affect subsequent eligibility checks only.
5. Every coupon has at least one explicitly stored applicable tour. Each selected tour must currently be eligible and owned by the caller. An “all tours” web selection resolves to the eligible tour IDs at save time; newly added tours do not join automatically. The update replaces the stored set atomically.
6. `validToUtc` must be later than `validFromUtc` and later than the current instant. Once `validFromUtc` has passed, it is immutable. An expired coupon may be extended, but the update leaves it disabled until a separate successful activation request. A manually disabled coupon remains disabled after an update. An active, unexpired coupon remains active. A future start is scheduled by its time window, not by a separate persisted `Scheduled` state.
7. Updates cannot make `usedCount > usageLimit`, including when a redemption and a limit reduction happen concurrently. If a competing redemption makes the requested limit invalid, the update fails without a partial change and the caller is told to reload the usage count.
8. Preserve the existing monetary rules: percentage value 1–100 with positive maximum discount; flat discount has no percentage cap; supported money has at most two decimal places; minimum order is non-negative; and a flat discount cannot exceed a positive minimum order amount.
9. `usedCount` means coupon slots currently occupied by pending-payment bookings plus slots consumed by bookings that reached paid status. Creating a pending-payment booking with a positive coupon discount reserves one slot and counts toward the per-user limit. A retryable payment failure on the same pending booking keeps its slot. When that booking definitively expires or is cancelled before payment, release its slot exactly once; its redemption record and booking monetary snapshot remain for audit, but the released record no longer counts toward per-user eligibility. A paid booking keeps its consumed slot even if later cancelled or refunded.
10. A release and its terminal unpaid booking transition must commit atomically. Cover every path that expires or cancels a pending booking, including the expiry worker, payment status query, payment link creation, payment completion and cancellation command. A repeated transition must not decrement twice. Competing booking creation, expiry/cancellation and coupon limit reduction must not oversubscribe the limit or produce a negative `usedCount`.

## API contract

- `GET /api/v1/operator/coupons` lists only the caller's coupons. `GET /api/v1/operator/coupons/{couponId}` returns the owned coupon, including code, discount type, current occupied usage count (`usedCount`), status, UTC validity instants, and applicable tour IDs. The response does not separately report pending and paid counts.
- `GET /api/v1/operator/coupons/eligible-tours` supplies currently eligible tour IDs.
- `PUT /api/v1/operator/coupons/{couponId}` takes the existing full update body: `name`, `discountType`, `discountValue`, `maxDiscountAmount`, `minOrderAmount`, `usageLimit`, `usageLimitPerUser`, `validFromUtc`, `validToUtc`, `applicableTourIds`. `discountType` must match the stored type; the body does not accept a new code or status. It returns the updated coupon on success.
- `POST /api/v1/operator/coupons/{couponId}/deactivate` and `/activate` remain separate actions. Activation checks the coupon's current validity and other eligibility rules.
- Unauthorized requests return `401`; ineligible operator requests return `403`; a missing or foreign coupon returns `404`; invalid input returns an actionable `400` or `422` validation response according to the existing coupon error mapping; a redemption conflict during a limit reduction returns `409`. Use the project's standard Result/ProblemDetails mapping and stable coupon error codes.

## Acceptance criteria

1. An eligible operator updates each supported field on an owned coupon; a read after write returns the saved values.
2. Code and discount type remain unchanged even if the request tries to change them.
3. A foreign coupon cannot be read or updated; invalid tour ownership, empty scope, and invalid values leave the previous coupon unchanged.
4. With `usedCount = 30`, limits `null`, `30`, and `50` are accepted; `29` is rejected. At limit `30`, another booking cannot redeem the coupon.
5. An expired coupon extended into the future remains unusable until activated. Updating a disabled coupon does not activate it.
6. Changing a used coupon affects a later booking but leaves earlier bookings' stored discount and total unchanged.
7. Concurrent booking redemption and limit reduction cannot persist `usedCount > usageLimit`; a stale update is rejected without a partial tour-scope or field change.
8. A pending booking occupies a slot; a retryable payment failure keeps it occupied. Expiry or cancellation before payment restores total and per-user availability exactly once; the original booking discount and redemption record remain unchanged. Paid cancellation/refund does not restore availability.
9. Every terminal unpaid transition path releases the slot atomically, and repeated/concurrent processing cannot drive `usedCount` negative or enable redemption beyond the configured limit.

## Test seams

Prefer existing endpoint integration tests for authentication, ownership, status and persistence; application/domain tests for validation and concurrency; and booking integration tests for new-versus-existing discounts. Tests assert observable results, not internal method calls.

## Out of scope

Creating coupons, applying them in the web booking UI, mobile screens, code/type conversion, automatic inclusion of future tours, and a coupon version-history table.
