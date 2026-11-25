# UC-39 — Update Coupon Backend Implementation Plan

Status: Approved by developer on 2026-10-26.

## Goal and boundaries

Complete the existing operator coupon update API and the agreed coupon-slot lifecycle for the web UC-39 rules. Reuse the current Voucher, CQRS handler, Result/ProblemDetails mapping, redemption record and booking transaction infrastructure. Do not add a coupon history table, mobile flow, new repository layer or unrelated cleanup. Add a database script only if the approved lifecycle cannot be implemented with the existing schema or if existing data needs a verified correction.

## Preconditions

1. Confirm the spec and plan approvals. Fast-forward local `develop` to the latest `origin/develop`, then work in an isolated `feature/uc-39-update-coupon` branch/worktree; preserve the current UC-10 branch's unrelated changes.
2. Recheck the current code and tests after branching. Run baseline `dotnet test` on the clean worktree and resolve any failure before implementation, as required by `AGENTS.md`.
3. Coordinate the API contract with the FE plan; backend owns all business validation. Use test-first changes, then a scope review and quality review after each task.
4. Audit existing persisted `usedCount` against occupied pending plus paid redemption records in a read-only query before deciding whether historical unpaid expiries require an explicit, idempotent data correction. Do not silently rewrite live counters.

## Task 1 — Domain rules and validator

**Files:** `src/TripMate.Domain/Entities/Voucher.cs`; `src/TripMate.Application/Features/Coupons/Update/UpdateCouponCommandValidator.cs`; new focused tests under `tests/TripMate.Application.UnitTests/Features/Coupons/Update/`.

- [x] RED: test immutable discount type and code, immutable elapsed start, `null`/equal/below-used limits, future end, expired extension stays disabled, manually disabled edit stays disabled, and retained used count.
- [x] GREEN: make the smallest entity and validator changes preserving the existing money and tour invariants. Keep activation as a separate command.
- [x] Verify: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter FullyQualifiedName~Coupons.Update`.
- **Done:** all tests pass and the entity cannot enter an invalid state through Update.

## Task 2 — Slot invariant and per-user eligibility

**Files:** `src/TripMate.Domain/Entities/Voucher.cs`; `src/TripMate.Application/Features/Traveler/Bookings/Create/CreateTourBookingCommandHandler.cs`; `src/TripMate.Application/Features/Traveler/Bookings/Quote/QuoteTourBookingQueryHandler.cs`; focused domain, booking and quote tests.

- [x] RED: prove a pending booking occupies a total and per-user slot; a terminal unpaid redemption record remains for audit but does not block a later coupon use by the same traveler. Prove a paid booking still counts after cancellation.
- [x] GREEN: add a guarded single-slot release operation and make per-user eligibility count only occupied/consumed redemptions. Do not delete historic redemption records or change booking money.
- [x] Verify: focused Voucher, booking-create and quote tests.
- **Done:** total and per-user eligibility follow the approved slot definition; release cannot make `usedCount` negative.

## Task 3 — Expiry releases a slot once

**Files:** `src/TripMate.Application/Features/Traveler/Bookings/Expiry/PendingBookingExpiryProcessor.cs`; `src/TripMate.Application/Features/Traveler/Payments/Status/GetBookingPaymentStatusQuery.cs`; `src/TripMate.Application/Features/Traveler/Payments/Create/CreateOrReusePaymentLinkCommandHandler.cs`; `src/TripMate.Application/Features/Traveler/Payments/Common/PaymentCompletionProcessor.cs`; focused expiry/payment tests.

- [x] RED: prove worker and payment entry points release a coupon slot only on a successful pending-to-expired transition; retryable payment failure retains it. Repeated and competing expiry calls release at most once.
- [x] GREEN: share the minimal transaction-safe expiry/release behavior across these callers. Commit booking transition, voucher decrement and related expiry effects atomically; preserve booking monetary snapshots and redemption records.
- [x] Verify: focused expiry/payment tests plus a SQL Server concurrency test for duplicate release and simultaneous redemption/release when the harness supports it.
- **Done:** every expiry path has the same outcome and cannot leave an inflated or negative `usedCount`.

## Task 4 — Unpaid cancellation releases a slot

**Files:** `src/TripMate.Application/Features/Bookings/Cancel/CancelBookingCommandHandler.cs`; focused cancellation tests.

- [x] RED: prove cancelling a pending unpaid booking releases one slot exactly once and a paid cancellation/refund retains its consumed slot; replay is idempotent.
- [x] GREEN: release within the existing serializable cancellation transaction only when the booking makes the eligible unpaid transition.
- [x] Verify: focused cancellation tests and a competing cancel/redemption SQL Server case if the harness supports it.
- **Done:** cancellation respects the approved coupon-slot lifecycle without changing refund or stored discount amounts.

## Task 5 — Atomic update and error mapping

**Files:** `src/TripMate.Application/Features/Coupons/Update/UpdateCouponCommandHandler.cs`; `src/TripMate.Application/Features/Coupons/Common/CouponErrorCodes.cs`; `src/TripMate.Api/Common/ApiControllerBase.cs`; focused Update handler tests.

- [x] RED: test owned/foreign/missing coupon, mixed tour ownership, no partial update on bad scope, unchanged scope replacement, and conflict when usage changes during the update.
- [x] GREEN: validate under the existing database transaction/concurrency facilities so booking redemption and limit changes cannot persist `usedCount > usageLimit`. Map a genuine stale/conflicting write to a stable `409` coupon code; clear or reload tracked state when the persistence path requires it. Do not turn a transient database fault into success.
- [x] Verify: `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter FullyQualifiedName~Coupons.Update`.
- **Done:** an invalid or competing update preserves previous fields and tour links; a valid update returns the canonical saved coupon.

## Task 6 — Endpoint and SQL Server behavior

**Files:** `src/TripMate.Api/Controllers/V1/OperatorCouponsController.cs` only if endpoint metadata/binding needs adjustment; new `tests/TripMate.Api.IntegrationTests/Coupons/UpdateCouponEndpointTests.cs` and `UpdateCouponSqlServerTests.cs`; existing booking endpoint test fixtures as needed.

- [x] RED: test real PUT request/response and `400/401/403/404/409/422` mapping, GET after PUT, retained tour links, expiration and activation sequence, and an overlapping redemption/update or release/update against SQL Server.
- [x] GREEN: repair only endpoint/persistence gaps exposed by those tests. Test that earlier booking `DiscountAmount` stays fixed and a later booking uses the new terms.
- [x] Verify: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter FullyQualifiedName~UpdateCoupon`.
- **Done:** HTTP and persistence outcomes match the spec, including concurrency, without a schema change unless an actual database constraint forces an approved plan revision.

## Task 7 — Full verification and handoff

- [x] Run `dotnet test` and report any environment-limited SQL Server tests distinctly from passed tests.
- [x] Compare stored counts with the agreed pending-plus-paid definition using a read-only audit. If historical unpaid rows require repair, prepare an idempotent SQL correction script and verify affected-row counts before any production execution; report it as a deployment prerequisite.
- [x] Review against the approved spec and `AGENTS.md`, first for scope and then code quality. Resolve blocking findings.
- [x] Supply the final field/error contract and sample responses to FE integration. Do not push, open a PR or merge without separate developer direction.

## Risks to verify

- The existing booking create path is serializable, while the current coupon update path is not; this is the main concurrency risk.
- Replacing a tour collection that retains an existing tour may collide with EF tracking or a composite database key. A real SQL Server test must cover it.
- The stored status has `Active`, `Expired` and `Disabled`; `Scheduled` is a derived time state. Extending an effectively expired active record must not silently reactivate it.
- Booking expiry currently occurs from worker, payment status, payment link and payment completion paths; cancellation is separate. Each terminal path must share the same single-release invariant. Existing unpaid expired bookings may leave inflated counters until an audited correction is applied.
