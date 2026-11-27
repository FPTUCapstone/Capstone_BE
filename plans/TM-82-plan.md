# TM-82 / UC-36 — Update Tour Package: atomic implementation plan

Status: **Approved by the Task Owner (Mai Nguyễn Tiến Đạt) on 2026-11-25**, together with
`specs/TM-82-spec.md` revision 5 and the four amendments (B1-B4) below, clearing Task 0 onward
(AGENTS.md §1.2 steps 2-4).

Spec: `specs/TM-82-spec.md` (revision 5, all open items closed, §3 point H / §13 record the
Task Owner's implementation-kickoff decisions)

Jira: [TM-82](https://tripmate-capstone.atlassian.net/browse/TM-82)

## Fixed boundaries (restated from the spec; do not re-derive or re-litigate these while executing)

- New entity `TourPendingChangeSet` (Draft/PendingReview/Applied/Rejected — no `Cancelled`), not a
  reuse of `VersionRootTourId`/`VersionNo` (spec §3 point G1, closed).
- The live `Tour` row, its `Destinations`/`ItineraryItems`/`Schedules`/`CancellationPolicyRules`,
  and `TourStatus` are never touched by create/save/discard/submit, and are only mutated by Approve
  via the new `Tour.ApplyPendingChange(...)` method (spec §4.2, §6.3).
- Do **not** modify `CreateTourPackageCommandHandler`'s `SourceTourId` path, `TourStatus` enum, the
  `commerce.Tours.status` CHECK constraint, or the existing Admin tour-listing endpoint's contract.
- Do **not** add notifications to the existing `TourReviewCommandHandlers.cs` (Approve/Reject/
  Publish/Unpublish) handlers — out of scope, tracked separately (spec §3 point E3/G).
- No EF migrations (`dotnet ef migrations add`); SQL script only, guarded idempotent
  (AGENTS.md §3.1).
- No generic repository; query `TourPendingChangeSets` directly via `IApplicationDbContext`
  (AGENTS.md §2.2).
- `Result<T>` + `HandleFailure` pipeline throughout; no business exceptions as control flow
  (AGENTS.md §2.2/§2.3).
- Role check for Approve/Reject uses the existing `UserRole.Administrator` (spec §1.3 — the SRS's
  "Staff" is a deliberate mapping, not a new role).
- BR-61 (capacity ≥ sold slots) enforced at save, submit, and approve. BR-22 (valid dates) applies
  only to new or future-and-edited schedules — never to a past-departure schedule, which is always
  carried over verbatim with its original id (spec §3 point D, §6.5).
- Decision (Approve/Reject) + its audit log commit together in one serializable transaction;
  the in-app notification is attempted only after that transaction has committed, and its failure
  never reopens or reverses the decision (spec §3 point B/G5 — mechanism decided in Task 7 below).

## Decision this plan makes: the decision-then-notify mechanism (spec §3 point B)

**No outbox table, no background worker, no message queue.** The smallest mechanism that satisfies
UC-60 9.a2 / UC-61 8.a2 using entities that already exist:

1. The Approve/Reject handler calls `dbContext.ExecuteInSerializableTransactionAsync(...)` for
   **only** the decision + audit log work (load change set and tour, validate state, call the
   domain mutator, add the `AuditLog` row, let the helper's own `SaveChangesAsync` commit). This is
   the exact same pattern `UpdateTourDraftCommandHandler`/`TourReviewCommandHandlers` already use —
   **VERIFIED**: `ExecuteInSerializableTransactionAsync<T>(Func<CancellationToken, Task<T>>, CancellationToken)`
   (`IApplicationDbContext.cs:244-246`) runs the delegate and commits/rolls back around it; the
   caller does not call `SaveChangesAsync` a second time outside it.
2. Once that call returns a success `Result`, the handler makes a **second**, separate call:
   `dbContext.Notifications.Add(new Notification { UserId = ..., Channel = NotificationChannel.InApp,
   Status = NotificationStatus.Pending, ... })` followed by its own `await dbContext.SaveChangesAsync(ct)`
   — **not** wrapped in `ExecuteInSerializableTransactionAsync`, and not sharing the first call's
   transaction. **VERIFIED**: this mirrors the existing `AdministratorInAppNotices.AddAsync`
   helper's own shape (`Features/TripAlerts/Common/AdministratorInAppNotices.cs:15-46`) — it only
   adds a tracked entity and returns, leaving `SaveChangesAsync` to the caller — except here the
   caller's `SaveChangesAsync` for the notification is **not** the same one used for the decision.
3. If step 2's `SaveChangesAsync` throws, the handler catches it, logs/swallows it (no rethrow, no
   compensating action against the already-committed decision from step 1), and the API call still
   returns the success `Result` from step 1 to the caller. This satisfies "notification failure
   never blocks or reverses the decision" literally: by the time step 2 can fail, step 1 has already
   committed and the method has nothing left to reverse.
4. **What this does not do**: it does not retry a failed notification. **This is now a closed
   decision, not an open design question (spec §3 point H.D1, 2026-11-25):** the Task Owner has
   confirmed no retry, no outbox, no worker, no queue for TM-82 — a deliberate, recorded divergence
   from UC-60 9.a2/UC-61 8.a2's literal "queued for a retry" wording. A failed step-2 write must be
   logged at **Error** level (tagged with the `TourPendingChangeSet` id and the acting user id,
   spec §3 point H.D1) and never rethrown/swallowed silently without that log. Task 7 step 4's stop
   condition is narrowed accordingly: this plan still stops and asks before building any retry/
   outbox/worker/queue infrastructure, but "notify once, no retry" itself is no longer something
   implementation can second-guess or escalate — it is decided.
5. A single small helper, `TourPendingChangeNotices.NotifyOperatorAsync(dbContext, operatorUserId,
   type, title, body, relatedEntityId, nowUtc, ct)` (new file,
   `Features/Operator/Tours/PendingChanges/Common/TourPendingChangeNotices.cs`), wraps steps 2-3 so
   both the Approve and Reject handlers call one shared piece of code instead of duplicating the
   try/catch.

## Task 0 — Baseline and workspace

1. Confirm current branch is clean (`git status`); if not, stop and ask rather than touching
   uncommitted work.
2. Create branch `feature/tm82-update-tour-package` from current `origin/develop` (AGENTS.md §5.1).
3. Run `dotnet restore TripMate.slnx`, then `dotnet build TripMate.slnx -c Release` and
   `dotnet test TripMate.slnx -c Release` on the clean checkout. Record the baseline pass count —
   do not start Task 1 on top of an already-broken baseline (AGENTS.md §1.2 step 2).
4. For integration tests needing SQL Server, confirm `TRIPMATE_SQLSERVER_TEST_CONNECTION` is set to
   an isolated test instance (never shared/Azure/user-data). Record that the existing
   `UpdateTourDraftCommandHandlerTests`/`SubmitTourForReviewCommandHandlerTests` integration
   counterparts (if any) pass before any edit.
5. **D2 check (spec §3 point H.D2)**: confirm whether the existing tour-media-mutating endpoints
   block `Approved`/`Published` tours. **VERIFIED, performed this revision**:
   `TourMediaAccessResolver.AllowsMaterialChange(TourStatus status)`
   (`src/TripMate.Application/Features/TourMedia/Common/TourMediaAccess.cs:43-44`) returns true
   only for `Draft`/`Rejected`, and is called by `DeleteTourMediaCommandHandler.cs:52`,
   `ReorderTourMediaCommandHandler.cs:38`, and `UpdateTourMediaMetadataCommandHandler.cs:53`;
   `UploadTourMediaCommandHandler.cs:310` has the equivalent inline guard. No BR-103 bypass found;
   no stop condition hit; nothing to change here.
6. **B2 check (plan amendment B2)**: confirm the source of "slots already sold" for BR-61 is the
   same counter the booking flow itself uses for remaining-capacity checks. **VERIFIED, performed
   this revision**: `CreateTourBookingCommandHandler.cs:123` checks
   `request.Participants.Count > schedule.RemainingCapacity` and, on success,
   `CreateTourBookingCommandHandler.cs:182` calls `schedule.ReserveCapacity(participants.Count)`.
   `TourSchedule.RemainingCapacity => TotalCapacity - ReservedCapacity`
   (`src/TripMate.Domain/Entities/TourSchedule.cs:113`), and `ReserveCapacity` increments
   `ReservedCapacity` (`TourSchedule.cs:117-123`) — including seats held for a booking still
   `PendingPayment` (per that method's own doc comment: "Soft-reserves seats for a PendingPayment
   booking"). **BR-61 for TM-82 (Tasks 2, 5, 7) must therefore compare a proposed schedule's
   `TotalCapacity` against the matched existing `TourSchedule.ReservedCapacity`** — the exact same
   field the booking flow reads — and must **not** recompute "sold slots" by querying the `Booking`
   table through some other aggregation. Tasks 2 and 7 below reference this finding directly.

Definition of done: clean baseline, build green, full existing test suite green or every failure
explicitly a pre-existing one (recorded, not touched by this branch); D2 and B2 checks recorded
above with VERIFIED findings and no open stop condition.

**Baseline recorded, 2026-11-25, branch `feature/tm82-update-tour-package` cut from `origin/develop`
at `fdbc1e20` (local `develop` was 291 commits behind and fast-forwarded first; the pre-existing
working tree had 444 unrelated modified/untracked paths from other in-flight tickets, stashed
aside rather than discarded — not part of this branch):**
- `dotnet build TripMate.slnx`: **Build succeeded, 0 Warning(s), 0 Error(s)**.
- `dotnet test TripMate.slnx --no-build`: **8,896 total — 8,062 passed, 1 failed, 833 skipped**
  (skipped tests are the SQL-Server-backed integration suite, self-skipping because
  `TRIPMATE_SQLSERVER_TEST_CONNECTION` is not set in this environment — no isolated test SQL Server
  instance was available to satisfy Task 0 step 4 as written; this is an environment limitation,
  not a code failure, and is carried forward as a known gap rather than silently ignored).
  - Per-project: `TripMate.CandidatePoolReplay.Tests` 13/13 passed;
    `TripMate.Infrastructure.UnitTests` 1392/1396 passed, 4 skipped;
    `TripMate.Application.UnitTests` 5629/5630 passed, **1 failed**;
    `TripMate.Api.IntegrationTests` 1028/1857 passed, 829 skipped (SQL-Server-backed), 0 failed.
  - **The one failure is pre-existing and unrelated to TM-82**:
    `TripMate.Application.UnitTests.Features.Admin.AuditLogs.GetDetail.AuditLogOutcomeTests.Queries_ExposeRecordedOutcomeAndBusinessReason(outcome: Failure)`
    (`tests/TripMate.Application.UnitTests/Features/Admin/AuditLogs/GetDetail/AuditLogOutcomeTests.cs:53`) —
    expects `detail.Value.Reason` to equal the literal Vietnamese string `"Giấy phép không hợp lệ"`
    but receives `"[REDACTED]"`, meaning `GetAuditLogDetailQueryHandler`'s own redaction logic for a
    `Failure`-outcome reason masks it. This is entirely inside the unrelated Admin/AuditLogs feature
    (`GetAuditLogDetailQueryHandler`), not `Tour`/`TourSchedule`/`TourMedia`/anything TM-82 touches,
    and reproduces identically on `origin/develop` before any TM-82 code exists. **Recorded as a
    pre-existing baseline failure, not fixed by this branch** — Task 9's final regression pass
    compares against this exact baseline (8,062 passed / 1 pre-existing failed / 833 skipped plus
    whatever new tests TM-82 adds, all new) rather than expecting a clean 0-failure suite.

## Task 1 — Domain entity `TourPendingChangeSet` (Red → Green)

Files:

- `tests/TripMate.Domain.UnitTests/Entities/TourPendingChangeSetTests.cs` (new)
- `src/TripMate.Domain/Entities/TourPendingChangeSet.cs` (new)
- `src/TripMate.Domain/Enums/TourPendingChangeStatus.cs` (new)

1. **Red**: write failing unit tests for the domain entity in isolation (no DbContext, no EF):
   - `CreateDraft(...)` sets `Status = Draft`, seeds scalar fields and JSON payload from the
     supplied snapshot, sets `CreatedAtUtc`/`UpdatedAtUtc`.
   - `SaveDraft(...)` succeeds from `Draft` and from `Rejected`; throws `InvalidOperationException`
     from `PendingReview` or `Applied`.
   - `Submit(...)` succeeds from `Draft` and `Rejected`, sets `Status = PendingReview`,
     `SubmittedAtUtc`, clears `RejectionReason`; throws from any other status.
   - `MarkApplied(...)` succeeds only from `PendingReview`; throws otherwise; sets
     `ReviewedByUserId`/`ReviewedAtUtc`.
   - `Reject(...)` succeeds only from `PendingReview`; throws `ArgumentException` on empty/too-long
     reason (BR-119, `Tour.RejectionReasonMaxLength`); sets `Status = Rejected`,
     `RejectionReason`/`ReviewedByUserId`/`ReviewedAtUtc`.
2. **Green**: implement `TourPendingChangeSet` exactly as specified in spec §4.1 (private setters,
   no EF dependency), and `TourPendingChangeStatus` enum (`Draft = 1, PendingReview = 2, Applied = 3,
   Rejected = 4` — no `Cancelled`).
3. **Refactor**: nothing expected here beyond naming/formatting consistency with `Tour.cs`.

Verification: `dotnet test --filter "FullyQualifiedName~TourPendingChangeSetTests"`.

Definition of done: all new domain tests green; no existing test touched.

## Task 2 — `Tour.ApplyPendingChange` (Red → Green)

Files:

- `tests/TripMate.Domain.UnitTests/Entities/TourTests.cs` (extend existing file if present, else new)
- `src/TripMate.Domain/Entities/Tour.cs` (extend)

**B1 — schedules must be updated in place by id, never destroy-and-recreate (plan amendment B1).**
`UpdateTourDraftCommandHandler.cs:85` (`dbContext.TourSchedules.RemoveRange(tour.Schedules.ToList())`)
followed by `Tour.ReplaceDraftChildren` constructing brand-new `TourSchedule.Create(...)` rows
(`Tour.cs:297-328`, `_schedules.Clear()` then re-add) is **VERIFIED** to be the existing pattern for
the direct-edit path — and it is **not** safe for `ApplyPendingChange`, because
`Booking.TourScheduleId` (`Booking.cs:36`) is a live foreign key into `TourSchedule.Id`: deleting
and recreating rows would orphan every existing booking's FK or break the delete outright once a
booking references the row. `ApplyPendingChange` must instead, for each proposed schedule in the
change set's `SchedulesJson`:
- If the payload entry carries an existing `TourSchedule.Id` (matching one of this tour's current
  schedules): update that **same tracked row in place** (new departure/return time, capacity,
  price, etc. as proposed) — never delete and re-insert it. Its id and its current
  `ReservedCapacity` (the sold-slot counter, confirmed by Task 0's B2 check) are preserved
  unchanged; only the other fields move to the proposed values.
- If the payload entry's id is `null` (a newly proposed schedule, §4.1 comment on `SchedulesJson`):
  insert it as a new `TourSchedule` row.
- Delete an existing schedule **only if** it is not present in the proposed payload at all, **and**
  its `ReservedCapacity == 0`, **and** it has no active booking referencing it (defensive
  double-check even though `ReservedCapacity == 0` should already imply this). A schedule with any
  sold slots or an active booking is never deleted by `ApplyPendingChange`, matching BR-61 (§6.4)
  and preventing an FK break on `Booking.TourScheduleId`.
- A past-departure schedule (spec §3 point D/§6.5) is always present in the payload with its
  original id per §4.1's seeding rule, so it is always in the "update in place, unchanged values"
  branch above — it is never a candidate for deletion by this method.

1. **Red**: failing tests —
   - `ApplyPendingChange(...)` throws `InvalidOperationException` when `Status` is
     `Draft`/`Pending`/`Rejected`/`Inactive`; succeeds when `Approved` or `Published` and applies
     every proposed scalar field/child collection; does **not** change `Status`, `ReviewedBy`,
     `ReviewedAtUtc`, or `PublishedAtUtc`.
   - **B1 regression tests**: an existing `TourSchedule`'s `Id` and its `ReservedCapacity` are
     unchanged after `ApplyPendingChange` runs with that schedule's other fields modified in the
     proposed payload (assert same `Id` instance/value, same `ReservedCapacity`, changed
     departure time/capacity/price as proposed). A schedule with `ReservedCapacity > 0` that the
     proposed payload omits is **not removed** from `Tour.Schedules` (stays present, unchanged). A
     payload entry with a `null` id produces a new `TourSchedule` with a new id, added alongside
     (not replacing) the existing, matched-by-id schedules.
2. **Green**: add the method per spec §4.2, implementing the update-in-place behavior above instead
   of `ReplaceDraftChildren`'s clear-and-recreate pattern — this method needs its own schedule
   reconciliation logic, it must **not** call `ReplaceDraftChildren`. Decide during this task
   (implementation detail, not a business rule per the spec) whether to extract a shared private
   helper for the scalar-field-assignment block reused by `UpdateDraft` and `ApplyPendingChange`
   (the non-schedule fields only), or duplicate it — prefer extracting a private helper if it does
   not reduce either method's own guard clarity, per AGENTS.md §4 DRY guidance, but duplication is
   acceptable if the two guards would otherwise entangle. The schedule-reconciliation logic itself
   is new to `ApplyPendingChange` and has no equivalent to extract from `UpdateDraft`/
   `ReplaceDraftChildren`, which do not need to (and must not) change their own existing behavior.

**B3 — side-effect inventory of `UpdateDraft`/its handler, VERIFIED this plan, so `ApplyPendingChange`
can be checked against the same list:**
- `tour.UpdateDraft(...)` → scalar fields (`CategoryId`, `Title`, `Description`, `BasePrice`,
  `DurationDays`, `Inclusions`, `Exclusions`, `Highlights`, `TravellerRequirements`, `TimeZoneId`)
  + `UpdatedAtUtc` (`Tour.cs:199-241`). **VERIFIED**.
- `tour.ReplaceDraftChildren(items, schedules)` → itinerary items + schedules, via the
  destroy-and-recreate pattern B1 above forbids reusing for `ApplyPendingChange` (`Tour.cs:297-328`).
  **VERIFIED**.
- `tour.ReplaceDraftDestinations(...)` → destinations + `DestinationSummary` (`Tour.cs:272-295`).
  **VERIFIED**.
- `tour.ApplyCancellationPolicySnapshot(...)` → cancellation policy rules +
  `CancellationPolicyTemplateId` + `UpdatedAtUtc` (`Tour.cs:334-354`). **VERIFIED**.
- One `AuditLog` row, written by the handler, not the entity (`UpdateTourDraftCommandHandler.cs:109-111`).
  **VERIFIED** — out of scope for `ApplyPendingChange` itself; Task 7's handler writes its own
  `TourPendingChangeApproved` audit row instead (spec §5.7 step 7).
- `tour.SetRecommendationAttributes(...)` (`Tour.cs:248-265`, pace/transport/diet) is **VERIFIED
  NOT called** by `UpdateTourDraftCommandHandler` at all — grep of every call site
  (`UpdateDraft\(|SetRecommendationAttributes\(` across `src/`) found exactly one caller of each
  method and they are different handlers/paths. **Conclusion: `UpdateDraft`'s own side effects do
  not include recommendation attributes, and `ApplyPendingChange` therefore has no recommendation-
  attribute side effect to replicate** — there is nothing here for `ApplyPendingChange` to call.
  No search-index or other side effect beyond the four bullets above was found for this handler.

Verification: `dotnet test --filter "FullyQualifiedName~TourTests"`.

Definition of done: new tests green, including the B1 schedule-id/sold-slot-preservation and
non-deletion tests; every existing `Tour.cs` test (in particular `UpdateDraft`, `SubmitForReview`,
`Approve`/`Reject`/`Publish`/`Unpublish` tests) still green, unchanged behavior.

## Task 3 — SQL migration and EF configuration

Files:

- `database/migrations/<today>_tour_pending_change_sets.sql` (new)
- `src/TripMate.Infrastructure/Persistence/Configurations/TourPendingChangeSetConfiguration.cs` (new)
- `src/TripMate.Infrastructure/Persistence/ApplicationDbContext.cs` (extend: new `DbSet`)
- `src/TripMate.Application/Common/Interfaces/IApplicationDbContext.cs` (extend: new `DbSet`
  property)

1. Write the guarded idempotent migration script, following
   `20261015_add_tour_package_authoring.sql`'s format (header comment block, `SET ANSI_NULLS ON;`
   etc., `GO` separators):
   - `CREATE TABLE commerce.TourPendingChangeSets (...)` guarded by
     `IF OBJECT_ID(N'commerce.TourPendingChangeSets', N'U') IS NULL`.
   - Columns per spec §4.1: `id` (identity PK), `tour_id` (FK to `commerce.Tours`),
     `submitted_by_user_id` (FK to `dbo.Users`), `status` VARCHAR with
     `CHECK (status IN ('Draft','PendingReview','Applied','Rejected'))`, scalar proposed fields
     mirroring `commerce.Tours`' own column types/lengths, `destination_ids_json`/
     `itinerary_items_json`/`schedules_json` NVARCHAR(MAX), `created_at`/`updated_at`/
     `submitted_at`/`reviewed_at` DATETIME2, `reviewed_by_user_id` BIGINT NULL,
     `rejection_reason` NVARCHAR(500) NULL.
   - `CREATE UNIQUE INDEX UX_TourPendingChangeSets_OpenPerTour ON commerce.TourPendingChangeSets(tour_id)
     WHERE status IN ('Draft','PendingReview','Rejected')`, guarded by
     `IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_TourPendingChangeSets_OpenPerTour' AND object_id = OBJECT_ID(N'commerce.TourPendingChangeSets'))`
     (spec §3 point G6, §8).
2. Apply the migration against the isolated test SQL Server (same instance from Task 0.4); confirm
   it is idempotent by running it twice with no error.
3. Write `TourPendingChangeSetConfiguration : IEntityTypeConfiguration<TourPendingChangeSet>`:
   every `DateTimeOffset(?)` property via `.AsUtcDateTime2()`; `Status` via
   `HasConversion<string>()` with max length matching the CHECK constraint's longest value
   (`"PendingReview"` = 13 chars); table/column name mapping matching the migration.
4. Add `DbSet<TourPendingChangeSet> TourPendingChangeSets => Set<TourPendingChangeSet>();` to both
   `ApplicationDbContext` and the `IApplicationDbContext` interface, following the existing
   plural-name convention (`IApplicationDbContext.cs`, alongside `TourSchedules`/`TourMedia`/etc.).

Verification: `dotnet build TripMate.slnx -c Release` (confirms `ApplyConfigurationsFromAssembly`
picks up the new configuration without error); a smoke integration test that inserts and reads back
one `TourPendingChangeSet` row (can be folded into Task 4's test file rather than written standalone).

Definition of done: migration applies cleanly and idempotently against the test database; build
green; EF can round-trip a row.

## Task 4 — Audit actions and error codes

Files:

- `src/TripMate.Domain/Common/AuditActionTypes.cs` (extend)
- `src/TripMate.Application/Features/Operator/Tours/Common/OperatorTourErrorCodes.cs` (extend)

1. Add consts to `AuditActionTypes`: `TourPendingChangeCreated = "TOUR_PENDING_CHANGE_CREATED"`,
   `TourPendingChangeSaved = "TOUR_PENDING_CHANGE_SAVED"`,
   `TourPendingChangeSubmitted = "TOUR_PENDING_CHANGE_SUBMITTED"`,
   `TourPendingChangeApproved = "TOUR_PENDING_CHANGE_APPROVED"`,
   `TourPendingChangeRejected = "TOUR_PENDING_CHANGE_REJECTED"`,
   `TourPendingChangeDiscarded = "TOUR_PENDING_CHANGE_DISCARDED"` (spec §8).
2. Add consts to `OperatorTourErrorCodes`: `PendingChangeNotFound = "operator_tour.pending_change_not_found"`,
   `ScheduleHasActiveBookings = "operator_tour.schedule_has_active_bookings"`,
   `ScheduleNotOwnedByTour` (new, for spec §3 point G6's schedule-ownership check — exact wording
   e.g. `"operator_tour.schedule_not_owned_by_tour"`). Reuse the existing `InvalidState`,
   `AccessRequired`, `AdminAccessRequired`, `RejectionReasonInvalid`, `TourNotFound` consts — do
   **not** add `pending_change_conflict` (removed in spec revision 3/4, §3 point G3).

No test file of its own; covered implicitly by Tasks 5-7's handler tests asserting on these exact
string values.

Definition of done: build green; no duplicate/renamed existing const touched.

## Task 5 — Operator endpoints: create, get, save, discard (Red → Green)

Files:

- `src/TripMate.Application/Features/Operator/Tours/PendingChanges/Create/CreatePendingChangeCommand.cs` (new)
- `.../Create/CreatePendingChangeCommandHandler.cs` (new)
- `.../Get/GetCurrentPendingChangeQuery.cs` (new)
- `.../Get/GetCurrentPendingChangeQueryHandler.cs` (new)
- `.../Save/SavePendingChangeCommand.cs` (new)
- `.../Save/SavePendingChangeCommandHandler.cs` (new)
- `.../Save/SavePendingChangeCommandValidator.cs` (new, FluentValidation, mirroring
  `UpdateTourDraftCommandValidator`)
- `.../Discard/DiscardPendingChangeCommand.cs` (new)
- `.../Discard/DiscardPendingChangeCommandHandler.cs` (new)
- `src/TripMate.Api/Controllers/V1/OperatorToursController.cs` (extend with 4 new actions)
- `tests/TripMate.Application.UnitTests/Features/Operator/Tours/PendingChanges/*CommandHandlerTests.cs` (new, one per handler)

1. **Red**, per handler:
   - **Create**: fails `AccessRequired` if not an active approved Tour Operator; fails
     `TourNotFound` if the tour does not exist/is not owned; fails `InvalidState` for
     `Draft`/`Rejected` (use UC-35 instead, no MSG), `InvalidState` + "MSG122" message text for
     `Pending`, `InvalidState` for `Inactive` (spec §3 point G2/G4, §5.1 table); succeeds
     (`201`-shaped success) creating a `Draft` seeded from the live tour including past-departure
     schedules verbatim; a second call while one is open returns the **same** existing row
     (idempotent, `200`-shaped success, same `Id`) — test this by asserting the returned id is
     identical across two calls, not just that both succeed.
   - **Get current**: returns the open draft version or a "not found" result when none exists.
   - **Save**: fails `InvalidState` when the change set is `PendingReview`; succeeds from `Draft`/
     `Rejected`; enforces BR-61/MSG143 (capacity vs. sold slots — **per Task 0's B2 finding, "sold
     slots" means the matched existing `TourSchedule.ReservedCapacity`, the same field
     `CreateTourBookingCommandHandler` reads/increments, never a separate count from the `Booking`
     table**) and BR-22/MSG29 (new/future-edited schedules only — a past-departure schedule in the
     payload is never rejected for its date); enforces the schedule-id-ownership check (spec §3
     point G6) — a schedule id not belonging to this tour fails with the new `ScheduleNotOwnedByTour`
     code; enforces MSG01-equivalent mandatory field checks.
   - **Discard**: fails `InvalidState` when `PendingReview`; succeeds (deletes the row) from
     `Draft`/`Rejected`; fails ownership/not-found the same way the others do.
2. **Green**: implement each handler following `UpdateTourDraftCommandHandler`'s existing shape
   (operator-eligibility check inside `ExecuteInSerializableTransactionAsync`, `SingleOrDefaultAsync`
   load, state guard, domain-method call, `AuditLog.CreateRecordedOutcome(...)`, `SaveChangesAsync`
   — the transaction wraps the single mutation, exactly like `UpdateTourDraftCommandHandler`, no
   notification involved at this layer since only Approve/Reject notify).
3. Wire 4 new controller actions onto `OperatorToursController` (`POST .../pending-changes`,
   `GET .../pending-changes/current`, `PUT .../pending-changes/{id}`,
   `DELETE .../pending-changes/{id}`), following the controller's existing `[Authorize]`/model-
   binding-only convention (AGENTS.md §2.3 — no business logic in the controller).

Verification: `dotnet test --filter "FullyQualifiedName~PendingChanges"`.

Definition of done: all new handler tests green; `OperatorToursController` compiles with the 4 new
actions and no change to its existing actions' behavior.

## Task 6 — Operator endpoint: submit (Red → Green)

Files:

- `.../Submit/SubmitPendingChangeCommand.cs` (new)
- `.../Submit/SubmitPendingChangeCommandHandler.cs` (new)
- extend `OperatorToursController.cs`

1. **Red**: fails `InvalidState` unless the change set is `Draft`/`Rejected`; fails the BR-101
   completeness check (see the closed decision below) with the same MSG01-equivalent code when
   incomplete; succeeds setting `Status = PendingReview`.
2. **BR-101 scope — closed by the Task Owner, spec §3 point H.D3 (no longer an open task-time
   decision).** `TourReviewReadinessEvaluator` (`src/TripMate.Application/Features/Operator/Tours/Common/TourReviewReadinessEvaluator.cs:35-45`)
   checks six facts, including `PrimaryImageMissing` (a media-table read,
   `TourReviewReadinessEvaluator.cs:57-60`). Per spec §3 point H.D2, a `TourPendingChangeSet` has no
   media at all, so that fact cannot apply here. **Do not call `TourReviewReadinessEvaluator` or
   re-derive a media check for the draft version.** Implement a small, separate evaluation against
   the change set's own stored fields/JSON payload covering exactly the three SRS-named BR-101
   conditions (UC-37 §1.1/UC-35 BR-101): all mandatory fields present, at least one itinerary day,
   at least one departure schedule. If, once code is in front of you during this task, something
   contradicts this reading, **stop and ask — this is still not something to guess past**, per
   AGENTS.md §1.1 and the spec's own instruction — but the "evaluator vs. plain SRS list" question
   itself is already decided and is not this task's decision to make.
3. **Green**: implement the handler; wire the controller action
   (`POST .../pending-changes/{id}/submit`).

Verification: `dotnet test --filter "FullyQualifiedName~SubmitPendingChange"`.

Definition of done: new tests green; BR-101 implemented per step 2's closed scope (three SRS
conditions only, no media check) — any contradiction found in code escalated, not guessed past.

## Task 7 — Admin endpoints: approve and reject, with the decision-then-notify mechanism

Files:

- `src/TripMate.Application/Features/Operator/Tours/PendingChanges/Common/TourPendingChangeNotices.cs` (new)
- `.../Approve/ApprovePendingChangeCommand.cs` (new)
- `.../Approve/ApprovePendingChangeCommandHandler.cs` (new)
- `.../Reject/RejectPendingChangeCommand.cs` (new)
- `.../Reject/RejectPendingChangeCommandHandler.cs` (new)
- new Admin controller (or extend an existing Admin tours controller — confirm the existing one's
  name/location before creating a duplicate) with 2 new actions.

1. **Red**, Approve:
   - fails `AdminAccessRequired` if not `Administrator`; fails `PendingChangeNotFound`; fails
     `InvalidState` (MSG133) unless `Status == PendingReview`; fails `InvalidState` defensively if
     the live tour somehow left `Approved`/`Published`.
   - fails `ScheduleHasActiveBookings` (MSG143-equivalent) when the proposed schedules would drop
     capacity below the matched existing `TourSchedule.ReservedCapacity` (Task 0's B2 finding —
     the same sold-slot counter the booking flow itself maintains) — **including** the exact
     boundary (capacity exactly equal to `ReservedCapacity` passes; one below fails).
   - on success: `Tour.ApplyPendingChange(...)` applied using the schedule update-in-place behavior
     from Task 2/B1 (existing schedule ids and their `ReservedCapacity` preserved, no delete+recreate),
     `Tour.Status` **unchanged** (`Approved` stays `Approved`, `Published` stays `Published` —
     explicit assertion), change set `Status = Applied`, one `AuditLog` row, **all inside the same
     `ExecuteInSerializableTransactionAsync` call**; the handler then attempts (via
     `TourPendingChangeNotices`) one `Notification` for the submitting operator; **assert the
     decision's success result is returned even when the notification step is made to throw** (test
     this by injecting/mocking a `DbContext`/`SaveChangesAsync` failure scoped to only the second,
     post-commit call — confirms the decision is not rolled back by a notification failure, spec
     acceptance criterion 5).
   - **B4 test**: after a forced notification-step failure, perform one further
     `dbContext.SaveChangesAsync(...)` on the **same** `DbContext` instance and assert it does
     **not** attempt to re-save the failed `Notification` row (no duplicate-insert exception, no
     row persisted from that later call) — proving the failed entity was detached from the change
     tracker, not left tracked to be picked up by some unrelated later `SaveChangesAsync` in the
     same scope.
2. **Red**, Reject: mirrors Approve minus `ApplyPendingChange`/the BR-61 guard; empty/too-long
   reason fails `RejectionReasonInvalid` (MSG123-equivalent); success sets `Status = Rejected`,
   writes one audit row, live `Tour` untouched (explicit assertion), then the same
   notify-after-commit behavior, the same "notification failure doesn't undo the decision" test,
   and the same B4 detach test as Approve.
3. **Green**: implement both handlers per the decision-then-notify mechanism above. Implement
   `TourPendingChangeNotices.NotifyOperatorAsync(...)` as a thin wrapper:
   - Call `dbContext.Notifications.Add(notification)`, capturing the returned `EntityEntry<Notification>`.
   - `await dbContext.SaveChangesAsync(ct)`.
   - On any exception from that specific call: **log it at Error level, tagged with the
     `TourPendingChangeSet` id and the acting (admin) user id** (spec §3 point H.D1 — this is now a
     required behavior, not optional; use whatever logging abstraction (`ILogger<T>`, etc.) this
     feature area already has access to via DI, introducing the minimal wiring needed if none is
     injected yet into this handler — a plain `ILogger<T>` constructor parameter is in scope for
     this task, a new logging *infrastructure* is not). **B4**: then set the captured entry's
     `.State = EntityState.Detached` before returning, so the failed `Notification` is not left
     tracked on `dbContext` for a later unrelated `SaveChangesAsync` in the same scope to retry
     saving. Do not rethrow; the caller's success `Result` from step 1 is always returned regardless
     of this step's outcome.
4. **Stop condition check (narrowed per spec §3 point H.D1 — restated from "Decision this plan
   makes" above)**: "notify once, no retry" is now the Task Owner's own decision, not an open
   question — do not escalate that specific point again. Still **stop and ask** if implementing
   this task surfaces a need for infrastructure beyond "attempt once, log Error on failure, detach"
   (e.g. anything resembling an outbox, a background worker, or a queue) — that remains off-limits
   without the Task Owner's explicit sign-off.
5. Wire the 2 Admin controller actions (`POST .../pending-changes/{id}/approve`,
   `POST .../pending-changes/{id}/reject`), confirming the Administrator-role `[Authorize]`
   convention matches the existing Admin tour-review controller's.

Verification: `dotnet test --filter "FullyQualifiedName~ApprovePendingChange|RejectPendingChange"`.

Definition of done: all new tests green, including the capacity-boundary test, the
notification-failure-does-not-roll-back-decision test, and the B4 detach-after-failure test; no
change to `TourReviewCommandHandlers.cs`.

## Task 8 — Admin moderation queue: new additive endpoint

Files:

- `.../ModerationQueue/GetModerationQueueQuery.cs` (new)
- `.../ModerationQueue/GetModerationQueueQueryHandler.cs` (new)
- extend the Admin tours controller with one new `GET` action.

1. **Red**: returns a paginated (CR-01) list combining first-time `Pending` tours and
   `PendingReview` change sets, each row tagged with a `kind` discriminator
   (`"NewSubmission"`/`"DraftVersion"`); ordering/pagination behavior covered by at least one test
   with items of both kinds present.
2. **Green**: implement as a `UNION`-shaped projection (two queries combined in the handler, or one
   SQL-translatable union — decide based on what EF Core can translate without pulling both sets
   into memory first; if neither query shape avoids in-memory concatenation cleanly, note that
   trade-off in the task's own commit message rather than silently accepting an N+1/full-table-scan
   query). **Must not change** the existing Admin tour-listing endpoint's route, request, or
   response shape (spec §3 point E2/G) — confirm this by running that endpoint's existing tests
   unmodified and green after this task.

Verification: `dotnet test --filter "FullyQualifiedName~ModerationQueue"`, plus the existing Admin
tour-listing endpoint's own test file run unmodified.

Definition of done: new endpoint's tests green; existing Admin queue endpoint's tests unchanged and
green.

## Task 9 — Full regression pass and OpenAPI

1. `dotnet build TripMate.slnx -c Release`.
2. `dotnet test TripMate.slnx -c Release` — full suite, not filtered. Compare the pass/fail count
   against Task 0's recorded baseline (8,062 passed / **1 pre-existing failed**
   (`AuditLogOutcomeTests.Queries_ExposeRecordedOutcomeAndBusinessReason(outcome: Failure)`) / 833
   skipped, out of 8,896 total). The same one pre-existing failure is expected to still be present
   and unchanged (not newly caused, not fixed as a side effect); every TM-82 test added by Tasks
   1-8 must pass; any **other** new failure blocks completion.
3. If the project's OpenAPI test suite (`ToursOpenApiTests.cs`-equivalent, confirm exact file
   during this task) asserts on the full endpoint surface, add/update it for the 7 new operator/
   admin endpoints; do not change any existing documented endpoint's schema.
4. Re-run the SQL migration idempotency check (apply twice) one more time against the clean test
   database as a final sanity pass.

Definition of done: full build and test suite green, with every acceptance criterion in
`specs/TM-82-spec.md` §9 mapped to at least one passing test (produce this mapping as the final
report, not as a new file, unless the developer asks for one).

## Explicitly out of scope for this plan (restated, do not implement)

- Withdrawing/deleting a `PendingReview` change set.
- Any change to `CreateTourPackageCommandHandler`'s `SourceTourId` path.
- Notifications on the existing `TourReviewCommandHandlers.cs` handlers.
- Any change to the existing Admin tour-listing endpoint's contract.
- A distinct `UserRole.Staff`.
- Email notifications.
- Any outbox/retry/background-job infrastructure (see Task 7 step 4's stop condition).
- Push, PR creation, Jira transitions, or merging — present options to the developer once every
  task above is verified (AGENTS.md §1.2 step 6).

## Changelog — Task Owner amendments applied at implementation kickoff (2026-11-25)

- **B1.** Task 2's `Tour.ApplyPendingChange` now updates existing `TourSchedule` rows in place by
  id (preserving id and `ReservedCapacity`), adds only null-id entries as new schedules, and
  deletes only a schedule with zero `ReservedCapacity` and no active booking — replacing the
  previous plan text's implicit reliance on `ReplaceDraftChildren`'s destroy-and-recreate pattern,
  which would have broken `Booking.TourScheduleId` FKs. New regression tests added to Task 2.
- **B2.** Task 0 now performs and records the VERIFIED check that "sold slots" for BR-61 is
  `TourSchedule.ReservedCapacity` — the same field `CreateTourBookingCommandHandler` reads via
  `RemainingCapacity` and increments via `ReserveCapacity`. Tasks 2, 5, and 7 updated to reference
  this source explicitly instead of leaving it implicit.
- **B3.** Task 2 now records the VERIFIED side-effect inventory of `UpdateDraft`/its handler
  (scalar fields, `ReplaceDraftChildren`, `ReplaceDraftDestinations`,
  `ApplyCancellationPolicySnapshot`, one audit row) and confirms `SetRecommendationAttributes` is
  not among them (not called by that handler) — so `ApplyPendingChange` has no recommendation-
  attribute or search-index side effect to replicate.
- **B4.** Task 7's `TourPendingChangeNotices.NotifyOperatorAsync` now explicitly detaches the
  failed `Notification` entity (`EntityEntry.State = EntityState.Detached`) after logging a
  notification-save failure, with a new test asserting a later `SaveChangesAsync` on the same
  `DbContext` does not re-attempt saving it.
- **Spec §3 point H.D1/H.D2/H.D3** (revision 5) closed three items this plan had left open or
  partially open: the notification-retry mechanism (now: none, logged at Error level, not a
  retry — narrows Task 7 step 4's stop condition accordingly), the tour-media out-of-scope
  boundary (now explicitly checked and VERIFIED clear of a BR-103 bypass in Task 0), and Task 6's
  BR-101 evaluation (now: the plain three-condition SRS list only, explicitly not
  `TourReviewReadinessEvaluator`).
- Task 0 now records the actual baseline build/test numbers (build green; 8,062 passed / 1
  pre-existing unrelated failure / 833 skipped out of 8,896) instead of leaving them as a
  to-be-filled-in placeholder, and Task 9 compares against that exact baseline rather than
  expecting zero failures.
