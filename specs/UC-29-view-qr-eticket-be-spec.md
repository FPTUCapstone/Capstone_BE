# UC-29 Backend Specification — View QR E-ticket

**Platform:** ASP.NET Core .NET 10, EF Core, SQL Server
**Status:** Draft — awaiting developer approval; implementation starts only after approval
**Sources:** SRS §3.5.6 (UC-29), §3.5.5 (UC-28 BR-80/PC-03), `database/tripmate_schema_v7.sql`,
`specs/UC-27-book-tour-be-spec.md` §1 ("UC-29 owns ticket issuance"), `docs/TM-78-api-contract.md`
**Client contract:** `docs/UC-29-api-contract.md`

## 1. Scope

UC-29 owns the whole e-ticket lifecycle up to check-in:

1. **Issue** one ticket when a payment is verified and confirms a booking (UC-28 completion path).
2. **Show** the ticket to its owning Traveler with a signed, encrypted, time-stamped QR payload.
3. **Refresh** the QR (rotate: revoke the old QR, issue a new one).
4. **Invalidate** the QR when the booking is cancelled (implements the existing no-op
   `RevokeActiveTicketQrCodesAsync`).
5. **Backfill** tickets for bookings confirmed before this release.

Out of scope: UC-43 scanning and validation, `commerce.CheckInLogs`, marking a ticket `Used`, any flow that
sets a booking `Completed`, notifications, and the Operator view of tickets. UC-43 will consume the QR
format defined in §6; it is specified here only so that UC-43 does not have to change it.

## 2. Confirmed decisions (developer, 2026-10-24)

| ID | Decision |
| --- | --- |
| D1 | **Departure window** = the whole departure date in `Asia/Ho_Chi_Minh`, 00:00:00 to 23:59:59.9999999 local, of `TourSchedule.StartAtUtc`. Before it → `NotYetActive` (MSG98); after it → `Expired`. |
| D2 | **One ticket per booking.** `participant_id` stays `NULL`; the screen shows the participant count. |
| D3 | SRS MSG74/MSG90/MSG99 do not match UC-29. New messages **MSG138–MSG140** are added (§8); the old ones are not reused. |
| D4 | BR references follow the meaning written in SRS §3.5.6. The appendix BR table numbers differ (appendix BR-86 = check-in record, BR-90 = POI composition) and are reported to the SRS owner; code comments cite §3.5.6. |
| D5 | **No short QR TTL.** A QR stays usable for the whole departure window. **Refresh rotates**: the active QR becomes `Revoked` and a new one is issued, so screenshots of the old QR stop working. |
| D6 | A Traveler opening another Traveler's ticket gets **403 + MSG126** (SRS 2.a1). An id that does not exist gets 404. |
| D7 | **Backfill** by idempotent SQL that inserts `Tickets` rows only. SQL cannot sign, so the QR row is issued lazily on first view. The payment path also creates only the `Ticket`; the QR is issued lazily as well. |

## 3. Persistence boundary

Reuses existing tables. One new index; no new tables or columns.

| Table | UC-29 use |
| --- | --- |
| `commerce.Tickets` | Create on payment confirmation / backfill. Read. Status `Valid → Cancelled` on cancellation (existing `Ticket.Cancel`). |
| `commerce.TicketQrCodes` | Insert the active QR lazily, rotate on refresh, revoke on cancellation. |
| `commerce.Bookings`, `BookingParticipants`, `TourSchedules`, `Tours`, `OperatorProfiles` | Read for ownership, state and display data. |

Column use in `commerce.Tickets`:

| Column | Rule |
| --- | --- |
| `ticket_code` | `TK-` + `booking_id` zero-padded to 10 digits, e.g. `TK-0000000456`. Deterministic, unique (one ticket per booking), identical in C# and the backfill SQL. |
| `participant_id` | Always `NULL` (D2). |
| `status` | Stored values only: `Valid`, `Used`, `Cancelled`, `Expired`. UC-29 never writes `Used` or `Expired`. |
| `issued_at` | Payment-verification time (`clock.UtcNow`) or backfill time. |
| `expires_at` | Left `NULL`. The window is always derived from `TourSchedule.StartAtUtc` at read time so a rescheduled departure cannot leave a stale value. |
| `checked_in_at` | Read only (written by UC-43). |

### 3.1 Migration `database/migrations/20261024_uc29_1_ticket_issuance.sql`

Idempotent. Guarded by `IF NOT EXISTS` on `sys.indexes`.

1. Pre-check: if any `booking_id` already has more than one ticket, `THROW` with the offending ids and stop.
2. `CREATE UNIQUE INDEX UX_Tickets_OneTicketPerBooking ON commerce.Tickets(booking_id);` — the database
   backstop for D2 and for repeated payment notifications. The existing `IX_Tickets_Booking` is kept.

### 3.2 Backfill `database/migrations/20261024_uc29_2_backfill_tickets.sql`

Idempotent, one transaction, prints the inserted row count. Inserts a `Valid` ticket for every booking with
`status IN ('Confirmed','Completed') AND payment_status = 'Paid' AND tour_schedule_id IS NOT NULL` that has
no ticket. `issued_at = SYSUTCDATETIME()`. Run and verified on **Azure SQL** (never a local DB) and the
counts recorded in the PR.

## 4. Domain changes

- `Ticket`: add `TicketCode`, `ParticipantId`, `IssuedAtUtc`, `ExpiresAtUtc`, `CheckedInAtUtc`, a `Booking`
  navigation, `internal static Ticket IssueFor(Booking booking, DateTimeOffset issuedAtUtc)` and
  `static string CodeFor(long bookingId)`. Private setters only.
- `Booking.IssueTicket(DateTimeOffset nowUtc)`: allowed only for a persisted booking (`Id > 0`, otherwise
  `InvalidOperationException`) with `Status == Confirmed` and `PaymentStatus == Paid`. A ticket is only
  issued after UC-28 confirms a booking that UC-27 already saved, so the id always exists and
  `CodeFor(Id)` can never produce `TK-0000000000`. Sets both `BookingId` and the `Booking` navigation.
  Returns `false` when the loaded collection already has a ticket.
- New `TicketQrCode` entity and `TicketQrCodeStatus` enum (`Active`, `Consumed`, `Revoked`, `Expired`)
  matching the SQL check. Methods: `Issue(...)`, `Revoke(nowUtc)`. `Consumed` is for UC-43.
- New `TicketDisplayStatus` (`NotYetActive`, `Valid`, `Used`, `Cancelled`, `Expired`). Derived only, never
  stored — the SQL check constraint has no `NotYetActive`.
- EF configurations use `.AsUtcDateTime2()` for every timestamp.

## 5. Behaviour

### 5.1 Issuance (UC-28 completion path)

In `PaymentCompletionProcessor`, in the same `SaveChangesAsync` as `booking.MarkPaid(now)`, call
`booking.IssueTicket(now)` **only when `MarkPaid` returned `true`**. Repeated or late notifications make
`MarkPaid` return `false`, so no second ticket is created (UC-28 10.a2); the unique index is the backstop.
The webhook, mock-payment and cancel-link paths all go through this processor, so all are covered.

### 5.2 Display status (evaluated in this order)

| # | Condition | Display status | QR returned | Message |
| --- | --- | --- | --- | --- |
| 1 | Booking `Cancelled` or ticket `Cancelled` | `Cancelled` | no | MSG139 |
| 2 | Ticket `Used` | `Used` | no | MSG95 |
| 3 | Ticket `Expired`, or now is after the window end | `Expired` | no | MSG140 |
| 4 | Now is before the window start | `NotYetActive` | **yes** | MSG98 |
| 5 | Otherwise | `Valid` | yes | MSG93 |

The QR is returned for `NotYetActive` so a Traveler can open the ticket before travelling and still show
it at a meeting point without connectivity (D5). The Operator-side check of the window is UC-43's job.

### 5.3 View — `GET /api/v1/traveler/tickets/{ticketId}`

1. Caller must be an authenticated, Active Traveler, else 401/403.
2. Ticket not found → 404. Ticket's booking belongs to another Traveler → 403 (D6). The ownership check
   runs before any other state is revealed.
3. Booking not `Confirmed`/`Completed` with `Paid` (data defect, since tickets only exist for paid
   bookings) → 409 `ticket.booking_not_confirmed` (MSG138), no QR.
4. Compute the display status (§5.2). If a QR is to be returned and no `Active` QR exists, issue one.
   Concurrent first views are resolved by `UX_TicketQrCodes_OneActivePerTicket`: on a unique-key conflict
   re-read the active row instead of failing.
5. Return the response in `docs/UC-29-api-contract.md`. Viewing never changes ticket status (PC-04).

### 5.4 View from Booking Confirmation — `GET /api/v1/traveler/bookings/{bookingId}/ticket`

The Booking Confirmation screen knows the booking id, not the ticket id. Same handler, keyed by booking:

- Booking not found → 404; another Traveler's → 403.
- Booking not `Confirmed`/`Completed` + `Paid` (e.g. `PendingPayment`, `Expired`) → 409
  `ticket.booking_not_confirmed` (MSG138, SRS 3.a1). A `Cancelled` booking with a ticket returns 200 as
  `Cancelled` (§5.2 row 1).
- Paid booking with no ticket (backfill not run) → 409 `ticket.not_issued` (MSG127). Never created on read.

Trip History keeps linking by `ticketId` (TM-78); clients must not swap the two ids.

### 5.5 Refresh — `POST /api/v1/traveler/tickets/{ticketId}/qr-code/refresh`

Same auth and ownership as §5.3. Allowed only for `NotYetActive` or `Valid`, otherwise 409
`ticket.not_refreshable` with the current `ticketStatus` in the problem extensions. In one transaction:
revoke the active QR (`Revoked`), insert a new `Active` QR with a new nonce and timestamp, commit, then
return the same body as §5.3. No idempotency key: repeating a refresh only rotates again.

If the response is lost after commit, the client still shows a revoked QR. `GET` always returns the
current active QR, so a client recovers by re-reading. The client rules for this case are in
`docs/UC-29-api-contract.md` (confirm before refresh, disable while offline, re-`GET` on failure).

### 5.6 Cancellation

Implement `ApplicationDbContext.RevokeActiveTicketQrCodesAsync` as an `ExecuteUpdateAsync` that sets
`status = 'Revoked'` for `Active` QR rows of the booking's tickets. It already runs inside the
cancellation transaction (`CancelBookingCommandHandler` line 102). No change to cancellation rules.

## 6. QR payload (BR-83, PC-02)

Signing does **not** use ASP.NET Data Protection: `AddDataProtection()` has no persisted key ring and the
API runs on Railway containers, so keys change on redeploy. Keys come from configuration instead.

- Options `TicketQr` with `EncryptionKey` and `SigningKey` — each 32 random bytes, Base64. Supplied by
  User Secrets locally and environment variables on Railway; never committed. Validated on start-up
  (`ValidateOnStart`): missing or wrong-length keys stop the API rather than issuing unsigned tickets.
- Plaintext: JSON `{ "v": 1, "tid": <ticketId>, "n": "<16-byte random nonce, base64url>", "iat": <unix seconds> }`.
  No personal data.
- `qr_token` = base64url( 12-byte AES-GCM nonce ‖ ciphertext ‖ 16-byte tag ), AES-256-GCM with
  `EncryptionKey`, associated data `"TM1"`.
- `signature` = base64url( HMAC-SHA256(`SigningKey`, `"TM1." + qr_token`) ).
- QR content returned to clients = `TM1.{qr_token}.{signature}` (about 180 characters). Clients render it
  as-is and never parse or build it.
- UC-43 will verify by: constant-time HMAC check → decrypt → look up `qr_token` (unique) → require `Active`.

## 7. Error contract

All failures go through `HandleFailure` (ProblemDetails with `errorCode`). New `TicketErrorCodes`:

| `errorCode` | HTTP | When | SRS message |
| --- | ---: | --- | --- |
| `ticket.unauthorized` | 401 | Not signed in / token expired | MSG125 |
| `ticket.forbidden` | 403 | Not a Traveler, not Active, or not the booking owner | MSG126 |
| `ticket.not_found` | 404 | No ticket / booking with that id | MSG127 |
| `ticket.booking_not_confirmed` | 409 | Booking not confirmed by a verified payment | MSG138 |
| `ticket.not_issued` | 409 | Paid booking without a ticket (§5.4) | MSG127 |
| `ticket.not_refreshable` | 409 | Refresh on `Used`/`Cancelled`/`Expired` | by `ticketStatus` |
| — | 400 | Non-positive or non-numeric id (route constraint) | — |
| — | 500 | Unexpected failure | MSG127 |

## 8. New SRS messages (to add to the message catalog)

| # | Type | Context | Content |
| --- | --- | --- | --- |
| MSG138 | In line | E-ticket requested for a booking not confirmed by a verified payment | *Your e-ticket will be available once the payment for booking {Booking_Code} is confirmed.* |
| MSG139 | In line | E-ticket of a cancelled booking | *This e-ticket was cancelled together with booking {Booking_Code} and cannot be used for check-in.* |
| MSG140 | In line | E-ticket after its departure window | *This e-ticket expired after the departure date {Departure_Date}.* |

Existing messages kept: MSG93 (valid), MSG95 (used), MSG98 (not yet active), MSG126, MSG127.

## 9. Tests (TDD, `tests/`)

- Domain: `IssueTicket` guards; `CodeFor`; `TicketQrCode.Revoke`; display-status evaluation for every row
  of §5.2, including the 00:00 and 23:59:59 boundaries in `Asia/Ho_Chi_Minh`.
- Signer: round-trip, tampered token/signature rejected, wrong key rejected, no PII in plaintext,
  content length within `qr_token`/`signature` column limits.
- Payment processor: ticket issued once on success; repeated success, late notification, expired booking,
  cancelled/failed outcomes issue none.
- View/refresh handlers: 401/403 (role, inactive, non-owner)/404/409 paths; lazy QR issuance; concurrent
  first view; refresh rotates and the old row becomes `Revoked`; viewing never changes status.
- Cancellation: active QR revoked in the same transaction.
- Controller: route constraints and error-code → HTTP mapping.

## 10. Approval gates

- [x] D1–D7 confirmed by the developer (2026-10-24).
- [x] Developer approves this spec and `docs/UC-29-api-contract.md` (2026-10-24).
- [ ] SRS owner adds MSG138–MSG140 and notes the BR numbering mismatch (D4).
- [ ] `TicketQr` keys created in Railway environment variables before deploy.
- [x] Migration and backfill applied and verified on Azure SQL (2026-10-25, plan §5).
- [ ] Web and Mobile confirm the contract.

## 11. Known limits

- Only the booking owner can open the ticket (BR-90, D6). Participants are named people, not linked
  accounts, and bookings are not linked to travel groups, so a companion cannot open it in their own
  app. If the owner cannot attend, the owner can forward the QR image, which stays valid for the
  departure date (D5), and can revoke that copy with [Refresh QR Code]. Because there is one ticket per
  booking (D2), that QR checks in the whole booking at UC-43. Per-participant tickets
  (`participant_id`) or ticket transfer would be a later extension.

## 12. Known risk outside scope

`DataProtectionPiiProtector` (UC-27 participant document numbers and UC-46 operator bank account
numbers) and `DataProtectionPushTokenProtector` use the same key ring, and nothing in the repository
persists it. Unless the Railway service mounts a volume for it (not visible in the repo), ciphertext
written before a redeploy can no longer be decrypted. No code calls `Unprotect` yet, so nothing fails
today; the first feature that reads these values will. Track it as a separate ticket: persist the key
ring (e.g. `PersistKeysToDbContext` on Azure SQL) and assess the rows already written.
