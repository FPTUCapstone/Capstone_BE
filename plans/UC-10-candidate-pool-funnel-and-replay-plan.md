# UC-10 Candidate Pool Funnel and Offline Replay Implementation Plan

## Status

Implemented — measurement release complete; decision gate recorded as **collect more evidence**.

This plan ends at the evidence/decision gate. It does not implement production diversity,
backfill, or cap changes.

## Goal

Measure the provider-to-matrix-to-plan funnel, extract the current selectors without behavioral
change, replay current and experimental policies offline, and produce the evidence/product inputs
needed to decide whether TM-215 should be amended.

## Source Specification

`specs/UC-10-candidate-pool-funnel-and-replay-spec.md`

## Baseline and Workspace

- Current branch: `feature/phuctv-uc10-poi-recommendations` (non-main).
- The working tree already contains extensive developer UC-10 changes. Inventory and preserve them.
- Before implementation, record:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter "FullyQualifiedName~PoiRankingOrchestratorTests|FullyQualifiedName~CreateSchedulingRequestCommandHandlerTests"
dotnet test TripMate.slnx --no-restore --nologo
```

- Capture current selection IDs/order for provider caps 0/1/40/60/over-60 and matrix capacities
  with 0/1/6 mandatory POIs before extraction.
- Do not treat skipped external-service tests as evidence that production latency/cost passed.

## Frozen Decisions

- Measurement release only; current 60/40/no-backfill behavior is normative.
- Online telemetry is aggregate, low-cardinality, and never persists candidate identities.
- Baseline replay references the production selectors, not copied ordering logic.
- Checked-in corpora are synthetic; production-derived corpora remain de-identified and outside Git.
- Experimental B/C/D strategies run offline only.
- Provider scores are never fabricated for outside-pool candidates.
- The plan pauses after its decision report; a separate approved TM-215 amendment is required for
  runtime selection/backfill changes.

## Approved TDD Seams

1. `PoiRankingOrchestrator.BuildRankingAsync` for provider-pool output compatibility.
2. `CreateSchedulingRequestCommandHandler.Handle` for matrix selection, funnel counts, provider/ORS
   call counts, snapshot retry, and no-backfill.
3. Internal pure selectors through the Application test assembly.
4. Metric/activity listeners for telemetry shape/privacy.
5. Replay CLI process exit code plus versioned input/output golden files.

Tests must not assert private helper call order or replace offline experiments with production code.

## Work Items

### W01 — Freeze current top-K and no-backfill contract

Files:

- Modify
  `tests/TripMate.Application.UnitTests/Features/Scheduling/Personalization/PoiRankingOrchestratorTests.cs`.
- Modify
  `tests/TripMate.Application.UnitTests/Features/Scheduling/Create/CreateSchedulingRequestCommandHandlerTests.cs`.
- Modify relevant SQL integration tests only if a real authoritative-invalidation case is missing.

Characterization tests:

- provider cap 60 with 61+ candidates and every frozen tie-break boundary;
- matrix cap 40 minus mandatory count, retaining all mandatory POIs;
- shuffled inputs produce identical ordered IDs;
- outside-provider candidates never enter matrix selection;
- invalidated pooled candidate leaves capacity unused rather than backfilling;
- snapshot retry preserves the currently characterized provider call count and never performs a
  provider call from inside final revalidation itself;
- route matrix point count stays within cap plus start/end.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter "FullyQualifiedName~PoiRankingOrchestratorTests|FullyQualifiedName~CreateSchedulingRequestCommandHandlerTests"
```

Definition of done: the intentional loss/no-backfill behavior is executable documentation and all
tests are observed green before refactoring.

### W02 — Extract deterministic production selectors

Files:

- Add
  `src/TripMate.Application/Features/Scheduling/Personalization/ProviderPoolSelector.cs`.
- Add
  `src/TripMate.Application/Features/Scheduling/Common/MatrixCandidateSelector.cs`.
- Modify
  `src/TripMate.Application/Features/Scheduling/Personalization/PoiRankingOrchestrator.cs`.
- Modify
  `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs`.
- Add selector tests under the matching Application unit-test folders.

Red tests first:

- exact strict ordering across every nullable tie-break;
- input order/culture/hash-order independence;
- mandatory-first capacity allocation;
- duplicate IDs reject deterministically rather than first/last-wins;
- zero/partial/full/over capacity;
- extracted selectors match every W01 golden ID sequence.

Implementation:

- move ordering/cap logic only; do not change fields or comparator direction;
- return ordered selection plus aggregate selected/dropped counts;
- keep selectors pure and internal; no database, logger, provider, or route client.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter "FullyQualifiedName~ProviderPoolSelector|FullyQualifiedName~MatrixCandidateSelector|FullyQualifiedName~PoiRankingOrchestratorTests|FullyQualifiedName~CreateSchedulingRequestCommandHandlerTests"
```

Definition of done: production calls shared pure selectors and output is byte-for-byte compatible
with W01.

### W03 — Funnel diagnostics model and online instrumentation

Files:

- Add
  `src/TripMate.Application/Features/Scheduling/Diagnostics/CandidatePoolFunnelDiagnostics.cs`.
- Add
  `src/TripMate.Application/Features/Scheduling/Diagnostics/CandidatePoolFunnelTelemetry.cs`.
- Modify
  `src/TripMate.Application/Features/Scheduling/Personalization/PoiRankingOrchestrator.cs`.
- Modify
  `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs`.
- Modify `src/TripMate.Infrastructure/Routing/OpenRouteServiceRouteDurationProvider.cs` only if its
  existing latency instrumentation cannot supply matrix latency/payload buckets.
- Add diagnostics tests under
  `tests/TripMate.Application.UnitTests/Features/Scheduling/Diagnostics/`.
- Modify route-provider infrastructure tests only when W03 changes that adapter.

Red tests first:

- success emits all reached stage counts and exact derived drops once;
- replay emits no funnel; snapshot retry uses the two allowed attempt values;
- provider fallback, infeasible, routing failure, and cancellation stop at truthful stages;
- prepared-stage inequalities and matrix `points <= cap + 2` hold;
- `finalize_valid_frozen_pool` may be lower than the already-built `matrix_optional` without
  violating telemetry consistency;
- a metadata-only hash mismatch may retain the full valid-pool count while still retrying;
- a status/planning-ready/radius invalidation changes the observed valid count but preserves the
  exact existing hash-mismatch retry/terminal decision;
- listener observes only approved stage/reason/attempt/outcome/mode dimensions;
- IDs, names, coordinates, raw scores/preferences, payload, and exception text are absent;
- a throwing listener/disabled listener cannot change the request result.

Implementation:

- carry one attempt-scoped aggregate diagnostics object through existing workflow boundaries;
- retain frozen provider-pool IDs in `PreparedGeneration` solely for finalize observation;
- inside `FinalizeAsync`, compare those IDs against Active, planning-ready, exact-radius POIs from
  the already-loaded authoritative data; do not add per-candidate queries or claim a specific
  invalidation reason when an ID is absent;
- compute this observation independently of `SchedulingGenerationSnapshot.Hash` and do not use it
  to remove/backfill candidates, rebuild the matrix/plan, call AI/ORS, or select retry vs terminal
  failure; the existing all-or-nothing hash comparison remains the sole data-change gate;
- emit counters/histograms plus one aggregate activity, never one event per candidate;
- centralize dimension constants and count consistency checks;
- derive conversion ratios only in reports/dashboard from matching count sums; do not emit a ratio
  histogram;
- preserve provider/route calls and selection exactly.

Verification:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter "FullyQualifiedName~CandidatePoolFunnel|FullyQualifiedName~PoiRankingOrchestratorTests|FullyQualifiedName~CreateSchedulingRequestCommandHandlerTests"
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --no-restore --filter FullyQualifiedName~OpenRouteServiceRouteDurationProviderTests
```

Definition of done: the online funnel is observable/privacy-safe and behavior/call counts remain
unchanged.

### W04 — Instrumentation overhead evidence

Files:

- Add/update a local load fixture under `tools/benchmarks/candidate-pool-funnel/`.

Actions/tests:

- load-test telemetry off/on using the same inputs and stubbed external latency;
- confirm no more than 2% p95 application-processing and managed-allocation overhead;
- record runtime/OS/CPU, corpus, sample count, p50/p95/p99, and allocation methodology.

Definition of done: instrumentation overhead satisfies the gate before evidence collection; no
dashboard provisioning work blocks replay construction.

### W05 — Versioned replay CLI and production-baseline adapter

Files:

- Add `tools/TripMate.CandidatePoolReplay/TripMate.CandidatePoolReplay.csproj`.
- Add replay input/output contracts and CLI entry point under that project.
- Add a synthetic corpus under `tools/TripMate.CandidatePoolReplay/fixtures/`.
- Add `tests/TripMate.CandidatePoolReplay.Tests/TripMate.CandidatePoolReplay.Tests.csproj` and add it
  to `TripMate.slnx`.
- Add golden input/output fixtures to the replay test project.

Red tests first:

- reject unknown schema, duplicate IDs, inconsistent options, missing/non-square/asymmetric-cell
  errors, and forbidden raw identity fields;
- strategy A calls the shared production selectors and matches W01 scenarios;
- same input twice and shuffled JSONL order produce identical normalized output;
- non-timing output remains stable with parallel execution;
- CLI returns nonzero for invalid input and never partially reports success.

Implementation:

- provide explicit `--input`, `--manifest`, and `--output` paths;
- separate deterministic result JSON/CSV from timing metadata;
- include corpus hash, code/runtime/options/strategy metadata;
- perform no network call and require a complete frozen directional matrix.

Verification:

```powershell
dotnet test tests/TripMate.CandidatePoolReplay.Tests/TripMate.CandidatePoolReplay.Tests.csproj --no-restore
```

Definition of done: a validated, deterministic, network-free replay of the exact production
baseline exists.

### W06 — Offline experimental strategies

Files:

- Add strategy implementations under `tools/TripMate.CandidatePoolReplay/Strategies/`.
- Add paired comparison/reporting under `tools/TripMate.CandidatePoolReplay/Reporting/`.
- Extend replay tests/fixtures; do not modify runtime selector registration.

Red tests first:

- B backfills only invalidated slots from frozen valid overflow in frozen order;
- B never uses an outside-provider candidate or calls/fabricates provider scoring;
- C preserves its configured desirability core and uses deterministic marginal category/geo-cell
  coverage with surrogate-ID tie-break;
- D changes only manifest-declared offline caps;
- comparable provider-score and Base-only tracks remain separately labeled;
- all strategies retain mandatory candidates, hard feasibility, and matrix/payload accounting;
- paired metrics and segment summaries match hand-calculated fixtures.

Implementation:

- keep every experimental parameter explicit in the manifest;
- share hard-constraint generation/evaluation code where practical;
- report paired utilities, coverage, travel, feasible rate, payload, work, and stability; do not
  label similarity to strategy D or any other strategy as recall/ground truth;
- never reference experimental strategies from production DI/handler/orchestrator.

Verification:

```powershell
dotnet test tests/TripMate.CandidatePoolReplay.Tests/TripMate.CandidatePoolReplay.Tests.csproj --no-restore
rg -n "CandidatePoolReplay|OverflowBackfill|Diversity" src/TripMate.Api src/TripMate.Infrastructure
```

The final `rg` must show no runtime registration/reference to offline experiments.

Definition of done: B/C/D can be compared reproducibly but cannot execute in production.

### W07 — Representative replay and decision record

Files:

- Add `docs/analysis/uc10-candidate-pool-replay-decision.md` with aggregate results only.
- When production-derived inputs exist, store them outside Git and record approved location, corpus
  hash, owner, retention, and access without exposing identities. Synthetic-only execution is valid
  and must be declared rather than blocked on a nonexistent export process.

Actions:

1. Predeclare correctness/exploratory/inferential intent, minimum material paired effects, corpus
   source, and analysis segments. Perform a power calculation only for inferential claims.
2. Validate corpus coverage and invalid/excluded scenario counts; report `n` for every segment and
   do not require a sparse four-dimensional Cartesian grid.
3. Run A/B/C/D with recorded manifests and at least the required eligible/mandatory/mode/density
   segments.
4. Join the aggregate replay result with online funnel/payload-latency evidence; do not export raw
   online identities.
5. Record paired distributions, evidence strength, confidence/uncertainty where justified, quality
   gains/regressions, cost, and stability.
6. State external validity explicitly. For a synthetic-only corpus, list modeled/missing properties
   and state that conclusions generalize only to the tested synthetic distribution.
7. Choose `retain`, `collect more evidence`, or `propose amendment`. Exploratory evidence may
   support a guarded follow-up proposal but must not be described as proven production uplift.
8. If proposing an amendment, obtain explicit product values for every threshold/policy listed in
   the spec. Stop here: author a successor spec/plan before runtime changes.

Definition of done: the recommendation is evidence-backed, segment-aware, reproducible, and does
not silently authorize a production policy change.

### W08 — Dashboard, regression, review, and delivery evidence

Files:

- Add `docs/operations/uc10-candidate-pool-funnel-observability.md`.
- Add a dashboard definition under the repository's deployment/observability location if one
  exists; otherwise record reproducible panel queries and any external dashboard reference in the
  operations document.
- Update status/evidence in the source spec and this plan.
- Update only the W04/W07/W08 benchmark, analysis, and operations artifacts with final aggregate
  evidence.

Actions:

1. Show prepared funnel counts, finalize valid-pool observation, drop counts, matrix points/elements,
   stage p50/p95/p99, outcomes, and initial/retry split.
2. Derive conversion ratios from matching stage-count sums for the same window/dimensions, display
   numerator/denominator, and document aggregation semantics.
3. Run targeted tests after every fix, then full tests and formatting.
4. Review first for spec compliance, then repository standards/code quality.
5. Verify all TM-215 no-backfill/top-K tests remain unchanged and green.
6. Inspect runtime dependency graph/code search to prove experiments are CLI-only.
7. Inspect diffs/status and preserve unrelated developer changes.
8. Do not commit, push, merge, or open a PR without explicit instruction.

Verification:

```powershell
dotnet test TripMate.slnx --no-restore --nologo
dotnet format TripMate.slnx --verify-no-changes
```

Definition of done: measurement/replay tooling and dashboard definition are complete, runtime
selection is unchanged, all tests/formatting pass, and no critical review finding remains.

## Dependency Order

```text
W01 → W02 → W03 → W04
              └→ W05 → W06 → W07 → W08
```

W04 and W05 may proceed after W03 on separate isolated workspaces. W07 requires overhead-approved
online funnel evidence plus replay. Dashboard work intentionally waits until W08 so it reflects the
final count semantics and decision report. Do not advance past red verification or unresolved
critical findings.

## Explicit Stop Gate

This plan is complete after W08. Even when W07 recommends change, do **not** modify production
selection, enable diversity, backfill invalidated candidates, increase caps, or amend TM-215 code in
this execution. First create and approve the successor specification and implementation plan with
the product thresholds and policy parameters recorded by W07.

## Risk Controls

| Risk | Control | Evidence |
|---|---|---|
| Measurement changes selection | W01 goldens and shared-selector equivalence | W01/W02 |
| Metrics leak identity/cardinality | aggregate model, closed dimensions, listener privacy tests | W03 |
| Telemetry double-counts replay/retry | attempt lifecycle and initial/retry-only dimension | W03 |
| Finalize observation changes retry semantics | observation-only comparison over already-loaded data; snapshot hash remains sole gate | W03 |
| Instrumentation harms latency | off/on benchmark and 2% overhead gate | W04 |
| Replay baseline drifts from production | shared production selectors | W02/W05 |
| AI scores invented outside pool | separate provider-comparable/Base-only tracks | W05/W06 |
| Experimental code leaks into runtime | tool-only project plus runtime reference scan | W06/W08 |
| Diversity policy is inferred | manifest parameters offline; explicit product decision gate | W06/W07 |
| ORS cost increases during experiment | network-free frozen matrices; no production shadow matrix | W05/W06 |
| Aggregate averages hide regressions | mandatory segment reporting and paired distributions | W07 |
| Synthetic evidence is over-generalized | corpus-source/evidence-strength/external-validity statement | W07 |
| Sensitive corpus is committed | synthetic-only Git fixtures, external controlled corpus | W05/W07 |
| Existing user changes are overwritten | path-level diff audit; no destructive reset/checkout | W08 |

## Definition of Done

- [x] Spec and plan approved before implementation.
- [x] Current provider/matrix top-K and no-backfill behavior is characterized and unchanged.
- [x] Production uses shared deterministic pure selectors.
- [x] Funnel counts/drop reasons/payload/stage latency are observable without identity labels.
- [x] Replay emits no funnel and snapshot retry is distinguishable without high cardinality.
- [x] Telemetry overhead satisfies the declared 2% local benchmark gate.
- [x] Deterministic, versioned, network-free replay uses production baseline code.
- [x] B/C/D experiments remain tool-only and provider-score semantics are honest.
- [x] Report covers paired utility, coverage, travel, feasibility, payload, latency/work, stability,
  segments, corpus metadata, evidence strength, uncertainty, and external validity without circular
  recall.
- [x] Decision gate records retain/more-evidence/amend plus all required product inputs.
- [x] No runtime diversity, backfill, cap/API/schema change or migration is introduced.
- [x] Full tests/formatting pass and review has no unresolved critical finding.
- [x] No commit, push, merge, or PR occurs without explicit instruction.

## Verification Evidence

Implemented on .NET 10. Targeted tests passed (123 Application, 5 replay, 10 routing); full solution
passed with 1,766 passed, 159 environment-dependent skipped, and 0 failed. Formatting verification
passed. The synthetic replay and overhead evidence are recorded in
`docs/analysis/uc10-candidate-pool-replay-decision.md`; reproducible dashboard queries are in
`docs/operations/uc10-candidate-pool-funnel-observability.md`.
