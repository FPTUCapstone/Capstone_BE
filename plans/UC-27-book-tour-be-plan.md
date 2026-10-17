# UC-27 Backend Implementation Plan — Book Tour

**Platform:** ASP.NET Core .NET 10, EF Core, SQL Server
**Status:** Blocked pending approval of the accompanying contract

## Phase 0 — contract decisions

1. Confirm the UC-26 departure-selection response and the shared string-ID convention.
2. Approve required participant fields and require `participantType` (`Adult`, `Child`, `Infant`); do not introduce DOB until SRS and schema change approve it.
3. Approve the effective-price policy: Adult schedule price then BasePrice; Child/Infant override then effective Adult price.
4. Approve payment expiry, unpaid-booking limit, capacity-release owner and public error mappings.
5. Review whether an existing idempotency facility can be reused. If not, design a SQL Server table scoped by traveler/key with request hash, response payload/status, timestamps and a unique constraint.

**Exit:** OpenAPI examples in the BE spec are agreed by Web and Mobile.

## Phase 1 — domain and persistence

1. Add UC-27 vertical-slice commands/queries under the existing .NET project structure; do not introduce Java/Spring folders.
2. Map existing `commerce.Bookings`, `commerce.BookingParticipants`, `commerce.TourSchedules` and voucher tables. Add only a reviewed SQL Server migration for idempotency data or missing approved fields.
3. Add entity/configuration tests that verify the migration on both fresh and upgrade databases.

**Exit:** Schema diff is clean; existing UC-28/29 tables and data remain usable.

## Phase 2 — quote

1. Implement `POST /api/v1/traveler/tour-bookings/quote` in the existing API/controller and application-handler conventions.
2. Read approved tour, viable scheduled departure and voucher data from the same database context.
3. Derive type counts from the request and calculate all monetary fields server-side: Adult uses schedule price then BasePrice; Child/Infant use their override then effective Adult price. Return a snapshot only; do not mutate capacity, voucher usage or bookings.
4. Map errors through existing `ApiResponse`/`ProblemDetails` conventions.

**Tests:** valid quote; Adult BasePrice fallback; Child/Infant effective-Adult fallback; per-type overrides; expired/cancelled/deadline-passed departure; no capacity; invalid/inapplicable voucher; non-Traveler caller.

## Phase 3 — atomic create

1. Require a UUID `Idempotency-Key`; normalize and hash the request.
2. In one SQL Server transaction, lock the schedule row, revalidate deadline/capacity/voucher, resolve per-type prices from the submitted participants, write the booking and participants, increase `reserved_capacity`, record voucher use and commit the idempotent response.
3. Return `201` on first success; return the stored `200` result for an identical retry; return `409` for payload mismatch.
4. Use a unique database constraint plus conflict handling so simultaneous same-key requests cannot create two bookings.

**Tests:** same-key retry, same-key mismatch, simultaneous requests for final seats, rollback after participant/voucher failure, no over-capacity booking.

## Phase 4 — expiry, release and integration

1. Run a server background worker every 30 seconds. It processes a bounded batch of due `PendingPayment` bookings; each booking and its schedule are updated in a separate serializable transaction. The worker marks the booking `Expired` and releases `Booking.Quantity`. A corrupt historic row rolls back and is logged without blocking other rows.
2. Run the Traveler-scoped expiry path inside booking creation before enforcing the configured limit of three unpaid bookings. The domain transition must be idempotent so a later worker sweep cannot release capacity twice.
3. No expiry notification is part of UC-27. A future notification use case must use an approved transactional-outbox contract; it is not introduced by this capacity-release slice.

**Tests:** a SQL Server test proves a due booking becomes `Expired` and releases capacity once; an Application test proves due holds are removed before the three-booking limit is applied. UC-28 owns the payment-callback versus expiry race once payment confirmation exists.

## Definition of done

- OpenAPI exposes quote and create with the approved examples.
- Existing `commerce.*` booking schema is reused; no parallel booking model exists.
- Quote never reserves capacity; create is atomic and idempotent.
- Fresh and upgrade migration tests, handler tests and SQL Server concurrency tests pass.
- Web/Mobile integration verifies pending-payment handoff without initiating payment in UC-27.
