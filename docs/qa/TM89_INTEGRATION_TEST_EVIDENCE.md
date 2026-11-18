# TM-89 / UC-43 — Integration Test Evidence

**Execution Date:** 2026-11-18 (Asia/Saigon)
**Execution Mode:** Isolated Disposable Containers (Zero Production / Shared Infrastructure Access)

---

## 1. Test Environment

| Component | Exact Version / Configuration |
|---|---|
| Host OS | Windows (PowerShell `pwsh`) |
| .NET SDK / Runtime | `10.0.401` / `.NET 10.0.12` |
| Flutter / Dart | Flutter `3.47.9` / Dart `3.13.7` |
| Docker Engine | `29.5.2` |
| Isolated SQL Server | `mcr.microsoft.com/mssql/server:2022-latest` — Microsoft SQL Server 2022 (RTM-CU27-GDR) `16.0.4305.5`, container `tm89-verify-sql`, loopback `127.0.0.1:14338` |
| Isolated Redis | `redis:8.2-alpine` — Redis `8.2.11`, container `tm89-verify-redis`, loopback `127.0.0.1:16388`, named volume `tm89_verify_redis_data:/data`, config `tm89-aof-always.conf` (`appendonly yes`, `appendfsync always`, `dir /data`) |
| Live API Host (Flutter E2E) | `TripMate.Api.dll` (Release build from `fix/tm89-uc43-engineering-completion`) listening on `http://127.0.0.1:5189`, `CheckInAuditJournal__Enabled=true`, `PollInterval=00:00:00.200` |

---

## 2. Database Compatibility Verification (`apply-schema.sh`)

Executed on fresh disposable database `TripMate_TM89_Fresh` on `tm89-verify-sql`:

1. **Run 1 (Fresh Apply):** `tripmate_schema_v7.sql` + all incremental migrations in `database/migrations/` (including `20261105_add_checkin_audit_journal_identity.sql`, UC-10 provenance/feedback migrations, payout v6, and refund migrations) → **Exit Code 0**.
2. **Run 2 (Idempotent Re-Run):** Re-ran `apply-schema.sh` against the same database → skipped v7 base cleanly, re-applied all incremental migrations idempotently → **Exit Code 0**.
3. **Verified Schema Objects:**
   - `dbo.AuditLogs.scan_attempt_id`: `uniqueidentifier`, `max_length = 16`, `is_nullable = 1`
   - `dbo.AuditLogs.journal_event_id`: `uniqueidentifier`, `max_length = 16`, `is_nullable = 1`
   - `UX_AuditLogs_JournalEventId`: `is_unique = 1`, `has_filter = 1`, `filter_definition = ([journal_event_id] IS NOT NULL)`
   - `UX_Tickets_OneTicketPerBooking`: `is_unique = 1` on `commerce.Tickets`
   - `commerce.CheckInLogs`: verified columns `checkin_log_id`, `ticket_id`, `qr_code_id`, `checked_in_by`, `scan_result`, `latitude`, `longitude`, `checked_in_at`

---

## 3. Real Flutter ↔ Live Backend ↔ SQL Server ↔ Redis E2E Run

### Synthetic Seed Dataset (`TripMate_TM89_Fresh`)

- **Accounts (real `PasswordHasherService` hashes, authenticated via `POST /api/v1/auth/login`):**
  - Owner Tour Operator (`Approved` + `Active`)
  - Rival Tour Operator (`Approved` + `Active`)
  - Traveler (`Active`)
- **Schedules (Asia/Ho_Chi_Minh calendar day):**
  - `scheduleId = 1`: Today 08:00 ICT (`Dragon Bridge Gate A`)
  - `otherScheduleId = 2`: Today 14:00 ICT (`Dragon Bridge Gate B`, for `WrongSchedule`)
  - `outsideWindowScheduleId = 3`: Tomorrow 08:00 ICT (`Dragon Bridge Gate C`, for `DepartureNotToday`)
- **Bookings & Cryptographically Signed QR Tickets (Schedule 1, issued via real `AesGcmHmacTicketQrCodec` + `TicketQrIssuer`):**
  - `BK-APIV-*`: Confirmed / Paid, `quantity = 2` (API valid QR test)
  - `BK-APIM-*`: Confirmed / Paid, `quantity = 3` (API manual code test)
  - `BK-UIV-*`: Confirmed / Paid, `quantity = 2` (Widget UI valid QR + rapid scan test)
  - `BK-UIM-*`: Confirmed / Paid, `quantity = 1` (Widget UI manual dialog test)
  - `BK-CANC-*`: Cancelled / Paid, `quantity = 4` (excluded from eligible count; QR issued prior to cancellation)
  - `BK-UNPD-*`: Confirmed / Unpaid, `quantity = 1` (excluded from eligible count)
  - **Expected Total Eligible Participants:** `2 + 3 + 2 + 1 = 8`

### Observed API & Flutter Outcomes (`tm89_uc43_live_backend_test.dart`)

Command: `flutter test test/integration/tm89_uc43_live_backend_test.dart --reporter expanded`
Result: **`00:07 +2: All tests passed!`**

| # | Scenario | Layer Exercised | Observed HTTP / Error Code | Mobile Result |
|---|---|---|---|---|
| 1 | Tour Operator login (`/api/v1/auth/login`) | Real `DioClient` → `AuthController` | `200 OK` + JWT issued | Authorized session established |
| 2 | Anonymous departures request | `OperatorCheckInRepositoryImpl` | `401 Unauthorized` | `CheckInFailureKind.unauthorized` |
| 3 | Traveler departures request | `OperatorCheckInRepositoryImpl` | `403 Forbidden` | `CheckInFailureKind.forbidden` |
| 4 | Rival operator scans owner's departure | `OperatorCheckInRepositoryImpl` | `403` / `checkin.schedule_forbidden` | `CheckInFailureKind.scheduleForbidden` |
| 5 | Invalid/forged QR signature | Repository + `OperatorCheckInPage` | `400` / `checkin.invalid_qr` | `CheckInFailureKind.invalidQr` + Rejected banner |
| 6 | Wrong departure schedule (`scheduleId=2`) | `OperatorCheckInRepositoryImpl` | `409` / `checkin.wrong_schedule` | `CheckInFailureKind.wrongSchedule` |
| 7 | Outside departure window (`scheduleId=3`) | `OperatorCheckInRepositoryImpl` | `409` / `checkin.departure_not_today` | `CheckInFailureKind.departureNotToday` |
| 8 | Cancelled booking QR | `OperatorCheckInRepositoryImpl` | `409` / `checkin.booking_cancelled` | `CheckInFailureKind.bookingCancelled` |
| 9 | Unpaid booking QR | `OperatorCheckInRepositoryImpl` | `409` / `checkin.booking_not_confirmed` | `CheckInFailureKind.bookingNotConfirmed` |
| 10 | Valid QR scan (API `2 pax` + Widget `2 pax`) | Repository + `OperatorCheckInPage` | `200 OK` | Accepted banner, progress `0→2→5→7` |
| 11 | Rapid 3x identical QR detection before `[Scan Next]` | `CheckInCameraScannerView` + `OperatorCheckInCubit` | Exactly **1** HTTP `POST /scan` | Duplicate callbacks latched/suppressed |
| 12 | Repeat QR scan after `[Scan Next]` | Repository + `OperatorCheckInPage` | `409` / `checkin.already_used` + `checkedInAt` | Warning banner with prior timestamp |
| 13 | Manual booking-code fallback (API `3 pax` + Dialog `1 pax`) | Repository + `CheckInManualCodeDialog` | `200 OK` | Accepted banner, progress reaches `8/8` (`0 waiting`) |
| 14 | Counter reconciliation across `/scan`, `/progress`, `/departures` | `OperatorCheckInRepositoryImpl` + `OperatorCheckInCubit` | `200 OK` | `checkedIn=8, totalEligible=8, waiting=0` everywhere |

### Observed Redis 8.2.11 Streams & Consumer Group State (Post-Run)

- `Admissions XLEN`: **13** (`4` successful check-ins + `9` business rejections)
- `Rejections XLEN`: **9** (all retained; zero trim/delete)
- `Rejections XPENDING`: **0** (all 9 committed to SQL and ACKed)

### Observed SQL Server 2022 Persistence (Post-Run)

- `commerce.Tickets`:
  - `TK-0000000001` (`BK-APIV-*`, 2 pax): `Used`, `checked_in_at` populated
  - `TK-0000000002` (`BK-APIM-*`, 3 pax): `Used`, `checked_in_at` populated
  - `TK-0000000003` (`BK-UIV-*`, 2 pax): `Used`, `checked_in_at` populated
  - `TK-0000000004` (`BK-UIM-*`, 1 pax): `Used`, `checked_in_at` populated
  - `TK-0000000005` (`BK-CANC-*`, 4 pax): `Valid`, `checked_in_at = NULL` (untouched)
  - `TK-0000000006` (`BK-UNPD-*`, 1 pax): `Valid`, `checked_in_at = NULL` (untouched)
- `commerce.CheckInLogs`: `Success = 4`, `AlreadyUsed = 2`
- `dbo.AuditLogs` (`journal_event_id IS NOT NULL`): **9 rows total**:
  - `checkin.already_used`: 2
  - `checkin.booking_cancelled`: 1
  - `checkin.booking_not_confirmed`: 1
  - `checkin.departure_not_today`: 1
  - `checkin.invalid_qr`: 2
  - `checkin.schedule_forbidden`: 1
  - `checkin.wrong_schedule`: 1

---

## 4. Redis 8.2 AOF Durability, Poison Handling & Concurrency Evidence

- **AOF Restart Survival (`CheckInAuditAofRestartTests`):**
  - Phase `write` confirmed `WAITAOF 1 0` on `tm89-verify-redis` (`runId=tm89_final_20261118`).
  - Container restarted via `docker restart tm89-verify-redis`.
  - Phase `verify` confirmed stream length `1` survived restart and same-`EventId` replay remained idempotent (`StreamLength == 1`).
  - 50-sample `append + WAITAOF` latency distribution on isolated container: **`p50 = 3.38 ms`, `p95 = 3.98 ms`, `p99 = 95.27 ms`** (no SLA inferred).
- **Malformed/Poison Event Isolation (`CheckInAuditProjectionRedisTests` & `CheckInAuditProjectionSqlServerTests`):**
  - Missing payload, invalid JSON, non-object JSON (`[1,2,3]`), missing required fields, wrong JSON property types, and an `Admission` event injected into the rejection stream all stayed pending (`XPENDING = 6` in Redis test, `3` in SQL+Redis test), incremented `metrics.Conflicts`, recorded `ProjectionFailures = 0`, preserved stream length (no `XDEL`/`XTRIM`), and allowed all healthy rejection events in the same and subsequent batches to project to `dbo.AuditLogs` and ACK cleanly.
- **SQL Outage & Recovery (`CheckInAuditProjectionSqlServerTests` & `CheckInAuditHttpSqlFailureTests`):**
  - 25 rejection events remained pending during SQL outage and projected 25/25 after recovery (`XPENDING = 0`, `StreamLength = 25`).
  - HTTP SQL unavailability after durable admission returned `HTTP 503` / `MSG127`, left ticket unused, recorded 1 admission and 0 rejections, never fabricated a rejected SQL audit row even after SQL recovery (`Http_AdmissionOnlyIndeterminateAttempt_NeverProjectsAsRejectedInSql`), and subsequent retry after SQL recovery succeeded once.
- **Concurrency Races (`CheckInTravelerRegressionSqlServerTests` & `CheckInAuditHttpSqlFailureTests`):**
  - Two concurrent scans of the same QR → exactly 1×200 and 1×409 `AlreadyUsed`, 1 consumption, 1 audited rejection.
  - Concurrent booking cancellation vs scan barrier (`CancelCommitsWhileScanWaitsForTransaction_ScanRejectsBookingCancelledAndLeavesTicketUnused`) → scan rejected with `checkin.booking_cancelled`, ticket stayed `Cancelled`, 0 `Success` check-in logs.
  - Concurrent QR refresh vs scan barrier → old QR rejected with `checkin.invalid_qr`, replacement QR remained active and succeeded.

---

## 5. Failures Encountered During Verification & Resolutions

1. **Disk space exhaustion on `D:` (`0.00 GB` free):** Relocated working git worktrees to `C:\tm89_worktrees\` (`56.68 GB` free) and cleaned up `D:` worktrees (freeing `1.28 GB` on `D:`).
2. **Test fixture bug in initial `Http_CancelledBooking` & `Http_DepartureCounterReconciliation` tests:** Initial test fixture tried to call `TicketQrIssuer.EnsureActiveAsync` on an already-cancelled booking (correctly rejected by UC-29). Updated test fixture to issue QR while `Confirmed/Paid` and then transition booking to `Cancelled`, matching real lifecycle; 18/18 passed.
3. **Known parallel-contention flake in unrelated `AuditLogOutcomeTests`:** Reproduced once during parallel `dotnet test` run (5,337/5,338); serial verification (`xUnit.MaxParallelThreads=1`) passed `5,338/5,338` cleanly with zero changes to production code or test expectations.
4. **Flutter `TestWidgetsFlutterBinding` HTTP override in live E2E test:** `testWidgets` intercepted real HTTP calls with synthetic 400 until `LiveTestWidgetsFlutterBinding.ensureInitialized()` + `HttpOverrides.global = null` were enabled when `live == true`; live Flutter E2E then passed 2/2.
