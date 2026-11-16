# UC-75 Respond to Partial Refund Proposal: Implementation Plan

Spec: `specs/UC-75-respond-partial-refund-proposal-spec.md`. Branch:
`feature/phuctv-uc75-partial-refund-response`, created from `origin/develop` 4d6c5618.

Every task is TDD: write a failing test, write the minimal code to pass it, then refactor.

| # | Task | Files | Verify |
|---|---|---|---|
| 1 | `PostTripComplaintStatus` enum and `PartialRefundProposalRules` (72-hour window, `IsOpen` with exact-deadline boundary) | `Domain/Enums`, `Domain/Rules`, `Application.UnitTests/Domain` | `dotnet test --filter PartialRefundProposalRules` |
| 2 | Ports, snapshot record, error codes, notification constants (MSG166–168) | `Application/Features/Complaints/PartialRefundProposals/*`, `Domain/Entities/Notification.cs` | build |
| 3 | `GetPartialRefundProposalQuery`: 401, 404, 403 (MSG126), `canRespond` | same folder, plus tests | `--filter GetPartialRefundProposal` |
| 4 | `RespondToPartialRefundProposalCommand`: accept and decline happy paths, G1, G5, lost race, Operator notification, refund request only on accept | same folder, plus tests | `--filter RespondToPartialRefundProposal` |
| 5 | `PartialRefundProposalDeadlineProcessor`: closes overdue proposals as Declined, sends MSG168 to both parties, skips lost races | same folder, plus tests | `--filter PartialRefundProposalDeadline` |
| 6 | Infrastructure: temporary port implementations, options, hosted service, DI registration | `Infrastructure/Complaints/*`, `DependencyInjection.cs` | `TripMate.Infrastructure.UnitTests` |
| 7 | API: `TravelerComplaintsController` and error mapping in `ApiControllerBase` | `Api/Controllers/V1`, `Api/Common` | full `dotnet test` and `dotnet format --verify-no-changes` |

Definition of Done: all tests pass, `dotnet format` reports no changes on the changed files, and
nothing is pushed.
