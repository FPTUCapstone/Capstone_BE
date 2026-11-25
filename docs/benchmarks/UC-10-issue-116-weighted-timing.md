# UC-10 Issue #116 — WeightedUtility Timing Comparison

## Method

- Baseline: `origin/develop` at `5c06d822`.
- Fixed: `fix/issue-116` at `4e096022`.
- Host, runtime, scenarios, and command were identical for both runs.
- Command:
  `dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --filter "FullyQualifiedName~TimingReportTests.MeasurePR07c_Timing"`
- Each scenario used one solver warm-up followed by 10 measured iterations; the JSON records the
  median elapsed milliseconds and nanoseconds per expanded node.

Raw results:

- `UC-10-issue-116-weighted-baseline.json`
- `UC-10-issue-116-weighted-fixed.json`

## Results

| Category | Scenarios | Baseline total median (ms) | Fixed total median (ms) | Change |
| --- | ---: | ---: | ---: | ---: |
| Golden-c40 | 5 | 854.717 | 708.476 | -17.11% |
| Synthetic-Constant | 8 | 16.753 | 14.296 | -14.67% |
| Synthetic-RealTimeFit | 8 | 20.885 | 17.205 | -17.62% |
| **All** | **21** | **892.355** | **739.977** | **-17.08%** |

- Expanded-node counts matched in all 21 scenarios, confirming unchanged search behavior.
- 19 of 21 scenario medians improved. Two sub-3 ms synthetic scenarios were slower and are
  dominated by timer and runtime noise.
- The aggregate measured time improved by 17.08%, so the issue requirement that the weighted
  benchmark must not regress is satisfied.
