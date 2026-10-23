# UC-10 candidate-pool funnel observability

## Runtime contract

Meter and activity source: `TripMate.Scheduling.CandidatePoolFunnel`.

| Instrument | Dimensions |
|---|---|
| `tripmate.scheduling.funnel.attempts` | `attempt`, `outcome` |
| `tripmate.scheduling.funnel.candidates` | `stage`, `attempt`, `outcome` |
| `tripmate.scheduling.funnel.dropped` | `reason`, `attempt` |
| `tripmate.scheduling.funnel.matrix.points/elements` | `transport_mode`, `attempt` |
| `tripmate.scheduling.funnel.plan.optional` | `kind`, `attempt`, `outcome` |
| `tripmate.scheduling.funnel.stage.duration` | `stage`, `outcome` |

Allowed attempt values are `initial` and `snapshot_retry`. Outcomes are exactly `success`,
`infeasible`, `routing_failure`, `cancelled`, `snapshot_mismatch_retryable`,
`snapshot_mismatch_terminal`, `lost_ownership`, and `unexpected_failure`. Transactional outcomes are
recorded only after commit succeeds; callback, persistence, or commit exceptions are recorded as
`unexpected_failure`. No user/request/POI/category/coordinate/score or exception text is emitted.
Idempotent replay emits no new attempt.

`finalize_valid_frozen_pool` is an observation only. A lower count must never be used to backfill,
change the prepared matrix, or choose retry. The authoritative snapshot hash remains the only
data-change gate.

## Reproducible panels

The following PromQL assumes the OpenTelemetry collector's normal dot-to-underscore Prometheus
name conversion. Confirm exported names in the target environment.

```promql
# Attempts by outcome and initial/retry
sum by (attempt, outcome) (rate(tripmate_scheduling_funnel_attempts_total[5m]))

# Aggregate candidate counts by stage (show _count beside _sum)
sum by (stage, attempt, outcome) (rate(tripmate_scheduling_funnel_candidates_sum[5m]))
sum by (stage, attempt, outcome) (rate(tripmate_scheduling_funnel_candidates_count[5m]))

# Drops by reason
sum by (reason, attempt) (rate(tripmate_scheduling_funnel_dropped_sum[5m]))

# Matrix points/elements by mode and attempt
histogram_quantile(0.95, sum by (le, transport_mode, attempt)
  (rate(tripmate_scheduling_funnel_matrix_points_bucket[5m])))
histogram_quantile(0.95, sum by (le, transport_mode, attempt)
  (rate(tripmate_scheduling_funnel_matrix_elements_bucket[5m])))

# Stage latency p95; repeat with 0.50 and 0.99
histogram_quantile(0.95, sum by (le, stage, outcome)
  (rate(tripmate_scheduling_funnel_stage_duration_bucket[5m])))
```

Conversion ratios are report-time divisions of matching count sums, never averages of per-request
ratios. Each ratio panel must show numerator and denominator:

```promql
sum(rate(tripmate_scheduling_funnel_candidates_sum{stage="provider_pool"}[5m]))
/
sum(rate(tripmate_scheduling_funnel_candidates_sum{stage="eligible_optional"}[5m]))
```

Use the same time window and matching `attempt`/`outcome` filters on both sides. Do not aggregate a
histogram ratio across incompatible segments. Environment owners set alert thresholds after a
baseline window; this implementation intentionally invents none.

## Verification

Run the listener/privacy tests and local overhead guard:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --no-restore --filter FullyQualifiedName~CandidatePoolFunnel
dotnet run --project tools/benchmarks/candidate-pool-funnel/CandidatePoolFunnelBenchmark.csproj --no-restore -c Release
```

Investigate missing stages by outcome before interpreting a funnel: early infeasible, routing
failure, cancellation, and unexpected-failure attempts truthfully emit only stages they reached.
