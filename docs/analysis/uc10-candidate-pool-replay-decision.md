# UC-10 candidate-pool replay decision

## Decision

**Collect more evidence.** Keep TM-215's production 60-provider/40-matrix/no-backfill behavior.
The replay proves deterministic mechanics and exposes plausible trade-offs, but two synthetic
scenarios cannot establish production-quality uplift.

## Evidence declaration

- Purpose: exploratory correctness and quality comparison; not inferential.
- Corpus: `tools/TripMate.CandidatePoolReplay/fixtures/synthetic-v1.jsonl`.
- Corpus SHA-256 reported by the tool:
  `226a81176751ec7195e9cb9297a3041ccfad782e441aedebffbc076f9a62d32e`.
- Manifest: schema 1.0; A=3/2 for compact boundary fixtures; B frozen overflow; C core 1 plus
  one diversity slot; D=4/3 sensitivity cap.
- Valid scenarios: 2; excluded/invalid: 0. No power calculation or confidence interval is claimed.
- Repeated executions produced byte-identical result SHA-256
  `11be6e508c42809548a64958d4d18803bb06d61ef1799810a410f89f99839d7e`.

The small caps are deliberate fixture parameters, not a proposal to change production defaults.
The baseline still invokes the same production selectors used by the 60/40 configuration.

## Paired observations

| Segment | n | A planned/base sum/mean | B delta sum/mean | C delta sum/mean | D delta sum/mean | Travel A → B/C/D |
|---|---:|---:|---:|---:|---:|---|
| eligible 4, mandatory 0, car, dense | 1 | 1 / 100 / 100 | +1 / +80 / -10 | +1 / +80 / -10 | +1 / +80 / -10 | 11 → 21/21/21 min |
| eligible 3, mandatory 1, walking, sparse | 1 | 1 / 75 / 75 | 0 / 0 / 0 | 0 / 0 / 0 | +1 / +70 / -2.5 | 14 → 14/14/23 min |

In the dense invalidation fixture, A freezes `c1,c2,c3`, drops invalid matrix candidate `c2`, and
does not admit `c3`; this is the production no-backfill baseline. B and C fill that unused slot
from the frozen provider pool, while D reaches the same two valid candidates only through its
larger declared caps. Each raises category and geo-cell coverage by one, effective-utility sum by
80, and travel by 10 minutes; mean effective utility falls by 10. In the sparse fixture, only D
changes the plan, raising base/effective sums by 70/72, category/geo coverage by one, and travel by
9 minutes while mean effective utility falls by 4. All strategies remain feasible.

Visit-set/order stability changes versus A occur in 1/2 scenarios (50%) for B and C and 2/2
scenarios (100%) for D. Dense matrix payload is 16 elements for A/B/C and 25 for D; A retains its
prepared `c1,c2` matrix payload even though invalid `c2` is absent from the plan. Sparse payload is
16 for A/B/C and 25 for D. These are raw paired outcomes, not global averages or statistical claims.

Effective-utility deltas are reported only because every candidate in this synthetic corpus has a
legitimately frozen provider score. No Base-only and provider-comparable observations are mixed.

## Online and overhead evidence

The runtime now emits aggregate funnel counts, drop reasons, matrix payload, bounded outcomes, and
stage durations. No production traffic window was available during this implementation, so this
document does not join or infer from real ORS latency. The local telemetry benchmark used .NET
10.0.0 on Windows 10.0.26200, 12 logical processors, 20,000 measured attempts plus 2,000 warmups,
with synthetic 60/40 successful-attempt work and no network latency. Its latest run reported:

- disabled p50/p95/p99: 7.2/7.9/18.9 µs;
- enabled p50/p95/p99: 6.6/7.5/19.5 µs;
- allocation: disabled 32,792 B/attempt, enabled 33,176 B/attempt (384 bytes overhead);
- observed p95 delta -5.06%, allocation delta 1.17%, gate PASS.

This microbenchmark is a regression guard, not a production latency estimate. CI or deployment
hardware should repeat it, and production dashboards should validate actual p95/p99 by payload.

The replay timing sidecar separately measured selector and generator work for each strategy on the
same two scenarios. Every strategy/stage therefore has only `n=2`; p50/p95/p99 collapse to the two
observations and the first execution includes JIT/cold-start cost. Observed selection p50/p95/p99 in
microseconds were A 1,544.4/17,826.5/17,826.5, B 99.7/279.2/279.2,
C 31.0/3,900.7/3,900.7, and D 28.2/30.1/30.1. Generation p50/p95/p99 were
A 1,111.1/26,300.0/26,300.0, B 79.5/1,628.0/1,628.0, C 56.3/148.8/148.8, and
D 67.7/108.5/108.5. Mean managed allocations (selection/generation bytes) were A 9,520/20,456,
B 8,468/21,500, C 8,784/21,456, and D 8,540/32,992. These measurements demonstrate that the
timing contract works; they are too sparse and cold-start-sensitive for a comparative performance
claim.

## External validity

The checked-in corpus is synthetic-only. It models deterministic score boundaries, finalization
invalidation, mandatory-count pressure, two transport modes, two density buckets, category/geo
diversity, and directional travel costs. It does not model real preference distributions, opening
hours, budgets, correlated geography, provider errors, seasonal demand, real ORS latency, or the
full itinerary generator's rejection distribution. **Conclusions generalize only to the tested
synthetic distribution.**

## What remains undecided

No product threshold is approved for minimum utility/coverage gain, maximum segment regression,
payload or ORS latency ceiling, diversity weights/cell granularity, backfill policy, or rollout and
rollback. A representative corpus and an online observation window are required before choosing
`propose TM-215 amendment`. Until then, B/C/D remain offline-only and production no-backfill tests
remain normative.
