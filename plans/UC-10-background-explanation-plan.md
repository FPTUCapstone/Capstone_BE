# UC-10 itinerary explanations in the background: Plan

This plan implements [UC-10-background-explanation-spec.md](../specs/UC-10-background-explanation-spec.md).
Status: approved by the developer on 2026-11-17; T0-T9 done on 2026-11-17, not committed. Branch `feature/hoangtv-uc10-background-explanation`, from `develop`
`34b8c278`. Backend only; Web and Mobile follow with their own spec and pull request.

Every task is test-first: the new tests are written and seen to fail (a compile error counts) before the code
changes. Commands are run from the repository root; `--filter` values are test class names.

## Time limits (decided 2026-11-17)

The AI service's limit stays shorter than the backend's, so the backend is still listening when a slow answer
arrives: AI service `LLM_TIMEOUT_SECONDS` = 90, backend `AiExplanation:BackgroundTimeoutSeconds` default = 100.

## Design in short

| Piece | Layer | Role |
|---|---|---|
| `IItineraryExplanationQueue`, `ItineraryExplanationJob`, `ItineraryExplanationJobItem` | Application | The waiting list's contract. A job holds the itinerary id, the explanation input, and for each item `(ItineraryItemId, SequenceNo, TemplateText)` (B4). |
| `BackgroundItineraryExplanationProcessor` | Application | Handles one job: asks the provider with the background time budget, validates, overwrites only items that still hold their template sentence, logs the outcome. |
| `InMemoryItineraryExplanationQueue` | Infrastructure | Bounded channel of 50 jobs; refuses when full (B6). |
| `ItineraryExplanationBackgroundService` | Infrastructure | Hosted worker: one job at a time, a new scope per job, never stopped by a failing job. |
| `IItineraryExplanationProvider.ExplainAsync` | Application / Infrastructure | Gains an optional `timeBudget`; when given it replaces the configured per-attempt and overall timeouts for that call. |

## Tasks

| Task | Files | Verification / done when |
|---|---|---|
| T0 Baseline on `34b8c278` (done) | — | Application 5263 passed, 1 failed of 5264; Infrastructure 1261 passed, 3 skipped. The failure is `AuditLogOutcomeTests.Queries_ExposeRecordedOutcomeAndBusinessReason(outcome: Failure)`: it also failed once on `develop` on 2026-11-15, passes alone, and is outside this change |
| T1 Execution options: `BackgroundEnabled` (default false) and `BackgroundTimeout`; validator allows 1-300 seconds (AC6) | `Scheduling/Explanation/ItineraryExplanationExecutionOptions.cs`; `ItineraryExplanationExecutionOptionsTests.cs` | `--filter ItineraryExplanationExecutionOptionsTests`: defaults, 0 and 301 seconds fail, 1 and 300 pass |
| T2 Provider options and mapping: `AiExplanation:BackgroundEnabled`, `AiExplanation:BackgroundTimeoutSeconds`, mapped onto the execution options | `AiExplanation/ExplanationProviderOptions.cs`, `ExplanationProviderOptionsValidator.cs`, `DependencyInjection.cs`; `ExplanationProviderOptionsValidatorTests.cs`, `ExplanationDependencyInjectionTests.cs` | `--filter "ExplanationProviderOptionsValidatorTests|ExplanationDependencyInjectionTests"`: values bind and map; out-of-range fails startup validation |
| T3 Provider time budget: optional `timeBudget` on `ExplainAsync`, honored by both providers; the explanation `HttpClient` gets no timeout of its own (its 100-second default would cut a longer budget), the provider's token enforces the limit | `IItineraryExplanationProvider.cs`, `TripMateAi/TripMateAiExplanationProvider.cs`, `AiExplanation/HttpItineraryExplanationProvider.cs`, `Ai/AiProviderRegistration.cs`; `TripMateAiExplanationProviderTests.cs`, `HttpItineraryExplanationProviderTests.cs`, DI test; the test doubles that implement the interface | `--filter "TripMateAiExplanationProviderTests|HttpItineraryExplanationProviderTests"`: with a budget an attempt outlives `TimeoutSeconds`; the budget itself still ends in `timeout`; without a budget every existing test passes unchanged |
| T4 Queue contract and in-memory queue (B6) | `Scheduling/Explanation/IItineraryExplanationQueue.cs` (new), `Infrastructure/AiExplanation/InMemoryItineraryExplanationQueue.cs` (new); `InMemoryItineraryExplanationQueueTests.cs` (new) | `--filter InMemoryItineraryExplanationQueueTests`: accepts 50, refuses the 51st, hands jobs out in order |
| T5 Processor (B2-B5; AC1, AC2, AC3) | `Scheduling/Explanation/BackgroundItineraryExplanationProcessor.cs` (new); `BackgroundItineraryExplanationProcessorTests.cs` (new, `TestDbContext` and a fake provider) | `--filter BackgroundItineraryExplanationProcessorTests`: a valid answer replaces every template sentence; unreachable, timeout and invalid answers keep them and log one outcome line; an item whose sentence changed is kept while the others are replaced; a deleted itinerary and a failing save do not throw; the provider receives the background budget; a shutdown cancellation propagates |
| T6 Create handler and response (B1, B7, B8; AC1, AC4, AC5, AC6, AC7) | `Scheduling/Create/CreateSchedulingRequestCommandHandler.cs`, `Scheduling/Common/SchedulingResponseDto.cs` (`ExplanationPending`); `CreateSchedulingRequestCommandHandlerTests.cs` | `--filter CreateSchedulingRequestCommandHandlerTests`: in background mode the provider is not called by the request, template sentences are saved and returned, `ExplanationPending` is true and the queued job carries each item's id and template sentence; a full queue gives `false` and a warning; `Enabled = false` queues nothing; a replay queues nothing; template sentences that could not be saved are not queued; with background off all existing tests pass and the flag is false |
| T7 Worker and registration | `Infrastructure/AiExplanation/ItineraryExplanationBackgroundService.cs` (new), `DependencyInjection.cs`; `ItineraryExplanationBackgroundServiceTests.cs` (new), `ExplanationDependencyInjectionTests.cs` | `--filter "ItineraryExplanationBackgroundServiceTests|ExplanationDependencyInjectionTests"`: two jobs are processed in order; a throwing job is logged and the next one still runs; the worker stops on cancellation; queue, processor and worker resolve from the container and the handler receives the queue |
| T8 Committed configuration | `src/TripMate.Api/appsettings.json`, `.env.example`, `docker-compose.yml` (the two backend settings; `LLM_TIMEOUT_SECONDS` default 9 → 90) | `--filter ExplanationDependencyInjectionTests` (`CommittedApiConfiguration_…` still boots); review |
| T9 Self-review against the spec (spec compliance, then code quality), then the full check | spec and this plan (status, T0 numbers) | `dotnet build` with no new warnings; Application and Infrastructure unit tests green; `dotnet format --verify-no-changes` on the changed files |

Log outcomes added to the existing explanation log line: `queued` and `queue-full` when the request ends, and the
existing names (`success`, `timeout`, `network`, …) when the worker finishes a job.

## Result of T9 (2026-11-17)

| Check | Result |
|---|---|
| `dotnet build` (whole solution) | 0 errors; 1 warning, CS8604 in `CreateSchedulingRequestCommandHandler.cs`, already on `develop` |
| `dotnet test tests/TripMate.Application.UnitTests` | 5296 passed, 0 failed (baseline 5263 + 1 flaky failure) |
| `dotnet test tests/TripMate.Infrastructure.UnitTests` | 1292 passed, 3 skipped, 0 failed (baseline 1261) |
| `dotnet format --verify-no-changes` on the changed files | clean, except four whitespace findings on untouched lines of `CreateSchedulingRequestCommandHandler.cs` that `develop` already has |

Added while executing, beyond the task table:
- `IBackgroundItineraryExplanationProcessor`, so the hosted worker can be tested without a database, and one
  end-to-end test of AC1 (real queue, worker and processor against an in-memory database).
- `docs/benchmarks/UC-10-csp-solver-comparison-2026-11-17-bgexplanation.*`: `SchedulingResponseDto.cs` is in
  `Scheduling/Common`, one of the folders the solver-comparison report fingerprints, so
  `SolverComparisonReportTests.LatestCommittedReport_MeasuredTheCurrentSource` required a regenerated report.
  All 907 per-scenario rows are identical to the previous report (`2026-11-15-tm101remediation`); only the
  timings and the host differ (this run: a 12-thread laptop).
- A paragraph in `docs/operations/uc10-tripmate-ai-measurement-runbook.md`: that runbook measures the inline mode
  and asks for `LLM_TIMEOUT_SECONDS` below 10, while the Compose default is now 90.
- Background mode without a registered waiting list (only possible in a hand-built handler, never in the
  running application) keeps the template sentences and still does not wait for the AI.

Not run: the SQL Server integration tests (CI runs them; they use background mode off).

## Not in this plan
- New SQL Server integration tests: there is no schema change and the worker's writes are plain updates covered
  by the unit tests. The existing integration tests run with background mode off and must stay green in CI.
- Web and Mobile (spec section 6).
- Turning the feature on. See below.

## After the pull request is merged (each step only with the developer's go-ahead)
1. Capstone_AI, separate pull request: `LLM_TIMEOUT_SECONDS` default 9 → 90 in `app/config.py`, `.env.example`
   and the README. Side effect to know: with the backend's old inline mode the AI may keep working for up to 90
   seconds on a request the backend already gave up on. Local `.env` on the laptop: 60 → 90, service restarted.
2. Railway: `AiExplanation__Enabled=true`, `AiExplanation__BackgroundEnabled=true`,
   `AiExplanation__BackgroundTimeoutSeconds=100`. No migration to run first.
3. Check with one real itinerary: the log shows `queued`, then the worker's `success`, and
   `GET /api/v1/itineraries/{id}` returns the AI sentences.
4. Web spec and pull request, then Mobile.
