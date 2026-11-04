# UC-70 Backend implementation plan

Canonical cross-module plan: `../../plan.md`.

- [x] Preserve weather readiness but make all qualifying alerts `ReviewNeeded`.
- [x] Add `EmergencyStopped` and `Interrupted` domain states and guarded transitions.
- [x] Add owner-only impact preview with deterministic stale-preview token.
- [x] Add idempotent `CancelBeforeDeparture` command.
- [x] Add idempotent `EmergencyStopActive` command and interrupt linked commercial sessions.
- [x] Revoke unused tickets/QR while preserving used history.
- [x] Keep refunds, payouts and commission out of the decision (UC-42 and UC-71 own them).
- [x] Persist audit and pending notifications in the same serializable transaction.
- [x] Require an active, approved owning Operator for preview and confirmation.
- [x] Keep lock-screen push generic and expose authenticated Traveler emergency details by booking.
- [x] Bound copied reason text to each dependent entity while retaining full operation evidence.
- [x] Add idempotent SQL migration and EF mappings.
- [x] Add application/domain/persistence tests and build verification.
- [x] Execute the SQL migration against a disposable SQL Server test database.
- [x] Add SQL Server endpoint/concurrency integration tests when the test database is available.
- [ ] Agree the UC-42 handoff contract for bookings cancelled by UC-70 (owned by UC-42, not implemented here).
- [x] Implement current-password step-up for active emergency stop without persisting, logging or
  hashing the supplied password into idempotency data.
- [x] Add `Itinerary.SourceBookingId`, derive the exact departure through Booking, and remove the
  compatibility matcher based on Traveler + Tour + time overlap. Unlinked legacy sessions return
  `trip_session.link_unverified`.
- [x] Add Operator-provided evidence URL/reference, provenance, safe URL validation and an add-later
  audit path. Do not fetch arbitrary URLs or add an Admin approval/registry dependency in the MVP.
- [ ] Implement FE Operator confirmation and Mobile Traveler safety-first presentation.

No commit, push, migration execution or deployment without a separate request.
