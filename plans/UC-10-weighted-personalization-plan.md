# UC-10 Weighted Personalization Rollout: Plan

This plan implements [UC-10-weighted-personalization-spec.md](../specs/UC-10-weighted-personalization-spec.md). Every task is test-first.

## G0 (done, `67e5bb1f`)

| Task | Files | Verification |
|---|---|---|
| G0.1 Pin shadow config: red, then green | `ShadowConfigurationStartupTests.cs`, `appsettings.json` | `--filter ShadowConfigurationStartupTests` |
| G0.2 Pin `Csp.Mode=Lexicographic` | `SchedulingSolverConfigurationTests.cs` | `--filter SchedulingSolverConfigurationTests` |
| G0.3 ADR-002, README operations note | `docs/adr/ADR-002-…`, `README.md` | Review |

## G1 (implemented)

| Task | Files | Verification |
|---|---|---|
| G1.1 Counterfactual endpoint tests (AC1.1) | `tests/TripMate.Api.IntegrationTests/Scheduling/WeightedPersonalizationEndpointTests.cs` | `--filter WeightedPersonalizationEndpointTests`: 5/5 pass |
| G1.2 Discrimination check (AC1.2) | Temporary `Csp.Mode=Lexicographic` override, then reverted | Pace, risk and cold-start fail; interests pass |
| G1.3 Pace in shadow metrics and log (AC1.3): red (CS1061), then green | `ShadowMetrics.cs`, `ShadowSchedulingRunner.cs`, `PersonalParamsFactory.EffectivePace`, `ShadowSchedulingRunnerTests.cs` | `--filter ShadowSchedulingRunnerTests`: 7/7 pass |
| G1.4 No traveler value in the parse-failure log (AC1.4): red, then green | `PersonalParamsFactory.cs`, `PersonalParamsFactoryTests.cs` | `--filter PersonalParamsFactoryTests`: 12/12 pass |
| G1.5 Spec, this plan, ADR-002 G1 row | `specs/`, `plans/`, `docs/adr/` | Review |
| G1.6 Full CI suite and format check | — | Exit 0. Application 4643/4643, Infrastructure 1125 (3 skipped), Api 825 (150 skipped), Replay 13/13. `dotnet format --verify-no-changes` on the changed files: exit 0 |

## G2 (next)

| Task | Files | Verification |
|---|---|---|
| G2.1 `SchedulingProvenance` record, produced by `ItineraryGenerationService` (outcome, mode, search completed, node limit) | `Scheduling/Common/*` | Unit: each outcome maps to provenance |
| G2.2 `TimeOfDayRefined` flag set by `TimeOfDayRefiner` when the order changes | `TimeOfDay/TimeOfDayRefiner.cs` | Unit: reorder sets the flag, keep does not |
| G2.3 Migration and schema for `Itineraries` provenance columns; EF configuration; entity | `database/migrations/…`, `tripmate_schema_v7.sql`, `ItineraryConfiguration.cs`, `Itinerary.cs` | SqlServer integration: migration applies to empty and seeded DBs |
| G2.4 DTO fields on create and detail responses | `SchedulingResponseDto.cs`, detail DTO | API test: `TimeLimitMilliseconds=0` returns the fallback outcome |
| G2.5 Regenerate and Adjust provenance (`manual_order`) | `ItineraryVersionService.cs` | Unit |
| G2.6 `planning.generation_timeout` when the CSP times out and there is no heuristic plan | `ItineraryGenerationService.cs`, `SchedulingErrorCodes` | Unit with `BeforeEvaluation` delay and an infeasible heuristic |
| G2.7 Outcome log fields | `SchedulingSolverOutcomeLogger.cs`, `SchedulingSolverDiagnostics.cs` | `SchedulingSolverOutcomeLoggerTests` |
| G2.8 `CspOptions` comment fix; regenerate the solver-comparison report (about 20 min, after `dotnet format`) | `Csp/CspOptions.cs`, `docs/benchmarks/` | `LatestCommittedReport_MeasuredTheCurrentSource` |
