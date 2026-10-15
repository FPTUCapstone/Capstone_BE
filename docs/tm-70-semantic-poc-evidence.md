# TM-70 semantic PoC executed evidence — 2026-10-15

Base: `bdb70d8a74002dc5c99f124c7dfa46f4a648c82b` (`origin/develop`).
Production search behavior, routes, schema and DI remain unchanged.

## Real inference and vector evaluation

Docker Desktop initially failed with missing `dockerDesktopLinuxEngine` pipe. After startup,
Docker Server 28.0.4 ran the separate `tripmate-semantic-poc` project. Real Qdrant 1.13.4 and
CPU Torch 2.10.0+cpu / Transformers 5.3.0 served actual model weights pinned at
`intfloat/multilingual-e5-small@fd1525a9fd15316a2d503bf26ab031a61d056e98`.
Model loader reported an unexpected legacy `embeddings.position_ids` buffer; real inference,
384 dimensions and unit normalization passed. No random embedding fallback exists.

The independent pinned Windows venv also ran real model smoke: cosine 0.935147508079924 for
Vietnamese query/passage, first query 287.72 ms, passage 12.28 ms. Startup warmup differs from
steady query evaluation. Docker inference was healthy and real cosine Qdrant retrieval ran.

12 synthetic tours and 12 AI-authored Vietnamese labelled queries covered exact name, accents,
no accents, natural language, synonym, destination, style, multiple conditions, irrelevant,
ambiguous and operator intent. Labels are not owner-validated. All ratings are absent.

| Metric | Semantic | Frozen lexical approximation |
|---|---:|---:|
| Recall@5 | 0.981818 | 0.454545 |
| Recall@10 | 1.000000 | 0.454545 |
| Precision@5 | 0.300000 | 0.116667 |
| nDCG@10 | 0.969186 | 0.454545 |

Recall/nDCG average 11 queries with positive labels; precision averages all 12. No-threshold
semantic retrieval returned unrelated items for the irrelevant query; recall/nDCG there are
undefined and precision is zero. This tiny curated dataset does not establish superiority,
production accuracy, calibrated confidence, an acceptance threshold or a production SLO.
Comparator implements the frozen per-token worst-best-class all-token algorithm, with Python
accent folding approximating Vietnamese SQL collation. It does not claim SQL collation parity
or runtime endpoint keyword benchmarking (the base endpoint has no keyword input).

| Executed measurement | Result |
|---|---:|
| End-to-end embedding + Qdrant p50 / p95 | 34.12 / 55.23 ms |
| Embedding service computation p50 / p95 | 11.23 / 13.77 ms |
| Vector HTTP retrieval p50 / p95 | 11.03 / 21.38 ms |
| 12-vector index build / idempotent update | 1170.49 / 446.44 ms |
| Inference process RSS after queries | 1,201,692,672 bytes |
| Inference CPU delta / one-core equivalent | 0.45 seconds / 105.27% |
| Docker memory snapshot inference / Qdrant | 846.9 MiB / 92.68 MiB |

Hardware: Intel 12th Gen Core i5-12500H (12 cores / 16 logical processors). Docker ran Linux WSL2
(kernel 6.18.54.1-microsoft-standard-WSL2), with 16 CPUs and 8,155,713,536 bytes (7.60 GiB)
allocated. Inference used two Torch threads. Other local workloads, including .NET tests, were
present; this was not an isolated load benchmark.

These are one short local run, not load testing; memory counters use different accounting,
CPU can exceed 100% with two threads, and SQL/startup are outside query latency. No target
threshold was invented. Real idempotent upsert and explicit point deletion passed; the isolated
UUID collection was deleted. The benchmark is reproducible via `tools/semantic-poc/demo.py`.
Raw generated JSON remains ignored; no model weights or benchmark dumps are committed.

## Verification record

- Application TDD red: missing semantic namespace; green 5 focused tests total (4 orchestration + 1 shared Unicode golden).
- Adapter TDD red: 3 malformed-payload failures; green 8 focused tests, including wrong distance/dimension rejection.
- Python TDD red: missing protocol, comparator accent mismatch, whitespace request accepted;
  green protocol/comparator/request tests, including shared adversarial Unicode SHA256 golden.
- SQL authority and real combined .NET/SQL/E5/Qdrant: final Release 4 passed, zero failed/skipped.
  Batched SQL, one clock read, current public state, stale content rejection, and genuinely embedded
  hidden tour exclusion passed on disposable databases; collection cleanup passed.
- Python: final 6 tests passed, including cleanup-failure reporting and whitespace request validation.
- Release solution build: passed, zero errors; existing System.Security.Cryptography.Pkcs 10.0.0
  NU1902/NU1903 advisory warnings remain unchanged.
- Complete Release solution regression, SQL disabled and real Redis enabled: 3,215 passed,
  zero failed, 386 skipped opt-in tests. Application 1,944/0 skipped; Infrastructure 740/3 skipped;
  CandidatePoolReplay 13/0 skipped; API 518/383 skipped. Skips are not counted as passes.
- Real outage experiments stopped only the isolated services: embedding outage returned unavailable
  and exit 1, collection deleted; Qdrant outage returned unavailable and exit 1 with cleanup failure
  explicitly recorded (creation never succeeded). Both services restored and health verified.
- Changed-file C# formatting and diff checks passed. No schema or production dependency changes.

PoC count is exact only within the complete eligible indexed fixture corpus (maximum 256).
Missing SQL embeddings have unknown coverage, stale/model-mismatched vectors are omitted,
publication remains SQL authoritative. No production indexing, filter acceptance, shared Review
eligibility resolution, rollout approval or semantic production readiness is claimed.
