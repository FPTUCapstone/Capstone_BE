# UC-10 behavior signals, taking an edit back: Plan

This plan implements [UC-10-undo-behavior-signal-spec.md](../specs/UC-10-undo-behavior-signal-spec.md).
Status: approved by the developer on 2026-11-15; T1-T6 implemented, not committed. Two points from the approval
are built in: U4 compares the visited mark with the previous operation's own stored `CreatedAtUtc` (no second
clock read), and U2 looks up exactly the `Dislike` that operation wrote, by the event id derived from its
idempotency key, so an older dislike of the same place is never taken for it. Branch `fix/hoangtv-uc10-undo-behavior-signal`, from `develop` `18d5137d`.
Baseline on that commit (2026-11-15): Application 5122/5122, Infrastructure 1243 (3 skipped).

Every task is test-first: the new tests are written and seen to fail before the code changes. All recorder tests
go in `tests/TripMate.Application.UnitTests/Features/RecommendationFeedback/DerivedBehaviorSignalRecorderTests.cs`
and run with `dotnet test tests/TripMate.Application.UnitTests --filter DerivedBehaviorSignalRecorderTests`.

| Task | Files | Verification / done when |
|---|---|---|
| T1 Test helper: seed an `Adjust` operation that links a previous version to a source version | `DerivedBehaviorSignalRecorderTests.cs` | Existing recorder tests still pass |
| T2 Put-back after `NoTime` records nothing (AC1); put-back after `NotInterested` or no reason is still a `Like` (AC3); a new place is still a `Like` and a source without a previous version behaves as today (AC7, AC8). Red, then green | `DerivedBehaviorSignalRecorder.cs` (read the previous version's visits and its `ItineraryEdit` dislikes; skip the like for a put-back stop without one) | AC1, AC3, AC7 (add), AC8 (add) tests pass |
| T3 Put-back after `AlreadyVisited` removes the visited mark that removal set and keeps an older mark (AC2, AC4). Red, then green | `DerivedBehaviorSignalRecorder.cs` (delete marks dated at or after the previous operation) | AC2, AC4 tests pass |
| T4 Taken-back add records no `Dislike`; with a reason the reason decides; a stop from the previous version removed without a reason is still a `Dislike` (AC5, AC6, AC7, AC8). Red, then green | `DerivedBehaviorSignalRecorder.cs` | AC5, AC6, AC7 (remove), AC8 (remove) tests pass |
| T5 A storage failure in the new reads is logged and the edit records what it records today | `DerivedBehaviorSignalRecorder.cs`, same test file | Test with a context whose `ItineraryVersionOperations` read throws passes |
| T6 Logger on every path (AC9): optional `ILogger` on `AdjustItineraryItemsCommandHandler`, `NavigationSessionMutator`, `ReachNavigationItemCommandHandler`, `SkipNavigationItemCommandHandler`, passed to the recorder. Red, then green | Those four files; `AdjustItineraryItemsCommandHandlerTests` / `NavigationSessionMutationHandlersTests` (a failing insert writes `derived_behavior_signal_failed` and the action succeeds) | `--filter "AdjustItineraryItems|NavigationSessionMutationHandlers"` pass |
| T7 Self-review against the spec, then the full check | — | `dotnet build` with no new warnings; `dotnet test` for Application and Infrastructure unit tests green; `dotnet format --verify-no-changes` on the changed files |

Not in this plan: integration tests against SQL Server (no schema change), client changes, a pull request
(opened only when the developer asks).
