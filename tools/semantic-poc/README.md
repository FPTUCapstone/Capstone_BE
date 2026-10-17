# TM-70 isolated semantic search PoC

This opt-in diagnostic does **not** register production DI or change `GET /api/v1/tours`.
Owner/Product/BA approval of the proposed semantic contract remains required.

## Start and run

From repository root, with Docker Desktop running:

```powershell
docker compose -f docker-compose.semantic.yml up -d --build
docker compose -f docker-compose.semantic.yml ps
python tools/semantic-poc/demo.py --output .semantic-results/benchmark.json
```

Wait for embedding and qdrant-health to be healthy. First startup downloads the pinned model
into a separate named cache volume; it can take several minutes. Services bind only to
`127.0.0.1:8017` (inference) and `127.0.0.1:6337` (Qdrant). No API keys or production data are used.
The separate Compose project contains no SQL service and does not touch ordinary SQL volumes.

Model configuration is deliberately fixed in `services/embedding-service/protocol.py` and
`SemanticModel`: `intfloat/multilingual-e5-small` revision
`fd1525a9fd15316a2d503bf26ab031a61d056e98`, 384 dimensions, CPU mean pooling, L2 normalization,
512-token truncation, `query: ` and `passage: ` prefixes. Changing it requires updating both
contracts and rebuilding the index. Inference loads once per process; one worker, serialized
inference, two Torch CPU threads. Weights stay outside Git. Direct dependencies and resolved
container graph are pinned in `requirements.txt` and `requirements.lock.txt`; Docker installs
Torch from the official CPU wheel index. Linux CPU wheel version is `2.10.0+cpu`.

Without Docker for inference (Qdrant still required):

```powershell
python -m venv .venv-semantic
.\.venv-semantic\Scripts\python.exe -m pip install -r services/embedding-service/requirements.txt
.\.venv-semantic\Scripts\python.exe services/embedding-service/smoke.py
.\.venv-semantic\Scripts\python.exe -m uvicorn app:app --app-dir services/embedding-service --host 127.0.0.1 --port 8017 --workers 1 --no-access-log
```

## Index and retrieval guarantees

`tours.json` contains 12 synthetic public tours, IDs 91001..91012; `queries.json` contains
12 AI-authored Vietnamese graded labels. These labels have not been independently validated.
The indexer constructs only public title, destination names and operator name. NFC Unicode
and UTF-8 byte-length framing avoid ambiguous field boundaries; destination names are unique
and sorted by UTF-8 bytes. C# and Python golden tests include emoji, control characters and
combining accents. SHA256 of the content detects changes; model version is stored per point.
Vector IDs are the positive TourId values. Upsert is idempotent. Explicit deletion demonstrates
unpublication invalidation. There is no automatic production change-feed or background indexer.

Each run creates `tm70_poc_<uuid>` with 384-dimensional Cosine vectors, indexes fixtures twice,
queries with `params.exact=true`, and deletes its own collection in `finally`. Collections must
be exclusively owned and immutable during retrieval. The .NET adapter requests all 257 points
as a sentinel for the 256-point hard limit; an oversized collection fails instead of returning
a misleading first page. It makes one bounded SQL batch resolution. SQL applies the existing
approved, published-at-or-before-one-clock-snapshot, approved operator, active account predicate.
Missing/currently private IDs and mismatched content/model fingerprints are excluded. Results
sort cosine descending then TourId ascending before paging. This diagnostic tie-break is not
an approved public relevance contract.

`TotalCount` is exact **only for the eligible indexed fixture corpus**. It is never the total SQL
Tour population. `SqlUniverseCoverageUnknown=true` explicitly discloses missing embedding
coverage. The PoC intentionally does not implement production filter/availability contracts or
rating relevance. SQL-backed tests and the real-service combined test exercise authoritative
resolution; the standalone Python benchmark measures inference/vector retrieval, not SQL latency.
The standalone indexer never opens SQL. No production embeddings or authorization data are written.

There is no similarity threshold or lexical fallback. Unrelated queries still rank all vectors;
this is an open product decision, not proof of meaningful matches. Scores are diagnostics only.
Empty/oversized queries, invalid pagination/vectors, oversized corpus, malformed dependency
responses and HTTP outages fail closed in orchestration. Caller cancellation propagates.
No raw query access log is enabled; process diagnostics expose only memory/CPU counters.

## Verification and comparison

```powershell
.\.venv-semantic\Scripts\python.exe -m unittest discover -s services/embedding-service -v
.\.venv-semantic\Scripts\python.exe services/embedding-service/smoke.py
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj --filter FullyQualifiedName~SemanticPoc
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj --filter FullyQualifiedName~SemanticAdapter
# Existing disposable SQL harness; do not supply a production database connection.
$env:TRIPMATE_EMBEDDING_POC_URL = 'http://127.0.0.1:8017/'
$env:TRIPMATE_QDRANT_POC_URL = 'http://127.0.0.1:6337/'
# Supply TRIPMATE_SQLSERVER_TEST_CONNECTION separately through your local environment.
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj --filter FullyQualifiedName~SemanticPoc
```

The lexical comparator emulates frozen three-field accent-insensitive all-token matching with
per-token best classes 0 exact title, 1 title prefix, 2 title substring, 3 destination, 4 operator;
worst token class ranks the tour. All fixture ratings are null; ties sort folded title then ID.
Python NFD accent folding including đ/d approximates SQL collation; SQL collation parity and full
rating ordering are not claimed. Existing runtime has no keyword parameter at this base commit;
this is a frozen-contract comparator, not a claim of benchmarking an existing keyword endpoint.
Recall/nDCG omit zero-relevance queries (undefined); precision includes them. Precision@5 divides
by 5 even when a comparator returns fewer items. Query timing excludes startup, index and SQL.
CPU is a process cumulative delta over queries, memory an RSS snapshot, not peak memory.
See `docs/tm-70-semantic-poc-evidence.md` for executed evidence and limitations.

## Troubleshooting and cleanup

Use `docker compose -f docker-compose.semantic.yml logs embedding` for download/model failures;
`docker compose -f docker-compose.semantic.yml logs qdrant` and the qdrant-health probe for
vector readiness. An unavailable model is an error; random vectors are never substituted.
Connection refusal means services are not ready or ports differ. Changing model/dimension requires
a new fixture collection. Do not reuse a production Qdrant collection or expose this unauthenticated
local diagnostic service publicly.

Stop only this project with `docker compose -f docker-compose.semantic.yml stop`.
`docker compose -f docker-compose.semantic.yml down` removes only its containers/network and
preserves its two named caches. The demo deletes its UUID collection automatically. Remove a
leftover **specific `tm70_poc_<uuid>` collection** only after verifying its name; never delete SQL
volumes or run a global cleanup. Model download caches can be retained for subsequent runs.

Primary contracts: [model card](https://huggingface.co/intfloat/multilingual-e5-small/raw/main/README.md),
[fixed model revision](https://huggingface.co/intfloat/multilingual-e5-small/tree/fd1525a9fd15316a2d503bf26ab031a61d056e98),
[Qdrant query API](https://api.qdrant.tech/api-reference/search/query-points).
