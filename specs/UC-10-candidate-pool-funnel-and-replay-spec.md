# UC-10 Candidate Pool Funnel and Offline Replay Specification

## Status

Implemented — measurement release complete. The exploratory synthetic decision is **collect more
evidence**; this specification does not authorize diversity selection, backfill, or cap changes in
production.

## Problem

UC-10 intentionally bounds two candidate sets:

- `PoiRankingOrchestrator` selects at most 60 optional POIs for the provider pool;
- `CreateSchedulingRequestCommandHandler` selects at most 40 total matrix candidates, including
  mandatory POIs.

TM-215 explicitly requires a frozen provider pool, ignores candidates outside that pool, and
forbids Phase-2 backfill. These limits protect provider payload, ORS matrix cost, latency, and
determinism. They are not an implementation defect.

However, the repository currently lacks end-to-end evidence showing how many eligible candidates
are lost at each stage, whether invalidation leaves useful matrix capacity empty, or whether a
different deterministic selection strategy would improve final-plan utility/coverage enough to
justify changing TM-215. Raising/removing caps without this evidence would trade bounded cost for
unquantified quality.

## Goals

1. Instrument the eligible-to-plan candidate funnel with privacy-safe, low-cardinality telemetry.
2. Make provider-pool and matrix-pool selection reusable as pure deterministic components without
   changing their current output.
3. Build an offline replay harness that runs the exact production baseline and experimental
   strategies against frozen, de-identified inputs and route matrices.
4. Compare paired desirability utility, category/geographic coverage, travel, feasible rate, matrix
   payload, CPU, and latency using one reproducible report without treating any strategy as ground
   truth.
5. Preserve current caps, ranking ownership, no-backfill, at most one provider call per preparation
   attempt, and API behavior during the measurement release.
6. Produce an explicit decision record that either retains the current policy or supplies the
   product thresholds and policy parameters required for a separate TM-215 amendment.

## Non-goals

- No production diversity/category/geographic quota or hybrid selector.
- No production overflow backfill.
- No provider-pool or matrix-pool cap increase/removal.
- No second AI ranking call within one preparation/backfill decision, partial provider call, or
  per-candidate provider call. A separately started snapshot-retry attempt retains existing behavior.
- No ORS call for the full catalog and no production shadow matrix larger than the configured cap.
- No change to ranking weights, hard feasibility rules, scheduler algorithm, request/response DTOs,
  HTTP status, database schema, or EF mappings.
- No persistence of per-request candidate IDs, scores, coordinates, preferences, or route matrices
  for analytics.
- No declaration that the current caps harm outcomes until the replay/online evidence supports it.

## Compatibility Freeze for This Release

The following TM-215 behavior remains authoritative:

1. `MaxProviderCandidates` defaults to 60.
2. `EffectiveMaxMatrixCandidates` defaults to 40 and includes mandatory POIs.
3. Provider-pool order remains:
   `TripMateBaseScore DESC`, scenic DESC, photo DESC, exploration distance ASC, estimated cost ASC,
   POI ID ASC.
4. Matrix-pool order remains:
   `EffectiveDesirabilityScore DESC`, frozen scenic DESC, frozen photo DESC, frozen exploration
   distance ASC, frozen estimated cost ASC, POI ID ASC.
5. Mandatory POIs are retained before optional matrix capacity is allocated.
6. Candidates outside `ProviderPoolPoiIds` are ignored.
7. Candidates invalidated after the frozen pool is built are dropped and are not backfilled.
8. Phase 2/final revalidation itself never calls the AI provider or recomputes personalization. If
   the existing snapshot workflow starts a new preparation attempt, its existing provider-call
   behavior remains unchanged in this measurement release.
9. The route matrix remains capped and requested once per fresh generation attempt.

Existing no-backfill and top-K tests are normative regression tests for this measurement release.

## Funnel Model

### 1. Preparation stages and finalize observation

For each fresh generation attempt, record these aggregate counts:

| Stage | Definition |
|---|---|
| `eligible_optional` | Active, planning-ready, in-radius, non-mandatory candidates before provider cap. |
| `provider_pool` | Frozen optional candidates selected for ranking/provider, at most 60. |
| `matrix_optional_capacity` | `EffectiveMaxMatrixCandidates - mandatory_count`, never below zero after existing validation. |
| `matrix_optional` | Prepared provider-pool candidates selected into the route matrix before finalization. |
| `planned_optional_visits` | Optional candidates persisted as visit items in the successful plan. |
| `planned_rest_pois` | Non-mandatory POIs used only as qualified rest items. |

At `FinalizeAsync`, separately record `finalize_valid_frozen_pool`: the number of frozen provider-
pool IDs that still satisfy current Active, planning-ready, and exact-radius eligibility in the
authoritative data already loaded for snapshot comparison. This is a revalidation observation, not
a stage that feeds the already-built matrix or plan.

Derive these counts:

- `provider_cap_dropped = eligible_optional - provider_pool`;
- `matrix_cap_dropped = max(0, provider_pool - matrix_optional)`;
- `finalize_invalidated = provider_pool - finalize_valid_frozen_pool`;
- `matrix_unused_for_visit = max(0, matrix_optional - planned_optional_visits)`.

`matrix_unused_for_visit` is not labeled a failure: it can include rest-only candidates and POIs
rejected by legitimate hours, budget, horizon, or route constraints.

Counts are per fresh preparation attempt, not per idempotent HTTP replay. Idempotent replay emits no
new funnel. Snapshot regeneration emits a separate attempt with an
`attempt=initial|snapshot_retry` dimension; the dimension has only these two values.

`finalize_valid_frozen_pool` must be computed only for observation. `PreparedGeneration` may carry
the frozen provider-pool IDs needed for comparison, and the calculation must reuse the
authoritative POIs already loaded by `FinalizeAsync`; it must not introduce a per-candidate query.
Missing IDs count only as generically invalidated because the existing Active/bounded query cannot
reliably distinguish inactive, deleted, or moved-out-of-scope reasons.

Most importantly, the observation must not remove a candidate, backfill capacity, rebuild a matrix,
call AI/ORS, or affect the snapshot hash decision. The existing all-or-nothing behavior remains:
matching hash continues; mismatching hash retries the whole attempt when allowed or returns the
existing terminal failure. Tests must prove that changing the observed count cannot change this
decision.

### 2. Online measurements

Use `System.Diagnostics.Metrics` plus one aggregate `ActivitySource` event. Required measurements:

| Measurement | Type | Approved dimensions |
|---|---|---|
| stage candidate count | histogram | `stage`, `attempt`, `outcome` |
| drop count | histogram | `reason=provider_cap|matrix_cap|finalize_invalidation`, `attempt` |
| route matrix point/element count | histogram | `transport_mode`, `attempt` |
| final optional visit/rest count | histogram | `kind=visit|rest`, `attempt`, `outcome` |
| pipeline stage duration | histogram | `stage=ranking|selection|matrix|generation|finalize_observation`, `outcome` |

The closed `outcome` set is exactly `success`, `infeasible`, `routing_failure`, `cancelled`,
`snapshot_mismatch_retryable`, `snapshot_mismatch_terminal`, `lost_ownership`, and
`unexpected_failure`. Transactional outcomes must be emitted only after the transaction commits;
a callback that completes but fails during commit emits `unexpected_failure`, never `success`.
Provider outcome and fallback remain separate existing telemetry rather than overloading generation
outcome. Names must be constants shared by emission and tests.

Do not emit a per-request conversion-ratio histogram. Dashboard and replay reports derive ratios
from aggregated stage-count sums over the same time window and dimensions, and must display the
underlying numerator/denominator beside the ratio.

No metric, activity, or routine log may contain user ID, request/idempotency key, POI ID/name,
category/tag text, coordinates, raw score, raw preference, route sequence, provider payload, or
exception text as a dimension. Existing provider outcome telemetry remains unchanged.

### 3. Count consistency

For every completed attempt with a candidate funnel:

```text
0 <= provider_pool <= min(eligible_optional, MaxProviderCandidates)
0 <= matrix_optional <= min(provider_pool, matrix_optional_capacity)
0 <= planned_optional_visits <= matrix_optional
0 <= finalize_valid_frozen_pool <= provider_pool        (when finalize observation ran)
0 <= route_matrix_points <= EffectiveMaxMatrixCandidates + 2
```

There is intentionally no inequality between `matrix_optional` and
`finalize_valid_frozen_pool`: the matrix was built earlier, and a later invalidation causes an
all-or-nothing snapshot retry rather than mutation of that matrix. The final route-point bound
includes start and end. Telemetry inconsistencies are implementation defects and must be detectable
in tests; metrics must not throw or alter the user result.

## Production Selector Extraction

Extract the existing selection algorithms into internal pure components owned by Application:

- `ProviderPoolSelector`: accepts already-scored optional candidates and returns the frozen top-K;
- `MatrixCandidateSelector`: retains mandatory POIs, applies remaining capacity, and returns the
  current frozen-order optional top-K.

The orchestrator and handler must call these components. The offline replay harness must reference
the same baseline selector code; it must not copy/reimplement production ordering.

Both selectors must:

- produce a strict total order ending in POI ID;
- be independent of input enumeration, dictionary/hash order, current culture, and host clock;
- reject duplicate candidate IDs rather than silently deduplicating;
- never query a database or call AI/ORS;
- expose selection/drop counts without returning sensitive diagnostic labels.

Characterization tests must prove byte-for-byte ID-order equivalence with the pre-extraction
behavior before instrumentation/replay work continues.

## Offline Replay Contract

### 1. Input dataset

The replay tool consumes versioned JSON Lines files outside normal API execution. Each scenario
contains:

- opaque scenario and candidate surrogate IDs unrelated to production database IDs;
- request constraints needed by selection/generation;
- candidate ranking fields, category surrogate, coarse geographic-cell surrogate, feasibility
  fields, and frozen provider score when legitimately available;
- a complete directional route-duration matrix for the scenario;
- expected input-schema version and options/cap snapshot.

The checked-in repository may contain synthetic fixtures only. Any production-derived corpus must
be exported through an approved process, de-identified before use, stored outside Git, and contain
no user identity, raw preference/tag text, exact coordinates, POI name, API key, or provider raw
payload. The tool rejects unknown schema versions, duplicate IDs, missing matrix cells, non-square
matrices, and inconsistent option metadata.

### 2. Baseline and experimental strategies

The replay always runs:

- **A — Current baseline:** exact production 60/40 selection and no backfill.

It may run these offline-only strategies:

- **B — Frozen overflow backfill:** keep current provider pool/order, freeze the matrix overflow,
  and fill capacity only when a selected optional becomes authoritatively invalid; no second AI call.
- **C — Desirability plus diversity:** retain a configurable top-desirability core, then fill reserved
  experimental slots by deterministic marginal category/geographic coverage, followed by frozen
  overflow backfill.
- **D — Cap sensitivity:** larger provider/matrix caps only in offline replay to estimate an upper
  bound; never send the larger matrix to production ORS.

Strategies B–D are experiments, not production requirements. Their parameters (core size, reserved
slots, geographic-cell definition, category weighting, backfill order, sensitivity caps) are
recorded in the replay manifest and output. No unrecorded default may enter a decision report.

Provider-enabled comparison uses only legitimately frozen provider scores. For candidates with no
provider score, the report must use a separately labeled Base-only track; it must not fabricate an
AI score or compare mixed score semantics as if they were equivalent.

### 3. Metrics and definitions

For every strategy, report distributions and paired deltas versus A:

- **Base utility:** sum and mean `TripMateBaseScore` of planned optional visits;
- **effective utility:** sum and mean `EffectiveDesirabilityScore` only on the comparable frozen-
  provider-score subset;
- **coverage:** distinct category-surrogate count and distinct coarse geo-cell count;
- **travel:** directional matrix travel minutes for the final plan, including start/end;
- **feasible rate:** scenarios producing a valid plan under unchanged hard constraints;
- **payload:** provider candidates, matrix points, and matrix elements (`points²`);
- **work/latency:** selector/generator elapsed p50/p95/p99, allocations where available, and
  production online ORS latency by payload bucket;
- **stability:** percentage of scenarios whose selected visit set/order changes.

No strategy output is ground truth, so the decision must not use a metric named `plan recall`.
Strategy D is a cap-sensitivity comparator, not an oracle. If a later analysis introduces an
explicit offline search oracle, it must predeclare the candidate universe, objectives, cap, and
evaluation budget and name the derived metric `oracle-set coverage`; that proxy is reported
separately and cannot be treated as real-world relevance ground truth.

The report includes sample size, excluded/invalid scenario counts, corpus version/hash, code commit,
runtime/OS/CPU, options, random seed if corpus generation uses one, and confidence intervals or raw
paired distributions. It must not reduce the decision to an unqualified global average; at minimum
segment by eligible-count bucket, mandatory-count bucket, transport mode, and density/radius bucket.

### 4. Evidence strength and external validity

The analysis manifest must state whether its purpose is correctness demonstration, exploratory
quality comparison, or inferential comparison. A full Cartesian product of all four segmentation
dimensions is not required, but every published segment must show its scenario count and paired-
observation count.

- Inferential claims require a pre-analysis power calculation based on a declared minimum material
  paired effect and pilot variance. The report records assumptions, target power, achieved sample,
  and missing/excluded cases.
- When that calculation or sample cannot be supplied, results are explicitly `exploratory`. Report
  paired raw distributions and effect sizes; do not turn a wide/unstable interval into a claim of
  no effect.
- Sparse segments may identify risks or motivate more data, but must not independently justify a
  production-wide quality claim.
- Lack of inferential power does not automatically force `collect more evidence`: a capstone may
  make a clearly labeled exploratory recommendation or propose a guarded follow-up experiment, but
  it may not claim statistically established production uplift.

The decision document must state whether the corpus is synthetic, production-derived, or mixed.
For a synthetic-only corpus it must list modeled distributions and missing real-world properties
and include this limitation: conclusions generalize only to the tested synthetic distribution.
Synthetic golden cases can prove deterministic correctness and expose trade-offs; they cannot alone
prove production outcome improvement.

### 5. Determinism

Given the same corpus, manifest, options, and code version, replay outputs must be byte-identical
apart from explicitly separated wall-clock measurements and report-generation timestamp. Strategy
ordering ends in surrogate candidate ID. Parallel execution may improve runtime but may not change
results or output order.

## Decision Gate

Completion of measurement does not automatically enable a new selector. The evidence report must
end in one of these decisions:

1. **Retain TM-215:** measured gain is not material or cost/risk is unacceptable.
2. **Collect more evidence:** corpus/traffic coverage or confidence is insufficient.
3. **Propose TM-215 amendment:** a named strategy has material paired improvement within approved
   cost/latency/regression limits.

Before decision 3 can be implemented, the developer/product owner must explicitly approve and
record:

- minimum paired utility, coverage, feasible-rate, and/or travel improvement considered material;
- maximum acceptable utility regression by segment;
- maximum provider payload, matrix points/elements, ORS p95/p99, and application CPU/allocation;
- diversity core/slot counts, category policy, and geographic-cell granularity;
- exact overflow/backfill eligibility and ordering;
- rollout/feature-flag and rollback policy.

These are product-quality and operating-cost decisions and are intentionally not inferred here.
Decision 3 requires a new or amended specification that explicitly supersedes TM-215 FR-215-008,
FR-215-012, and FR-215-016 plus a new implementation plan. Until that document is approved, the
production no-backfill tests must continue to pass.

## Acceptance Criteria

1. Provider and matrix selector extraction produces exactly the same IDs/order as the current code
   for normal, boundary, shuffled-input, null tie-break, mandatory, and over-cap fixtures.
2. More than 60 eligible optionals still yields at most 60 provider candidates; more than remaining
   matrix capacity still uses current top-K; outside-pool/invalidated candidates are not backfilled.
3. One fresh successful request emits internally consistent prepared-stage counts and drop reasons,
   plus the separate finalize observation when reached; replay emits no duplicate attempt.
4. Infeasible, provider-fallback, routing-failure, snapshot-retry, and cancellation cases emit only
   the stages/observation they actually reached. Finalize validity counts never alter the existing
   all-or-nothing hash match, retry, or terminal-failure decision.
5. Metric-listener/activity tests prove only approved low-cardinality dimensions exist and contain
   no user/POI identity, coordinates, raw scores/preferences, or payload content.
6. Instrumentation cannot change selection/order, call AI/ORS again, throw into the request path, or
   increase the production matrix beyond `EffectiveMaxMatrixCandidates + 2` route points.
7. The replay tool validates schema/input integrity and uses the production selector for strategy A.
8. Replaying the same corpus twice produces identical non-timing output; shuffled scenario/candidate
   input produces identical ordered results.
9. Synthetic fixtures prove B backfills only from frozen valid overflow with no fabricated score or
   second provider call, while C and D remain offline-only.
10. The report compares A against every enabled experiment on Base/effective utility, coverage,
    travel, feasible rate, payload, work/latency, and stability with declared definitions; no
    strategy is mislabeled as ground truth or used to create circular recall.
11. A dashboard or reproducible dashboard definition displays funnel counts/ratios, drop reasons,
    payload size, stage latency, outcome, and snapshot-retry split.
12. The evidence report records a decision from the gate and every required product value for a
    future amendment, or explicitly records why the values remain undecided. It reports per-segment
    sample counts, evidence strength, corpus source, and external-validity limits.
13. All existing TM-215, scheduling, SQL integration, and OpenAPI tests remain green; no schema,
    public API, production diversity, backfill, or cap change is introduced.

## Operational and Privacy Constraints

- Metrics are aggregate and bounded; do not label by request/user/POI/category/geo cell.
- Do not log per-candidate drop decisions in production.
- Do not run cap-sensitivity matrices against production ORS traffic.
- If no approved production export exists, use synthetic data and state that fact; production data
  is optional, never an implied prerequisite for completing the measurement release.
- Treat production-derived replay corpora as controlled data even after de-identification; define
  owner, retention, access, and deletion outside Git.
- Dashboard alert thresholds remain environment-owned. Record them when deployed; do not invent
  them as unit-test assertions.
- Instrumentation overhead must be measured. On the same representative load, enabled telemetry
  may add no more than 2% p95 application processing time and 2% managed allocation before ORS/AI
  network time; otherwise optimize or disable the detailed activity while retaining counters.

## Alternatives Not Chosen

- **Remove/increase caps immediately:** changes provider/ORS cost and latency without evidence.
- **Send the full catalog to ORS:** makes matrix elements grow quadratically and abandons the
  bounded-provider contract.
- **Backfill from newly discovered/outside-pool POIs:** lacks frozen provider ranking and would
  violate TM-215 ownership unless a new scoring contract is approved.
- **Call AI again after invalidation:** adds cost/latency and breaks the frozen-snapshot rule.
- **Instrument per POI/user:** creates privacy and metric-cardinality risks.
- **Implement diversity before replay:** chooses weights, geo granularity, and quality trade-offs
  without product evidence.

## Delivery Constraints

- Follow red-green-refactor and the measurement-first plan.
- Preserve current TM-215 normative tests, especially no-backfill.
- Keep selector/telemetry/replay code out of Domain and keep provider/ORS clients out of the replay
  strategy core.
- Do not commit any production-derived replay dataset.
- Preserve unrelated working-tree changes.
- Do not add a database migration.
- Do not commit, push, merge, or open a pull request without explicit developer instruction.
