# TM-89 / UC-43 — Final Engineering Report

**Date:** 2026-11-18 (Asia/Saigon)
**Task:** TM-89 — UC-43 Check In Traveler by QR Code
**Canonical SRS Authority:** `Report3_Software-Requirement-Specification.docx` (Original Report 3, SHA-256 `74d81c867c068cf5312187337afa6bd5a83f1f8e2a5ef0cc1e19415f4edc00c8`)
**Backend Repository / Branch:** `FPTUCapstone/Capstone_BE` — `fix/tm89-uc43-engineering-completion` (based on `origin/develop` `9fefc826`)
**Mobile Repository / Branch:** `FPTUCapstone/Capstone_Mobile` — `fix/tm89-uc43-engineering-completion` (`3fee2b983cd72de9986ec5ffb3117d478c9b7103`, PR [#92](https://github.com/FPTUCapstone/Capstone_Mobile/pull/92))
**Production Status:** **NO-GO** (Production infrastructure / Redis backup in GitHub Issue #162 remains explicitly deferred)
**Jira Status:** Unchanged (`TM-89` not transitioned)

---

## 1. Executive Summary & Scope Implemented

Continuing from the merged `develop` baseline (Backend PR #137, Backend PR #146, test commit `2979a2926b0b9d3719976b0b7e15334b16441bed`, Mobile PR #66, Mobile PR #67), this engineering completion pass audited the full UC-43 & BE-03 implementation against the canonical SRS and closed every remaining in-scope engineering defect and verification gap:

1. **BE-03 Malformed/Poison Event Crash Fix (`CheckInAuditProjectionBackgroundService`)** — **VERIFIED**
   - **Root cause:** `CheckInAuditProjectionBackgroundService.Parse(StreamEntry)` called `root.GetProperty(...)`, `.GetInt32()`, `.GetInt64()`, and `.GetString()` directly. Missing properties threw `KeyNotFoundException` and wrong JSON types / non-object roots threw `InvalidOperationException`, escaping the per-entry `catch` filter (`JsonException | FormatException | InvalidDataException | ArgumentException`), aborting the entire projection batch, blocking healthy events, and incrementing `ProjectionFailures` instead of isolating the poison entry as a conflict.
   - **Fix:** Refactored `Parse(StreamEntry)` in `src/TripMate.Infrastructure/Services/Audit/CheckInAuditProjectionBackgroundService.cs` to validate object root, required properties via `TryGetProperty`, and value kinds, throwing `InvalidDataException` on any malformed shape. Malformed entries now remain pending (no ACK/XDEL/XTRIM), increment `metrics.Conflicts`, record `ProjectionFailures == 0`, and never block healthy events in the same or subsequent batches.
2. **Concurrent Booking Cancellation TOCTOU Fix (`CheckInTravelerCommandHandler`)** — **VERIFIED**
   - **Root cause:** Inside `ExecuteInSerializableTransactionAsync`, the handler reloaded `Ticket` without its `Booking` status/payment status, so a concurrent booking cancellation between pre-check (Step 7) and transaction start (Step 9) mapped to generic `checkin.ticket_invalid` (or risked missing a booking-only status transition).
   - **Fix:** Included `Booking` inside the serializable transaction in `src/TripMate.Application/Features/Operator/CheckIn/Scan/CheckInTravelerCommandHandler.cs` and enforced `BookingCancelled` and `BookingNotConfirmed` checks prior to ticket consumption. Verified with a real SQL Server concurrency barrier test (`CancelCommitsWhileScanWaitsForTransaction_ScanRejectsBookingCancelledAndLeavesTicketUnused`).
3. **Departure Counter Reconciliation & HTTP + SQL + Redis Acceptance Coverage** — **VERIFIED**
   - Added real HTTP + SQL Server 2022 + Redis 8.2 tests in `tests/TripMate.Api.IntegrationTests/Tickets/CheckInAuditHttpSqlFailureTests.cs` verifying wrong schedule (`409`), cancelled booking (`409`), after-departure-window (`409`), manual booking-code check-in (`200`), multi-booking counter reconciliation across `POST /scan`, `GET /schedules/{id}/progress`, `GET /departures`, and SQL tables (excluding cancelled and unpaid bookings), and admission-only indeterminate attempts (confirmed in `admissions`, 0 in `rejections`, 0 projected SQL rows).
4. **Flutter Mobile Fixes & Real Flutter ↔ Backend End-to-End Integration** — **VERIFIED**
   - Fixed stale departure summary counts in `OperatorCheckInCubit` after check-in / progress refresh, made `MSG127` (`503` → `system`) and HTTP `400` validation (`invalidInput`) mappings explicit in `OperatorCheckInRepositoryImpl`, and differentiated `MobileScannerErrorCode.permissionDenied` in `CheckInCameraScannerView`.
   - Added and executed real Flutter–Backend integration suite (`test/integration/tm89_uc43_live_backend_test.dart`) hitting live `TripMate.Api` backed by isolated SQL Server 2022 and Redis 8.2 AOF (`CheckInAuditJournal:Enabled=true`).
5. **Fresh Schema & Migration Compatibility Verification** — **VERIFIED**
   - Applied `database/tripmate_schema_v7.sql` + all incremental migrations on `origin/develop` (including UC-10, payout, refund, and `20261105_add_checkin_audit_journal_identity.sql`) twice (fresh + idempotent re-run) on disposable SQL Server 2022 with exit code `0`.

---

## 2. Canonical SRS Traceability & Recorded Conflicts

Source: `Report3_Software-Requirement-Specification.docx` (extracted line references recorded from canonical document).

| SRS Reference | Requirement | Implementation & Verification Evidence | Status |
|---|---|---|---|
| §2.2.2, §3.8.4.4 | UC-43 Check In Traveler by QR Code (Tour Operator, Flutter Mobile + ASP.NET Core BE) | `OperatorCheckInController`, `CheckInTravelerCommandHandler`, `OperatorCheckInPage` | VERIFIED |
| §3.1.2 (#88), §3.1.3 | Screen #88 Tour Participant QR Check-in Scanner — Tour Operator role only | `[Authorize(Roles = nameof(UserRole.TourOperator))]`, `OperatorBookingAccess`, Flutter `app_router.dart` + live 401/403 checks | VERIFIED |
| BR-82 | Only confirmed + verified-payment booking holds a valid e-ticket | `CheckInTravelerCommandHandler` (pre-check + serializable tx); HTTP & live Flutter `BookingNotConfirmed` (409) | VERIFIED |
| BR-83 | Cryptographic signature & timestamp verification; reject forged/altered/revoked payload | `AesGcmHmacTicketQrCodec`, DB active credential match + unix-second check; unit, SQL & live HTTP/Flutter tests | VERIFIED |
| BR-84 | Accepted only within departure window of associated tour schedule | `TicketValidityWindow.For(schedule.StartAtUtc)` (Asia/Ho_Chi_Minh 00:00–23:59:59.9999999); before-window & after-window HTTP tests | VERIFIED |
| BR-85 | Strict single-use e-ticket enforcement | Serializable transaction + deadlock retry; `AlreadyUsed` (409) with prior `checkedInAt`; concurrent scan SQL & HTTP tests | VERIFIED |
| BR-86 | Cancelled booking invalidated e-ticket never accepted | Pre-check + serializable tx check (`BookingCancelled` 409); concurrent cancel-vs-scan SQL race test | VERIFIED |
| BR-108 | Departure date & owning Tour Operator enforcement | `OperatorBookingAccess` + `schedule.OperatorUserId == operatorId` (`ScheduleForbidden` 403) + departure date window | VERIFIED |
| Abnormal 8.a1 / MSG127 | Recording/infrastructure failure displays MSG127, leaves ticket unused, allows retry | Fail-closed BE-03 admission/outcome & `SqlServerCheckInPersistenceErrorClassifier` → HTTP 503 `MSG127`; recovery retry passes | VERIFIED |
| Alt Flow | Manual check-in by booking code | `POST /api/v1/operator/check-in/scan` (`bookingCode`) + `CheckInManualCodeDialog` live E2E test | VERIFIED |

### Recorded SRS §3.8.4.4 vs §5.3 Message-ID Conflicts (Not Silently Altered)

| Message ID | §3.8.4.4 Citation | §5.3 Catalog Citation | Engineering Posture |
|---|---|---|---|
| `MSG46` | Camera permission denied (Validation & Abnormal Case 2.a1) | Device **location** permission denied by user | Preserved safe camera-permission guidance keeping manual fallback open; did **not** display §5.3 location text. Pending Owner/SRS reconciliation. |
| `MSG74` | Booking has been cancelled (Validation & Abnormal Case 6.a2) | Confirmation pop-up: Operator pauses/unpublishes an active tour | Backend returns semantic `checkin.booking_cancelled` (409); Mobile renders rejected banner. Pending Owner/SRS reconciliation. |
| `MSG97` | E-ticket belongs to another departure (Validation & Abnormal Case 5.a2) | Alert pop-up: QR ticket belongs to a cancelled or refunded booking | Backend separates `checkin.wrong_schedule` (409) and `checkin.booking_cancelled` (409). Pending Owner/SRS reconciliation. |
| `MSG99` | Departure window has passed (Validation & Abnormal Case 7.a2) | Toast: Voucher created successfully by Tour Operator | Backend returns `checkin.departure_not_today` (409) for both before- and after-window attempts. Pending Owner/SRS reconciliation. |

---

## 3. Postcondition Acceptance Matrix (PC-01 through PC-05)

| Postcondition | Required Invariant | Verification Evidence | Dev-Level Status |
|---|---|---|---|
| **PC-01** | Check-in record exists with performing Tour Operator and timestamp | `commerce.CheckInLogs` (`Success`, `checked_in_by`, `checked_in_at`) + `commerce.Tickets.checked_in_at` verified in SQL regression, HTTP tests, and live Flutter E2E (`4` Success rows) | **VERIFIED** |
| **PC-02** | E-ticket marked `Used` and cannot be accepted again (including concurrency) | `TicketStatus.Used`, `TicketQrCodeStatus.Consumed`, repeat scan → 409 `checkin.already_used` with prior `checkedInAt`; concurrent 2-scan HTTP & SQL tests produce exactly 1×200 and 1×409 | **VERIFIED** |
| **PC-03** | Departure check-in counter accurately reflects recorded check-ins | `Http_DepartureCounterReconciliation_MatchesAcrossScanProgressDeparturesAndSql` (`0/5 → 3/5 → 5/5`) + live Flutter E2E (`0/8 → 2/8 → 5/8 → 7/8 → 8/8`) reconciled across `/scan`, `/progress`, `/departures`, and SQL | **VERIFIED** |
| **PC-04** | Failed verification or recording never consumes ticket or creates success record | `RecordingFailure_RollsBackTicketQrAndLog_AndAllowsRetry` (aggregation/save/commit), `AdmissionFailure_ReturnsMsg127`, `SqlVerificationUnavailable_AfterDurableAdmission`, `InvalidCredential_LeavesTicketUnused`, and live E2E cancelled/unpaid tickets remaining `Valid`/`checked_in_at=NULL` | **VERIFIED** |
| **PC-05** | Every rejected verification attempt is written to the audit log | Durable Redis Streams + `WAITAOF` + SQL projection (`dbo.AuditLogs` with `journal_event_id` & `scan_attempt_id`), SQL outage/recovery replay (25 events), duplicate idempotency, conflict retention, poison isolation, AOF restart survival, and live Flutter E2E (`9/9` rejections projected, `XPENDING=0`) | **VERIFIED (Dev/Isolated)** — Production PC-05 remains **BLOCKED / PRODUCTION NO-GO** pending Issue #162 & Owner policy approvals |

---

## 4. Executed Commands & Verified Test Results

### Backend (`C:\tm89_worktrees\be`, .NET SDK `10.0.401`, .NET Runtime `10.0.12`)

| Suite / Gate | Exact Command | PASS | FAIL | SKIP | Result |
|---|---|---:|---:|---:|---|
| Formatting | `dotnet format TripMate.slnx --verify-no-changes --no-restore --include <changed .cs>` + `git diff --check` | — | 0 | — | **PASS** |
| Build | `dotnet build TripMate.slnx -c Release` | 0 errors | 0 | — | **PASS** |
| Application Unit Tests (full serial) | `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --no-build -- xUnit.MaxParallelThreads=1` | 5,338 | 0 | 0 | **PASS** |
| Infrastructure Unit Tests (full) | `dotnet test TripMate.slnx -c Release --no-restore -m:1 --filter "Category!=SqlServer&Category!=Redis"` (Infrastructure project) | 1,312 | 0 | 3 (real Cloudinary smoke) | **PASS** |
| Infrastructure Focused Check-In / Audit | `dotnet test tests/TripMate.Infrastructure.UnitTests/... --filter "FullyQualifiedName~CheckInAudit\|FullyQualifiedName~CheckInPersistence"` | 38 | 0 | 0 | **PASS** |
| CandidatePoolReplay Tests | `dotnet test TripMate.slnx ...` (Replay project) | 13 | 0 | 0 | **PASS** |
| API Integration (non-SQL/Redis) | `dotnet test TripMate.slnx ...` (API Integration non-SQL/Redis) | 940 | 0 | 162 (gated SQL/Redis) | **PASS** |
| Real Redis 8.2 Journal & Projection (incl. poison) | `dotnet test tests/TripMate.Api.IntegrationTests/... --filter "FullyQualifiedName~CheckInAuditRedisJournalTests\|FullyQualifiedName~CheckInAuditProjectionRedisTests"` | 6 | 0 | 0 | **PASS** |
| Real SQL Server 2022 Audit Migration | `dotnet test tests/TripMate.Api.IntegrationTests/... --filter "FullyQualifiedName~AuditMigrationSqlServerTests"` | 5 | 0 | 0 | **PASS** |
| Real SQL + Redis Projection (outage, recovery, duplicate, conflict, poison) | `dotnet test tests/TripMate.Api.IntegrationTests/... --filter "FullyQualifiedName~CheckInAuditProjectionSqlServerTests"` | 3 | 0 | 0 | **PASS** |
| Real SQL Check-In Regression & Concurrency (incl. cancel race & QR issuer) | `dotnet test tests/TripMate.Api.IntegrationTests/... --filter "FullyQualifiedName~CheckInTravelerRegressionSqlServerTests\|FullyQualifiedName~TicketQrIssuerConcurrencyTests"` | 23 | 0 | 0 | **PASS** |
| Real HTTP + SQL + Redis Acceptance (all UC-43 scenarios, counters, admission-only) | `dotnet test tests/TripMate.Api.IntegrationTests/... --filter "FullyQualifiedName~CheckInAuditHttpSqlFailureTests"` | 18 | 0 | 0 | **PASS** |
| Real Redis 8.2.11 AOF Restart Probe & Latency | `CheckInAuditAofRestartTests` (`write` → `docker restart tm89-verify-redis` → `verify` + 50 samples) | 2 | 0 | 0 | **PASS** (`p50=3.38 ms, p95=3.98 ms, p99=95.27 ms`) |
| Fresh Schema + All Migrations (`apply-schema.sh`) | Fresh run + idempotent second run on SQL Server 2022 (`TripMate_TM89_Fresh`) | 2/2 runs | 0 | 0 | **PASS** |

### Mobile (`C:\tm89_worktrees\mobile`, Flutter `3.47.9`, Dart `3.13.7`)

| Suite / Gate | Exact Command | PASS | FAIL | SKIP | Result |
|---|---|---:|---:|---:|---|
| Formatting | `dart format --output=none --set-exit-if-changed .` | 909 files | 0 | — | **PASS** |
| Static Analysis | `flutter analyze` | 0 issues | 0 | — | **PASS** |
| Focused UC-43 Unit/Widget/Router | `flutter test test/features/operator_check_in test/app/router/operator_check_in_route_test.dart` | 91 | 0 | 0 | **PASS** |
| Full Mobile Regression | `flutter test` | 2,800 | 0 | 6 (env-gated live tests) | **PASS** |
| Real Flutter ↔ Live Backend E2E | `flutter test test/integration/tm89_uc43_live_backend_test.dart --reporter expanded` (against live `TripMate.Api` + SQL 2022 + Redis 8.2 AOF) | 2/2 suites | 0 | 0 | **PASS** |

---

## 5. Remaining Limitations & Production Blockers

1. **Physical Mobile Camera Hardware QA:** `NOT_VERIFIED` — no physical Android/iOS camera device attached to the execution host. Camera callback handling, rapid-scan latch, and permission-denied fallback were verified via automated widget/live tests; physical camera verification must be completed on a device before Release QA sign-off.
2. **Deferred Production Infrastructure (GitHub Issue #162):** Railway Pro / Redis backup (R2/B2) / Production Redis AOF + `noeviction` + `WAITAOF` verification / Production Azure SQL migration approval (`20261105_add_checkin_audit_journal_identity.sql`) / Production `CheckInAuditJournal__Enabled=true` activation are explicitly **DEFERRED** (`PRODUCTION_READY = NO`).
3. **Pending Product/Owner Policy Decisions:**
   - Official audit-record definition, retention/trimming policy, and projection/availability SLA.
   - Business reconciliation policy for admission-only indeterminate attempts (current safe behavior preserved: admission stays durable in `admissions`, never projected as a fabricated `Rejected` SQL row).
   - Canonical SRS §3.8.4.4 vs §5.3 message-ID conflict resolution (`MSG46`, `MSG74`, `MSG97`, `MSG99`).
4. **Repository Merge Governance:** `develop` branch protection requires human reviewer approval and Railway auto-deploy freeze verification (`docs/operations/tm89-be03-production-readiness.md`) before merge.
