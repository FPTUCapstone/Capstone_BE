# TM-89 / UC-43 — Engineering Handoff

**Date:** 2026-11-18 (Asia/Saigon)
**Target Branch:** `develop` (NOT `main`)
**Production Ready:** **NO**
**Jira Status:** Unchanged (`TM-89` not transitioned)

---

## 1. Completed Engineering Work

- **Backend (`FPTUCapstone/Capstone_BE`, branch `fix/tm89-uc43-engineering-completion`):**
  - Fixed BE-03 malformed/poison event parsing crash in `CheckInAuditProjectionBackgroundService.Parse(StreamEntry)` so non-object JSON, missing required properties, and mismatched JSON types are isolated per entry (`Conflicts++`, `ProjectionFailures=0`, kept pending without ACK/XDEL/XTRIM) without blocking healthy events.
  - Closed concurrent booking cancellation TOCTOU window inside `CheckInTravelerCommandHandler`'s serializable transaction (`BookingCancelled` / `BookingNotConfirmed` enforced atomically on reloaded `Ticket.Booking`).
  - Added automated unit + real Redis 8.2 + real SQL Server 2022 tests covering poison-event isolation, concurrent cancellation race, wrong schedule, cancelled booking, after-window rejection, manual booking-code check-in, full departure counter reconciliation across `/scan`, `/progress`, `/departures`, and SQL, plus admission-only indeterminate non-projection.
  - Verified full fresh + idempotent `apply-schema.sh` compatibility on SQL Server 2022 against `origin/develop` (`9fefc826`), full BE regression (`5,338` Application + `1,312` Infrastructure + `13` Replay + `940` API non-SQL/Redis + `57` real SQL/Redis/AOF integration tests, 0 failures), and `dotnet format`.
- **Mobile (`FPTUCapstone/Capstone_Mobile`, branch `fix/tm89-uc43-engineering-completion`, PR [#92](https://github.com/FPTUCapstone/Capstone_Mobile/pull/92)):**
  - Reconciled `departures` and `selectedDeparture` participant counts inside `OperatorCheckInCubit` on successful check-in and progress refresh.
  - Added explicit `MSG127` (`503` → `system`) and HTTP `400` validation (`invalidInput`) mapping in `OperatorCheckInRepositoryImpl` with unit test coverage.
  - Distinguished `MobileScannerErrorCode.permissionDenied` guidance in `CheckInCameraScannerView` while keeping manual booking-code fallback accessible.
  - Added and executed real Flutter ↔ live `TripMate.Api` ↔ SQL Server 2022 ↔ Redis 8.2 AOF end-to-end integration test suite (`test/integration/tm89_uc43_live_backend_test.dart`, 2/2 suites PASS).
  - Verified `dart format` (909 files, 0 changed), `flutter analyze` (0 issues), and full `flutter test` (`2,800` PASS, 0 FAIL).

---

## 2. Explicitly Deferred Scope (GitHub Issue #162 & Production)

Do **NOT** claim Production readiness. The following remain explicitly deferred and untouched:

- Railway Pro upgrade & Redis backup architecture (Cloudflare R2 / Backblaze B2) — tracked in GitHub Issue #162.
- Production Redis AOF (`appendonly yes`, `appendfsync always`), `maxmemory-policy noeviction`, capacity sizing, and restart/restore drills.
- Production Azure SQL migration approval & execution (`20261105_add_checkin_audit_journal_identity.sql` via manual `DB migrate (Azure SQL)` workflow).
- Production activation (`CheckInAuditJournal__Enabled=true` in Railway).
- Audit retention/trimming policy, projection/availability SLA, official audit-record definition, and admission-only reconciliation policy.

---

## 3. Local Engineering Configuration (Isolated Development / QA)

To run Backend + Mobile locally for UC-43 verification:

1. Start isolated SQL Server 2022 and initialize with `database/apply-schema.sh` (applies `tripmate_schema_v7.sql` + all `database/migrations/*.sql`).
2. Start isolated Redis 8.2 with `tests/TripMate.Api.IntegrationTests/Fixtures/Redis/tm89-aof-always.conf` (`appendonly yes`, `appendfsync always`, `/data` volume).
3. Run `TripMate.Api` with:
   - `ConnectionStrings__Default=<isolated SQL Server connection>`
   - `ConnectionStrings__Redis=<isolated Redis 8.2 connection>`
   - `CheckInAuditJournal__Enabled=true`
   - `Jwt__SigningKey=<base64 dev key>`, `TicketQr__EncryptionKey=<base64 32-byte key>`, `TicketQr__SigningKey=<base64 32-byte key>`
4. Run Flutter live integration test against the local API:
   - Set `TM89_API_BASE_URL`, seeded credentials, schedule IDs, and QR/booking codes, then run:
     `flutter test test/integration/tm89_uc43_live_backend_test.dart --reporter expanded`

---

## 4. Next Required Actions (Before Merge & Before Production)

### Before Merging PRs into `develop`

1. Verify GitHub Actions CI passes on the exact final HEAD of both PRs (`Capstone_BE` and `Capstone_Mobile` PR #92).
2. Obtain required human code review approval on both PRs.
3. **Release safety gate for `Capstone_BE`:** Confirm Railway GitHub auto-deploy on `develop` is disabled or explicitly authorized under `docs/operations/tm89-be03-production-readiness.md` before clicking Merge (note: `CheckInAuditJournal:Enabled` remains `false` by default in `appsettings.json`, preserving fail-closed `MSG127` safety).
4. Merge both PRs into `develop` (NOT `main`) only after human approval and merge authorization.

### Before Any Future Production Activation (Separate Release Gate)

1. Complete physical Android/iOS device camera QA for Screen #88 (currently `NOT_VERIFIED`).
2. Obtain Owner/Product sign-off on SRS §3.8.4.4 vs §5.3 message conflicts (`MSG46`, `MSG74`, `MSG97`, `MSG99`) and BE-03 audit governance policies (retention, SLA, official audit record, admission-only reconciliation).
3. Complete GitHub Issue #162 (Redis durability, `noeviction`/capacity, backup & restore drill).
4. Complete DBA inventory, backup/PITR verification, `dry_run=true`, and `azure-db` reviewer approval for `20261105_add_checkin_audit_journal_identity.sql`.
5. Execute controlled Production activation (`CheckInAuditJournal__Enabled=true`) and non-destructive smoke verification per `docs/operations/tm89-be03-production-readiness.md`.
