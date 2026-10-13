# UC-10 TripMate AI Measurement Runbook

For a teammate who measures the team's AI service with the real models on their own machine. The
results table at the end tells the developer whether the demo environment may switch POI ranking
(TM-215) and itinerary explanations (TM-217) from Gemini to the TripMate AI service.

Spec: `specs/UC-10-tripmate-ai-provider-spec.md` (§9.3 measurements, §13 smoke test, §14 this runbook).

---

## 1. Purpose and decision

The backend gives each provider call a fixed time budget. A model that answers too slowly produces a
fallback for the user: base scores for ranking, the fallback text for explanations. This runbook
measures whether the machine answers within those budgets, and whether the Vietnamese explanations
and the ranking scores are good enough.

Pass rules (spec §14.2):

| Check | Pass rule | Source |
|---|---|---|
| rank-pois, warm, 60 candidates | Every measured call returns 200, and p95 < 5 s. | `Personalization:ProviderTimeout`, 5 s (`PersonalizationRankingOptions.cs:33`, `appsettings.json:69`) |
| explain-itinerary, warm, sample and full-day payloads | Every measured call returns 200, and p95 < 10 s. | `AiExplanation:TimeoutSeconds`, 10 s (`ExplanationProviderOptions.cs:15`, `appsettings.json:82`) |
| Service deadlines | `EMBEDDING_TIMEOUT_SECONDS` < 5 and `LLM_TIMEOUT_SECONDS` < 10 during the measurement. | spec §8.5 |
| Cold calls (first after start, first after an idle period longer than `LLM_KEEP_ALIVE`, first with new POIs) | Recorded, not pass or fail. A cold failure adds a warm-up step to the demo checklist. | spec §8.7 |

Each service deadline sits below its backend timeout, so a 200 answer already arrived in time. A model
that is too slow shows up as 504 answers, which break the "every call returns 200" rule.

Switching the demo environment to `Provider=TripMateAi` and `Enabled=true` is **not** part of this
runbook; the developer decides from the results (§9).

---

## 2. Prerequisites

- **An NVIDIA GPU.** On a 4-vCPU CPU one explanation takes 13–57 s (spec §9.3), far over the 10 s
  budget. The Ollama container of the Compose files is CPU-only, so the models run in a **native
  Ollama on the host**:
  - NVIDIA driver ≥ 531 (`nvidia-smi` prints the version);
  - Ollama for Windows (or Linux) ≥ 0.40;
  - `ollama pull bge-m3`;
  - `tripmate-explainer`: download `tripmate-explainer-q4_k_m.gguf` and `Modelfile` from the Kaggle kernel
    output (`kaggle kernels output <owner>/tripmate-explainer-qlora -p <folder>`), then run
    `ollama create tripmate-explainer -f Modelfile` in that folder;
  - `ollama ps` after the first call shows `100% GPU`. Record the backend Ollama reports in its log
    at start (CUDA or Vulkan).
- Docker Desktop. The AI service needs 512 MB (`AI_MEM_LIMIT`); Part D adds the backend stack
  (SQL Server alone takes about 1.4 GB).
- About 4 GB of disk: the models are about 2.5 GB, the images the rest.
- `Capstone_BE` and `Capstone_AI` cloned side by side (the backend profile builds `../Capstone_AI`).
- **One stack at a time.** The backend Compose file uses fixed container names (`tripmate-sqlserver`,
  `tripmate-api`, `tripmate-ai`, …). List them first:
  ```powershell
  docker ps -a --filter "name=tripmate-" --format "table {{.Names}}\t{{.Status}}\t{{.Label \"com.docker.compose.project\"}}"
  ```
  Remove another project's containers with that project's `docker compose down` (it keeps volumes).
- Keep a laptop on mains power: on battery the GPU is throttled.

Reference numbers, laptop RTX 3050 Laptop 4 GB, driver 531.61, Ollama 0.40.3 (Vulkan), 2026-10-12:

| Measurement | Result |
|---|---|
| rank-pois, 60 candidates | first call 1.5–2.2 s; 0.01 s once the embeddings are cached |
| explain-itinerary, 2 / 4 / 6 / 8 stops, first call per itinerary | 1.7 / 2.9–3.5 / 4.8–6.4 / 6.1–7.2 s |
| `scripts/benchmark.py`, 10 measured calls each | 3 items: median 2.05 s, p95 2.17 s; 8 items: median 5.57 s, p95 5.73 s, 0 errors; **12 items: 6 of 11 calls 504**, successes 8.7–8.9 s (a second run: 8 items median 5.67 s, 0 errors; 12 items 2 of 4 calls 504) |
| `evaluate_model.py`, 130 itineraries, `--timeout 9` | 130/130 clean, p95 3.7–5.0 s |
| Full backend request (Part D) | 4.2–5.8 s per fresh generation, explanation 3.2–4.8 s for 4–5 items |

So on that laptop, itineraries up to 8 stops pass and about 12 stops fall back roughly half the time.

---

## 3. Preflight without models

Proves the backend wiring before downloading models. It uses the fake Ollama of the AI repository's
tests. These are the files and commands of the 2026-10-12 smoke test (spec §13, plan T6).

1. In `Capstone_BE`, create `.env` from `.env.example` (or your own) and set each key once:
   ```
   TRIPMATE_AI_API_KEY=<random secret>
   AiRanking__Enabled=true
   AiRanking__Provider=TripMateAi
   AiRanking__Endpoint=http://ai:8000/v1/rank-pois
   AiRanking__ApiKey=<the same secret>
   AiExplanation__Enabled=true
   AiExplanation__Provider=TripMateAi
   AiExplanation__Endpoint=http://ai:8000/v1/explain-itinerary
   AiExplanation__ApiKey=<the same secret>
   AiExplanation__RateLimitPerMinute=60
   SchedulingRateLimit__MaxFreshGenerationsPerMinute=60
   SchedulingRateLimit__CooldownSeconds=0
   ```
   A random secret: `docker run --rm python:3.12-slim python -c "import secrets; print(secrets.token_urlsafe(32))"`.
   `git check-ignore .env` must print `.env`. A real `OPENROUTESERVICE__APIKEY` is required: every
   generation asks OpenRouteService for travel times.
2. Save `compose.fake-ollama.yml` outside the repository, for example in `$env:TEMP\tripmate-ai-smoke\`:
   ```yaml
   services:
     ollama:
       image: python:3.12-slim
       volumes:
         - ${AI_REPO_PATH:-../Capstone_AI}:/src:ro
       command:
         - sh
         - -c
         - >-
           pip install -q -r /src/requirements.txt &&
           cd /src &&
           python -c "import uvicorn; from tests.fake_ollama import FakeOllama, create_fake_ollama_app;
           fake = FakeOllama(); fake.chat_mode = '${FAKE_CHAT_MODE:-ok}';
           uvicorn.run(create_fake_ollama_app(fake), host='0.0.0.0', port=11434)"
       healthcheck:
         test: ["CMD", "python", "-c", "import urllib.request; urllib.request.urlopen('http://127.0.0.1:11434/api/tags', timeout=3)"]
         interval: 5s
         timeout: 5s
         retries: 60
     ollama-init:
       image: python:3.12-slim
       entrypoint: ["true"]
   ```
3. Check and start (PowerShell, in `Capstone_BE`). Do not print the full `config`: it contains the
   resolved secrets.
   ```powershell
   $o  = "$env:TEMP\tripmate-ai-smoke\compose.fake-ollama.yml"
   $dc = @('-f', 'docker-compose.yml', '-f', $o, '--profile', 'ai')
   docker compose config --services                  # sqlserver, db-init, redis, api
   docker compose --profile ai config --services     # also ollama, ollama-init, ai
   docker compose @dc config --services              # the same seven services
   docker compose @dc up -d --build
   docker compose @dc ps -a                          # sqlserver, redis, ollama, ai healthy; db-init and ollama-init exited 0
   docker exec tripmate-ai python -c "import urllib.request as u; print(u.urlopen('http://127.0.0.1:8000/ready', timeout=5).read().decode())"
   ```
   `/ready` answers `{"status":"ready","models":{"bge-m3":true,"qwen3:1.7b":true}}`.
4. One generation: seed the Da Nang POIs and a traveler with interests, create a token, and post a
   request as in plan T6 step 6 (`plans/UC-10-tripmate-ai-provider-plan.md`). The API log must show
   `POI ranking provider TripMateAiPoiRankingProvider; … outcome=success` and
   `Itinerary explanation provider=TripMateAiExplanationProvider; … outcome=success`.
5. Stop it: `docker compose @dc stop`.

---

## 4. Part A — Service latency

Uses only the `ai` service of the standalone `Capstone_AI` stack, pointed at the host Ollama. Its own
`ollama` container is CPU-only and would also clash with the host Ollama on port 11434.

1. In `Capstone_AI`, copy `.env.example` to `.env` and set `AI_API_KEY=<random secret>`,
   `LLM_MODEL=tripmate-explainer`. Keep `LLM_TIMEOUT_SECONDS` below 10 and `EMBEDDING_TIMEOUT_SECONDS`
   below 5 (the defaults are 9 and 4).
2. Save `compose.host-ollama.yml` outside the repository:
   ```yaml
   services:
     ai:
       environment:
         OLLAMA_BASE_URL: http://host.docker.internal:11434
       depends_on: !reset {}
   ```
3. Start only the service and wait for `/ready`:
   ```powershell
   docker compose -f docker-compose.yml -f "$env:TEMP\tripmate-ai-smoke\compose.host-ollama.yml" up -d --build --no-deps ai
   curl.exe -s http://127.0.0.1:8000/ready     # {"status":"ready","models":{"bge-m3":true,"tripmate-explainer":true}}
   ```
   `MODEL_WARMUP=true` and `LLM_KEEP_ALIVE=-1` (the defaults) load both models at start and keep them
   loaded. The first call after a model load takes about 18 s; record it as a cold call.
4. Latency with `scripts/benchmark.py`. It runs one cold call and 10 measured calls for each sample and
   prints the sample name, cold call, median, p95, max and the error count. The samples are the small
   ones (15 candidates, 3 items) and the backend-size ones (`rank_request_60.json`, and full-day
   itineraries `explain_request_8.json` and `explain_request_12.json`). Run it from the service image,
   which has its dependencies, in the network of the `ai` container (`capstone_ai-ai-1`, or the name
   from `docker compose ps`):
   ```powershell
   docker run --rm --network container:capstone_ai-ai-1 -v "${PWD}:/w:ro" -w /w tripmate-ai:latest `
     python scripts/benchmark.py --url http://127.0.0.1:8000 --key <AI_API_KEY> --runs 10
   ```
   **Run one measurement at a time.** Two runs share the GPU and both slow down: on the reference
   laptop an overlapping run turned the 8-item sample from 0 errors into 2 of 2 timeouts. In PowerShell
   do not cut the output with `Select-Object -First`; the container keeps running after the pipe stops.
5. Quality and time together over the evaluation sets, with the production budget of 9 s. The image
   runs as a non-root user, so it writes the results to `/tmp` inside the container and `docker cp`
   copies them out afterwards with the host user's ownership; the repository itself stays mounted
   read-only and the container never runs as root:
   ```powershell
   docker run --name tripmate-eval -v "${PWD}:/w:ro" -w /w tripmate-ai:latest python training/llm/evaluate_model.py `
     --dataset training/llm/datasets/eval.jsonl --model tripmate-explainer `
     --ollama http://host.docker.internal:11434 --timeout 9 `
     --output /tmp/eval_tuned.jsonl --summary /tmp/eval_tuned_summary.json
   docker cp tripmate-eval:/tmp/eval_tuned.jsonl training/llm/work/
   docker cp tripmate-eval:/tmp/eval_tuned_summary.json training/llm/work/
   docker rm tripmate-eval
   ```
   Repeat with `eval_unseen.jsonl`. Record the clean count, p95 and the timeouts from the summary.
6. Optional: two explanation calls at the same time, the backend's default
   `AiExplanation:MaxConcurrency` of 2 (`ExplanationProviderOptions.cs:25`). The service runs one
   model call at a time (`LLM_MAX_CONCURRENCY=1`), so the second call waits; record both times.

---

## 5. Part B — Vietnamese quality

Read about 20 explanations from varied itineraries: the outputs of step A.5, or real generations
from Part D. Count how many pass each check:

- Vietnamese only, one sentence;
- consistent with the item's name, time and kind (visit, rest at a POI, free rest);
- no invented facts such as prices, opening hours or distances;
- no advice to change the schedule;
- natural tone, at most 500 characters.

Known weakness to look for: a "closing the day" phrase on an item that is not the last one.

---

## 6. Part C — Ranking scores

1. Use POIs of the demo city with Vietnamese tags (the backend's 12 seeded POIs have English categories
   and no tags; `Capstone_AI/samples/rank_request_60.json` has 60 Da Nang POIs).
2. Send several preference sets the way the backend does: lowercase, without diacritics, for example
   `["bai bien","am thuc"]`. The service restores the diacritics; the same set with diacritics must
   give the same `aiScore` per POI (checked on 2026-10-12: identical for 12 POIs).
3. Record the `aiScore` minimum, median, maximum and the buckets 0–0.2, …, 0.8–1, and whether the
   order is sensible for each set.
4. If needed, adjust `RANKING_SIMILARITY_FLOOR` and `RANKING_SIMILARITY_CEILING`
   (`Capstone_AI/app/config.py`) and record the values before and after. The backend `ai` profile does
   not forward these settings; set them in the standalone `.env`.

---

## 7. Part D — Backend check (optional)

The backend `ai` profile with the real models. Stop the standalone `ai` first
(`docker compose down` in `Capstone_AI`): both services would send their calls to the same GPU. Then,
in `Capstone_BE`, save `compose.host-ollama.yml`:

```yaml
services:
  ai:
    environment:
      OLLAMA_BASE_URL: http://host.docker.internal:11434
      LLM_MODEL: tripmate-explainer
    depends_on: !reset {}
```

```powershell
$h = @('-f', 'docker-compose.yml', '-f', "$env:TEMP\tripmate-ai-smoke\compose.host-ollama.yml", '--profile', 'ai')
docker compose @h up -d --build sqlserver db-init redis api ai
```

Seed data and post a few requests as in §3 step 4. Read the outcomes and times from the API log:

```powershell
docker logs tripmate-api --since 5m 2>&1 | Select-String 'POI ranking provider|Itinerary explanation provider|TripMate AI explanation attempt|Request finished HTTP/1.1 POST'
```

Record `latencyMs`, `outcome` and `fallbackUsed` of both providers and the total request time.
`SchedulingGeneration__SolverMode=Csp` in `.env` runs the CSP solver together with both providers.

---

## 8. Results table

| Field | Value |
|---|---|
| Date, tester | |
| Machine: CPU and cores, RAM, GPU and VRAM, OS, Docker memory | |
| NVIDIA driver, Ollama version, backend (CUDA/Vulkan), `ollama ps` processor | |
| Models (`EMBEDDING_MODEL`, `LLM_MODEL`), `LLM_TIMEOUT_SECONDS`, `EMBEDDING_TIMEOUT_SECONDS`, `LLM_NUM_CTX` | |
| Cold calls: first after start (rank, explain), first after idle, first with new POIs | |

| Endpoint and sample | Warm median | p95 | Max | Non-200 / calls | Pass |
|---|---|---|---|---|---|
| rank-pois, `rank_request.json` (15) | | | | | |
| rank-pois, `rank_request_60.json` (60) | | | | | |
| explain, `explain_request.json` (3 items) | | | | | |
| explain, `explain_request_8.json` | | | | | |
| explain, `explain_request_12.json` | | | | | |
| `evaluate_model.py` eval / unseen (clean, p95, timeouts) | | | | | |

| Part | Result |
|---|---|
| B: explanations passing every check / read | |
| C: `aiScore` min, median, max, buckets; order sensible (yes/no); floor/ceiling used | |
| D (optional): total request time, outcomes | |
| **Verdict** (pass, pass up to N stops, or fail) | |

---

## 9. Decision and rollback

- **Pass:** the developer may enable TripMateAi on the demo environment (outside this runbook) with
  the settings of spec §7.6, including `LLM_MODEL=tripmate-explainer`.
- **Pass up to N stops:** longer itineraries get the fallback text; acceptable for the demo or not is
  the developer's call (spec Q7).
- **Fail:** keep Gemini, tune the model or settings (smaller `LLM_NUM_CTX`, a faster GPU), and measure
  again.

Rollback (spec §7.6):

- **Immediate, no credentials:** `AiRanking__Enabled=false` and `AiExplanation__Enabled=false`, then
  recreate the API (`docker compose up -d --no-deps api`). Ranking uses base scores and explanations
  use the fallback text; no provider is called.
- **Back to Gemini:** set `Provider=Gemini` (or remove the line) **and** restore the HTTPS Gemini
  `Endpoint`, the Gemini `ApiKey` and `ModelName`. Changing only `Provider` fails startup, because the
  Gemini validation rejects `http://ai:8000` and a missing model name.

---

## 10. Troubleshooting

| Symptom | Cause and fix |
|---|---|
| `/ready` 503 `models_missing` | The models are not in the Ollama the service uses. Check `ollama list` on the host and `OLLAMA_BASE_URL`; with the Compose Ollama, `ollama-init` may still be pulling. |
| 401 on rank-pois or explain-itinerary | The backend key (`AiRanking__ApiKey`, `AiExplanation__ApiKey`) differs from the service key (`TRIPMATE_AI_API_KEY` in the backend profile, `AI_API_KEY` standalone). The backend logs `invalid-response` and falls back without a retry. |
| 504 on explain-itinerary | The model did not answer within `LLM_TIMEOUT_SECONDS`. Check `ollama ps` shows `100% GPU`, the laptop is on mains power, and no other program uses the GPU. Long itineraries (about 12 stops) hit it on a 4 GB laptop GPU. |
| 503 on both endpoints, each call about 4 s | Ollama is not reachable from the container (host Ollama stopped, or `host.docker.internal` not resolvable). Every request then takes about 12 s before the fallback. |
| Ranking still `success` while Ollama is down | Expected: the service caches embeddings in memory; only new POIs or preferences need Ollama. |
| Port 11434 or 8000 already in use | A native Ollama and the Compose `ollama` both want 11434; use one. Another service on 8000: set `AI_HOST_PORT`. |
| `Conflict. The container name "/tripmate-…" is already in use` | Another stack uses the fixed names; see §2. |
| Backend request 503 `planning.routing_provider_unavailable` | OpenRouteService could not route between some POIs (seen with a 30 km radius). Not an AI issue; use a smaller radius. |
