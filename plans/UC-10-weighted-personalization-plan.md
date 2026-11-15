# UC-10 Weighted Personalization Rollout: Plan

This plan implements [UC-10-weighted-personalization-spec.md](../specs/UC-10-weighted-personalization-spec.md). Every task is test-first.

## G0 (done, `67e5bb1f`)

| Task | Files | Verification |
|---|---|---|
| G0.1 Pin shadow config: red, then green | `ShadowConfigurationStartupTests.cs`, `appsettings.json` | `--filter ShadowConfigurationStartupTests` |
| G0.2 Pin `Csp.Mode=Lexicographic` | `SchedulingSolverConfigurationTests.cs` | `--filter SchedulingSolverConfigurationTests` |
| G0.3 ADR-002, README operations note | `docs/adr/ADR-002-…`, `README.md` | Review |

## G1 (done, `26038562`)

| Task | Files | Verification |
|---|---|---|
| G1.1 Counterfactual endpoint tests (AC1.1) | `tests/TripMate.Api.IntegrationTests/Scheduling/WeightedPersonalizationEndpointTests.cs` | `--filter WeightedPersonalizationEndpointTests`: 5/5 pass |
| G1.2 Discrimination check (AC1.2) | Temporary `Csp.Mode=Lexicographic` override, then reverted | Pace, risk and cold-start fail; interests pass |
| G1.3 Pace in shadow metrics and log (AC1.3): red (CS1061), then green | `ShadowMetrics.cs`, `ShadowSchedulingRunner.cs`, `PersonalParamsFactory.EffectivePace`, `ShadowSchedulingRunnerTests.cs` | `--filter ShadowSchedulingRunnerTests`: 7/7 pass |
| G1.4 No traveler value in the parse-failure log (AC1.4): red, then green | `PersonalParamsFactory.cs`, `PersonalParamsFactoryTests.cs` | `--filter PersonalParamsFactoryTests`: 12/12 pass |
| G1.5 Spec, this plan, ADR-002 G1 row | `specs/`, `plans/`, `docs/adr/` | Review |
| G1.6 Full CI suite and format check | — | Exit 0. Application 4643/4643, Infrastructure 1125 (3 skipped), Api 825 (150 skipped), Replay 13/13. `dotnet format --verify-no-changes` on the changed files: exit 0 |

## G2 (implemented)

Every task was test-first, with the failure observed before the implementation. Most failures were compile errors (CS0246, CS1061, CS0117).

| Task | Files | Verification |
|---|---|---|
| G2.1 `SchedulingProvenance` and `SchedulingProvenanceDto`; `GeneratedItineraryPlan.Provenance`, set by `ItineraryGenerationService` | `Scheduling/Common/SchedulingProvenance.cs`, `GenerationInput.cs`, `ItineraryGenerationService.cs` | `GenerationProvenanceTests` (CSP complete, CSP time limit fallback, heuristic mode) |
| G2.2 `TimeOfDayRefined` set only when the refiner changes the order | `TimeOfDay/TimeOfDayRefiner.cs` | `TimeOfDayRefinerTests.RefineAsync_MarksTheProvenanceAsRefined_OnlyWhenItReordersTheSolverPlan`. The fixture's static `SkewedProvider` is set per input. |
| G2.3 `Itinerary.RecordGenerationProvenance`; EF mapping; idempotent migration (not added to schema v7) | `Itinerary.cs`, `ItineraryConfiguration.cs`, `database/migrations/20261110_uc10_itinerary_solver_provenance.sql`, test csproj link | `ItineraryTests` (3 tests); `ItinerarySolverProvenanceMigrationSqlServerTests` (fresh schema, legacy row with a rerun); `CreateSchedulingRequestSqlServerTests.Success_PersistsTheScheduleProvenanceOnTheItineraryRow` |
| G2.4 `provenance` in the create response (fresh and replay) and in the itinerary detail | `SchedulingResponseDto.cs`, `CreateSchedulingRequestCommandHandler.cs`, `ItineraryDetailResponse.cs`, `GetItineraryDetailQueryHandler.cs` | `Handle_CspMode_ReturnsAndPersistsTheScheduleProvenance`, `ToResponse_ReturnsTheSavedScheduleProvenance_AndNullWhenNoneWasSaved`, API `Post_ReturnsTheScheduleProvenance` |
| G2.5 Regenerate and Adjust provenance | `ItineraryVersionService.cs` | `CreateRegeneratedVersion_RecordsHowTheNewScheduleWasProduced`, `CreateAdjustedVersion_RecordsTheTravelersOwnOrder` |
| G2.6 Retryable timeout: `planning.generation_timeout` (request released) and `itinerary.generation_timeout`, both 503 | `ItineraryGenerationService.cs`, `SchedulingErrorCodes.cs`, `ItineraryErrorCodes.cs`, the handler, `CandidatePoolFunnel*`, `ApiControllerBase.cs` | `CspTimeLimit_WithoutAHeuristicPlan_ReportsARetryableTimeout_NotAnInfeasibleRequest`, `Handle_WhenTheCspTimesOutWithoutAPlan_ReleasesTheRequestSoTheSameKeyCanRetry`, `CreateRegeneratedVersion_WhenTheCspTimesOutWithoutAPlan_ReportsARetryableTimeout`, API `Post_WhenTheCspTimesOutWithoutAPlan_ReturnsRetryableServiceUnavailable` |
| G2.7 `solver.objective_mode` tag; log fields `ObjectiveMode`, `CspSearchCompleted` | `SchedulingSolverDiagnostics.cs`, `SchedulingSolverOutcomeLogger.cs`, allow-list in `CspItinerarySolverTests` | `CspOutcome_IsRecordedWithTheObjectiveMode`, `LogsTheObjectiveModeAndWhetherTheSearchCompleted` |
| G2.8 `CspOptions` comment fix; `dotnet format`, then the regenerated report `2026-11-10-g2provenance` | `Csp/CspOptions.cs`, `docs/benchmarks/` | `LatestCommittedReport_MeasuredTheCurrentSource` |
