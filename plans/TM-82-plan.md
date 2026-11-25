# TM-82 / UC-36 — Update Tour Package: atomic implementation plan

Status: **Awaiting developer approval of this plan** (AGENTS.md §1.2 step 3). Spec approved by the
Task Owner (Mai Nguyễn Tiến Đạt) on 2026-11-25 — see `specs/TM-82-spec.md` revision 4, §3 point G.

Spec: `specs/TM-82-spec.md` (revision 4, all open items closed)

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
4. **What this does not do**: it does not retry a failed notification. The spec's point B
   explicitly separates "the shape" (locked: decision commits first, notify second, independently)
   from "the retry mechanics" (left open, with an explicit instruction to stop and ask before
   adding infrastructure). A failed step-2 write today simply does not happen again — there is no
   queue to re-drive it. **This plan does not add one.** If, during implementation, this is found
   to be unacceptable (e.g. the Task Owner wants an actual retry, not just "failure is silent and
   decision stands"), this plan stops and asks before building anything like an outbox, per the
   Task Owner's explicit instruction — this is flagged again in Task 7 below as the one place this
   plan could hit that stop condition.
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

Definition of done: clean baseline, build green, full existing test suite green or every failure
explicitly a pre-existing one (recorded, not touched by this branch).

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

1. **Red**: failing tests — `ApplyPendingChange(...)` throws `InvalidOperationException` when
   `Status` is `Draft`/`Pending`/`Rejected`/`Inactive`; succeeds when `Approved` or `Published` and
   replaces every field/child collection exactly as the proposed values specify; does **not** change
   `Status`, `ReviewedBy`, `ReviewedAtUtc`, or `PublishedAtUtc`.
2. **Green**: add the method per spec §4.2. Decide during this task (implementation detail, not a
   business rule per the spec) whether to extract a shared private helper for the field-assignment
   block reused by `UpdateDraft` and `ApplyPendingChange`, or duplicate it — prefer extracting a
   private helper if it does not reduce either method's own guard clarity, per AGENTS.md §4 DRY
   guidance, but duplication is acceptable if the two guards would otherwise entangle.

Verification: `dotnet test --filter "FullyQualifiedName~TourTests"`.

Definition of done: new tests green; every existing `Tour.cs` test (in particular `UpdateDraft`,
`SubmitForReview`, `Approve`/`Reject`/`Publish`/`Unpublish` tests) still green, unchanged behavior.

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
     `Rejected`; enforces BR-61/MSG143 (capacity vs. sold slots) and BR-22/MSG29 (new/future-edited
     schedules only — a past-departure schedule in the payload is never rejected for its date);
     enforces the schedule-id-ownership check (spec §3 point G6) — a schedule id not belonging to
     this tour fails with the new `ScheduleNotOwnedByTour` code; enforces MSG01-equivalent mandatory
     field checks.
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
   completeness check (reuse `TourReviewReadinessEvaluator`-equivalent logic — see note below)
   with the same MSG01-equivalent code when incomplete; succeeds setting `Status = PendingReview`.
2. **Note on reusing `TourReviewReadinessEvaluator`**: that evaluator currently reads facts
   straight from `IApplicationDbContext` for a given `tourId` (media, schedules, destinations,
   cancellation policy — all live-`Tour`-table reads). A `TourPendingChangeSet`'s proposed state
   lives in its own scalar columns and JSON payload, not in those live tables, so the **existing**
   evaluator cannot be called as-is against a change set. Write a small adapter/overload (exact
   shape decided during this task, implementation detail) that evaluates the same four BR-101
   facts (primary image presence is not applicable here — the spec's completeness check carries
   over `UpdateTourDraftCommand`'s own validator in §5.3, not this evaluator's media check; confirm
   during this task whether BR-101 for a draft version re-uses all four of
   `TourReviewReadinessEvaluator`'s facts or only the "mandatory fields/itinerary/schedule" subset
   spec §1.1 quotes for UC-37 — if genuinely ambiguous once code is in front of you, stop and ask
   rather than guessing which facts apply, per AGENTS.md §1.1).
3. **Green**: implement the handler; wire the controller action
   (`POST .../pending-changes/{id}/submit`).

Verification: `dotnet test --filter "FullyQualifiedName~SubmitPendingChange"`.

Definition of done: new tests green; the ambiguity in step 2 either resolved by inspection (record
which it was and why) or escalated to the developer before proceeding — not guessed silently.

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
     capacity below an existing active booking on a matched schedule — **including** the exact
     boundary (capacity exactly equal to sold slots passes; one below fails).
   - on success: `Tour.ApplyPendingChange(...)` applied, `Tour.Status` **unchanged**
     (`Approved` stays `Approved`, `Published` stays `Published` — explicit assertion), change set
     `Status = Applied`, one `AuditLog` row, **all inside the same
     `ExecuteInSerializableTransactionAsync` call**; the handler then attempts (via
     `TourPendingChangeNotices`) one `Notification` for the submitting operator; **assert the
     decision's success result is returned even when the notification step is made to throw** (test
     this by injecting/mocking a `DbContext`/`SaveChangesAsync` failure scoped to only the second,
     post-commit call — confirms the decision is not rolled back by a notification failure, spec
     acceptance criterion 5).
2. **Red**, Reject: mirrors Approve minus `ApplyPendingChange`/the BR-61 guard; empty/too-long
   reason fails `RejectionReasonInvalid` (MSG123-equivalent); success sets `Status = Rejected`,
   writes one audit row, live `Tour` untouched (explicit assertion), then the same
   notify-after-commit behavior and the same "notification failure doesn't undo the decision" test.
3. **Green**: implement both handlers per the decision-then-notify mechanism above. Implement
   `TourPendingChangeNotices.NotifyOperatorAsync(...)` as a thin wrapper: add one `Notification`
   row, call `SaveChangesAsync`, catch and swallow (with a log, if a logger is already injected
   elsewhere in this feature folder — do not introduce a new logging dependency just for this) any
   exception from that specific call.
4. **Stop condition check (restated from "Decision this plan makes" above)**: if, while
   implementing this task, it becomes clear that "notify once, no retry" does not actually satisfy
   what the Task Owner meant by "queues the notification for a retry" in UC-60 9.a2/UC-61 8.a2 —
   for example if a code reviewer or test reveals the one-shot attempt is unacceptably lossy — **stop
   this task and ask the Task Owner before adding any retry/outbox/background-job infrastructure.**
   Do not silently build one to make a test pass.
5. Wire the 2 Admin controller actions (`POST .../pending-changes/{id}/approve`,
   `POST .../pending-changes/{id}/reject`), confirming the Administrator-role `[Authorize]`
   convention matches the existing Admin tour-review controller's.

Verification: `dotnet test --filter "FullyQualifiedName~ApprovePendingChange|RejectPendingChange"`.

Definition of done: all new tests green, including the capacity-boundary test and the
notification-failure-does-not-roll-back-decision test; no change to `TourReviewCommandHandlers.cs`.

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
   against Task 0's baseline; any new failure blocks completion.
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
