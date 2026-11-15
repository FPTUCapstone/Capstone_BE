# UC-29 Implementation Plan — View QR E-ticket (Backend)

Spec: `specs/UC-29-view-qr-eticket-be-spec.md` (approved 2026-10-24, decisions D1–D7).
Contract: `docs/UC-29-api-contract.md` (approved 2026-10-24).
Branch: `feature/uc29-view-qr-eticket` (from `develop` @ `a6eb590e`; fast-forwarded to `5cbb25e7` on
2026-10-25 with PR #106 and #123 — no SQL migrations, no conflict with UC-29 files).
Status: **Approved 2026-10-24 (A1–A8 accepted); Tasks 1–11 done 2026-10-25 (§5). Branch not committed yet.**
Baseline on `develop` @ `a6eb590e` (2026-10-24): `dotnet test TripMate.slnx` green, exit 0.
Application.UnitTests 3985 passed; Api.IntegrationTests 641 passed + 484 skipped (SQL Server tests, no
server configured); Infrastructure.UnitTests and CandidatePoolReplay.Tests passed (counts not captured).

## 0. Ground Rules

- TDD per AGENTS.md §1.2: failing test first, confirm it fails for the right reason, minimum code,
  refactor, run the task's verification command. Code written before its test is deleted.
- One task at a time. After each task: two-pass self-review (spec compliance, then code quality).
  A Critical finding blocks the next task.
- Database-first: SQL scripts only, no EF migrations. No commit, push or PR unless the developer asks.
- No raw string literals for statuses, error codes, config keys or the QR prefix (AGENTS.md §4.1).
- Pure reads use `AsNoTracking()`; handlers that issue, rotate or revoke QR rows keep tracking.
- Controllers hold no logic. Every failure goes through `HandleFailure`.
- Secrets: the `TicketQr` keys are never committed. Tests use throw-away keys generated in the test.

## 1. Assumptions Introduced by This Plan (confirm with approval)

| # | Assumption | Why |
| --- | --- | --- |
| A1 | The departure date and window use a fixed `+07:00` offset, not a time-zone lookup. | Same as TM-78 A4: Vietnam has no DST and zone ids differ between Windows dev machines and Linux containers. |
| A2 | `TicketQr` keys are **required in every environment**, including Development. A developer without them gets a start-up error naming the two settings; `README.md` gets the `dotnet user-secrets set` commands. | Spec §6 says fail fast. The alternative, a Development-only fallback key, is a silent fake that could leak into a deploy. **This affects every teammate who runs the API locally.** |
| A3 | `GET` may insert the first `Active` QR row (lazy issuance, D7). It never changes ticket or booking status. | Spec §5.3 step 4. The insert is invisible to the client except as the returned QR. |
| A4 | Concurrent first views or concurrent refreshes that hit `UX_TicketQrCodes_OneActivePerTicket` are not errors: the handler re-reads and returns the QR that won. A new `SqlServerTicketQrPersistenceErrorClassifier` detects that index by name, following the existing classifiers. | Spec §5.3 step 4. Two taps on Refresh must not show an error. |
| A5 | Participant counts in the response come from `BookingParticipants.ParticipantType`; `participantCount` is `Booking.Quantity`. | Same source TM-78 uses. |
| A6 | The new endpoints live in a new `TravelerTicketsController`. The booking-keyed route uses an absolute route template on the same controller. | Keeps all UC-29 routes in one place and does not touch `BookingsController` (Traveler + Operator roles). |
| A7 | Unit tests use the existing in-memory `TestDbContext`, which does not enforce unique or filtered indexes. Concurrency, the unique indexes, the revoke `ExecuteUpdate` and both SQL scripts are tested in `[SqlServerFact]` integration tests, which skip unless `TRIPMATE_SQLSERVER_TEST_CONNECTION` is set. | Same split as UC-41 cancellation. |
| A8 | `[SqlServerFact]` tests are run against the SQL Server container from `docker-compose.yml`, not Azure: each test creates and drops its own database through `master`, which Azure SQL would bill as new databases. These are throw-away test databases, not seed/UAT verification, so the Azure-only rule still applies to Task 11. **Developer to confirm.** | Without a server they all skip and Tasks 3, 7 and 10 would prove nothing about the indexes. |

## 2. Task Breakdown

Projects: `tests/TripMate.Application.UnitTests` (U), `tests/TripMate.Infrastructure.UnitTests` (IU),
`tests/TripMate.Api.IntegrationTests` (I). Filter used below: `--filter "FullyQualifiedName~Ticket"`.

### Task 1 — Domain: Ticket, TicketQrCode, issuance on Booking

- **Files:** `Domain/Entities/Ticket.cs` (extend), `Domain/Entities/TicketQrCode.cs` (new),
  `Domain/Enums/TicketQrCodeStatus.cs`, `Domain/Enums/TicketDisplayStatus.cs` (new),
  `Domain/Entities/Booking.cs` (`IssueTicket`); tests `Application.UnitTests/Domain/TicketTests.cs`,
  `TicketQrCodeTests.cs`, `BookingTicketIssuanceTests.cs`.
- **Tests first (U):**
  - `Ticket.CodeFor(456)` → `TK-0000000456`; `CodeFor(0)` and negative ids throw; max `long` fits 30 chars.
  - `Booking.IssueTicket`: throws for `Id == 0` (spec §4); returns `false` and adds nothing unless
    `Confirmed` + `Paid`; issues one `Valid` ticket with `IssuedAtUtc`, `ParticipantId == null`,
    `ExpiresAtUtc == null`, both `BookingId` and navigation set; a second call returns `false`.
  - `TicketQrCode.Issue` validates token/signature length against the column limits (500);
    `Revoke` moves `Active → Revoked` and is a no-op for other states.
  - Existing `Ticket.Cancel` behaviour unchanged.
- **Verify:** `dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~Ticket"`.
- **DoD:** private setters only; no EF or framework references in Domain.

### Task 2 — EF mapping and context surface

- **Files:** `Infrastructure/Persistence/Configurations/TicketConfiguration.cs` (extend),
  `TicketQrCodeConfiguration.cs` (new), `IApplicationDbContext.cs` (`DbSet<TicketQrCode> TicketQrCodes`),
  `ApplicationDbContext.cs`, `tests/.../TestUtilities/TestDbContext.cs`; test
  `Infrastructure.UnitTests/Persistence/TicketMappingTests.cs`.
- **Tests first (IU):** model metadata maps every property to the v7 column names
  (`ticket_code`, `participant_id`, `issued_at`, `expires_at`, `checked_in_at`; `commerce.TicketQrCodes`
  columns), statuses are stored as strings, all timestamps use `AsUtcDateTime2()`, and the unique indexes
  `UX_Tickets_OneTicketPerBooking` and filtered `UX_TicketQrCodes_OneActivePerTicket` are declared by name.
- **Verify:** `dotnet test tests/TripMate.Infrastructure.UnitTests --filter "FullyQualifiedName~Ticket"`.
- **DoD:** existing `TripCardHydrator` and cancellation tests still pass (they already query `Tickets`).

### Task 3 — SQL migration and backfill scripts

- **Files (new):** `database/migrations/20261024_uc29_1_ticket_issuance.sql`,
  `database/migrations/20261024_uc29_2_backfill_tickets.sql`; test
  `Api.IntegrationTests/Bookings/TicketMigrationSqlServerTests.cs` (pattern:
  `BookingCancellationMigrationTests`).
- **Tests first (I, SQL Server):** migration is idempotent (runs twice); refuses with `THROW` when a booking
  already has two tickets; creates `UX_Tickets_OneTicketPerBooking`. Backfill inserts exactly one `Valid`
  ticket with code `TK-` + 10-digit id for `Confirmed`/`Completed` + `Paid` bookings with a schedule;
  skips `PendingPayment`, `Expired`, `Cancelled`, unpaid, itinerary-only and already-ticketed bookings;
  a second run inserts 0; prints the count.
- **Verify:** `dotnet test tests/TripMate.Api.IntegrationTests --filter "FullyQualifiedName~TicketMigration"`.
- **DoD:** the SQL `ticket_code` expression and `Ticket.CodeFor` produce identical strings (asserted).

### Task 4 — Validity window and display status

- **Files (new):** `Application/Features/Tickets/Common/TicketValidityWindow.cs`,
  `TicketDisplayStatusEvaluator.cs`; tests `Features/Tickets/TicketValidityWindowTests.cs`,
  `TicketDisplayStatusEvaluatorTests.cs`.
- **Tests first (U):**
  - Start `2026-10-30T01:00Z` → departure date `2026-10-30`, from `2026-10-29T17:00Z`, until
    `2026-10-30T16:59:59.9999999Z`. A start at `16:59Z` and at `17:00Z` fall on different local dates.
  - Every row of spec §5.2 in order, including precedence (cancelled booking with a `Used` ticket →
    `Cancelled`; `Used` after the window → `Used`), and the exact boundary instants.
  - `ShowsQr` is true only for `NotYetActive` and `Valid`; `CanRefresh` likewise.
- **Verify:** filter `Ticket`.
- **DoD:** §5.2 lives in exactly one class; takes `now` as a parameter, never reads a clock.

### Task 5 — QR codec and `TicketQr` options

- **Files (new):** `Application/Common/Interfaces/ITicketQrCodec.cs` (`Issue(ticketId, issuedAtUtc)` →
  token, signature, content; `TryVerify(content, out ticketId, out token)` for UC-43),
  `Infrastructure/Security/TicketQrOptions.cs`, `AesGcmHmacTicketQrCodec.cs`; registration in
  `Infrastructure/DependencyInjection.cs` with `.Validate(...)` + `.ValidateOnStart()`;
  empty `TicketQr` section in `appsettings.json`; test keys in the integration test host; README section.
  Tests `Infrastructure.UnitTests/Security/AesGcmHmacTicketQrCodecTests.cs`, `TicketQrOptionsTests.cs`.
- **Tests first (IU):** round-trip; content starts with the `TM1.` constant and has three parts; a flipped
  bit in token or signature, a different key, or a reordered part fails `TryVerify`; signature check is
  constant-time (`CryptographicOperations.FixedTimeEquals`); plaintext has only `v`, `tid`, `n`, `iat`;
  two issues for the same ticket differ (nonce); token ≤ 500 and signature ≤ 500 chars; options reject
  missing, non-Base64 and non-32-byte keys, and identical encryption and signing keys.
- **Verify:** `dotnet test tests/TripMate.Infrastructure.UnitTests --filter "FullyQualifiedName~TicketQr"`.
- **DoD:** no key material in logs or exception messages; `ValidateOnStart` proven by an API host test
  that starts without keys and fails with the setting names.

### Task 6 — Issuance in the payment completion path

- **Files:** `Application/Features/Traveler/Payments/Common/PaymentCompletionProcessor.cs`; tests in
  `Features/Traveler/Payments/PaymentTests.cs` (extend).
- **Tests first (U):** verified success on a `PendingPayment` booking → exactly one `Valid` ticket saved in
  the same `SaveChangesAsync`; repeated success, late notification after a terminal state, success on an
  already-expired hold, and `Cancelled`/`Expired`/`Failed` outcomes → no ticket. Mock-payment and
  cancel-link commands inherit the behaviour (one test each).
- **Code:** `if (booking.MarkPaid(now)) booking.IssueTicket(now);` — nothing else changes.
- **Verify:** `dotnet test tests/TripMate.Application.UnitTests --filter "FullyQualifiedName~Payment"`.
- **DoD:** all existing payment tests still green; no QR row is created here (D7).

### Task 7 — Revoke on cancellation

- **Files:** `Infrastructure/Persistence/ApplicationDbContext.cs` (`RevokeActiveTicketQrCodesAsync`); test
  `Api.IntegrationTests/Bookings/TicketQrRevocationSqlServerTests.cs`.
- **Tests first (I, SQL Server):** only `Active` rows of the given booking's tickets become `Revoked`;
  other bookings and non-active rows untouched; returns the affected count; rolled back with the
  cancellation transaction when that fails. Existing `CancelBookingCommandHandlerTests` (which use the
  `TestDbContext` fake) stay green.
- **Verify:** `dotnet test tests/TripMate.Api.IntegrationTests --filter "FullyQualifiedName~TicketQrRevocation"`
  then the cancellation filter `~CancelBooking`.
- **DoD:** cancellation rules unchanged.

### Task 8 — View query (by ticket and by booking) with lazy QR issuance

- **Files (new):** `Application/Features/Tickets/Common/TicketErrorCodes.cs`, `TicketResponse.cs`,
  `TicketResponseBuilder.cs`, `ITicketQrPersistenceErrorClassifier.cs`;
  `Features/Tickets/Get/GetTravelerTicketQuery.cs` (+ handler, keyed by `TicketId` or `BookingId`);
  `Infrastructure/Persistence/SqlServerTicketQrPersistenceErrorClassifier.cs`; tests
  `Features/Tickets/GetTravelerTicketQueryHandlerTests.cs`.
- **Tests first (U):**
  - Not a Traveler / inactive / another Traveler's ticket → `ticket.forbidden`; unknown id →
    `ticket.not_found`; ownership is checked before any state-dependent result.
  - By booking: `PendingPayment`/`Expired` → `ticket.booking_not_confirmed`; paid without ticket →
    `ticket.not_issued` and nothing is created; cancelled with ticket → 200 `Cancelled`.
  - Each display status returns the right `qr` (present/null) and fields of the contract example,
    including `validity`, `participants`, `checkedInAtUtc`, nullable `meetingPoint`.
  - First view issues one `Active` QR; second view returns the same content; a `Used`/`Cancelled`/
    `Expired` view issues none; viewing never changes ticket or booking status (PC-04).
  - Unique conflict reported by the classifier → re-read and return the winner (A4).
- **Verify:** filter `Ticket`.
- **DoD:** one query for ticket+booking+schedule+tour+operator, one for participant counts; no N+1.

### Task 9 — Refresh command

- **Files (new):** `Features/Tickets/Refresh/RefreshTicketQrCommand.cs` (+ handler); tests
  `Features/Tickets/RefreshTicketQrCommandHandlerTests.cs`.
- **Tests first (U):** same auth/ownership errors as Task 8; `Used`/`Cancelled`/`Expired` →
  `ticket.not_refreshable` carrying the current `ticketStatus`; allowed for `NotYetActive` and `Valid`;
  old row `Revoked`, new `Active` row with new token and later `IssuedAtUtc`, both in one save; works when
  no active QR exists yet; unique conflict → return the winner (A4).
- **Verify:** filter `Ticket`.
- **DoD:** shares `TicketResponseBuilder` with Task 8, so both endpoints return identical bodies.

### Task 10 — API: controller, error mapping, endpoint tests

- **Files:** `Api/Controllers/V1/TravelerTicketsController.cs` (new), `Api/Common/ApiControllerBase.cs`
  (map `TicketErrorCodes`); tests `Api.IntegrationTests/Tickets/TravelerTicketsEndpointTests.cs`,
  `TravelerTicketsSqlServerTests.cs`.
- **Tests first (I):** routes and verbs exactly as the contract; `{id:long:min(1)}` → 400 for `0`, `-1`,
  `abc`; 401 without token; 403 for Operator/Admin roles; every `errorCode` → HTTP status of spec §7 with
  `errorCode` present; `not_refreshable` problem carries `ticketStatus`; JSON ids are numbers; `qr` is
  `null` (not omitted) when hidden. SQL Server: concurrent first views and concurrent refreshes end with
  exactly one `Active` row; full flow payment success → view → refresh → cancel → view shows `Cancelled`
  with no QR.
- **Verify:** `dotnet test tests/TripMate.Api.IntegrationTests --filter "FullyQualifiedName~TravelerTickets"`.
- **DoD:** Swagger shows the three operations with their response types.

### Task 11 — Full verification and hand-off

- `dotnet build TripMate.slnx` with no new warnings; `dotnet test TripMate.slnx` green and compared with
  the baseline counts.
- Azure SQL (never local, per team rule): apply the migration, run the backfill, record before/after
  counts of eligible bookings and tickets, and run the second-run check (0 inserted). Needs the developer's
  Azure access; results go in the PR description.
- Update `docs/UC-29-api-contract.md` status to implemented with the branch and date.
- Present the finishing options (merge / PR / keep / discard). No push or PR without instruction.

## 3. Out of Scope (from spec)

UC-43 scan and `CheckInLogs`, writing `Used`/`Expired`, booking `Completed` transition, notifications,
Operator ticket view, persisting the Data Protection key ring (separate ticket after UC-29).

## 4. Risks

| Risk | Mitigation |
| --- | --- |
| Teammates' local API stops starting once A2 lands. | README commands and a clear start-up message; announce in the PR. |
| Railway deploy without `TicketQr` variables fails at start-up. | Approval gate in spec §10; add the variables before merging to `develop`. |
| Azure already holds duplicate tickets for a booking. | Migration pre-check stops with the ids instead of half-applying. |
| In-memory tests cannot prove index behaviour. | A7: SQL Server integration tests for every index-dependent rule. |

## 5. Implementation Notes (2026-10-25)

Tasks 1–10 implemented with TDD; each task's verification command was green before the next task.
Deviations from the plan text, each forced by a finding during implementation:

| # | Deviation | Reason |
| --- | --- | --- |
| N1 | Migration files are `20261024_uc29_1_ticket_issuance.sql` and `20261024_uc29_2_backfill_tickets.sql`. | Test databases and `apply-azure.sh` run migrations in ordinal file-name order; the original names would have run the backfill first. |
| N2 | No empty `TicketQr` section in `appsettings.json`. | Infrastructure tests load `appsettings.json` after their own settings, so an empty section overwrote the test keys. Real hosts read User Secrets / environment variables after the JSON, so nothing changes at runtime; README §9.2 and `.env.example` document the keys. |
| N3 | The ValidateOnStart host test compares the same override path with and without keys instead of asserting the message. | WebApplicationFactory replaces the start-up exception with `ObjectDisposedException`; the message is asserted in `TicketQrOptionsTests`. |
| N4 | `ITicketQrCodec` gained `ContentFor(token, signature)`. | The QR content is rebuilt from the stored token and signature on every view. |
| N5 | Existing tests that build `AddInfrastructure` (AiRanking, AiExplanation, TripMateAi, ReviewMedia) now supply `TicketQrTestKeys`. | Direct effect of A2. |
| N6 | TM-78 `TravelerTripsSqlServerTests` seeds one ticket per booking (was two on booking 1). | D2's `UX_Tickets_OneTicketPerBooking` makes several tickets per booking impossible in the database; the T2 several-tickets preference stays covered by the in-memory `TripCardHydratorTests`. |
| N7 | Ticket id / booking id are parsed by the controller, 400 has no `errorCode`. | Route constraints would answer `0`, `-1`, `abc` with 404; the contract row for 400 has no code (contract wording adjusted). |

### Verification on `5cbb25e7` + UC-29 (2026-10-25)

`dotnet test TripMate.slnx` with `TRIPMATE_SQLSERVER_TEST_CONNECTION` set (local SQL Server container, A8),
so the 484 SQL Server tests skipped in the baseline now run:

| Project | Result |
| --- | --- |
| Application.UnitTests | 4066 passed |
| Infrastructure.UnitTests | 974 passed, 3 skipped (real Cloudinary smoke tests) |
| CandidatePoolReplay.Tests | 13 passed |
| Api.IntegrationTests | 1269 passed, 11 skipped (Redis / Qdrant / Cloudinary external tests), 1 failed |

The one failure, `ServiceTripReviewEndpointSqlServerTests.CompletedOwnedService_CreateGetAndEdit_UsesPoiPolicyAndOriginalDeadline`
(TM-79, no ticket or booking code involved), took 54 s under full-suite load and passed 3/3 twice when
rerun alone; it is load-sensitive, not a UC-29 regression. An earlier full run also showed
`SolverAllocationBaselineTests` failing only under load (passes alone in 35 s).
`dotnet build TripMate.slnx`: 0 errors, 42 warnings, none in UC-29 files (same as before UC-29).
Task 11 (Azure SQL migration + backfill) is pending the developer's Azure login.

### Task 11 — Azure SQL UAT database (2026-10-25)

Read-only pre-checks found the UAT seed had issued one ticket per participant (567 tickets, 191 bookings
with 2–4 tickets) and 612 unsigned seed QR rows (240 Active), and that the Azure ledger stops at
`20261017_*` (11 other features' migrations are not recorded). Developer decisions (2026-10-25): keep D2
and rewrite the UAT tickets; revoke the seeded Active QR rows; apply only the two UC-29 files.

1. Backup of `commerce.Tickets` (567), `TicketQrCodes` (612), `CheckInLogs` (320) to
   `ops/uat-seed/backup-uc29-20261025/*.psv` (local, not in any repository).
2. `ops/uat-seed/uat-uc29-one-ticket-per-booking.sql` dry-run (rolled back), then committed: one ticket
   per booking kept by status Used → Valid → Expired → Cancelled, then lowest id; participant_id cleared;
   seeded Active QR revoked. Result: 261 tickets, 281 QR rows (0 Active), 156 check-in logs; 0 bookings
   with several tickets, 0 paid bookings without a ticket, 0 Used tickets without exactly one Success log.
3. `20261024_uc29_1_ticket_issuance.sql` applied: `UX_Tickets_OneTicketPerBooking` exists (unique, no filter).
4. `20261024_uc29_2_backfill_tickets.sql` applied: inserted 0 tickets (every eligible booking already had one).
5. Both recorded in `dbo.SchemaMigrations` as `Applied` with their SHA-256, as `apply-azure.sh` does.
   Rerunning both files is a no-op.
6. UAT seed generator updated (`ops/uat-seed/gen`: one ticket per booking, no seeded Active QR, verify
   rules `ticket.count` = 1 and at most one Active QR); regenerated output reproduces exactly the
   261 / 281 / 156 rows now on Azure. `uat-verify.sql` on Azure reports no ticket violations; its only
   findings are 7 `trip.active_session_expiry` rows caused by the seed's run-relative timestamps (seeded
   2026-10-18), unrelated to UC-29.

The 11 unrecorded migrations of other features were not touched; the team owner of each should bring
Azure up to date. The UC-29 migration checksums in the ledger match the files on this branch: if either
file changes before merge, the ledger row must be updated, or `apply-azure.sh` will refuse to run.
