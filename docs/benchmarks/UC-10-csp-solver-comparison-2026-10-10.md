# UC-10 CSP Solver Comparison — 2026-10-10

## Environment and corpus

- Commit: `21807a2`
- OS: Microsoft Windows NT 10.0.26200.0; logical processors: 12; runtime: .NET 10.0.0
- Runner: `tools/benchmarks/solver-comparison`, Release, 1 warmup + 3 measured iterations per mode
- CSP options: MaxNodes 200000, TimeLimitMilliseconds 5000, MaxStops 10, MaxOptionalDomainSize 20
- Corpus: **synthetic only**. 7 named `OptionalRouteOptimizationScenarios` fixtures plus `CreateSyntheticCorpusScenario` for candidates {10, 20, 40} × mandatory {0, 1, 3, 6} × seeds 1–25; sample size 307.
- Excluded (a solver produced no plan): 0
- Comparison rule: the product rule of `ScheduleGlobalComparator` (optional inclusion in canonical rank order, then matrix travel, duration, end time, visit IDs).

## Quality: CSP vs Heuristic under the product rule

| Segment | n | CSP better | Equal | CSP worse | Avg optional (H / CSP) | Avg travel min (H / CSP) |
|---|---:|---:|---:|---:|---:|---:|
| named | 7 | 0 | 6 | 1 | 2.71 / 2.71 | 48.3 / 48.3 |
| c10-m0 | 25 | 22 | 3 | 0 | 10.00 / 10.00 | 139.1 / 126.8 |
| c10-m1 | 25 | 22 | 3 | 0 | 9.00 / 9.00 | 139.1 / 126.8 |
| c10-m3 | 25 | 18 | 7 | 0 | 7.00 / 7.00 | 133.2 / 126.8 |
| c10-m6 | 25 | 19 | 6 | 0 | 4.00 / 4.00 | 133.2 / 126.8 |
| c20-m0 | 25 | 2 | 6 | 17 | 10.56 / 10.40 | 142.6 / 130.3 |
| c20-m1 | 25 | 2 | 6 | 17 | 9.56 / 9.40 | 142.6 / 131.8 |
| c20-m3 | 25 | 1 | 3 | 21 | 7.72 / 7.40 | 141.3 / 130.1 |
| c20-m6 | 25 | 5 | 3 | 17 | 4.64 / 4.52 | 140.5 / 134.4 |
| c40-m0 | 25 | 0 | 6 | 19 | 10.84 / 10.48 | 140.3 / 130.2 |
| c40-m1 | 25 | 0 | 6 | 19 | 9.84 / 9.48 | 140.3 / 131.7 |
| c40-m3 | 25 | 0 | 2 | 23 | 7.92 / 7.48 | 136.4 / 135.6 |
| c40-m6 | 25 | 3 | 1 | 21 | 4.84 / 4.64 | 137.5 / 136.5 |

## Latency (generator only)

| Segment | Disabled p95 | Heuristic p95 | CSP p50 | CSP p95 | CSP max | CSP added p95 (gate ≤ 300 ms) | Node limit hit | Time limit hit | Fell back to heuristic |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| named | 0.10 ms | 0.55 ms | 0.08 ms | 0.13 ms | 0.26 ms | 0.03 ms ✅ | 0 | 0 | 0 |
| c10-m0 | 0.07 ms | 7.81 ms | 7.72 ms | 32.64 ms | 120.48 ms | 32.57 ms ✅ | 0 | 0 | 0 |
| c10-m1 | 0.02 ms | 3.29 ms | 9.05 ms | 28.27 ms | 30.14 ms | 28.25 ms ✅ | 0 | 0 | 0 |
| c10-m3 | 0.05 ms | 4.84 ms | 4.79 ms | 14.75 ms | 24.56 ms | 14.70 ms ✅ | 0 | 0 | 0 |
| c10-m6 | 6.38 ms | 14.73 ms | 4.39 ms | 15.84 ms | 18.58 ms | 9.46 ms ✅ | 0 | 0 | 0 |
| c20-m0 | 0.02 ms | 2.64 ms | 97.83 ms | 221.38 ms | 237.72 ms | 221.36 ms ✅ | 7 | 0 | 0 |
| c20-m1 | 0.02 ms | 2.09 ms | 96.80 ms | 232.60 ms | 279.78 ms | 232.58 ms ✅ | 8 | 0 | 0 |
| c20-m3 | 0.05 ms | 5.25 ms | 79.37 ms | 183.32 ms | 219.21 ms | 183.28 ms ✅ | 8 | 0 | 0 |
| c20-m6 | 20.99 ms | 11.05 ms | 0.52 ms | 312.60 ms | 339.36 ms | 291.61 ms ✅ | 6 | 0 | 0 |
| c40-m0 | 0.03 ms | 4.16 ms | 20.09 ms | 208.99 ms | 248.12 ms | 208.96 ms ✅ | 7 | 0 | 0 |
| c40-m1 | 0.03 ms | 5.00 ms | 43.24 ms | 263.84 ms | 319.10 ms | 263.81 ms ✅ | 9 | 0 | 0 |
| c40-m3 | 0.10 ms | 8.08 ms | 108.94 ms | 229.26 ms | 276.05 ms | 229.15 ms ✅ | 10 | 0 | 0 |
| c40-m6 | 17.01 ms | 23.58 ms | 0.22 ms | 409.75 ms | 532.06 ms | 392.74 ms ❌ | 8 | 0 | 0 |

## Gate status

- Quality (100% better or equal): **failed** in 155 scenarios
- Scenarios where the CSP is worse: zigzag, c20-m0-s2, c20-m0-s3, c20-m0-s4, c20-m0-s5, c20-m0-s6, c20-m0-s8, c20-m0-s9, c20-m0-s10, c20-m0-s12, c20-m0-s15, c20-m0-s16, c20-m0-s17, c20-m0-s18, c20-m0-s21, c20-m0-s22, c20-m0-s23, c20-m0-s24, c20-m1-s2, c20-m1-s3, c20-m1-s4, c20-m1-s5, c20-m1-s6, c20-m1-s8, c20-m1-s9, c20-m1-s10, c20-m1-s12, c20-m1-s15, c20-m1-s16, c20-m1-s17, c20-m1-s18, c20-m1-s21, c20-m1-s22, c20-m1-s23, c20-m1-s24, c20-m3-s1, c20-m3-s2, c20-m3-s3, c20-m3-s4, c20-m3-s5, …
- Time limit reached in any run: no

## Limitations

- Synthetic, generator-only evidence: no OpenRouteService, ranking, or database latency is included,
  and conclusions generalize only to the tested synthetic distribution.
- Per-scenario raw results: `UC-10-csp-solver-comparison-2026-10-10.csv`.
