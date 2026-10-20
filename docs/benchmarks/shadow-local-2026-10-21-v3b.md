# Shadow Benchmark v3b

- Run date: 2026-10-21
- Build: Release
- OS: Microsoft Windows 10.0.26200
- Runtime: .NET 10.0.0
- Machine: Intel64 Family 6 Model 141 Stepping 1, GenuineIntel
- Logical processors: 12
- Warm-up iterations: 1
- Scenarios: 21
- Personas: 5
- Modes: 2
- Total cases: 210
- Failed cases: 0

Runtime unit is milliseconds; p50/p95 use sorted sample index floor(n × p), clamped to n - 1.

Midday outdoor means an unsheltered outdoor visit overlapping [11:00, 14:30) local time; rate denominator is all visits.

Diversity is the count of distinct `PoiCategoryNames.GetDiversityGroup` values.

## Aggregate v3b

| Mode | Cases | Feasible | Avg stops | Avg total travel | Avg diversity groups | Avg midday rate | Runtime samples | p50 (ms) | p95 (ms) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| Lexicographic | 105 | 105 | 8.94 | 116.61 | 3.94 | 39.22% | 315 | 66.09 | 171.54 |
| WeightedUtility | 105 | 105 | 3.96 | 65.40 | 2.72 | 20.59% | 315 | 33.38 | 147.26 |

## v3a → v3b comparison

### Behavior and data

- v3b keeps the reviewed 20 Golden scenarios plus one Da Nang scenario, but extends that city fixture from 16 to 19 POIs using three nature records from the approved planning seed. No second coastal POI was invented because the approved seed has none.
- v3a travel omitted the start and return legs; v3b reports authoritative `EvaluatedItinerarySchedule.TotalMatrixTravelMinutes`. v3a counted canonical categories and every midday visit; v3b reports diversity groups and only unsheltered outdoor visits overlapping midday. Those columns are semantic corrections, not like-for-like deltas.

The baseline 16-POI outcomes are locked by a deterministic regression test. The table below isolates the effect of adding the three approved nature POIs while using the same execution pipeline and metric definitions on both sides.

| Persona | Mode | Stops 16 -> 19 | Travel 16 -> 19 | Groups 16 -> 19 | Midday 16 -> 19 | Objective 16 -> 19 | Route IDs 16 -> 19 |
|---|---|---:|---:|---:|---:|---:|---|
| ActiveExplorerFast | Lexicographic | 5 -> 5 | 50 -> 50 | 2 -> 2 | 2 -> 2 | - -> - | 5,12,13,1,8 -> 5,12,13,1,8 |
| ActiveExplorerFast | WeightedUtility | 5 -> 5 | 50 -> 50 | 2 -> 2 | 2 -> 2 | 21040 -> 21040 | 5,12,13,1,8 -> 5,12,13,1,8 |
| CultureEnthusiast | Lexicographic | 5 -> 5 | 58 -> 58 | 1 -> 1 | 0 -> 0 | - -> - | 1,5,11,2,3 -> 1,5,11,2,3 |
| CultureEnthusiast | WeightedUtility | 6 -> 6 | 31 -> 31 | 1 -> 1 | 0 -> 0 | 26841 -> 26841 | 5,7,1,3,2,6 -> 5,7,1,3,2,6 |
| DefaultGeneralist | Lexicographic | 5 -> 5 | 55 -> 55 | 2 -> 2 | 1 -> 1 | - -> - | 1,5,9,11,7 -> 1,5,9,11,7 |
| DefaultGeneralist | WeightedUtility | 5 -> 5 | 35 -> 35 | 3 -> 3 | 0 -> 0 | 10209 -> 10209 | 5,9,3,1,8 -> 5,9,3,1,8 |
| FoodLoverRelaxed | Lexicographic | 6 -> 6 | 62 -> 62 | 2 -> 2 | 2 -> 2 | - -> - | 7,11,5,1,8,10 -> 7,11,5,1,8,10 |
| FoodLoverRelaxed | WeightedUtility | 3 -> 3 | 18 -> 18 | 2 -> 2 | 0 -> 0 | 6453 -> 6453 | 5,8,1 -> 5,8,1 |
| ScenicPhotography | Lexicographic | 5 -> 5 | 55 -> 55 | 3 -> 3 | 0 -> 0 | - -> - | 5,9,11,1,8 -> 5,9,11,1,8 |
| ScenicPhotography | WeightedUtility | 5 -> 5 | 38 -> 38 | 3 -> 3 | 1 -> 1 | 19253 -> 19253 | 8,9,3,1,5 -> 8,9,3,1,5 |

### Runtime

- v3a timed one solve per case and calculated percentiles across different scenarios. v3b performs the declared warm-up and repeated samples for every case, then applies one documented percentile convention. Runtime numbers are therefore not directly comparable.
- This is a local diagnostic benchmark, not an SLA or a production performance claim; OS scheduling, thermal state and background load remain uncontrolled.

## Per-case results

| Scenario | Persona | Mode | Status | Stops | Total travel (min) | Diversity groups | Midday outdoor | Midday rate | Samples | p50 (ms) | p95 (ms) | Model objective |
|---|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| budget-limited-optional-s23 | ActiveExplorerFast | Lexicographic | Feasible | 3 | 52 | 1 | 0/3 | 0.00% | 3 | 2.35 | 2.40 | - |
| budget-limited-optional-s23 | ActiveExplorerFast | WeightedUtility | Feasible | 3 | 37 | 1 | 0/3 | 0.00% | 3 | 17.18 | 17.47 | 14350 |
| budget-limited-optional-s23 | CultureEnthusiast | Lexicographic | Feasible | 3 | 66 | 1 | 0/3 | 0.00% | 3 | 2.25 | 2.44 | - |
| budget-limited-optional-s23 | CultureEnthusiast | WeightedUtility | Feasible | 3 | 42 | 1 | 0/3 | 0.00% | 3 | 17.08 | 18.08 | 12180 |
| budget-limited-optional-s23 | DefaultGeneralist | Lexicographic | Feasible | 3 | 55 | 3 | 0/3 | 0.00% | 3 | 2.10 | 2.43 | - |
| budget-limited-optional-s23 | DefaultGeneralist | WeightedUtility | Feasible | 3 | 33 | 3 | 0/3 | 0.00% | 3 | 17.19 | 17.35 | 5610 |
| budget-limited-optional-s23 | FoodLoverRelaxed | Lexicographic | Feasible | 3 | 55 | 3 | 0/3 | 0.00% | 3 | 2.08 | 2.30 | - |
| budget-limited-optional-s23 | FoodLoverRelaxed | WeightedUtility | Feasible | 2 | 25 | 2 | 0/2 | 0.00% | 3 | 17.37 | 18.76 | 2070 |
| budget-limited-optional-s23 | ScenicPhotography | Lexicographic | Feasible | 3 | 58 | 1 | 0/3 | 0.00% | 3 | 2.06 | 2.31 | - |
| budget-limited-optional-s23 | ScenicPhotography | WeightedUtility | Feasible | 3 | 45 | 2 | 0/3 | 0.00% | 3 | 17.46 | 17.50 | 10560 |
| danang-real-1day | ActiveExplorerFast | Lexicographic | Feasible | 5 | 50 | 2 | 2/5 | 40.00% | 3 | 1.05 | 1.10 | - |
| danang-real-1day | ActiveExplorerFast | WeightedUtility | Feasible | 5 | 50 | 2 | 2/5 | 40.00% | 3 | 23.77 | 24.73 | 21040 |
| danang-real-1day | CultureEnthusiast | Lexicographic | Feasible | 5 | 58 | 1 | 0/5 | 0.00% | 3 | 0.73 | 1.09 | - |
| danang-real-1day | CultureEnthusiast | WeightedUtility | Feasible | 6 | 31 | 1 | 0/6 | 0.00% | 3 | 22.79 | 22.79 | 26841 |
| danang-real-1day | DefaultGeneralist | Lexicographic | Feasible | 5 | 55 | 2 | 1/5 | 20.00% | 3 | 0.98 | 1.42 | - |
| danang-real-1day | DefaultGeneralist | WeightedUtility | Feasible | 5 | 35 | 3 | 0/5 | 0.00% | 3 | 23.68 | 23.86 | 10209 |
| danang-real-1day | FoodLoverRelaxed | Lexicographic | Feasible | 6 | 62 | 2 | 2/6 | 33.33% | 3 | 3.91 | 4.26 | - |
| danang-real-1day | FoodLoverRelaxed | WeightedUtility | Feasible | 3 | 18 | 2 | 0/3 | 0.00% | 3 | 29.77 | 29.92 | 6453 |
| danang-real-1day | ScenicPhotography | Lexicographic | Feasible | 5 | 55 | 3 | 0/5 | 0.00% | 3 | 0.54 | 0.55 | - |
| danang-real-1day | ScenicPhotography | WeightedUtility | Feasible | 5 | 38 | 3 | 1/5 | 20.00% | 3 | 26.01 | 26.15 | 19253 |
| grid-c10-m0-none-s1 | ActiveExplorerFast | Lexicographic | Feasible | 10 | 132 | 5 | 5/10 | 50.00% | 3 | 6.61 | 10.62 | - |
| grid-c10-m0-none-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 3 | 46 | 1 | 0/3 | 0.00% | 3 | 45.70 | 96.27 | 13522 |
| grid-c10-m0-none-s1 | CultureEnthusiast | Lexicographic | Feasible | 10 | 132 | 5 | 5/10 | 50.00% | 3 | 8.63 | 10.01 | - |
| grid-c10-m0-none-s1 | CultureEnthusiast | WeightedUtility | Feasible | 3 | 53 | 3 | 0/3 | 0.00% | 3 | 136.75 | 147.26 | 6250 |
| grid-c10-m0-none-s1 | DefaultGeneralist | Lexicographic | Feasible | 10 | 132 | 5 | 5/10 | 50.00% | 3 | 3.79 | 4.12 | - |
| grid-c10-m0-none-s1 | DefaultGeneralist | WeightedUtility | Feasible | 3 | 35 | 2 | 0/3 | 0.00% | 3 | 15.28 | 15.46 | 4390 |
| grid-c10-m0-none-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 10 | 132 | 5 | 5/10 | 50.00% | 3 | 7.75 | 8.10 | - |
| grid-c10-m0-none-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 2 | 26 | 1 | 0/2 | 0.00% | 3 | 6.98 | 7.24 | 853 |
| grid-c10-m0-none-s1 | ScenicPhotography | Lexicographic | Feasible | 10 | 132 | 5 | 5/10 | 50.00% | 3 | 3.43 | 3.61 | - |
| grid-c10-m0-none-s1 | ScenicPhotography | WeightedUtility | Feasible | 3 | 55 | 2 | 0/3 | 0.00% | 3 | 25.56 | 26.56 | 8900 |
| grid-c10-m1-frequent-s1 | ActiveExplorerFast | Lexicographic | Feasible | 9 | 120 | 5 | 4/9 | 44.44% | 3 | 65.65 | 66.54 | - |
| grid-c10-m1-frequent-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 3 | 59 | 2 | 0/3 | 0.00% | 3 | 21.26 | 21.73 | 10622 |
| grid-c10-m1-frequent-s1 | CultureEnthusiast | Lexicographic | Feasible | 8 | 141 | 4 | 3/8 | 37.50% | 3 | 163.65 | 165.53 | - |
| grid-c10-m1-frequent-s1 | CultureEnthusiast | WeightedUtility | Feasible | 3 | 73 | 2 | 1/3 | 33.33% | 3 | 26.19 | 26.30 | 6098 |
| grid-c10-m1-frequent-s1 | DefaultGeneralist | Lexicographic | Feasible | 9 | 120 | 5 | 4/9 | 44.44% | 3 | 66.04 | 66.58 | - |
| grid-c10-m1-frequent-s1 | DefaultGeneralist | WeightedUtility | Feasible | 3 | 56 | 3 | 0/3 | 0.00% | 3 | 28.79 | 32.07 | 2770 |
| grid-c10-m1-frequent-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 9 | 120 | 5 | 4/9 | 44.44% | 3 | 65.57 | 66.00 | - |
| grid-c10-m1-frequent-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 2 | 47 | 2 | 0/2 | 0.00% | 3 | 5.87 | 6.11 | -2130 |
| grid-c10-m1-frequent-s1 | ScenicPhotography | Lexicographic | Feasible | 9 | 120 | 5 | 4/9 | 44.44% | 3 | 57.76 | 60.08 | - |
| grid-c10-m1-frequent-s1 | ScenicPhotography | WeightedUtility | Feasible | 4 | 88 | 3 | 2/4 | 50.00% | 3 | 25.72 | 25.83 | 3460 |
| grid-c10-m1-none-s1 | ActiveExplorerFast | Lexicographic | Feasible | 10 | 132 | 5 | 5/10 | 50.00% | 3 | 2.56 | 2.67 | - |
| grid-c10-m1-none-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 3 | 59 | 2 | 0/3 | 0.00% | 3 | 25.73 | 25.75 | 10622 |
| grid-c10-m1-none-s1 | CultureEnthusiast | Lexicographic | Feasible | 10 | 132 | 5 | 5/10 | 50.00% | 3 | 6.62 | 6.73 | - |
| grid-c10-m1-none-s1 | CultureEnthusiast | WeightedUtility | Feasible | 3 | 73 | 2 | 1/3 | 33.33% | 3 | 30.97 | 32.73 | 6098 |
| grid-c10-m1-none-s1 | DefaultGeneralist | Lexicographic | Feasible | 10 | 132 | 5 | 5/10 | 50.00% | 3 | 5.47 | 5.87 | - |
| grid-c10-m1-none-s1 | DefaultGeneralist | WeightedUtility | Feasible | 3 | 56 | 3 | 0/3 | 0.00% | 3 | 23.17 | 23.43 | 2770 |
| grid-c10-m1-none-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 10 | 132 | 5 | 5/10 | 50.00% | 3 | 6.31 | 9.63 | - |
| grid-c10-m1-none-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 2 | 47 | 2 | 0/2 | 0.00% | 3 | 5.90 | 6.13 | -2130 |
| grid-c10-m1-none-s1 | ScenicPhotography | Lexicographic | Feasible | 10 | 132 | 5 | 5/10 | 50.00% | 3 | 2.69 | 2.74 | - |
| grid-c10-m1-none-s1 | ScenicPhotography | WeightedUtility | Feasible | 3 | 67 | 3 | 1/3 | 33.33% | 3 | 28.12 | 28.86 | 4640 |
| grid-c10-m3-auto-s1 | ActiveExplorerFast | Lexicographic | Feasible | 10 | 132 | 5 | 4/10 | 40.00% | 3 | 3.75 | 4.04 | - |
| grid-c10-m3-auto-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 4 | 79 | 2 | 1/4 | 25.00% | 3 | 30.62 | 36.24 | 10482 |
| grid-c10-m3-auto-s1 | CultureEnthusiast | Lexicographic | Feasible | 10 | 132 | 5 | 4/10 | 40.00% | 3 | 15.72 | 15.79 | - |
| grid-c10-m3-auto-s1 | CultureEnthusiast | WeightedUtility | Feasible | 4 | 86 | 2 | 1/4 | 25.00% | 3 | 38.63 | 40.76 | 3410 |
| grid-c10-m3-auto-s1 | DefaultGeneralist | Lexicographic | Feasible | 10 | 132 | 5 | 4/10 | 40.00% | 3 | 7.18 | 7.22 | - |
| grid-c10-m3-auto-s1 | DefaultGeneralist | WeightedUtility | Feasible | 3 | 75 | 2 | 1/3 | 33.33% | 3 | 32.65 | 38.87 | -717 |
| grid-c10-m3-auto-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 10 | 132 | 5 | 4/10 | 40.00% | 3 | 20.86 | 21.65 | - |
| grid-c10-m3-auto-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 3 | 75 | 2 | 1/3 | 33.33% | 3 | 17.34 | 18.37 | -7717 |
| grid-c10-m3-auto-s1 | ScenicPhotography | Lexicographic | Feasible | 10 | 132 | 5 | 4/10 | 40.00% | 3 | 8.81 | 10.98 | - |
| grid-c10-m3-auto-s1 | ScenicPhotography | WeightedUtility | Feasible | 5 | 88 | 3 | 2/5 | 40.00% | 3 | 31.98 | 33.25 | 2142 |
| grid-c20-m0-none-s1 | ActiveExplorerFast | Lexicographic | Feasible | 11 | 131 | 4 | 5/11 | 45.45% | 3 | 17.85 | 17.87 | - |
| grid-c20-m0-none-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 3 | 57 | 1 | 0/3 | 0.00% | 3 | 24.75 | 25.00 | 12570 |
| grid-c20-m0-none-s1 | CultureEnthusiast | Lexicographic | Feasible | 11 | 133 | 4 | 5/11 | 45.45% | 3 | 34.07 | 34.21 | - |
| grid-c20-m0-none-s1 | CultureEnthusiast | WeightedUtility | Feasible | 3 | 62 | 1 | 0/3 | 0.00% | 3 | 30.63 | 31.74 | 9618 |
| grid-c20-m0-none-s1 | DefaultGeneralist | Lexicographic | Feasible | 11 | 133 | 4 | 5/11 | 45.45% | 3 | 31.86 | 31.87 | - |
| grid-c20-m0-none-s1 | DefaultGeneralist | WeightedUtility | Feasible | 3 | 37 | 3 | 0/3 | 0.00% | 3 | 33.40 | 34.17 | 5100 |
| grid-c20-m0-none-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 11 | 133 | 4 | 5/11 | 45.45% | 3 | 31.67 | 32.16 | - |
| grid-c20-m0-none-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 2 | 27 | 2 | 0/2 | 0.00% | 3 | 34.40 | 34.57 | 1620 |
| grid-c20-m0-none-s1 | ScenicPhotography | Lexicographic | Feasible | 11 | 131 | 5 | 5/11 | 45.45% | 3 | 42.61 | 42.77 | - |
| grid-c20-m0-none-s1 | ScenicPhotography | WeightedUtility | Feasible | 3 | 43 | 2 | 0/3 | 0.00% | 3 | 32.24 | 32.52 | 10780 |
| grid-c20-m1-frequent-s1 | ActiveExplorerFast | Lexicographic | Feasible | 9 | 120 | 4 | 4/9 | 44.44% | 3 | 75.30 | 78.10 | - |
| grid-c20-m1-frequent-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 4 | 66 | 2 | 1/4 | 25.00% | 3 | 24.24 | 24.27 | 10892 |
| grid-c20-m1-frequent-s1 | CultureEnthusiast | Lexicographic | Feasible | 9 | 126 | 4 | 3/9 | 33.33% | 3 | 73.71 | 74.77 | - |
| grid-c20-m1-frequent-s1 | CultureEnthusiast | WeightedUtility | Feasible | 3 | 58 | 1 | 0/3 | 0.00% | 3 | 26.52 | 26.59 | 9990 |
| grid-c20-m1-frequent-s1 | DefaultGeneralist | Lexicographic | Feasible | 9 | 123 | 4 | 3/9 | 33.33% | 3 | 71.16 | 72.04 | - |
| grid-c20-m1-frequent-s1 | DefaultGeneralist | WeightedUtility | Feasible | 3 | 45 | 3 | 0/3 | 0.00% | 3 | 36.66 | 36.68 | 4030 |
| grid-c20-m1-frequent-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 9 | 123 | 4 | 3/9 | 33.33% | 3 | 67.61 | 69.73 | - |
| grid-c20-m1-frequent-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 3 | 45 | 3 | 0/3 | 0.00% | 3 | 38.43 | 39.04 | -1170 |
| grid-c20-m1-frequent-s1 | ScenicPhotography | Lexicographic | Feasible | 9 | 131 | 4 | 3/9 | 33.33% | 3 | 90.08 | 92.57 | - |
| grid-c20-m1-frequent-s1 | ScenicPhotography | WeightedUtility | Feasible | 5 | 71 | 3 | 2/5 | 40.00% | 3 | 24.36 | 24.93 | 7150 |
| grid-c20-m1-none-s1 | ActiveExplorerFast | Lexicographic | Feasible | 11 | 131 | 4 | 5/11 | 45.45% | 3 | 30.34 | 30.49 | - |
| grid-c20-m1-none-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 4 | 66 | 2 | 1/4 | 25.00% | 3 | 22.30 | 22.43 | 10989 |
| grid-c20-m1-none-s1 | CultureEnthusiast | Lexicographic | Feasible | 11 | 133 | 4 | 5/11 | 45.45% | 3 | 33.97 | 34.54 | - |
| grid-c20-m1-none-s1 | CultureEnthusiast | WeightedUtility | Feasible | 3 | 62 | 1 | 0/3 | 0.00% | 3 | 32.89 | 33.27 | 9510 |
| grid-c20-m1-none-s1 | DefaultGeneralist | Lexicographic | Feasible | 11 | 133 | 4 | 5/11 | 45.45% | 3 | 40.88 | 42.59 | - |
| grid-c20-m1-none-s1 | DefaultGeneralist | WeightedUtility | Feasible | 3 | 49 | 3 | 0/3 | 0.00% | 3 | 31.75 | 33.86 | 3610 |
| grid-c20-m1-none-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 11 | 133 | 4 | 5/11 | 45.45% | 3 | 48.92 | 57.33 | - |
| grid-c20-m1-none-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 3 | 45 | 3 | 0/3 | 0.00% | 3 | 34.38 | 34.41 | -1170 |
| grid-c20-m1-none-s1 | ScenicPhotography | Lexicographic | Feasible | 11 | 131 | 5 | 5/11 | 45.45% | 3 | 46.74 | 47.04 | - |
| grid-c20-m1-none-s1 | ScenicPhotography | WeightedUtility | Feasible | 5 | 71 | 3 | 2/5 | 40.00% | 3 | 29.69 | 30.52 | 7390 |
| grid-c20-m3-auto-s1 | ActiveExplorerFast | Lexicographic | Feasible | 10 | 129 | 4 | 4/10 | 40.00% | 3 | 72.16 | 72.57 | - |
| grid-c20-m3-auto-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 5 | 80 | 2 | 2/5 | 40.00% | 3 | 26.04 | 27.65 | 10254 |
| grid-c20-m3-auto-s1 | CultureEnthusiast | Lexicographic | Feasible | 10 | 130 | 4 | 4/10 | 40.00% | 3 | 75.20 | 75.52 | - |
| grid-c20-m3-auto-s1 | CultureEnthusiast | WeightedUtility | Feasible | 4 | 90 | 2 | 1/4 | 25.00% | 3 | 40.26 | 42.06 | 3210 |
| grid-c20-m3-auto-s1 | DefaultGeneralist | Lexicographic | Feasible | 10 | 116 | 4 | 4/10 | 40.00% | 3 | 71.36 | 73.68 | - |
| grid-c20-m3-auto-s1 | DefaultGeneralist | WeightedUtility | Feasible | 4 | 77 | 3 | 1/4 | 25.00% | 3 | 38.75 | 38.91 | -840 |
| grid-c20-m3-auto-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 10 | 116 | 4 | 4/10 | 40.00% | 3 | 71.51 | 73.07 | - |
| grid-c20-m3-auto-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 3 | 79 | 2 | 0/3 | 0.00% | 3 | 40.41 | 40.48 | -8280 |
| grid-c20-m3-auto-s1 | ScenicPhotography | Lexicographic | Feasible | 10 | 121 | 5 | 4/10 | 40.00% | 3 | 72.95 | 73.06 | - |
| grid-c20-m3-auto-s1 | ScenicPhotography | WeightedUtility | Feasible | 5 | 87 | 3 | 2/5 | 40.00% | 3 | 34.62 | 35.10 | 3151 |
| grid-c20-m3-frequent-s1 | ActiveExplorerFast | Lexicographic | Feasible | 9 | 130 | 4 | 3/9 | 33.33% | 3 | 75.06 | 77.78 | - |
| grid-c20-m3-frequent-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 5 | 80 | 2 | 2/5 | 40.00% | 3 | 53.37 | 54.22 | 10254 |
| grid-c20-m3-frequent-s1 | CultureEnthusiast | Lexicographic | Feasible | 9 | 127 | 3 | 4/9 | 44.44% | 3 | 74.83 | 75.11 | - |
| grid-c20-m3-frequent-s1 | CultureEnthusiast | WeightedUtility | Feasible | 4 | 75 | 2 | 1/4 | 25.00% | 3 | 46.35 | 46.83 | 4000 |
| grid-c20-m3-frequent-s1 | DefaultGeneralist | Lexicographic | Feasible | 9 | 112 | 4 | 3/9 | 33.33% | 3 | 85.93 | 87.41 | - |
| grid-c20-m3-frequent-s1 | DefaultGeneralist | WeightedUtility | Feasible | 4 | 77 | 3 | 1/4 | 25.00% | 3 | 51.27 | 70.55 | -840 |
| grid-c20-m3-frequent-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 9 | 112 | 4 | 3/9 | 33.33% | 3 | 85.56 | 86.68 | - |
| grid-c20-m3-frequent-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 3 | 79 | 2 | 0/3 | 0.00% | 3 | 46.84 | 47.31 | -8280 |
| grid-c20-m3-frequent-s1 | ScenicPhotography | Lexicographic | Feasible | 9 | 121 | 4 | 4/9 | 44.44% | 3 | 71.51 | 72.79 | - |
| grid-c20-m3-frequent-s1 | ScenicPhotography | WeightedUtility | Feasible | 5 | 87 | 3 | 2/5 | 40.00% | 3 | 50.50 | 53.58 | 3151 |
| grid-c20-m6-auto-s1 | ActiveExplorerFast | Lexicographic | Feasible | 10 | 129 | 5 | 4/10 | 40.00% | 3 | 86.81 | 91.34 | - |
| grid-c20-m6-auto-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 8 | 103 | 5 | 4/8 | 50.00% | 3 | 72.38 | 74.57 | 7334 |
| grid-c20-m6-auto-s1 | CultureEnthusiast | Lexicographic | Feasible | 10 | 125 | 5 | 4/10 | 40.00% | 3 | 89.51 | 91.69 | - |
| grid-c20-m6-auto-s1 | CultureEnthusiast | WeightedUtility | Feasible | 6 | 92 | 5 | 3/6 | 50.00% | 3 | 63.28 | 88.20 | -3160 |
| grid-c20-m6-auto-s1 | DefaultGeneralist | Lexicographic | Feasible | 10 | 120 | 5 | 4/10 | 40.00% | 3 | 87.83 | 93.33 | - |
| grid-c20-m6-auto-s1 | DefaultGeneralist | WeightedUtility | Feasible | 6 | 92 | 5 | 3/6 | 50.00% | 3 | 49.78 | 52.28 | -6160 |
| grid-c20-m6-auto-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 10 | 120 | 5 | 4/10 | 40.00% | 3 | 89.11 | 90.86 | - |
| grid-c20-m6-auto-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 6 | 92 | 5 | 3/6 | 50.00% | 3 | 51.01 | 52.35 | -20513 |
| grid-c20-m6-auto-s1 | ScenicPhotography | Lexicographic | Feasible | 10 | 127 | 5 | 4/10 | 40.00% | 3 | 91.12 | 96.81 | - |
| grid-c20-m6-auto-s1 | ScenicPhotography | WeightedUtility | Feasible | 6 | 92 | 5 | 3/6 | 50.00% | 3 | 67.67 | 69.60 | -3830 |
| grid-c40-m0-none-s1 | ActiveExplorerFast | Lexicographic | Feasible | 11 | 145 | 1 | 5/11 | 45.45% | 3 | 101.93 | 109.12 | - |
| grid-c40-m0-none-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 4 | 38 | 1 | 1/4 | 25.00% | 3 | 31.14 | 31.16 | 15794 |
| grid-c40-m0-none-s1 | CultureEnthusiast | Lexicographic | Feasible | 11 | 141 | 1 | 5/11 | 45.45% | 3 | 104.06 | 105.15 | - |
| grid-c40-m0-none-s1 | CultureEnthusiast | WeightedUtility | Feasible | 3 | 35 | 1 | 0/3 | 0.00% | 3 | 44.34 | 46.20 | 13020 |
| grid-c40-m0-none-s1 | DefaultGeneralist | Lexicographic | Feasible | 11 | 130 | 4 | 5/11 | 45.45% | 3 | 106.49 | 106.70 | - |
| grid-c40-m0-none-s1 | DefaultGeneralist | WeightedUtility | Feasible | 3 | 36 | 3 | 0/3 | 0.00% | 3 | 45.09 | 45.39 | 5170 |
| grid-c40-m0-none-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 11 | 130 | 4 | 5/11 | 45.45% | 3 | 111.15 | 126.17 | - |
| grid-c40-m0-none-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 2 | 42 | 2 | 0/2 | 0.00% | 3 | 46.54 | 46.61 | -970 |
| grid-c40-m0-none-s1 | ScenicPhotography | Lexicographic | Feasible | 11 | 137 | 2 | 5/11 | 45.45% | 3 | 110.88 | 125.12 | - |
| grid-c40-m0-none-s1 | ScenicPhotography | WeightedUtility | Feasible | 4 | 51 | 2 | 1/4 | 25.00% | 3 | 41.31 | 41.59 | 11650 |
| grid-c40-m1-auto-s1 | ActiveExplorerFast | Lexicographic | Feasible | 10 | 125 | 2 | 4/10 | 40.00% | 3 | 106.13 | 109.02 | - |
| grid-c40-m1-auto-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 5 | 52 | 2 | 2/5 | 40.00% | 3 | 34.01 | 35.09 | 14002 |
| grid-c40-m1-auto-s1 | CultureEnthusiast | Lexicographic | Feasible | 10 | 131 | 1 | 4/10 | 40.00% | 3 | 109.45 | 109.69 | - |
| grid-c40-m1-auto-s1 | CultureEnthusiast | WeightedUtility | Feasible | 3 | 41 | 1 | 0/3 | 0.00% | 3 | 53.61 | 54.75 | 12354 |
| grid-c40-m1-auto-s1 | DefaultGeneralist | Lexicographic | Feasible | 10 | 123 | 4 | 4/10 | 40.00% | 3 | 112.94 | 113.49 | - |
| grid-c40-m1-auto-s1 | DefaultGeneralist | WeightedUtility | Feasible | 3 | 43 | 3 | 0/3 | 0.00% | 3 | 51.14 | 52.05 | 4320 |
| grid-c40-m1-auto-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 10 | 123 | 4 | 4/10 | 40.00% | 3 | 111.35 | 112.02 | - |
| grid-c40-m1-auto-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 3 | 43 | 3 | 0/3 | 0.00% | 3 | 52.33 | 52.65 | -760 |
| grid-c40-m1-auto-s1 | ScenicPhotography | Lexicographic | Feasible | 10 | 131 | 3 | 4/10 | 40.00% | 3 | 106.95 | 108.85 | - |
| grid-c40-m1-auto-s1 | ScenicPhotography | WeightedUtility | Feasible | 5 | 74 | 3 | 2/5 | 40.00% | 3 | 38.92 | 39.15 | 8634 |
| grid-c40-m1-none-s1 | ActiveExplorerFast | Lexicographic | Feasible | 11 | 141 | 2 | 5/11 | 45.45% | 3 | 99.45 | 103.58 | - |
| grid-c40-m1-none-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 5 | 52 | 2 | 2/5 | 40.00% | 3 | 30.29 | 30.37 | 14002 |
| grid-c40-m1-none-s1 | CultureEnthusiast | Lexicographic | Feasible | 10 | 155 | 1 | 5/10 | 50.00% | 3 | 100.36 | 101.16 | - |
| grid-c40-m1-none-s1 | CultureEnthusiast | WeightedUtility | Feasible | 3 | 41 | 1 | 0/3 | 0.00% | 3 | 44.59 | 44.65 | 12354 |
| grid-c40-m1-none-s1 | DefaultGeneralist | Lexicographic | Feasible | 11 | 129 | 4 | 5/11 | 45.45% | 3 | 109.55 | 109.83 | - |
| grid-c40-m1-none-s1 | DefaultGeneralist | WeightedUtility | Feasible | 3 | 43 | 3 | 0/3 | 0.00% | 3 | 47.27 | 48.28 | 4320 |
| grid-c40-m1-none-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 11 | 129 | 4 | 5/11 | 45.45% | 3 | 107.29 | 108.48 | - |
| grid-c40-m1-none-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 3 | 43 | 3 | 0/3 | 0.00% | 3 | 47.12 | 58.99 | -760 |
| grid-c40-m1-none-s1 | ScenicPhotography | Lexicographic | Feasible | 11 | 135 | 3 | 5/11 | 45.45% | 3 | 104.49 | 105.60 | - |
| grid-c40-m1-none-s1 | ScenicPhotography | WeightedUtility | Feasible | 5 | 74 | 3 | 2/5 | 40.00% | 3 | 41.63 | 41.83 | 8634 |
| grid-c40-m3-none-s1 | ActiveExplorerFast | Lexicographic | Feasible | 11 | 145 | 2 | 5/11 | 45.45% | 3 | 107.68 | 108.26 | - |
| grid-c40-m3-none-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 6 | 65 | 3 | 3/6 | 50.00% | 3 | 34.38 | 34.47 | 12721 |
| grid-c40-m3-none-s1 | CultureEnthusiast | Lexicographic | Feasible | 11 | 143 | 2 | 5/11 | 45.45% | 3 | 110.01 | 110.88 | - |
| grid-c40-m3-none-s1 | CultureEnthusiast | WeightedUtility | Feasible | 5 | 70 | 2 | 2/5 | 40.00% | 3 | 52.96 | 53.81 | 5798 |
| grid-c40-m3-none-s1 | DefaultGeneralist | Lexicographic | Feasible | 11 | 124 | 4 | 5/11 | 45.45% | 3 | 115.70 | 118.17 | - |
| grid-c40-m3-none-s1 | DefaultGeneralist | WeightedUtility | Feasible | 4 | 58 | 3 | 1/4 | 25.00% | 3 | 51.94 | 52.06 | 1212 |
| grid-c40-m3-none-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 11 | 124 | 4 | 5/11 | 45.45% | 3 | 113.58 | 115.34 | - |
| grid-c40-m3-none-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 3 | 65 | 2 | 0/3 | 0.00% | 3 | 52.40 | 52.91 | -5688 |
| grid-c40-m3-none-s1 | ScenicPhotography | Lexicographic | Feasible | 11 | 130 | 3 | 5/11 | 45.45% | 3 | 110.67 | 113.51 | - |
| grid-c40-m3-none-s1 | ScenicPhotography | WeightedUtility | Feasible | 5 | 76 | 3 | 2/5 | 40.00% | 3 | 50.92 | 50.97 | 4974 |
| grid-c40-m6-frequent-s1 | ActiveExplorerFast | Lexicographic | Feasible | 8 | 150 | 5 | 3/8 | 37.50% | 3 | 263.07 | 263.34 | - |
| grid-c40-m6-frequent-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 7 | 130 | 5 | 3/7 | 42.86% | 3 | 509.38 | 543.12 | 5142 |
| grid-c40-m6-frequent-s1 | CultureEnthusiast | Lexicographic | Feasible | 9 | 135 | 5 | 3/9 | 33.33% | 3 | 235.18 | 239.73 | - |
| grid-c40-m6-frequent-s1 | CultureEnthusiast | WeightedUtility | Feasible | 7 | 145 | 5 | 3/7 | 42.86% | 3 | 545.49 | 550.15 | -9478 |
| grid-c40-m6-frequent-s1 | DefaultGeneralist | Lexicographic | Feasible | 9 | 133 | 5 | 3/9 | 33.33% | 3 | 247.66 | 252.60 | - |
| grid-c40-m6-frequent-s1 | DefaultGeneralist | WeightedUtility | Feasible | 6 | 144 | 5 | 3/6 | 50.00% | 3 | 515.78 | 534.52 | -11910 |
| grid-c40-m6-frequent-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 9 | 133 | 5 | 3/9 | 33.33% | 3 | 240.56 | 241.44 | - |
| grid-c40-m6-frequent-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 6 | 144 | 5 | 3/6 | 50.00% | 3 | 519.11 | 523.71 | -29383 |
| grid-c40-m6-frequent-s1 | ScenicPhotography | Lexicographic | Feasible | 8 | 148 | 5 | 4/8 | 50.00% | 3 | 253.77 | 256.85 | - |
| grid-c40-m6-frequent-s1 | ScenicPhotography | WeightedUtility | Feasible | 7 | 132 | 5 | 3/7 | 42.86% | 3 | 516.31 | 535.30 | -7520 |
| grid-c40-m6-none-s1 | ActiveExplorerFast | Lexicographic | Feasible | 10 | 161 | 5 | 4/10 | 40.00% | 3 | 141.41 | 142.93 | - |
| grid-c40-m6-none-s1 | ActiveExplorerFast | WeightedUtility | Feasible | 8 | 122 | 5 | 4/8 | 50.00% | 3 | 53.23 | 54.28 | 6906 |
| grid-c40-m6-none-s1 | CultureEnthusiast | Lexicographic | Feasible | 10 | 159 | 5 | 5/10 | 50.00% | 3 | 131.33 | 132.59 | - |
| grid-c40-m6-none-s1 | CultureEnthusiast | WeightedUtility | Feasible | 7 | 139 | 5 | 5/7 | 71.43% | 3 | 81.62 | 83.13 | -10708 |
| grid-c40-m6-none-s1 | DefaultGeneralist | Lexicographic | Feasible | 11 | 137 | 5 | 5/11 | 45.45% | 3 | 156.19 | 171.54 | - |
| grid-c40-m6-none-s1 | DefaultGeneralist | WeightedUtility | Feasible | 6 | 143 | 5 | 4/6 | 66.67% | 3 | 70.81 | 71.05 | -12810 |
| grid-c40-m6-none-s1 | FoodLoverRelaxed | Lexicographic | Feasible | 11 | 137 | 5 | 5/11 | 45.45% | 3 | 153.41 | 157.68 | - |
| grid-c40-m6-none-s1 | FoodLoverRelaxed | WeightedUtility | Feasible | 6 | 143 | 5 | 4/6 | 66.67% | 3 | 72.88 | 75.78 | -30223 |
| grid-c40-m6-none-s1 | ScenicPhotography | Lexicographic | Feasible | 10 | 161 | 5 | 4/10 | 40.00% | 3 | 135.08 | 135.93 | - |
| grid-c40-m6-none-s1 | ScenicPhotography | WeightedUtility | Feasible | 7 | 132 | 5 | 4/7 | 57.14% | 3 | 58.01 | 60.20 | -8546 |
| incumbent-valid-c20-m2-none-s10 | ActiveExplorerFast | Lexicographic | Feasible | 11 | 114 | 5 | 5/11 | 45.45% | 3 | 26.33 | 26.61 | - |
| incumbent-valid-c20-m2-none-s10 | ActiveExplorerFast | WeightedUtility | Feasible | 4 | 59 | 2 | 1/4 | 25.00% | 3 | 25.76 | 26.84 | 11122 |
| incumbent-valid-c20-m2-none-s10 | CultureEnthusiast | Lexicographic | Feasible | 11 | 144 | 4 | 4/11 | 36.36% | 3 | 66.09 | 67.74 | - |
| incumbent-valid-c20-m2-none-s10 | CultureEnthusiast | WeightedUtility | Feasible | 3 | 63 | 2 | 0/3 | 0.00% | 3 | 33.89 | 34.33 | 7656 |
| incumbent-valid-c20-m2-none-s10 | DefaultGeneralist | Lexicographic | Feasible | 11 | 124 | 5 | 5/11 | 45.45% | 3 | 63.68 | 74.21 | - |
| incumbent-valid-c20-m2-none-s10 | DefaultGeneralist | WeightedUtility | Feasible | 3 | 47 | 3 | 0/3 | 0.00% | 3 | 33.27 | 33.35 | 3820 |
| incumbent-valid-c20-m2-none-s10 | FoodLoverRelaxed | Lexicographic | Feasible | 11 | 124 | 5 | 5/11 | 45.45% | 3 | 59.61 | 60.96 | - |
| incumbent-valid-c20-m2-none-s10 | FoodLoverRelaxed | WeightedUtility | Feasible | 3 | 47 | 3 | 0/3 | 0.00% | 3 | 35.57 | 35.88 | -1500 |
| incumbent-valid-c20-m2-none-s10 | ScenicPhotography | Lexicographic | Feasible | 11 | 111 | 5 | 5/11 | 45.45% | 3 | 15.37 | 15.37 | - |
| incumbent-valid-c20-m2-none-s10 | ScenicPhotography | WeightedUtility | Feasible | 4 | 50 | 3 | 1/4 | 25.00% | 3 | 33.82 | 41.66 | 6975 |
| named-two-opt | ActiveExplorerFast | Lexicographic | Feasible | 4 | 30 | 4 | 1/4 | 25.00% | 3 | 0.04 | 0.04 | - |
| named-two-opt | ActiveExplorerFast | WeightedUtility | Feasible | 4 | 30 | 4 | 1/4 | 25.00% | 3 | 0.06 | 0.06 | 10800 |
| named-two-opt | CultureEnthusiast | Lexicographic | Feasible | 4 | 30 | 4 | 1/4 | 25.00% | 3 | 0.04 | 0.04 | - |
| named-two-opt | CultureEnthusiast | WeightedUtility | Feasible | 4 | 30 | 4 | 1/4 | 25.00% | 3 | 0.05 | 0.05 | 6300 |
| named-two-opt | DefaultGeneralist | Lexicographic | Feasible | 4 | 30 | 4 | 1/4 | 25.00% | 3 | 0.04 | 0.04 | - |
| named-two-opt | DefaultGeneralist | WeightedUtility | Feasible | 4 | 30 | 4 | 1/4 | 25.00% | 3 | 0.05 | 0.25 | 6300 |
| named-two-opt | FoodLoverRelaxed | Lexicographic | Feasible | 4 | 30 | 4 | 1/4 | 25.00% | 3 | 0.04 | 0.04 | - |
| named-two-opt | FoodLoverRelaxed | WeightedUtility | Feasible | 2 | 20 | 2 | 0/2 | 0.00% | 3 | 0.05 | 0.05 | 2950 |
| named-two-opt | ScenicPhotography | Lexicographic | Feasible | 4 | 30 | 4 | 1/4 | 25.00% | 3 | 0.04 | 0.04 | - |
| named-two-opt | ScenicPhotography | WeightedUtility | Feasible | 4 | 30 | 4 | 1/4 | 25.00% | 3 | 0.06 | 0.06 | 10100 |
| named-zigzag | ActiveExplorerFast | Lexicographic | Feasible | 4 | 81 | 4 | 2/4 | 50.00% | 3 | 0.13 | 0.14 | - |
| named-zigzag | ActiveExplorerFast | WeightedUtility | Feasible | 3 | 74 | 3 | 1/3 | 33.33% | 3 | 0.18 | 0.18 | 6500 |
| named-zigzag | CultureEnthusiast | Lexicographic | Feasible | 4 | 81 | 4 | 2/4 | 50.00% | 3 | 0.11 | 0.13 | - |
| named-zigzag | CultureEnthusiast | WeightedUtility | Feasible | 3 | 74 | 3 | 1/3 | 33.33% | 3 | 0.14 | 0.15 | 240 |
| named-zigzag | DefaultGeneralist | Lexicographic | Feasible | 4 | 81 | 4 | 2/4 | 50.00% | 3 | 0.11 | 0.11 | - |
| named-zigzag | DefaultGeneralist | WeightedUtility | Feasible | 3 | 74 | 3 | 1/3 | 33.33% | 3 | 0.14 | 0.14 | 240 |
| named-zigzag | FoodLoverRelaxed | Lexicographic | Feasible | 4 | 81 | 4 | 2/4 | 50.00% | 3 | 0.14 | 0.39 | - |
| named-zigzag | FoodLoverRelaxed | WeightedUtility | Feasible | 2 | 70 | 2 | 0/2 | 0.00% | 3 | 0.14 | 0.15 | -6300 |
| named-zigzag | ScenicPhotography | Lexicographic | Feasible | 4 | 81 | 4 | 2/4 | 50.00% | 3 | 0.13 | 0.16 | - |
| named-zigzag | ScenicPhotography | WeightedUtility | Feasible | 2 | 70 | 2 | 0/2 | 0.00% | 3 | 0.14 | 0.38 | 4200 |
