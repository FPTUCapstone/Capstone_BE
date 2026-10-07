# UC-10 Optional Route Optimization Benchmark — 2026-10-07

## Environment

- OS: Microsoft Windows NT 10.0.26200.0
- Logical processor count: 12
- Runtime: .NET 10.0.0
- Configuration: Release, 20 measured iterations after warmup
- Runner: `tools/benchmarks/optional-route-optimization`

## Results

| Candidates | Mandatory | Disabled p95 | Enabled p95 | Delta p95 | Ratio | Disabled travel | Enabled travel | Travel saved |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 10 | 0 | 0.07 ms | 2.45 ms | 2.38 ms | 35.96x | 191 min | 104 min | 87 min |
| 10 | 1 | 0.05 ms | 1.29 ms | 1.24 ms | 26.52x | 191 min | 104 min | 87 min |
| 10 | 3 | 0.20 ms | 5.12 ms | 4.92 ms | 25.45x | 191 min | 104 min | 87 min |
| 10 | 6 | 16.19 ms | 27.86 ms | 11.67 ms | 1.72x | 143 min | 91 min | 52 min |
| 20 | 0 | 0.06 ms | 4.22 ms | 4.16 ms | 76.12x | 185 min | 121 min | 64 min |
| 20 | 1 | 0.04 ms | 4.07 ms | 4.03 ms | 107.03x | 185 min | 121 min | 64 min |
| 20 | 3 | 0.16 ms | 11.04 ms | 10.89 ms | 69.82x | 201 min | 121 min | 80 min |
| 20 | 6 | 7.49 ms | 10.31 ms | 2.82 ms | 1.38x | 161 min | 142 min | 19 min |
| 40 | 0 | 0.06 ms | 2.16 ms | 2.11 ms | 38.13x | 170 min | 118 min | 52 min |
| 40 | 1 | 0.04 ms | 2.02 ms | 1.98 ms | 46.43x | 170 min | 118 min | 52 min |
| 40 | 3 | 0.19 ms | 9.49 ms | 9.30 ms | 50.73x | 175 min | 127 min | 48 min |
| 40 | 6 | 11.50 ms | 22.90 ms | 11.40 ms | 1.99x | 155 min | 127 min | 28 min |

## Gate assessment

- Absolute overhead gate (`enabled p95 - disabled p95 <= 100 ms`): passed in every row.
- Travel-quality signal: improved in every row of this synthetic corpus.
- Relative gate (`enabled p95 <= 2x disabled p95`) applies only when disabled p95 is at least
  5 ms: passed in all three applicable rows (10/6, 20/6, and 40/6).
- Ratios in the remaining nine rows are reported as non-gating context because their disabled p95
  is below 5 ms.

The approved performance gate passes this run. This result remains generator-only synthetic
evidence and must not be presented as a production end-to-end latency claim.
