# UC-10 TripMate AI Provider — Specification

## 1. Document Control and Status

| Field | Value |
| --- | --- |
| Spec ID | `UC-10-tripmate-ai-provider`. Provisional: Jira is not accessible, so there is no official task ID yet (Q1). |
| Use case | UC-10 itinerary generation: TM-215 AI POI ranking and TM-217 itinerary explanation |
| Status | **Implemented and verified on 2026-10-12** (plan T1–T8). Approved earlier the same day with A1, A2 and the D3 extension confirmed and Q8 answered (§6.1 D9). |
| Date | 2026-10-12 |
| Baseline | `origin/develop` @ `31f9a5aa22a531a63e9e1237cafbeb0049662dc5` (merge of PR #62). The local `develop` ref of this clone is stale (`0075fcb`); always compare with `origin/develop`. On 2026-10-12 the branch merged `origin/develop` @ `b66e22b` (PR #65 CSP Phase 3, PR #56 UC-13) without conflicts. |
| Branch and worktree | `feature/phuctv-tripmate-ai-provider` at `D:\FPTUCapstone\Capstone_BE_AI`: `4b536bd` (spec and plan), `949058c` (patch 0003), `a5ebafe`, `e20a9de`, `ee0a320` (tests), `8f174a4` (A2), `9be5725` (runbook), the merge of `origin/develop`, then the verification record |
| Companion repository | `Capstone_AI` at `D:\FPTUCapstone\Capstone_AI`, branch `feature/phuctv-tripmate-ai-service`, committed and not yet pushed: `3283021` (service hardening and dataset), `447836c` (fine-tune, Kaggle and CPU benchmark tooling), `1872508` (diacritics restoration for ranking), `812831a` (backend-size benchmark samples, F5), on top of the scaffold commit `021f720`; 358 tests pass. It gets its own PR. |
| Revision | 2026-10-12, afternoon: aligned with the latest AI service (fine-tuned explainer, model warm-up, diacritics restoration), the laptop GPU measurements, the demo hosting answer to Q2, recommendation R11, and the traveler-preferences dependency (§17); then the CSP and personalization interaction (§18), corrected L10, new L11–L13, adaptation A2, S14. Evening: implementation and verification results (this section, §6.4 L14–L16, §8.8, §9.3, §13.3). |
| Implementation source | Developer patch 0003 `tripmate-ai-providers` (§3, source 2) |
| Plan | `plans/UC-10-tripmate-ai-provider-plan.md` |
| Predecessors | TM-215, TM-216, TM-217. The UC-10 CSP solver spec excluded patch 0003 and `Capstone_AI` (`specs/UC-10-csp-itinerary-solver-spec.md:62-63`, `:617`). |

Verification on 2026-10-12 (details and evidence paths in the plan's progress table):
- **Gates after merging `origin/develop`:** build with 0 warnings; Infrastructure 808 passed and 3
  skipped; Application 1,861 passed; integration 474 passed and 349 skipped locally (SQL Server and
  Redis); the Scheduling SQL Server classes against a disposable SQL Server 2022 container: 61 passed,
  9 skipped (Redis). Before the merge, the whole integration project against SQL Server: 871 passed
  after one more A2 test setup. CI must run the SQL Server and Redis suites without skips.
- **Smoke test S0–S10** (fake Ollama, §13.3): every scenario passed. S7 differs from the table when
  the embeddings are cached (L16); S7b, with an empty cache, matches it.
- **Real models on the laptop GPU (S11–S14):** S11 201 in 5.8 s, explanations in Vietnamese and
  about their own items. S12 is limited by the 12-POI seed to 5 visits (201, explanation 3.8–4.8 s); a
  direct 8-stop call timed out once in 3. S13: accentless and accented preferences give the same
  `aiScore` for all 12 POIs. S14 after the merge: `Scheduling solver outcome csp`, CSP 20 ms on the
  first request and 0.8 ms on the next, both providers `success`, request 8.5 s first and 3.6 s warm.
- **Backend-size benchmark** (`Capstone_AI` `812831a`, §9.3): 60-candidate ranking and 8-item
  explanations pass; 12-item explanations time out in about half of the calls (L14, accepted).
- **Runbook:** `docs/operations/uc10-tripmate-ai-measurement-runbook.md` (plan T7); every command in
  it was run on the laptop.
- A 30 km request failed with `planning.routing_provider_unavailable` because OpenRouteService
  returned an invalid duration. That is existing routing behavior, not part of this task.

Approving this spec approves:
- the binding decisions as recorded in §6.1;
- the adaptations in §6.2: A1 (the hand-merged `.env.example` hunk) and, if confirmed, A2 (no ranking
  call without preference tokens);
- the deviations in §6.3, which follow from applying patch 0003 unchanged and concern the TripMateAi
  providers only;
- the known limitations in §6.4;
- if confirmed, the D3 extension: real-model smoke scenarios S11–S14 on the laptop GPU (§6.1, §13).

The open questions in §16 do not block applying the patch unless marked.

Conventions:
- Backend paths are relative to the repository root.
- `Capstone_AI/...` is the AI service branch described above. Its line numbers are those of commit
  `1872508`; where the code moved, §8 names functions instead of lines.
- `P:<n>` is line n of the patch 0003 file.

## 2. Purpose

Let the backend call the team's self-hosted AI service (`Capstone_AI`, open models served by Ollama)
instead of Google Gemini for the two existing AI features, chosen per feature by configuration.
Gemini stays the default and remains available.

The AI service replaces only the external model call. The backend keeps the same provider contracts,
local budget, error codes, timeouts, output validation and deterministic fallbacks. With the shipped
configuration nothing changes for users.

The work also prepares the evidence needed before anyone switches the demo environment:
- an end-to-end smoke test, first with a fake Ollama and then with the real models on the developer's
  laptop GPU (§13);
- a runbook that a teammate follows to measure latency and Vietnamese quality with the real models (§14).

### 2.1 How one itinerary is generated (plain example)

Minh plans a day in Da Nang: 08:00–17:00, by motorbike, a budget of 500,000 VND, the Cham Museum as a
must-visit, and the interests "Beach" and "Food".

1. **Candidates.** The backend collects the nearby POIs (museums, beaches, markets, restaurants…).
2. **Scores: how much Minh would like each POI.**
   - *Base score* (backend, no AI): 30% category match with the interests, 20% tag match, 25% Minh's
     own history (likes, dislikes, skips), 12.5% scenic quality, 12.5% photo quality.
   - *AI score* (TripMateAi ranking): the AI reads the meaning of the interests, so "Holiday Beach
     Club" scores high for "Beach" even without a "Beach" tag.
   - *Final score* = 60% base + 40% AI. Example: Mỹ Khê Beach 0.85, Hàn Market 0.78, Holiday Beach
     Club 0.70, the cathedral 0.30.
3. **Plan.** The heuristic builds a plan, then the CSP searches for a better one within 2.5 s. Both
   respect the hard rules (the must-visit POI, opening hours, the budget, real travel times by
   motorbike, the end time, rests) and prefer high-scoring POIs first, then less travel. If the CSP
   runs out of time, the heuristic plan is used.
4. **Explanations.** The AI writes one sentence per stop, for example "Matching your interest in the
   beach, Mỹ Khê Beach is your stop for 90 minutes from 10:15 to 11:45."
5. **Result.** The app shows the plan and the sentences. A replay of the same request returns the
   stored plan.

What exists on 2026-10-12, and what this task adds:

| Step | Today on `develop` | After this task | Still missing |
| --- | --- | --- | --- |
| Interests reach the backend | No (§17) | No | The traveler-preferences use case (§17) |
| Base score | Yes (TM-213, TM-215); its interest part is 0 without interests | Same | Interests |
| AI score | Gemini adapter only, disabled by default | TripMateAi selectable; skipped without interests if A2 is confirmed | Interests to be useful |
| Heuristic and CSP | Yes (CSP opt-in; default switch in PR #65) | Unchanged | PR #65 |
| Explanations | Gemini adapter only, disabled by default | TripMateAi selectable, works without interests | Enabling it on a GPU host (Q2) |

## 3. Sources

1. Developer request, 2026-10-12: task lists for the AI repository (1.x), the backend (2.x: spec and
   plan approval per AGENTS.md, provider switch, two providers, Compose profile, tests) and a local
   end-to-end run without real models (3).
2. Developer patch 0003: `C:\Users\han12\Downloads\CSP (2)\CSP\0003-tripmate-ai-providers (1).patch`.
   - Commit `d7c60df` in the developer's tree, 2026-10-09 16:19 +0700.
   - Subject `feat(ai): add TripMate AI providers to replace Gemini behind a config switch` (P:1-22).
   - 13 files, +1010/−12 (P:24-42). Read in full for this spec.
3. Binding decisions given by the developer for this spec (§6.1).
4. The `Capstone_AI` branch, the contract source for §8. Verified on 2026-10-12:
   - its pytest suite passes (351/351 at `1872508`);
   - the image builds and the container runs as a non-root user;
   - against a fake Ollama: `/health` 200, `/ready` 200, rank-pois 401 without a key and 200 with
     it, and explain-itinerary 200 for a rest item whose `poiId` and `poiName` are null;
   - against the real models on the developer's laptop GPU, through the service image and HTTP with
     the key: the measurements of §9.3;
   - the fine-tuned explanation model `tripmate-explainer` (QLoRA on Qwen3-1.7B, trained on Kaggle,
     GGUF Q4_K_M, 1.1 GB): 130 of 130 evaluation itineraries pass every writing rule, against 38%
     and 35% for the base `qwen3:1.7b` (`Capstone_AI/training/llm/README.md`, section "Kết quả").
     It is selected with `LLM_MODEL=tripmate-explainer`; the backend contract does not change.
5. Approved specs that constrain this work:
   - `specs/TM-215-ai-poi-ranking-provider-spec.md`;
   - `specs/TM-216-ai-csp-responsibility-boundary-spec.md`;
   - `specs/TM-217-itinerary-explanation-spec.md`.
6. Precedent for applying a developer patch unchanged: `specs/UC-10-csp-itinerary-solver-spec.md:136`
   and `plans/UC-10-csp-itinerary-solver-plan.md:52-57`.
7. `AGENTS.md`: workflow (:11-26), Result pattern (:43), Git and secrets (:92-107).

## 4. Scope

### 4.1 In scope

1. The provider switch `AiRanking:Provider` / `AiExplanation:Provider` (`Gemini` by default, or
   `TripMateAi`), its options and validation.
2. `TripMateAiPoiRankingProvider` and `TripMateAiExplanationProvider`, which call the AI service with
   the header `X-TripMate-Ai-Key`.
3. DI registration through `AiProviderRegistration`.
4. The Docker Compose `ai` profile (Ollama, model pull, AI service built from
   `${AI_REPO_PATH:-../Capstone_AI}`), `.env.example` and `appsettings.json`.
5. Tests: the 26 cases shipped with the patch and the additional backend tests of §12.2, all with
   controlled HTTP handlers.
6. An end-to-end smoke test backend → AI service → fake Ollama (S0–S10), then with the real models on
   the developer's laptop GPU, including one generation with the CSP and both AI providers (S11–S14,
   §13).
7. A measurement runbook under `docs/operations/` (§14).
8. If confirmed: adaptation A2, no ranking call without preference tokens (§6.2).

### 4.2 Out of scope

- Switching any default to `TripMateAi`. The shipped configuration stays Gemini with both features
  disabled.
- Production hosting and deployment of the AI service.
- Model training or fine-tuning (QLoRA, LightGBM learning-to-rank).
- Changes to the provider contracts (`PoiRankingContracts`, `ItineraryExplanation*`), to the ranking
  blend or to the CSP.
- The measurement itself. The teammate runs the runbook later.
- Changes to the `Capstone_AI` repository. Findings are handed to that PR (§8.8).
- Saving traveler preferences and tagging POIs (§17). Without them the TripMateAi ranking has no
  effect for real users (L10); the explanation provider does not depend on them.

## 5. Verified Baseline

All references were verified on the baseline. Line numbers are those of the unpatched tree.

### 5.1 Ranking (TM-215)

| Element | Location | Behavior |
| --- | --- | --- |
| Port | `src/TripMate.Application/Features/Scheduling/Personalization/PoiRankingContracts.cs:5-26` | `RankAsync(PoiRankingRequest, CancellationToken)` returns `Result<PoiRankingResult>`. The request holds `PreferenceTokens` and candidates `(PoiId, Name, CategoryName, TagNames)`. Result items are `(PoiId, AiScore, Reason?)`. |
| Error codes | `src/TripMate.Application/Features/Scheduling/Personalization/PoiRankingProviderErrorCodes.cs:5-11` | `Quota`, `Network`, `ServerError`, `InvalidResponse`. There is no timeout code. |
| Gemini adapter | `src/TripMate.Infrastructure/AiRanking/HttpPoiRankingProvider.cs` | No logger (:15-18). POST to `Endpoint` with `x-goog-api-key` (:67-71). Budget permit taken before the send; `Quota` when denied (:73-79); released after the send (:94-97). `HttpRequestException`/`IOException` → `Network` (:86-93, :111-118). 429 → `Quota`, 5xx → `ServerError`, other non-2xx → `InvalidResponse` (:187-207). No retry and no timeout of its own. |
| Orchestrator | `src/TripMate.Application/Features/Scheduling/Personalization/PoiRankingOrchestrator.cs` | Calls the provider only when enabled (:100-115), under `CancelAfter(ProviderTimeout)` (:129-131). Caller cancellation is rethrown (:138-150); any other cancellation gives a `timeout` base snapshot (:151-167); failure codes give a base snapshot (:170-186, :328-336). Validation: item count equals the pool, IDs in the pool and unique, `AiScore` in [0,1]; reasons trimmed and cut to 500 (:230-272, :33). Success blends `BaseWeight × base + AiWeight × aiScore` (:208-227); the fallback uses the base score (:303-314). One log line with `_provider.GetType().Name` (:351-369). |
| Snapshot | `src/TripMate.Application/Features/Scheduling/Personalization/PoiRankingSnapshot.cs:22-29` | Carries no `AiScore` and no reason. The API never exposes them. |
| Pool and timeout | `src/TripMate.Application/Features/Scheduling/Personalization/PersonalizationRankingOptions.cs:31`, `:33`; `src/TripMate.Api/appsettings.json:68-69` | `MaxProviderCandidates` 60; `ProviderTimeout` 5 s. |
| Caller | `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs:142-159`, `:460-463` | The ranking snapshot is frozen across the snapshot retry, so the provider is called at most once per request. |

### 5.2 Explanation (TM-217)

| Element | Location | Behavior |
| --- | --- | --- |
| Port and contracts | `src/TripMate.Application/Features/Scheduling/Explanation/IItineraryExplanationProvider.cs:5-10`; `ItineraryExplanationInput.cs:11-34`; `ItineraryExplanationResult.cs:3-9` | Input items `(SequenceNo, PoiId?, PoiName?, CategoryName?, TagNames, Kind, IsMandatory, PlannedArrivalUtc, PlannedDepartureUtc, StayDurationMinutes, EstimatedCost?, TravelDurationToNextMinutes?, CspRecommendationReason)` and context `(PreferenceTokens, StartAtUtc, TimeZoneId, TotalDurationMinutes)`. Output items `(SequenceNo, PoiId?, FriendlyExplanation)`. |
| Input builder | `src/TripMate.Application/Features/Scheduling/Explanation/ItineraryExplanationInput.cs:36-66`, `:78-85` | Every plan item in sequence order, with no item cap (:48-51). `PoiName` and `CategoryName` are null for items without a POI or with missing metadata. |
| Error codes | `src/TripMate.Application/Features/Scheduling/Explanation/ExplanationProviderErrorCodes.cs:5-13` | `Network`, `Quota`, `ServerError`, `InvalidResponse`, `Timeout`. |
| Validator | `src/TripMate.Application/Features/Scheduling/Explanation/ItineraryExplanationValidator.cs:9-52` | All or nothing: same item count, known sequence numbers, exact `PoiId`, trimmed text of 1–500 characters (`src/TripMate.Domain/Entities/ItineraryItem.cs:10`), no duplicates. |
| Gemini adapter | `src/TripMate.Infrastructure/AiExplanation/HttpItineraryExplanationProvider.cs` | At most `Clamp(MaxAttempts, 1, 2)` attempts, each capped at `min(TimeoutSeconds, remaining overall budget)` (:64-67, :73-89). A budget permit per attempt (:91-99). Attempt timeout → `Timeout`, no retry (:113-117). One retry for 429/502/503/504 (:170-179) when the delay fits the remaining budget (:181-189). The delay honors `Retry-After` for 429, otherwise base delay plus 0–49 ms of jitter (:335-355). 429 → `Quota`, 5xx → `ServerError`, other → `InvalidResponse` (:301-321). One log line per attempt without payload (:357-392). |
| Handler | `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs:241-253`, `:836-942` | Runs only for a fresh generation. The fallback text is computed first (:850). The provider is called only when `Enabled` (:856), under an outer `CancelAfter(OverallTimeout)` (:858-860). The result is validated again (:864-876). A failure code becomes the outcome, and `Timeout` sets `timedOut` (:877-886, :950-957). The outer timeout gives `timeout` (:888-894). The log line carries the provider type name (:897-905, :974). Explanations are saved separately; a save failure is logged and ignored (:907-935). |
| Fallback text | `src/TripMate.Application/Features/Scheduling/Explanation/ItineraryExplanationFallback.cs:14-17` and the variants that follow | Deterministic Vietnamese text, independent of the provider. |

### 5.3 Options, validation, DI and budget

| Element | Location | Behavior |
| --- | --- | --- |
| Ranking options | `src/TripMate.Infrastructure/AiRanking/PoiRankingProviderOptions.cs:5-17` | Section `AiRanking`: `Enabled` false, `Endpoint`, `ApiKey`, `ModelName`, `RateLimitPerMinute` 4, `MaxConcurrency` 2. |
| Ranking validator | `src/TripMate.Infrastructure/AiRanking/PoiRankingProviderOptionsValidator.cs` | Budget ranges always (:12, :44-55). Disabled → stop (:13-18). Enabled → absolute HTTPS `Endpoint` (:20-25), `ApiKey` (:27-31), `ModelName` (:33-37). |
| Explanation options | `src/TripMate.Infrastructure/AiExplanation/ExplanationProviderOptions.cs:5-25` | The same keys plus `TimeoutSeconds` 10, `OverallTimeoutSeconds` 20, `MaxAttempts` 2, `RetryBaseDelayMilliseconds` 500. |
| Explanation validator | `src/TripMate.Infrastructure/AiExplanation/ExplanationProviderOptionsValidator.cs` | As for ranking (:12-37, :74-85), plus the timeout, attempt and delay ranges (:39-67). |
| DI | `src/TripMate.Infrastructure/DependencyInjection.cs` | Options bound with `ValidateOnStart` (:166-177, :188-193). One `InMemoryAiProviderBudget` for both features (:178-186). Typed clients `AddHttpClient<IPoiRankingProvider, HttpPoiRankingProvider>()` (:187) and `AddHttpClient<IItineraryExplanationProvider, HttpItineraryExplanationProvider>()` (:194-196), with no `BaseAddress` or `Timeout`. Execution options mapped from `ExplanationProviderOptions` (:197-213). The orchestrator receives `Enabled` (:214-220). |
| Budget | `src/TripMate.Application/Common/Interfaces/IAiProviderBudget.cs:3-6`; `src/TripMate.Infrastructure/Ai/InMemoryAiProviderBudget.cs` | Process-local (:7-9). One window per provider name, `ranking` or `explanation` (:80-84). A call over the concurrency limit is denied, not queued (:40-43); rolling 60 s rate window (:47-61). |

There is no Polly or resilience handler in `src`, and no AI health check in the API.

### 5.4 Configuration files

| File | Content |
| --- | --- |
| `src/TripMate.Api/appsettings.json:71-88` | `AiRanking` and `AiExplanation` with `Enabled: false`, the Gemini endpoint and model, and no API key; explanation 10/20/2/500; both 4 calls per minute and 2 concurrent. |
| `src/TripMate.Api/appsettings.Development.json:40-45` | Both features enabled in Development. |
| `docker-compose.yml` | Services `sqlserver`, `db-init`, `redis`, `api`. `api` runs as Development (:75) and reads `.env` (:62-63). Both features default to enabled and both API keys are required (:91-107; `:?` at :93 and :100). There is no `ai` profile. |
| `.env.example:10-28` | Gemini ranking enabled (:12). Explanation disabled with an empty key (:19, :21). Pre-existing issue: with the file copied verbatim, Compose rejects the empty `AiExplanation__ApiKey` because `docker-compose.yml:100` uses `:?`. |

### 5.5 Tests and CI

| Suite | Location | Relevant content |
| --- | --- | --- |
| Ranking adapter | `tests/TripMate.Infrastructure.UnitTests/AiRanking/HttpPoiRankingProviderTests.cs` | Request shape, budget, status mapping, transport failures, malformed output, cancellation. |
| Ranking DI | `tests/TripMate.Infrastructure.UnitTests/AiRanking/AiRankingDependencyInjectionTests.cs` | The disabled graph resolves (:20-48). Invalid enabled settings fail startup, including an `http://` endpoint (:49-68, :52). Committed defaults are pinned (:103-131). |
| Explanation adapter | `tests/TripMate.Infrastructure.UnitTests/AiExplanation/HttpItineraryExplanationProviderTests.cs` | Retries (:239-293), a retry skipped when the delay exceeds the budget (:296), the attempt timeout (:325-345). |
| Explanation configuration | `tests/TripMate.Infrastructure.UnitTests/AiExplanation/ExplanationProviderOptionsValidatorTests.cs:23-46`; `ExplanationDependencyInjectionTests.cs:14-91` | `http://` rejected (:25); committed defaults (:39-67); no raw `bool` registered (:85). |
| Integration host | `tests/TripMate.Api.IntegrationTests/Infrastructure/TripMateApiFactory.cs` | Forces both features off (:92-95) and replaces the orchestrator with a disabled one (:246-252). |
| CI | `.github/workflows/backend-tests.yml` | Format check of changed `.cs` files (:58-75). The SQL Server (:77-100) and Redis (:102-125) categories must run without skips, then the full suite runs (:127). |

### 5.6 Patch 0003 against the baseline

`git apply --check -v` on the baseline (run 2026-10-12):
- every hunk applies except the single `.env.example` hunk (P:48). Its trailing context
  `# Cloudinary Tour media …` (P:65) was replaced on develop by `.env.example:30-33`;
- `docker-compose.yml` hunk 3 and both `appsettings.json` hunks apply at offset +1, and the
  `DependencyInjection.cs` hunk at +27 (line 184). The five new files and the four options and
  validator files apply exactly;
- `git apply --check --exclude=.env.example` exits 0.

Patch base: the preimage blob IDs of the four modified shared files (P:45, P:69, P:150, P:394) equal
those at develop commit `ec9736f` (2026-10-09 14:11). Between `ec9736f` and the baseline:
- only those four files changed (`.env.example`, `docker-compose.yml`, `appsettings.json`,
  `DependencyInjection.cs`; 39 insertions, 3 deletions, all unrelated to AI);
- nothing changed in the Application `Personalization` and `Explanation` folders,
  `IAiProviderBudget.cs`, `Common/Models`, `ItineraryItemKind.cs` or the AI unit-test folders.

## 6. Decisions

### 6.1 Binding decisions (developer, 2026-10-12)

**D1 — Goal.** Let the backend use the team's self-hosted AI service (repository `Capstone_AI`)
instead of Gemini for TM-215 POI ranking and TM-217 itinerary explanation, selected per feature by
configuration: `AiRanking:Provider` and `AiExplanation:Provider` = `Gemini` | `TripMateAi`. Gemini
stays the default and stays available. Nothing changes for users unless configured.

**D2 — Implementation source.** Apply developer patch 0003 with its content unchanged, except that
the `.env.example` conflict is resolved by hand. Precedent: UC-10 CSP Phase 1 applied patches 0001,
0002 and 0004 unchanged. Any further adaptation that current develop requires is listed explicitly
(§6.2).

**D3 — In scope.**
- The provider switch and its validation.
- Both TripMateAi providers and their DI registration.
- The docker-compose `ai` profile that builds `${AI_REPO_PATH:-../Capstone_AI}`.
- `.env.example` and `appsettings.json`.
- Tests.
- An end-to-end smoke test backend → AI service → fake Ollama, without real models. It covers
  provider selection, the API key header, the success path for ranking and for explanation, and the
  fallback when the AI service is down or returns errors (base ranking scores and the deterministic
  fallback explanation).
  - *Extension confirmed 2026-10-12 (D9), conditional on the host check:* the developer's laptop GPU now
    runs the real models (§9.3), so the smoke test continues with S11–S14 against them, including one
    generation with the CSP and both AI providers (§13, §18).
- A measurement runbook under `docs/operations/` so a teammate can measure latency and Vietnamese
  quality with the real models on their own machine, with thresholds derived from the verified
  backend timeouts.

**D4 — Out of scope.** Switching any default to `TripMateAi`; production hosting; model training or
fine-tuning; changes to the provider contracts (`PoiRankingContracts`, `ItineraryExplanation*`), the
ranking blend or the CSP; the measurement itself.

**D5 — Constraints.** The TM-216 boundary holds: the AI only scores and explains. No database
migration, no new HTTP endpoint, and no user identity sent to the AI service.

**D6 — Identifiers.** The spec and plan IDs are provisional (`UC-10-tripmate-ai-provider`) because
Jira is not accessible. Status: Draft, awaiting developer approval.

**D7 — Open questions to carry.** The official task ID; where the AI service runs for the demo and in
production; who reviews the `Capstone_AI` PR; the merge order (§16).

**D9 — Approval decisions (developer, 2026-10-12, after the revisions).**
- A1 confirmed: the agent merges the `.env.example` hunk by hand (§6.2).
- A2 confirmed: no ranking call without preference tokens, implemented test-first (§6.2, plan T5a).
- D3 extension confirmed, conditional on the host: the agent checks the laptop before S11–S14 and
  runs them if it qualifies. Checked on 2026-10-12: GPU, Ollama with both models, 33 GB free on C:
  and 66 GB on D: qualify; free RAM was 0.8 GB, so other applications are closed before T6 to free
  about 4 GB.
- Q8: Option A. Patch 0003 is applied unchanged apart from A1 and A2; R1–R11 go to a follow-up PR,
  configuration items first (R1, R2, R3, R5, R11).

**D8 — Plan rules.** The plan follows AGENTS.md:
- atomic tasks, each with exact files, steps, a verification command and a Definition of Done;
- TDD for any new code;
- applying the patch unchanged is a task of its own, with no code edits;
- final verification with `dotnet format --verify-no-changes`, `dotnet build`, `dotnet test` and
  `git diff --check`;
- the SQL Server and Redis suites run in CI.

### 6.2 Adaptations

| # | Adaptation | Reason | How |
| --- | --- | --- | --- |
| A1 | Merge the `.env.example` hunk by hand | Its trailing context (P:65) no longer exists on develop (§5.6). | Insert the 13 added lines of P:52-64 (12 commented lines and one blank line), unchanged, after `.env.example:29` (the blank line after `AiExplanation__MaxConcurrency=2`) and before the Cloudinary comment at `:30`. No other line changes. |
| A2 | **Confirmed 2026-10-12 (D9).** Do not call the ranking provider when the request has no preference tokens: return the base snapshot with outcome `provider-skipped`, as when the feature is disabled (R12). | L10: today the provider is called with an empty token list, the service answers 0.5 for every candidate, and the blend `0.6 × base + 0.2` changes the relative skip penalties used by the CSP search and the MiniRouting solver (§18.2). TM-213 expects "no preference tokens → existing ordering unchanged" (`specs/TM-213-traveler-preference-profile-spec.md:181`). It also saves a provider call that carries no information. Applies to Gemini and TripMateAi alike. | `PoiRankingOrchestrator.BuildRankingAsync` (`src/TripMate.Application/Features/Scheduling/Personalization/PoiRankingOrchestrator.cs:100-115`): treat an empty token list like a disabled provider. Test-first in `PoiRankingOrchestratorTests` (plan T5a). Not a provider-contract change. |

A1 is the only adaptation that current develop requires: every other hunk applies with offsets only,
and nothing the patch depends on has changed since its base (§5.6). A2 is a proposed behavior change,
not required by develop.

Not yet verified, because the patch may not be applied before approval: the Release build,
`dotnet format` on the patched files, and the patch's own tests. If any of them fails on the patched
tree, the plan stops and the developer decides on a separate fix (plan T2). Such a fix would be a new
adaptation recorded here.

### 6.3 Deviations that follow from D2

Applying patch 0003 unchanged brings these differences. They apply to the TripMateAi providers only;
Gemini behavior is unchanged in every row. Approving this spec accepts them.

| # | Topic | Approved rule or current behavior | TripMateAi behavior | Note |
| --- | --- | --- | --- | --- |
| V1 | Explanation retry set | TM-217 decision 12: one retry for 429/502/503/504 (`specs/TM-217-itinerary-explanation-spec.md:82-83`). Gemini adapter: `HttpItineraryExplanationProvider.cs:170-173`. | One retry for 429 and 503 only (P:568-573). 502 and 504 fail at once. | Patch rationale (P:445-447): the service already retries invalid model output before it answers 502 (`Capstone_AI/app/explanation.py:45-64`), and 504 means the model was too slow. The service never answers 429 (§8.4). |
| V2 | 504 on explanation | TM-217 §15 maps every 5xx to `server-error` (`specs/TM-217-itinerary-explanation-spec.md:296`). | 504 → `Timeout` code (P:662-665) and attempt outcome `timeout` (P:683). The handler then logs `outcome=timeout` and `timedOut=True` (`CreateSchedulingRequestCommandHandler.cs:882-885`, `:955`). | Ranking keeps 504 → `ServerError` (P:861-867): there is no ranking timeout code. |
| V3 | Retry delay | Gemini honors `Retry-After` for 429, otherwise base delay plus 0–49 ms of jitter (`HttpItineraryExplanationProvider.cs:335-355`). | Fixed `RetryBaseDelayMilliseconds`, no jitter, no `Retry-After` (P:570, P:581-584). | No practical effect: the service sends neither 429 nor `Retry-After`. |
| V4 | Key header | TM-217 describes `x-goog-api-key` for the Gemini transport (`specs/TM-217-itinerary-explanation-spec.md:206`, `:320`). | `X-TripMate-Ai-Key` (P:193, P:509, P:773). | Same handling: read from options, never logged or serialized. |
| V5 | `ModelName` | Required when enabled (both validators :33-37). | Not required for TripMateAi (P:320-321, P:388-389). | Consistent with TM-215 §23: adapter-specific settings are required only when the selected adapter needs them (`specs/TM-215-ai-poi-ranking-provider-spec.md:187`). |
| V6 | Endpoint scheme | Absolute HTTPS (both validators :20-25). | Absolute HTTP or HTTPS, for any host (P:302-313, P:370-381). | Plain HTTP is meant for the Docker network only (§10, Q5). |
| V7 | Attempt log text | `Itinerary explanation provider attempt=…` (`HttpItineraryExplanationProvider.cs:373`). | `TripMate AI explanation attempt=…`, with the same fields (P:699). | Log queries keyed on the message text must match both. |

### 6.4 Known limitations accepted for this task

- **L1 — Provider parsing.** The provider kind is read from raw configuration while services are
  registered (P:253-254).
  - An unparseable value such as `OpenAI` stops host startup with a configuration-binder exception,
    not an options-validation message.
  - An undefined numeric value such as `7` registers the Gemini adapter. The validator rejects it
    only when the feature is enabled, because the new check (P:296-300, P:364-368) runs after the
    validators' early return (:13-18).
  - In both cases no TripMateAi call can happen.
  - P:254 uses the literal `"Provider"` rather than `nameof(...)` (AGENTS.md §4.1). Unchanged per D2.
- **L2 — Profile runtime.** The `ai` profile is CPU-only: there is no GPU device reservation
  (P:98-109). It uses the unpinned image `ollama/ollama:latest` (P:100, P:114), and `ollama-init`
  pulls on every start, so a start without network hangs even when the models are already in the
  volume. The `Capstone_AI` standalone stack now pins `ollama/ollama:0.40.3` and skips the pull for a
  model that is present; the same change for the profile is R2 (§8.9).
  - CPU-only is too slow for the explanation feature: on a 4-vCPU CPU an itinerary takes 13–57 s
    against the 9 s service deadline (§9.3), so the backend would almost always use the fallback text.
  - On the developer's laptop the GPU works only through a native Windows Ollama (driver 531.61 →
    Ollama's Vulkan backend). An Ollama container cannot use it: the bundled CUDA libraries need a
    newer driver, and the Vulkan path is not available inside the container. The demo therefore
    points the `ai` service at the host Ollama (§7.6); the profile hard-codes
    `OLLAMA_BASE_URL: http://ollama:11434` (P:136), so that needs a Compose override or R11 (§8.9).
- **L3 — Forwarded settings.** The profile passes five settings to the service: `AI_API_KEY`,
  `OLLAMA_BASE_URL`, `EMBEDDING_MODEL`, `LLM_MODEL`, `LLM_TIMEOUT_SECONDS` (P:135-139). Every other
  service setting keeps its default (`Capstone_AI/app/config.py:82-103`) unless a local Compose
  override sets it. This includes `RANKING_SIMILARITY_FLOOR`/`CEILING`, `LLM_NUM_CTX`,
  `LLM_KEEP_ALIVE`, `EMBEDDING_TIMEOUT_SECONDS` and the new `LLM_MAX_CONCURRENCY`. Values tuned with
  the runbook therefore need an override or a later profile change (R1 in §8.9, Q2).
- **L4 — Readiness.** The `ai` service publishes no port and has no Compose healthcheck; it relies on
  the image `HEALTHCHECK` (`Capstone_AI/Dockerfile:16-17`). `api` does not depend on `ai`, so requests
  made before the service is ready fall back with outcome `network`.
- **L5 — Two stacks.** The `Capstone_AI` standalone stack no longer sets container names, so it no
  longer clashes with the profile's `tripmate-ollama`, `tripmate-ollama-init` and `tripmate-ai`
  (P:101, P:115, P:131). It names its model volume `tripmate-ollama-data`. The profile's volume
  (P:148) has no `name:`, so Compose prefixes it with the project name and the two stacks still keep
  separate model stores (about 2.5 GB each) until R3 (§8.9). Running both stacks at once starts two
  Ollama instances; the AI README says to stop the standalone stack first.
- **L6 — Size limits.** The backend and the service limit sizes differently (§8.6). Preference-token
  and tag lists longer than 50 are truncated by the service. Any other request over a service limit
  gets 422, which the backend treats as `InvalidResponse` and answers with the fallback.
- **L7 — Local budget.** The process-local budget applies to TripMateAi exactly as to Gemini: 4 calls
  per minute and 2 concurrent calls per feature (`PoiRankingProviderOptions.cs:15-17`,
  `ExplanationProviderOptions.cs:23-25`), and every explanation retry uses a permit. Raising it is a
  configuration choice outside this task. The service runs one LLM call at a time by default
  (`LLM_MAX_CONCURRENCY=1`, matching Ollama's `OLLAMA_NUM_PARALLEL=1`). A second concurrent
  explanation waits only while its own time budget allows and otherwise gets 503 at once (§8.4), so
  with the backend's 2 concurrent explanation permits the second schedule usually falls back. R5
  (§8.9) sets the backend to 1.
- **L8 — Existing `.env.example` issue.** Unchanged by this task (§5.4). Uncommenting the TripMateAi
  block sets the missing key.
- **L9 — Doc comment slip.** The comment on `ExplanationProviderOptions.Provider` gives
  `http://ai:8000/v1/rank-pois` as its example endpoint (P:273); it should be `/v1/explain-itinerary`.
  Unchanged per D2.
- **L10 — No preference input in production.** No production path writes
  `TravelerProfiles.interest_tags_json` (§17): real travelers send no preference tokens. Without A2
  the ranking provider is still called with an empty token list and the service answers the neutral
  0.5 for every candidate. That is **not** a no-op: the blend becomes `0.6 × base + 0.2`. The order is
  kept, so the heuristic and the CSP product rule rank the same, but the skip penalties that the CSP
  search and the MiniRouting solver use are relative to the best score (§18.2). With
  `SolverMode=Csp` (the default after PR #65), enabling TripMateAi could therefore change CSP plans
  for travelers without preferences whenever the search stops at its node budget or time limit. It
  also adds a provider call (about 2 s on the laptop GPU) that carries no information. A2 removes
  both effects. The explanation provider is unaffected.
- **L11 — Two score scales.** A successful ranking call gives `0.6 × base + 0.4 × aiScore`; any
  ranking fallback gives the base score alone (`PoiRankingOrchestrator.cs:217-227`, `:303-314`). The
  same request can therefore produce a different fresh plan when the AI answers in time and when it
  does not (for example on a busy host). A replay returns the stored itinerary, so a stored plan
  never changes. Same class of effect as the CSP wall-clock limit
  (`specs/UC-10-csp-itinerary-solver-spec.md`, R5).
- **L12 — Regenerate and Adjust Items do not use AI ranking.** `ItineraryVersionService` sets
  `EffectiveDesirabilityScore` from `TravelerPreferenceScoring` only
  (`src/TripMate.Application/Features/Itineraries/Common/ItineraryVersionService.cs:69-74`, `:192-197`).
  AI personalization applies to the first generation only; a regenerated plan is ranked without it.
  Unchanged by this task; a follow-up if personalization must survive regeneration.
- **L13 — Host-dependent AI scores.** Embeddings computed on a GPU and on a CPU can differ in the last
  digits. `aiScore` is rounded to 4 decimals, but candidates with near-equal scores can swap places, so
  the solvers can choose differently on two hosts for the same request.
- **L14 — Long itineraries on a 4 GB laptop GPU** (measured 2026-10-12). With the backend-size
  samples, 12-item explanations timed out at the service's 9 s deadline in 6 of 11, 2 of 4 and 7 of 11
  calls; the successful ones took 8.7–9.0 s. The backend does not retry a 504 and uses the fallback
  text. Up to 8 items no call failed (median about 5.7 s). **Accepted by the developer for the demo**:
  the settings stay as they are, and current Da Nang plans have 4–5 items. If longer plans matter
  later: `LLM_TIMEOUT_SECONDS` of about 9.5 (still below the backend's 10 s), or fewer output tokens
  for long plans in the AI service.
- **L15 — An Ollama outage costs about 12 s per fresh generation.** Each service call waits about
  3.85 s before answering 503; ranking once plus two explanation attempts (503 is retried) add about
  12 s before both fallbacks (S7b). The bound is the same as for a slow Gemini (§9.3). Watching
  `/ready` on the demo host catches it before users do.
- **L16 — The embedding cache hides an Ollama outage from ranking.** Candidates and preferences already
  in the service's in-memory cache (`Capstone_AI/app/ranking.py`, `EmbeddingCache`) are ranked without
  Ollama, so ranking stays `success` (S7). New POIs or preferences fail with 503. Explanations always
  need Ollama.

## 7. Design

### 7.1 Provider switch

- `src/TripMate.Infrastructure/Ai/AiProviderKind.cs` (new, P:169-195) defines
  `enum AiProviderKind { Gemini = 0, TripMateAi = 1 }` and `TripMateAiHeaders.ApiKey = "X-TripMate-Ai-Key"`.
- Both options classes gain `Provider`, default `Gemini` (P:271-275, P:339-343). The configuration
  binder parses the value case-insensitively.
- `AiProviderRegistration` (new, P:196-256) selects the implementation once at startup, per feature.
  `TripMateAi` registers the TripMateAi provider; anything else, including a missing value, registers
  the Gemini adapter.
- `Enabled` still decides whether any call happens. The orchestrator receives it at construction
  (`DependencyInjection.cs:214-220`, `PoiRankingOrchestrator.cs:100-115`) and the handler checks it
  (`CreateSchedulingRequestCommandHandler.cs:856`). With `Provider=TripMateAi` and `Enabled=false`,
  the TripMateAi provider is registered but never called.

Validation rules. The budget ranges are always checked; every other rule applies only when the
feature is enabled.

| Rule | Gemini | TripMateAi |
| --- | --- | --- |
| `Provider` is a defined value | required (P:296-300, P:364-368) | required |
| `Endpoint` | absolute HTTPS; message unchanged | absolute HTTP or HTTPS; the message names TripMateAi |
| `ApiKey` | required | required (the shared key) |
| `ModelName` | required | optional |
| Explanation timeouts, attempts and delay | unchanged (`ExplanationProviderOptionsValidator.cs:39-67`) | unchanged |

### 7.2 TripMateAiPoiRankingProvider

`src/TripMate.Infrastructure/TripMateAi/TripMateAiPoiRankingProvider.cs` (new, P:716-898). Its
constructor matches the Gemini adapter: `HttpClient`, `IOptions<PoiRankingProviderOptions>` and an
optional `IAiProviderBudget` (P:741-744).

1. **Body.** `{"preferenceTokens":[…],"candidates":[{"poiId","name","categoryName","tagNames"}]}`
   with Web JSON defaults (P:746, P:761-767). No model name and no prompt are sent.
2. **Request.** `POST Endpoint` with the header `X-TripMate-Ai-Key: <ApiKey>` (P:769-773).
3. **Budget.** A `ranking` permit is taken before the send. When it is denied, the result is
   `Quota` and no request is made (P:775-781). The permit is released when the send ends (P:796-799).
4. **Transport.** `HttpRequestException` or `IOException` while sending or reading → `Network`
   (P:788-795, P:814-821). Invalid JSON → `InvalidResponse` (P:822-825).
5. **Status.** 429 → `Quota`; 500–599 → `ServerError`; any other non-2xx, such as 401 or 422 →
   `InvalidResponse` (P:852-872).
6. **Success.** `ranked` must be present, and every item non-null with `poiId` and `aiScore`;
   otherwise `InvalidResponse` (P:827-831). Range, count, membership, duplicates and reason length
   are left to the orchestrator (P:833-838), as for Gemini.
7. **No retry, no own timeout, no logging.** Caller cancellation propagates. The orchestrator's 5 s
   timeout and base-score fallback apply unchanged.

### 7.3 TripMateAiExplanationProvider

`src/TripMate.Infrastructure/TripMateAi/TripMateAiExplanationProvider.cs` (new, P:416-715). Its
constructor takes `HttpClient`, `IOptions<ExplanationProviderOptions>`, an optional `ILogger` and an
optional `IAiProviderBudget` (P:449-453).

1. **Body.** The `ItineraryExplanationInput` itself, with Web JSON defaults and camelCase enum
   strings, so `kind` is `visit` or `rest` and null fields are written (P:455, P:505-508, P:708-713).
   The service receives the same fields that the Gemini adapter puts in its prompt input.
2. **Header.** `X-TripMate-Ai-Key` (P:509).
3. **Attempts.** `Clamp(MaxAttempts, 1, 2)`. Each attempt is cancelled after
   `min(TimeoutSeconds, remaining OverallTimeoutSeconds)`. When no overall budget remains, the result
   is `Timeout` (P:473-490).
4. **Budget.** An `explanation` permit per attempt. A denial returns `Quota` and logs `local-budget`
   (P:492-499). The permit is released after the send (P:527-530).
5. **Failures during an attempt.** Attempt timeout → `Timeout`, no retry (P:512-516, P:542-546).
   Transport errors → `Network` (P:517-526, P:547-556). Caller cancellation propagates.
6. **Success.** The root must be exactly `{items}` and each item exactly
   `{sequenceNo, poiId, friendlyExplanation}` (P:456-457, P:591-616, P:632-636). Then
   `ItineraryExplanationValidator.TryValidate` runs, and the texts are returned trimmed (P:618-629).
   Any failure → `InvalidResponse`, no retry (P:558-562).
7. **Non-2xx.** 429 and 503 are retried once, when `attempt < maxAttempts` and the fixed delay is
   shorter than the remaining overall budget (P:568-584). Otherwise: 429 → `Quota`, 504 → `Timeout`,
   other 5xx → `ServerError`, other → `InvalidResponse` (P:653-678).
8. **Logging.** One line per attempt with attempt, HTTP status, latency, outcome and retry flag;
   Information on success, Warning otherwise; no payload, text or key (P:688-706).

The handler flow (§5.2) is unchanged: it validates again, logs
`provider=TripMateAiExplanationProvider`, and uses the deterministic fallback on any failure.

### 7.4 Dependency injection

- `DependencyInjection.cs:187` becomes `services.AddPoiRankingHttpProvider(configuration);`, and
  `:194-196` become `services.AddItineraryExplanationHttpProvider(configuration);` (P:401-402,
  P:409-412). The required `using TripMate.Infrastructure.Ai;` already exists (`:21`).
- Everything else is unchanged: options binding and `ValidateOnStart`, the shared budget, the
  execution options and the orchestrator registration (`:166-186`, `:188-193`, `:197-220`).
- Both providers stay typed clients registered for the same service interfaces, without
  `BaseAddress` or `HttpClient.Timeout`. The orchestrator's and the provider's cancellation sources
  bound every call.

### 7.5 Docker Compose `ai` profile

Added to `docker-compose.yml` (P:68-148). Without `--profile ai` nothing new starts; the `api`
service only gains two environment lines that default to Gemini.

| Service | Definition | Notes |
| --- | --- | --- |
| `api` (changed) | `AiRanking__Provider: ${AiRanking__Provider:-Gemini}` (P:76) and `AiExplanation__Provider: ${AiExplanation__Provider:-Gemini}` (P:84) | No `depends_on` on `ai`. |
| `ollama` | Image `ollama/ollama:latest`, container `tripmate-ollama`, volume `tripmate-ollama-data:/root/.ollama`, `mem_limit ${OLLAMA_MEM_LIMIT:-6g}`, healthcheck `ollama list` (P:98-109) | No published port, no GPU. |
| `ollama-init` | One-shot `ollama pull ${EMBEDDING_MODEL:-bge-m3} && ollama pull ${LLM_MODEL:-qwen3:1.7b}` against `ollama:11434`, after `ollama` is healthy (P:111-122) | The first run downloads about 2.5 GB (P:111). |
| `ai` | Image `tripmate-ai:latest` built from `${AI_REPO_PATH:-../Capstone_AI}`, container `tripmate-ai`. Environment: `AI_API_KEY: ${TRIPMATE_AI_API_KEY:-}`, `OLLAMA_BASE_URL: http://ollama:11434`, the two models, `LLM_TIMEOUT_SECONDS: ${LLM_TIMEOUT_SECONDS:-9}`. Waits for `ollama` healthy and `ollama-init` completed (P:124-144). | No published port. `AI_API_KEY` deliberately has no `:?`, because Compose interpolates profile services too (P:133-134). The service itself refuses to start without a key (`Settings.validate`, `Capstone_AI/app/config.py:105` ff.). |
| Volume | `tripmate-ollama-data` (P:148) | |

The backend reaches the service as `http://ai:8000` on the default Compose network, as it reaches
`sqlserver` and `redis` (`docker-compose.yml:78-79`).

### 7.6 Configuration

| Key | Code default | `appsettings.json` | Compose `api` | Read by |
| --- | --- | --- | --- | --- |
| `AiRanking:Provider` (new) | `Gemini` (P:343) | `"Gemini"` (P:157) | `${AiRanking__Provider:-Gemini}` | registration and validator |
| `AiExplanation:Provider` (new) | `Gemini` (P:275) | `"Gemini"` (P:165) | `${AiExplanation__Provider:-Gemini}` | registration and validator |
| Other `AiRanking:*` and `AiExplanation:*` keys | unchanged | unchanged | unchanged | as today |
| `TRIPMATE_AI_API_KEY`, `EMBEDDING_MODEL`, `LLM_MODEL`, `LLM_TIMEOUT_SECONDS`, `OLLAMA_MEM_LIMIT`, `AI_REPO_PATH` | — | — | `ai` profile only | AI service and Ollama |

`.env.example` gains the commented block of P:52-64 (A1).

To use the AI service locally in Docker:
1. Set `TRIPMATE_AI_API_KEY` to a random secret.
2. Keep ranking enabled (`.env.example:12`) and set `AiRanking__Provider=TripMateAi`,
   `AiRanking__Endpoint=http://ai:8000/v1/rank-pois` and `AiRanking__ApiKey=<the same key>`.
3. Set `AiExplanation__Enabled=true`, `AiExplanation__Provider=TripMateAi`,
   `AiExplanation__Endpoint=http://ai:8000/v1/explain-itinerary` and
   `AiExplanation__ApiKey=<the same key>`.
4. Run `docker compose --profile ai up -d --build`.

Outside Docker (`dotnet run`), the endpoints must point at a reachable service, for example the
standalone `Capstone_AI` stack, which publishes port 8000 on 127.0.0.1 only (`Capstone_AI/docker-compose.yml`, service `ai`).

**Demo configuration on the developer's laptop GPU** (Q2, measured in §9.3). The models run in a
native Windows Ollama; only the AI service runs in Compose.
1. Host: NVIDIA driver ≥ 531 (installed 531.61); Ollama for Windows 0.40.3 (per-user install, starts
   at login); `ollama pull bge-m3`; `ollama create tripmate-explainer -f Modelfile` in the folder that
   holds `tripmate-explainer-q4_k_m.gguf` (Kaggle kernel `tripmate-explainer-qlora`, version 4
   output). The models use about 2.2 GB of the 4 GB VRAM.
2. Backend `.env`: the TripMateAi settings of steps 1–3 above, plus `LLM_MODEL=tripmate-explainer`
   and `AiExplanation__MaxConcurrency=1` (R5).
3. A local Compose override (not committed) that sets
   `services.ai.environment.OLLAMA_BASE_URL: http://host.docker.internal:11434` and removes the
   `ai` service's dependency on `ollama` and `ollama-init` (`depends_on: !reset {}`), then
   `docker compose --profile ai up -d --build ai`. R11 replaces the override with a variable.
4. The laptop stays on mains power during the demo: on battery the GPU is throttled.

Rollback:
- **Immediate, no credentials:** `AiRanking__Enabled=false` and `AiExplanation__Enabled=false`.
  Ranking uses base scores and explanations use the fallback text; no provider is called.
- **Back to Gemini:** set `Provider=Gemini` (or remove the key) **and** restore the HTTPS Gemini
  `Endpoint`, the Gemini `ApiKey` and `ModelName`. Changing only `Provider` fails startup, because
  Gemini validation rejects `http://ai:8000` and a missing model name
  (`PoiRankingProviderOptionsValidator.cs:20-37`, `ExplanationProviderOptionsValidator.cs:20-37`).

`appsettings.Development.json:40-45` enables both features, so every Development run needs a valid
key for the selected provider.

### 7.7 API, DTOs, domain and persistence

No change (D5). `POST /api/v1/scheduling-requests` keeps its request and response DTOs
(`src/TripMate.Api/Controllers/V1/Requests/CreateSchedulingRequest.cs`,
`src/TripMate.Application/Features/Scheduling/Common/SchedulingResponseDto.cs`). No domain rule
changes, and there is no migration. The contract with the AI service (§8) stays inside the two
Infrastructure adapters.

## 8. Contract with the AI Service

### 8.1 Endpoints and authentication

| Endpoint | Key | Response |
| --- | --- | --- |
| `GET /health` | no | 200 `{"status":"ok"}` (`Capstone_AI/app/main.py:155-157`) |
| `GET /ready` | no | 200 `{"status":"ready","models":{…}}` when Ollama lists both models; 503 `models_missing` or `ollama_unreachable` (`:159-170`) |
| `POST /v1/rank-pois` | yes | `RankingResponse` (`:172-182`) |
| `POST /v1/explain-itinerary` | yes | `ExplanationResponse` (`:184-195`) |

- The key travels in `X-TripMate-Ai-Key` and is compared in constant time. A missing or wrong key
  gets 401 `{"detail":"Invalid or missing API key."}`. An ASGI middleware checks the key on `/v1/*`
  before the body is read, so an unauthenticated request never reaches JSON parsing (`_ApiGuard` in
  `Capstone_AI/app/main.py`).
- Bodies over 1 MiB (`MAX_BODY_BYTES`) get 413, by `Content-Length` or while a chunked body is read.
  Backend requests are tens of KB.
- The service refuses to start with an empty `AI_API_KEY` unless `AI_ALLOW_ANONYMOUS=true`, and
  refuses `AI_ALLOW_ANONYMOUS=true` together with a key (`Settings.validate` in
  `Capstone_AI/app/config.py`). `/docs`, `/redoc` and `/openapi.json` are off unless
  `AI_ENABLE_DOCS=true`.
- JSON field names are camelCase, and unknown request fields are ignored (`ApiModel` in
  `Capstone_AI/app/schemas.py`).

### 8.2 rank-pois

- **Request** (`RankingRequest` in `Capstone_AI/app/schemas.py`): `preferenceTokens` (the first 50
  are used, each ≤ 1000 characters) and `candidates` (at most 200), each
  `{poiId, name (≤ 400), categoryName (≤ 200), tagNames (the first 50 are used, each ≤ 120)}`. This
  is the body of §7.2.
- **Response** (`RankingResponse`, `schemas.py:67-74`): `{"ranked":[{"poiId","aiScore" in [0,1],"reason" string or null}]}`.
- **Behavior** (`Capstone_AI/app/ranking.py`):
  - one item per candidate, in input order, with the input IDs (`ranking.py:111-124`);
  - no candidates → an empty list (`:83-84`);
  - no usable preference tokens → every score 0.5 and no reason (`:88-92`);
  - scores rounded to 4 decimals (`:119`); reasons of at most 200 characters, or null (`_reason`, `:190`).
- The backend strips diacritics from preference tokens
  (`src/TripMate.Application/Features/Scheduling/Common/TravelerPreferenceScoring.cs:58-65`). The
  service restores them before embedding (`restore_diacritics` in `Capstone_AI/app/textutil.py`):
  first from the tags and categories in the same request, then from a built-in travel vocabulary
  (`Capstone_AI/app/vocabulary.py`: the web interest labels, sample categories, Vietnamese OSM tags
  and common travel words), then from the longest multi-word terms inside the token. Measured on the
  laptop GPU with 60 OSM POIs and `bien, am thuc, van hoa`: without the vocabulary the ranking was
  near-random (hotels first, restaurants last); with it the ranking equals the accented input's.
  R10 (§8.9) would remove the dependence on that list.

### 8.3 explain-itinerary

- **Request** (`ExplanationRequest` in `Capstone_AI/app/schemas.py`): the serialized
  `ItineraryExplanationInput`; 1–50 `items`. `poiId`, `poiName` and `categoryName` may be null, as
  for rest items. `kind` is `visit` or `rest`.
  - `startAtUtc`, `plannedArrivalUtc` and `plannedDepartureUtc` must carry a UTC offset; the
    backend's `DateTimeOffset` always does.
  - `timeZoneId` must be an IANA ID such as `Asia/Ho_Chi_Minh`; anything else gets 422 rather than
    silently wrong local times. The web and mobile clients send `Asia/Ho_Chi_Minh`; a Windows ID
    such as `SE Asia Standard Time`, which the backend validator accepts, falls back (R7, §8.9).
  - String limits: `poiName` ≤ 400, `categoryName` ≤ 200, each tag ≤ 120,
    `cspRecommendationReason` ≤ 1000, each preference token ≤ 1000, `timeZoneId` ≤ 64. Tag and token
    lists keep their first 50 entries.
  - Optional `language`: `vi` (default; the backend does not send it) or `en`.
- **Response** (`ExplanationResponse`, `schemas.py:128-129`): `{"items":[{"sequenceNo","poiId","friendlyExplanation"}]}` with exactly
  these three keys. `poiId` is written even when null, which the provider's exact-property check
  requires (P:456-457).

### 8.4 Status codes and backend mapping

The Gemini column shows how the Gemini adapter maps the same status, for comparison.

| AI status | Cause (`Capstone_AI/...`) | Ranking (both adapters) | Explanation, TripMateAi | Explanation, Gemini adapter |
| --- | --- | --- | --- | --- |
| 200 | success | parsed; the orchestrator validates | parsed; validated twice | parsed; validated twice |
| 401 | missing or wrong key, checked before the body is read (`_ApiGuard`, `app/main.py`) | `InvalidResponse` | `InvalidResponse`, no retry | `InvalidResponse`, no retry |
| 413 | body over 1 MiB (`_ApiGuard`) | `InvalidResponse` | `InvalidResponse`, no retry | `InvalidResponse`, no retry |
| 422 | request fails schema validation, including the limits of §8.6, a naive timestamp or a non-IANA `timeZoneId` | `InvalidResponse` | `InvalidResponse`, no retry | `InvalidResponse`, no retry |
| 502 | model output still invalid after the service's own attempts, an invalid Ollama payload, or an Ollama 4xx other than 404 | `ServerError` | `ServerError`, no retry (V1) | `ServerError` after one retry |
| 503 | Ollama unreachable, Ollama 5xx or 404 (model not pulled), or, for explanation only, the LLM slot is busy and the request's budget cannot cover a wait (`LLM_MAX_CONCURRENCY`) | `ServerError` | `ServerError` after one retry | `ServerError` after one retry |
| 504 | Ollama timeout | `ServerError` | `Timeout`, no retry (V1, V2) | `ServerError` after one retry |
| 500 | unexpected error | `ServerError` | `ServerError`, no retry | `ServerError`, no retry |
| 429 | never sent by the service | `Quota` | `Quota` after one retry | `Quota` after one retry |

### 8.5 Timing invariants

Each service deadline sits below the matching backend timeout, so a slow model shows up as a 504
that the backend maps, not as a backend cancellation.

| Feature | Service deadline | Backend timeout |
| --- | --- | --- |
| Ranking | One embed call bounded by `EMBEDDING_TIMEOUT_SECONDS`, 4 s (`Capstone_AI/app/config.py:91`, `app/ranking.py` `_embed`, `:131-139`) | `Personalization:ProviderTimeout`, 5 s (`PersonalizationRankingOptions.cs:33`) |
| Explanation | `LLM_TIMEOUT_SECONDS`, 9 s, covers all internal attempts; a second attempt needs at least 1.5 s left (`app/config.py:90`, `app/explanation.py:29`, `:46-59`) | `AiExplanation:TimeoutSeconds` 10 s per attempt; `OverallTimeoutSeconds` 20 s (`ExplanationProviderOptions.cs:15-17`) |

Any change to these values must keep each service deadline below its backend timeout
(`Capstone_AI/.env.example:18-21`).

### 8.6 Size limits

| Field | Backend | Service | Over the limit |
| --- | --- | --- | --- |
| Ranking candidates | `MaxProviderCandidates` 60, with a lower bound only (`PersonalizationRankingOptions.cs:31`; validator `:105-111`) | 200 | 422 → ranking fallback |
| Preference tokens | From `interest_tags_json`, at most 1000 characters (`TravelerProfileConfiguration.cs:16-18`); no count limit | First 50 used, each ≤ 1000 characters (both endpoints) | Extra tokens ignored |
| Tags per candidate or item | All of the POI's tags (`PoiRankingOrchestrator.cs:117-125`); tag name 60 | First 50 used, each ≤ 120 | Extra tags ignored |
| POI name | 200 (`PointOfInterest.cs:8`) | 400 | — |
| Category name | 100 (`PoiCategoryConfiguration.cs:20-23`) | 200 | — |
| Recommendation reason | 500 (`ItineraryItem.cs`) | 1000 | — |
| Explanation items | Every plan item, no cap (`ItineraryExplanationInput.cs:48-51`) | 1–50 | 422 → explanation fallback |
| Request body | tens of KB | 1 MiB | 413 → fallback |
| Explanation text | 500 (`ItineraryItem.cs:10`) | Cut to 500 UTF-16 units by the service (`clean_explanation`) | The backend validator still rejects anything over 500. |

The service limits on strings sit at about twice the backend's domain limits, so no payload the
backend can build is rejected (`test_largest_payload_the_backend_can_send_is_accepted` in
`Capstone_AI/tests/test_contract.py`).

### 8.7 Service behavior the backend depends on

- **Repair before answering** (`parse_model_output` and `clean_explanation` in
  `Capstone_AI/app/explanation.py`; tests in `tests/test_explanation.py`,
  `tests/test_review_fixes.py`, `tests/test_explanation_hardening.py`):
  - `sequenceNo` and `poiId` are copied from the request;
  - texts are matched by `sequenceNo` when the model's numbers are unique and complete, otherwise
    by position; either way each echoed `poiId` must equal its item's (`null` or `0` for a rest
    item), or the output is rejected, so a text is never attached to another stop;
  - surrounding quotes are stripped;
  - empty texts, texts with CJK characters or fullwidth punctuation, texts without letters and, for
    Vietnamese, texts without any Vietnamese letter (the model drifted into English) are rejected
    and retried;
  - texts over 500 UTF-16 units are cut at a sentence or word boundary.
- **One LLM call at a time** (`ExplanationService._take_slot`): the slot is held for the whole retry
  loop; time spent waiting for it counts against `LLM_TIMEOUT_SECONDS` (§8.4, L7).

  TM-217 forbids per-item repair and truncation in the backend
  (`specs/TM-217-itinerary-explanation-spec.md:241-243`). The backend keeps that rule and validates
  strictly. Whether repair inside the service is acceptable is Q6.
- **Embedding cache.** Embeddings are cached in memory, so the first ranking after a restart, or with
  new POIs, is slower (`app/ranking.py:131-139`).
- **Model unloading.** Models stay loaded for `LLM_KEEP_ALIVE`, `-1` (never unload) by default, which
  the service sends with every Ollama call; `MODEL_WARMUP=true` loads both models at start. It accepts
  a duration with a unit (`30m`, `1h`) or a whole number of seconds; any other value stops startup.
  With a finite value, the first explanation after a longer idle period reloads the model (about 18 s,
  §9.3).
- **Process.** One uvicorn worker, running as non-root user 10001 (`Capstone_AI/Dockerfile:12-13`,
  `:19`).

### 8.8 Findings for the Capstone_AI pull request

These are not part of this backend task. They are listed so that the AI PR can fix them before the
runbook relies on them. Status 2026-10-12: F1, F2, F3, F4 (comment), F6 and F7 are fixed on the
`Capstone_AI` branch (commits `3283021`–`1872508`), together with 20 more review findings (auth before body read, body and
field limits, IANA time zones, language check, LLM concurrency slot, pinned versions, CI token
permissions). F5 was fixed on 2026-10-12 by `812831a`: backend-size samples, their generator
`scripts/make_backend_samples.py`, and a benchmark that prints the sample name and the maximum.

- **F1.** `README.md:77` says that switching back to Gemini only needs the two `Provider` lines.
  Gemini validation also needs the HTTPS endpoint, the Gemini key and `ModelName` (§7.6).
- **F2.** `README.md:102` and `scripts/benchmark.py:8` name `PersonalizationRanking:ProviderTimeout`.
  The key is `Personalization:ProviderTimeout` (`PersonalizationRankingOptions.cs:9`;
  `appsettings.json:57`, `:69`).
- **F3.** `README.md:52` maps 504 to `Timeout` for both features. That holds for explanation only
  (§8.4).
- **F4.** `app/prompts.py:17` says the prompt follows the Gemini rules, but `:20` asks for at most
  300 characters where Gemini asks for 500 (`HttpItineraryExplanationProvider.cs:26`). This is
  harmless for validation; the comment or the number should be aligned.
- **F5.** `scripts/benchmark.py:22-25` measures only the committed samples, which hold 15 candidates
  and a 3-item itinerary. The backend sends up to 60 candidates and full-day plans; the largest CSP
  plan in the UC-10 benchmark has 11 visits (`specs/UC-10-csp-itinerary-solver-spec.md:415`).
  Samples of backend size are needed (§14).
- **F6.** `scripts/benchmark.py:54` reports as "p95" the 9th of 10 values and prints no maximum.
- **F7.** The standalone stack and the backend profile share container names (L5). The README should
  say to run one stack at a time.

### 8.9 Recommended changes to patch 0003 (not adopted; developer decides, Q8)

The `Capstone_AI` review found these on the backend side. Each one changes patch content, so under
D2 none is part of this task unless the developer adopts it as an adaptation (§6.2).

| # | Change | Why | Where |
| --- | --- | --- | --- |
| R1 | In the `ai` service `environment:`, add the other service settings as bare keys: `EMBEDDING_TIMEOUT_SECONDS:`, `LLM_TEMPERATURE:`, `LLM_NUM_CTX:`, `LLM_KEEP_ALIVE:`, `LLM_MAX_CONCURRENCY:`, `EXPLANATION_MAX_ATTEMPTS:`, `RANKING_SIMILARITY_FLOOR:`, `RANKING_SIMILARITY_CEILING:`, `RANKING_LEXICAL_BOOST:`, `EMBEDDING_CACHE_SIZE:`. A bare key forwards the value when the backend `.env` sets it and leaves it unset otherwise. Do not forward `AI_ALLOW_ANONYMOUS`, and do not use `env_file: .env` (it would copy every backend secret into the AI container). | L3: values tuned with the runbook are silently dropped today. | P:135-139 |
| R2 | Pin `ollama/ollama:0.40.3` on `ollama` and `ollama-init`; change the `ollama-init` entrypoint to `for M in ${EMBEDDING_MODEL:-bge-m3} ${LLM_MODEL:-qwen3:1.7b}; do ollama show "$$M" >/dev/null 2>&1 \|\| ollama pull "$$M" \|\| exit 1; done`. | L2: reproducible runtime; a start without network no longer hangs on the pull. | P:100, P:114-120 |
| R3 | Give the volume a fixed name: `tripmate-ollama-data: { name: tripmate-ollama-data }`. | L5: one model store shared with the standalone stack. | P:148 |
| R4 | `mem_limit: ${AI_MEM_LIMIT:-512m}` on `ai`. | Caps the Python service; Ollama keeps its own `OLLAMA_MEM_LIMIT`. | P:124-144 |
| R5 | In the TripMateAi block of `.env.example`, add `AiExplanation__MaxConcurrency=1`. | L7: the service runs one LLM call at a time; a second concurrent call would mostly fall back after a 503. | P:52-63 |
| R6 | Accept `http://` for TripMateAi only for internal hosts (Docker service name without a dot, loopback, private or link-local address); everything else needs HTTPS. | V6, §10 item 3; replaces the operational rule of Q5. | Both validators (P:296-313, P:364-381) |
| R7 | Before sending an explanation, convert a Windows time-zone ID with `TimeZoneInfo.TryConvertWindowsIdToIanaId`. | The service now rejects non-IANA IDs with 422 (§8.3). | `TripMateAiExplanationProvider.ExplainAsync` |
| R8 | Add an upper bound `MaxProviderCandidates <= 200` to `PersonalizationRankingOptionsValidator`. | Keeps a configuration change from exceeding the service limit (§8.6). | Application options validator |
| R9 | Give `TripMateAiPoiRankingProvider` an optional `ILogger` and log the HTTP status and outcome on failures, like the explanation provider. | The orchestrator log shows only the outcome, so a 401 (wrong key) looks like any invalid response. | P:716-898 |
| R10 | Send the traveler's original interest tags (with diacritics) to the AI provider alongside or instead of the normalized tokens. This needs a field in the provider contract (`PoiRankingContracts`), which D4 keeps out of this task. | The backend strips diacritics (`TravelerPreferenceScoring.cs:58-65`) and `bge-m3` ranks accentless Vietnamese poorly: on the laptop GPU (2026-10-12), 60 OSM POIs ranked for `bien, am thuc, van hoa` put hotels first and restaurants last. The service now restores diacritics from a built-in travel vocabulary (`Capstone_AI/app/vocabulary.py`), which gives the same ranking as accented input for the known terms; R10 removes the dependence on that list. | Application contract + both adapters |
| R11 | Make the service's model host configurable: `OLLAMA_BASE_URL: ${TRIPMATE_AI_OLLAMA_BASE_URL:-http://ollama:11434}`, and document that `ollama`/`ollama-init` are skipped when it points at a host Ollama (start only `ai`, or move the two Ollama services to a separate profile). | L2: on the developer's laptop the GPU is usable only by a native Windows Ollama, which the profile cannot reach today without a Compose override. | P:136, P:98-122 |
| R12 | Skip the ranking provider when there are no preference tokens. Proposed for this task as A2 (§6.2). | L10, §18.2. | `PoiRankingOrchestrator.cs:100-115` |

## 9. Error, Timeout, Retry and Fallback Behavior

### 9.1 Ranking (at most one call per fresh generation)

| Condition | Provider result | Orchestrator outcome | Effect |
| --- | --- | --- | --- |
| `Enabled=false`, or no candidates | no call | `provider-skipped` | base scores |
| No preference tokens (A2, if confirmed) | no call | `provider-skipped` | base scores |
| Budget denied | `Quota`, no request | `quota` | base scores |
| 200 with a valid result | success | `success` | `0.60 × base + 0.40 × aiScore` |
| 200 failing orchestrator validation | success | `invalid-response` | base scores for the whole pool |
| 200 with a malformed body | `InvalidResponse` | `invalid-response` | base scores |
| 401, 422 or another 4xx | `InvalidResponse` | `invalid-response` | base scores |
| 429 | `Quota` | `quota` | base scores |
| 500, 502, 503 or 504 | `ServerError` | `server-error` | base scores |
| Connection or read failure | `Network` | `network` | base scores |
| No answer within 5 s | cancelled | `timeout` | base scores |
| Caller cancels | exception propagates | `cancelled` | request aborted |

Neither adapter retries. Sources: §7.2 and `PoiRankingOrchestrator.cs:100-227`.

### 9.2 Explanation (fresh generation only; never on replay or for an infeasible plan)

| Condition | TripMateAi result | Handler outcome | Gemini difference |
| --- | --- | --- | --- |
| `Enabled=false` | no call | `provider-skipped` | — |
| Budget denied on any attempt | `Quota` | `quota` | — |
| 200, valid | success | `success` | — |
| 200, invalid shape or validator failure | `InvalidResponse`, no retry | `invalid-response` | — |
| 401, 422 or another 4xx | `InvalidResponse`, no retry | `invalid-response` | — |
| 429 | one retry, then `Quota` | `quota` | Gemini honors `Retry-After` (V3). |
| 503 | one retry, then `ServerError` | `server-error` | — |
| 502 | `ServerError`, no retry | `server-error` | Gemini retries (V1). |
| 504 | `Timeout`, no retry | `timeout` | Gemini retries, then `server-error` (V1, V2). |
| 500 | `ServerError`, no retry | `server-error` | — |
| Attempt timeout (10 s) | `Timeout`, no retry | `timeout` | — |
| Overall budget (20 s) used up | `Timeout` | `timeout` | — |
| Connection or read failure | `Network` | `network` | — |
| The handler's outer timeout fires | cancelled | `timeout` | — |
| Caller cancels | exception propagates | — | — |

Every outcome other than `success` keeps the deterministic fallback text. The response is still 201
and the scheduling result is unchanged (`CreateSchedulingRequestCommandHandler.cs:850`, `:937-941`).

### 9.3 Added latency per fresh generation

- Ranking: at most `ProviderTimeout`, 5 s.
- Explanation: one attempt, at most 10 s, when the service answers 504 or the attempt times out. Up to
  the 20 s overall budget only when a 429 or 503 retry happens.
- This is the same bound as with Gemini (TM-217 open issue 2,
  `specs/TM-217-itinerary-explanation-spec.md:382-384`).

Measured on 2026-10-12 against the real models (AI service only, no backend). Times are for the first
call of each itinerary, so the prompt is not cached, as for a real request.

| Host | Ranking, 60 candidates, first call | Explanation, 2 / 4 / 6 / 8 stops (`tripmate-explainer`) | Explanation evaluation, 130 itineraries, 9 s budget |
| --- | --- | --- | --- |
| Laptop RTX 3050 4 GB, native Ollama (Vulkan) | 2.2 s; 0.01 s once cached | 1.7 / 2.9–3.5 / 4.8–6.4 / 6.1–7.2 s | 130/130 clean, p95 3.7–5.0 s, max 5.0 s |
| Kaggle T4 GPU | — | p50 about 1.4–1.7 s, p95 3.3–4.1 s on the evaluation sets | 130/130 clean |
| Kaggle CPU, 4 vCPU | — | 13–19 / 22–25 / 36–42 / 48–57 s | — (every itinerary over 9 s) |

- Backend-size samples (`Capstone_AI` `812831a`), `scripts/benchmark.py`, 10 measured calls each, on
  the laptop: rank-pois with 60 candidates 1.35–2.9 s on the first call and 0.01 s afterwards;
  explain-itinerary with 8 items median 5.6–5.7 s, p95 at most 5.95 s, no errors; with 12 items 504
  in 50–65 % of the calls (L14).
- `scripts/benchmark.py` through the service image, with the key, on the laptop: explain-itinerary
  median 2.1 s and p95 2.2 s, rank-pois without errors; 0 of 22 calls failed.
- The first call after the model is loaded into memory takes about 18 s. `MODEL_WARMUP=true` and
  `LLM_KEEP_ALIVE=-1` (service defaults) load both models at service start and keep them loaded.
- Conclusion for hosting (Q2): a GPU host keeps every itinerary up to 8 stops within the deadlines; a
  CPU-only host does not with the 1.7B models, and would need asynchronous explanations, which
  is a separate backend change.

### 9.4 Observability

| Source | Log line | Location |
| --- | --- | --- |
| Backend, ranking | `POI ranking provider TripMateAiPoiRankingProvider; enabled=…; candidates=…; pool=…; latencyMs=…; outcome=…; fallbackUsed=…; timedOut=…` | `PoiRankingOrchestrator.cs:351-369` |
| Backend, explanation | `Itinerary explanation provider=TripMateAiExplanationProvider; enabled=…; itemCount=…; latencyMs=…; outcome=…; fallbackUsed=…; timedOut=…` | `CreateSchedulingRequestCommandHandler.cs:959-998` |
| Backend, explanation attempts | `TripMate AI explanation attempt=…/…; httpStatus=…; latencyMs=…; outcome=…; retryScheduled=…` | P:699 |
| AI service | `rank_pois candidates=… tokens=… latency_ms=…` and `explain_itinerary items=… latency_ms=…`, plus one uvicorn access-log line per request | `Capstone_AI/app/main.py:176-181`, `:192-194` |

The provider type name is the only signal for which provider ran. `aiScore` is never exposed
(`PoiRankingSnapshot.cs:22-29`).

## 10. Security and Privacy

1. **Payload.** The fields do not change.
   - Ranking sends preference tokens plus each candidate's ID, name, category and tags (TM-215 §18,
     `specs/TM-215-ai-poi-ranking-provider-spec.md:151-153`).
   - Explanation sends the `ItineraryExplanationInput` fields (TM-217 §8,
     `specs/TM-217-itinerary-explanation-spec.md:196-202`).
   - No traveler identity, coordinates, scores, idempotency key or auth material is sent. The only
     added header is the key (D5).
2. **Shared key.** One secret: `TRIPMATE_AI_API_KEY` for the service, and `AiRanking__ApiKey` /
   `AiExplanation__ApiKey` for the backend, set to the same value.
   - It lives only in the git-ignored `.env` (`.gitignore:494`) or in user secrets, and is never
     committed (AGENTS.md §5.4).
   - It is never logged. Neither the provider log line (P:699) nor the service logs
     (`Capstone_AI/app/main.py:176-181`, `:190-194`) contain it, and validator messages name properties
     only.
   - The backend requires it whenever a feature is enabled; the service refuses to start without it.
     `AI_ALLOW_ANONYMOUS` is for local development only, and the profile does not set it.
3. **Transport.** Plain HTTP is accepted for TripMateAi endpoints on any host (V6). Operational rule
   for this task: use `http://` only for the service on the same Docker network (`http://ai:8000`,
   no published port); any other host must use HTTPS. Enforcing this in code is Q5.
4. **Data location.** With TripMateAi, preference tokens and POI metadata stay on infrastructure the
   team runs instead of going to Google. TM-215 §44 requires product sign-off on the AI vendor,
   endpoint and key (`specs/TM-215-ai-poi-ranking-provider-spec.md:306-308`). That sign-off is needed
   before TripMateAi is enabled on a shared environment (Q2), not for this task.
5. **Untrusted output.** The orchestrator validates ranking output, and the reason is never stored.
   Explanation output is validated by the provider and again by the handler, stored with at most 500
   characters, and returned as plain text (TM-217 §17, `specs/TM-217-itinerary-explanation-spec.md:317-324`).
6. **Logs.** As in §9.4: no payload, text, key or identity on either side.
7. **Containers.** The AI service runs as a non-root user (`Capstone_AI/Dockerfile`), on a pinned
   Python image with pinned dependencies. The profile's Ollama image is unpinned (L2, R2).
8. **Service hardening** (`Capstone_AI`): the key is checked before the request body is read, bodies
   over 1 MiB are refused, and the API docs are off unless `AI_ENABLE_DOCS=true` (§8.1).

## 11. Acceptance Criteria

1. **Shipped configuration.** `appsettings.json` has `"Provider": "Gemini"` in both sections, and
   both features stay disabled. DI registers `HttpPoiRankingProvider` and
   `HttpItineraryExplanationProvider`. Gemini validation is unchanged. Every existing test passes
   without edits (A2 excepted, AC13).
2. **Per-feature selection.** `Provider=TripMateAi` in one section registers the TripMateAi provider
   for that feature only. All four combinations resolve correctly, and the value is
   case-insensitive.
3. **Validation.** With `Enabled=true` and `TripMateAi`: an absolute HTTP or HTTPS endpoint and a key
   are required, and `ModelName` is optional. An undefined provider value fails validation when
   enabled; an unparseable value stops startup. `Enabled=false` needs nothing beyond the budget
   ranges.
4. **Ranking adapter.** Behaves as §7.2 and §9.1, proven with controlled HTTP handlers.
5. **Explanation adapter.** Behaves as §7.3 and §9.2, proven with controlled HTTP handlers.
6. **Fallback.** Every TripMateAi failure gives base scores for the whole pool and the deterministic
   fallback text. The response is 201, and the itinerary equals the one generated with AI disabled
   for the same request (smoke test S7–S9 against S1).
7. **Compose.** Without `--profile ai`, `docker compose config --services` lists `sqlserver`,
   `db-init`, `redis` and `api`. With it, the list adds `ollama`, `ollama-init` and `ai`. The `api`
   service resolves both `Provider` values to `Gemini` unless they are set.
8. **Patch fidelity.** On the patch commit, `git apply --reverse --check --exclude=.env.example` of
   patch 0003 succeeds, and `.env.example` differs from the baseline only by the 13 lines of P:52-64
   inserted after line 29.
9. **Unchanged areas.** No contract, blend, CSP, handler, migration or endpoint change, and no
   orchestrator change except A2 if confirmed: the branch diff contains only the files listed in the
   plan.
10. **Smoke test.** S0–S11, S13 and S14 pass; S12 is recorded, and so are S14's times and solver
    outcome; evidence has no secrets.
11. **Runbook.** It exists, follows §14, and its no-model preflight was executed during the smoke test.
12. **Gates.** `dotnet format --verify-no-changes`, a Release build without new warnings, the full
    test suite and `git diff --check` pass locally. CI passes, including the SQL Server and Redis
    categories without skips.
13. **A2 (if confirmed).** With the ranking feature enabled and no preference tokens, the provider is
    not called, the outcome is `provider-skipped`, and every `EffectiveDesirabilityScore` equals the
    base score, so the plan equals the one generated with ranking disabled. With tokens, behavior is
    unchanged.
14. **CSP and AI together.** S14 completes with 201 and both AI outcomes `success`; its total time and
    solver outcome are recorded, not judged (§18.3, §18.4).

## 12. Required Tests

### 12.1 Shipped with patch 0003

`tests/TripMate.Infrastructure.UnitTests/TripMateAi/TripMateAiProviderTests.cs` (P:899-1252): 14
methods, 26 cases.

- **Ranking:**
  - the request carries the key and a camelCase body, and the response is mapped (P:935);
  - three invalid bodies (P:956-968);
  - 429/503/504/401 mapping, four cases (P:970-982);
  - network failure (P:984-992);
  - budget denied without a request (P:994-1007).
- **Explanation:**
  - the request carries the key and the input; a visit item and a rest item come back validated and
    trimmed (P:1011-1036);
  - four validation failures (P:1038-1051);
  - 503 once, then success, with two requests (P:1053-1066);
  - 502/504/401 not retried, three cases (P:1068-1081);
  - network failure (P:1083-1091).
- **Configuration:**
  - TripMateAi accepts `http://` without a model (P:1095-1100);
  - Gemini still needs HTTPS and a model (P:1102-1118);
  - TripMateAi needs the key (P:1120-1131);
  - registration picks the provider from configuration, three cases (P:1133-1162).

### 12.2 Additional backend tests

New files in the same folder. They close gaps in the patch's coverage. Because they test code that
already exists, each one must first be shown to fail against a deliberate local break of that code
(§12.3).

`TripMateAiPoiRankingProviderTests.cs`:
- **RT1.** 422, 400 and 403 → `InvalidResponse`; 500 and 502 → `ServerError`.
- **RT2.** Caller cancellation during the send propagates. An already cancelled token throws before
  any request.
- **RT3.** The body has exactly `preferenceTokens` and `candidates`, and each candidate exactly
  `poiId`, `name`, `categoryName` and `tagNames`. There is no model field, and the key appears once,
  in the header only.
- **RT4.** `reason` passes through unchanged: null, and a 700-character value (the orchestrator
  truncates).
- **RT5.** `{"ranked":null}`, `{"ranked":[null]}`, a missing `poiId` and an empty body →
  `InvalidResponse`. Unknown extra fields are ignored.
- **RT6.** A failure while reading the body → `Network`.
- **RT7.** With a real `InMemoryAiProviderBudget` limited to one concurrent call, two sequential calls
  both reach the handler: the permit is released after each send.

`TripMateAiExplanationProviderTests.cs`:
- **E1.** Budget denied on the first attempt → `Quota`, no request.
- **E2.** With a real budget of one call per minute, a 503 is followed by a denied retry → `Quota`
  after one request.
- **E3.** A handler slower than `TimeoutSeconds` → `Timeout` after one request.
- **E4.** 503 twice → `ServerError` after two requests. 429 then 200 → success after two. 429 twice →
  `Quota` after two.
- **E5.** 500 → `ServerError`; 422, 400 and 403 → `InvalidResponse`; one request each.
- **E6.** A retry delay that does not fit the remaining overall budget → no retry.
- **E7.** Caller cancellation during the send, and during the retry delay, propagates.
- **E8.** For a rest item, the body carries `poiId`, `poiName` and `categoryName` as null, `tagNames`
  empty and `kind` `rest`. The body's property sets equal those of the `ItineraryExplanationInput`
  records exactly.
- **E9.** A root with an extra property, a root named `Items`, and a root array → `InvalidResponse`.
- **E10.** Captured logs: Information on success, Warning otherwise. No message contains the key, a
  POI name or an explanation text.

`PoiRankingOrchestratorTests.cs` (only with A2):
- **O1.** Ranking enabled, candidates present, no preference tokens → the provider is never called,
  outcome `ProviderSkipped`, entries equal the base scores, one `provider-skipped` log line. It fails
  before the A2 change (today the provider is called).

`TripMateAiConfigurationTests.cs`:
- **C1.** The shipped configuration selects Gemini explicitly. `appsettings.json` has `Provider`
  `Gemini` in both sections, the bound options say `Gemini`, and the resolved providers are the
  Gemini adapters (pattern: `AiRankingDependencyInjectionTests.cs:103-131`).
- **C2.** `AddInfrastructure` with both features enabled on TripMateAi (`http://ai:8000/...`, a key,
  no model) passes `IStartupValidator` and resolves both TripMateAi providers. The explanation
  execution options are mapped as before.
- **C3.** Mixed selection: ranking on TripMateAi with explanation on Gemini, and the reverse.
- **C4.** `Provider=TripMateAi` with `Enabled=false` and no endpoint or key passes startup
  validation.
- **C5.** An unparseable value (`OpenAI`) makes `AddInfrastructure` throw.
- **C6.** An undefined numeric value (`7`): enabled → validation fails with the provider message;
  disabled → it passes and registers the Gemini adapter.
- **C7.** TripMateAi validator matrix: HTTPS accepted; a relative URI and `ftp://` rejected; a blank
  key rejected; budget and explanation timeout ranges still enforced; no failure message contains the
  key value.
- **C8.** Lower-case `tripmateai` selects TripMateAi.

### 12.3 Rules

- No automated test calls a real endpoint; only controlled `HttpMessageHandler`s are used (TM-217
  §19, `specs/TM-217-itinerary-explanation-spec.md:364-366`).
- Timing tests use the margins of the existing Gemini tests, for example a 5 s handler against a 1 s
  attempt timeout (`HttpItineraryExplanationProviderTests.cs:325-345`).
- **Break check.** Each new test must fail when the behavior it covers is broken locally (for
  example by removing 503 from the retry set, or the `finally` that releases the permit), and pass
  again after the change is reverted. Each task records its checks. The breaking change is never
  committed.
- Existing tests are not edited, except the setups T5a updates if A2 is confirmed (plan T5a).
- If a new test exposes a defect in patch code, stop. D2 keeps the patch unchanged, so the developer
  decides on a separate fix.

### 12.4 Gates

- Focused runs per task, then:
  - `dotnet format TripMate.slnx --verify-no-changes --no-restore`;
  - `dotnet build TripMate.slnx -c Release --no-restore --nologo`;
  - `dotnet test TripMate.slnx -c Release --no-build --nologo`;
  - `git diff --check`.
- Without their connection variables, the SQL Server and Redis categories are skipped locally. CI
  runs them without skips (`.github/workflows/backend-tests.yml:35-37`, `:77-125`).

## 13. Smoke Test (backend → AI service → fake Ollama, then real models)

Purpose: prove on the developer's machine, first without real models (S0–S10) and then with them on the
laptop GPU (S11–S14), that the backend:
- selects the configured provider;
- calls the right endpoints with the right key;
- uses the AI results on success;
- falls back to base scores and the deterministic text when the service fails, is slow, or is off.

### 13.1 Environment

**Compose project.** The project of this worktree (`docker compose` run in
`D:\FPTUCapstone\Capstone_BE_AI`), so it gets its own SQL Server volume, started with `--profile ai`.

**Override file.** Kept outside the repository. It replaces `ollama` with the fake Ollama and turns
`ollama-init` into a no-op:
- the fake runs in `python:3.12-slim` with `${AI_REPO_PATH:-../Capstone_AI}` mounted read-only, and
  serves `tests.fake_ollama:create_fake_ollama_app` with uvicorn on port 11434
  (`Capstone_AI/tests/fake_ollama.py:122` ff.);
- its `chat_mode` comes from a variable, so the same file can also produce invalid model output
  (`:75-116`);
- the healthcheck is an HTTP GET on `/api/tags`;
- the fake lists `bge-m3:latest` and `qwen3:1.7b`, so `/ready` answers 200 with the default models
  (`:35-36`).

The source is mounted because `tests` is excluded from the AI image (`Capstone_AI/.dockerignore:8`).
The plan holds the exact file; its merge and the fake's start command were checked on 2026-10-12.

**Real components.** The backend image built from this worktree, the AI service image built from the
AI working tree, SQL Server, Redis, and the developer's OpenRouteService key (generation needs real
travel times).

**`.env`** (git-ignored). Based on the developer's local `.env`, with the TripMateAi settings of §7.6.
For a fast run, raise these values (allowed ranges in parentheses); otherwise pace the runs at 3
generations per minute with a 15 s cooldown and 4 AI calls per minute:
- `SchedulingRateLimit__MaxFreshGenerationsPerMinute` (1–1000) and `SchedulingRateLimit__CooldownSeconds`
  (0–59) (`SchedulingRateLimitOptionsValidator.cs:18`, `:23`);
- both `RateLimitPerMinute` values (1–1000) (`PoiRankingProviderOptionsValidator.cs:46-49`,
  `ExplanationProviderOptionsValidator.cs:76-79`).

### 13.2 Data and credentials

- **POIs.** Apply `database/seeds/20260914_seed_danang_planning_pois.sql` with `sqlcmd` inside the
  `sqlserver` container. The `database` folder is mounted at `/scripts` (`docker-compose.yml:19`).
- **Traveler.**
  - A disposable `Traveler` row in `dbo.Users` (pattern:
    `database/seeds/20261001_seed_uc59_active_trip_details.sql:28-29`).
  - A `dbo.TravelerProfiles` row whose interest tags match seeded categories
    (`database/tripmate_schema_v7.sql:217-228`).
- **Token.** An HS256 JWT with the claims the API itself issues
  (`src/TripMate.Infrastructure/Services/JwtTokenService.cs:22-29`), issuer `TripMate` and audience
  `TripMateClients` (`docker-compose.yml:80-81`), signed with the base64-decoded `JWT_SIGNING_KEY`
  (`src/TripMate.Api/Program.cs:151-152`). Neither the key nor the token appears in evidence.
- **Request.** `POST /api/v1/scheduling-requests` with a new `Idempotency-Key` per scenario
  (`src/TripMate.Api/Controllers/V1/SchedulingRequestsController.cs:16-17`, `:33`). Start at a seeded
  POI in central Da Nang (for example the coordinates at
  `database/seeds/20260914_seed_danang_planning_pois.sql:67`); 480 available minutes, transport `Car`,
  rest preference `Auto`.

### 13.3 Scenarios

| # | Setup | Expected backend | Expected AI service |
| --- | --- | --- | --- |
| S0 | Stack started | — | `/health` 200 and `/ready` 200 (through `docker exec`); the `ai` container is healthy. |
| S1 | `Provider` keys removed (Compose default `Gemini`); both features disabled | 201. The log lines name `HttpPoiRankingProvider` and `HttpItineraryExplanationProvider`, with `enabled=False` and `provider-skipped`. Texts are the fallback. | No `POST`. |
| S2 | `Provider=TripMateAi` for both; both features disabled | 201. The log lines name the TripMateAi providers, with `enabled=False` and `provider-skipped`. | No `POST`. |
| S3 | TripMateAi, both features enabled, matching key | 201. Ranking `success`, `fallbackUsed=False`; explanation `success`. Every `friendlyExplanation` is the fake model's sentence: it contains `được xếp lúc`, or starts with `Nghỉ ngơi lúc`. | `POST /v1/rank-pois` 200 and `POST /v1/explain-itinerary` 200, once each. |
| S4 | S3's exact request again (same key and body) | 201 replay with the stored texts. | No new `POST`. |
| S5 | Backend keys differ from `TRIPMATE_AI_API_KEY` | 201. Ranking `invalid-response`, explanation `invalid-response`; fallback. | Each endpoint answers 401 once. |
| S6 | Fake in `not_json` mode | 201. Ranking `success`. Explanation `server-error` after one attempt (`retryScheduled=False`); fallback. | explain-itinerary answers 502 once (the service tried twice internally). |
| S7 | Fake Ollama stopped, then `ai` restarted so that its embedding cache is empty (with a warm cache, ranking stays `success`: L16) | 201. Ranking `server-error`. Explanation attempt 1 `retryScheduled=True`, then `server-error`; fallback. The itinerary equals S1's. | rank-pois 503 once; explain-itinerary 503 twice. |
| S8 | AI container paused | 201 after about 5 s plus 10 s of waiting. Ranking `timeout`, `timedOut=True`; explanation `timeout` after one attempt; fallback. The itinerary equals S1's. | No answer until it is unpaused. |
| S9 | AI container stopped | 201. Ranking `network`, explanation `network`; fallback. The itinerary equals S1's. | — |
| S10 | Everything restored | As S3. | As S3. |

"The itinerary equals S1's" means the same sequence of `poiId` and `plannedArrival` in the response
items.

**Real-model run on the laptop GPU (after S10).** Replace the fake with the demo configuration of
§7.6 (host Ollama with `tripmate-explainer` and `bge-m3`) and repeat with real models:

| # | Setup | Expected backend | Expected AI service |
| --- | --- | --- | --- |
| S11 | TripMateAi, both features enabled, `LLM_MODEL=tripmate-explainer`, host Ollama | 201. Ranking `success`; explanation `success` on the first attempt; every `friendlyExplanation` is one Vietnamese sentence about its own item (checked by reading). Backend `latencyMs` for the explanation below 10,000. | rank-pois 200 and explain-itinerary 200 once each. |
| S12 | As S11, with a full-day request (8 or more visits) | 201. Explanation `success`, or `timeout` with the fallback text; the outcome and `latencyMs` are recorded, not judged. | explain-itinerary 200 or 504. |
| S13 | As S11 with interest tags that the backend strips (for example `["Bãi biển","Ẩm thực"]` stored in `TravelerProfiles`) | 201. Ranking `success`. | rank-pois 200. The service does not log tokens, so restoration is checked by a direct `POST /v1/rank-pois` with the same candidates: `bai bien, am thuc` and `bãi biển, ẩm thực` must give the same `aiScore` per POI (§8.2). |
| S14 | As S11, plus `SchedulingGeneration__SolverMode=Csp`, with a traveler who has interest tags | 201. Ranking and explanation `success`; the solver outcome is recorded (expected `csp`; `heuristic_fallback_csp_time_limit` is recorded, not a failure). Record the total request time (ASP.NET `Request finished`) and the CSP elapsed time. The solver outcome log line exists once PR #65 is merged; before that, record the request time only. | As S11. |

S14 is the only scenario where the CSP and both AI providers run in one generation (§18).

S12 documents the margin for long itineraries: on the laptop 8 stops took 6.1–7.2 s (§9.3), so 9+
stops may exceed the 9 s deadline.

### 13.4 Pass criteria and evidence

- Every scenario matches its row. Any mismatch stops the smoke test and is investigated before the
  final verification.
- Evidence per scenario:
  - the backend status code;
  - the backend ranking and explanation log lines, and the attempt lines;
  - the AI access-log lines (method, path, status, count);
  - for S7–S9, the itinerary comparison with S1.
- The evidence is recorded in §1 of this spec and in the PR description.
- Never recorded: keys, tokens, or the output of `docker compose config` (it prints resolved
  secrets).
- **Cleanup.**
  - Stop the stack with `down`. Add `-v` only for this worktree's project, to drop its disposable
    database and model volume.
  - Delete the override folder.
  - Rebuild the developer's main stack image afterwards, because both stacks tag the API image
    `tripmate-api:latest` (`docker-compose.yml:57`).

## 14. Measurement Runbook (deliverable outline)

### 14.1 Deliverable

`docs/operations/uc10-tripmate-ai-measurement-runbook.md`, written in English like the other
operations guides, for a teammate who runs the real models on their own machine. It ends with a
results table that the teammate fills in and the developer uses to decide (§14.4). The measurement
itself is out of scope (D4).

### 14.2 Thresholds (from the verified backend timeouts)

| Check | Pass rule | Source |
| --- | --- | --- |
| rank-pois, warm, 60 candidates | Every measured call returns 200, and p95 latency < 5 s. | `Personalization:ProviderTimeout`, 5 s (`PersonalizationRankingOptions.cs:33`, `appsettings.json:69`) |
| explain-itinerary, warm, sample and full-day payloads | Every measured call returns 200, and p95 latency < 10 s. | `AiExplanation:TimeoutSeconds`, 10 s (`ExplanationProviderOptions.cs:15`, `appsettings.json:82`) |
| Service deadlines | `EMBEDDING_TIMEOUT_SECONDS` < 5 and `LLM_TIMEOUT_SECONDS` < 10 during the measurement | §8.5 |
| Cold calls: the first after start, the first after an idle period longer than `LLM_KEEP_ALIVE`, the first with new POIs | Recorded, not pass or fail. A cold failure adds a warm-up step to the demo checklist. | §8.7 |

Because each service deadline sits below its backend timeout, a 200 answer already arrived within the
deadline. A model that is too slow shows up as 504 answers, which break the "every call returns 200"
rule. Each payload gets one warm-up call and 10 measured calls (`Capstone_AI/scripts/benchmark.py` also
judges the first, cold run; record it separately per the cold-call row).

### 14.3 Outline

1. **Purpose and decision.** Which numbers decide whether the demo environment may set
   `Provider=TripMateAi` and `Enabled=true`. That switch is not part of the runbook.
2. **Prerequisites.**
   - Docker Desktop with enough memory for Ollama: `OLLAMA_MEM_LIMIT` is 6 GB by default (P:104),
     plus the backend stack if Part D runs.
   - About 4 GB of disk for images and models; the models are about 2.5 GB (P:111).
   - Both repositories side by side.
   - No other stack using the same container names (L5).
   - **An NVIDIA GPU is required for the explanation feature** (§9.3: a 4-vCPU CPU takes 13–57 s per
     itinerary). The profile's Ollama container is CPU-only (L2), so the runbook uses a native
     Ollama on the host: driver ≥ 531, Ollama for Windows, `bge-m3` and `tripmate-explainer`, and the
     §7.6 override. Record the backend Ollama reports at start (CUDA or Vulkan).
   - The model files: `tripmate-explainer-q4_k_m.gguf` and `Modelfile` from the Kaggle kernel output
     (`kaggle kernels output <owner>/tripmate-explainer-qlora`).
   - Reference numbers to compare with: the laptop row of §9.3.
3. **Preflight without models.** The smoke override of §13.1: `/ready` 200 and one successful
   generation, to prove the wiring before downloading models.
4. **Part A, service latency.** Use only the `ai` service of the standalone `Capstone_AI` stack
   (`docker compose up -d --build --no-deps ai`) with a local override setting
   `OLLAMA_BASE_URL: http://host.docker.internal:11434`; its own `ollama` container is CPU-only and
   would clash with the host Ollama on port 11434. It publishes port 8000 and reads its other
   settings from its own `.env` (`Capstone_AI/docker-compose.yml:31-46`).
   - Pull the models and check `/ready`.
   - For each payload, one cold call and 10 measured calls, with `scripts/benchmark.py` or an
     equivalent loop.
   - Payloads: the committed samples, a 60-candidate ranking request, and itineraries of 2, 4, 6, 8
     and about 12 items (F5). `Capstone_AI/training/llm/kaggle/bench_cpu.py` already times 2–8 items
     with the service prompts; point it at the host with `TRIPMATE_BENCH_OLLAMA`.
   - Explanation quality and time together: `Capstone_AI/training/llm/evaluate_model.py` on the
     evaluation sets with `--timeout 9`.
   - Optional: 2 concurrent calls, the backend's per-feature concurrency
     (`ExplanationProviderOptions.cs:25`).
5. **Part B, Vietnamese quality.** About 20 explanations from varied itineraries, either generated by
   the AI repository's input generator (`Capstone_AI/training/llm/generate_inputs.py:1-15`) or taken
   from real generations in Part D. Judge each one against a checklist:
   - Vietnamese only, one sentence;
   - consistent with the item's name, time and kind;
   - no invented facts such as prices, opening hours or distances;
   - no advice to change the schedule;
   - natural tone, at most 500 characters.
6. **Part C, ranking scores.** Over several preference sets on the demo POIs, record the `aiScore`
   minimum, median, maximum and buckets. Send the preferences the way the backend does (lowercase,
   without diacritics) and check that the order is sensible (§8.2). This needs POIs with Vietnamese
   tags; the seeded 12 POIs have English categories and no tags (§17). Adjust `RANKING_SIMILARITY_FLOOR`/`CEILING`
   (`Capstone_AI/app/config.py:99-100`) and record the values before and after. The backend profile
   does not forward these settings (L3).
7. **Part D, optional backend check.** The backend `ai` profile with the real models: read
   `latencyMs` and the outcomes from the backend log lines (§9.4) for a few generations.
8. **Results table.**
   - Date and tester.
   - Machine: CPU, cores, RAM, GPU and VRAM, OS, Docker memory.
   - Models and settings; payload sizes.
   - Cold latencies.
   - Per endpoint and payload: warm median, p95 and maximum, and the count of non-200 answers.
   - Score distribution and quality-review counts.
   - Verdict.
9. **Decision and rollback.** Pass: the developer may enable TripMateAi on the demo environment
   (outside this task). Fail: keep Gemini, tune models or settings, and measure again. Rollback as in
   §7.6.
10. **Troubleshooting.**
    - `/ready` 503 `models_missing` while `ollama-init` is still pulling.
    - 401: key mismatch.
    - 504: the model is too slow.
    - Port conflicts with a native Ollama on 11434, or with another service on 8000.
    - Container-name conflicts.

### 14.4 What the developer decides from it

The go/no-go decision needs the latency rules of §14.2 and an agreed quality bar (Q7).

## 15. Non-goals

- Switching any default to TripMateAi, or enabling it on a shared environment.
- Deploying or hosting the AI service; GPU support in the profile; pinning the Ollama image.
- Training or fine-tuning models, and using Gemini output as training data
  (`Capstone_AI/AGENTS.md:8`).
- Changing the provider contracts, the 0.60/0.40 blend, the CSP, the handler flow, the budget or the
  fallback texts.
- Hardening patch 0003 beyond D2, for example restricting plain HTTP, unless the developer decides
  so (Q5).
- A backend health check or `depends_on` for the AI service.
- Changes to the integration-test infrastructure (`TripMateApiFactory`).
- Changes to the `Capstone_AI` repository (§8.8 lists findings for its PR).
- The traveler-preferences use case: preferences API, tag catalog and POI tags, client screens (§17).
- AI ranking in Regenerate and Adjust Items (L12), and asynchronous explanations for CPU-only hosts
  (§9.3).
- Front-end or mobile changes.

## 16. Open Questions

| # | Question | Blocks |
| --- | --- | --- |
| Q1 | **Official task ID.** Jira is not accessible, so both files use the provisional ID. Rename them and their references once the ID is known. | Only the commit of the spec and plan. |
| Q2 | **Where the AI service runs.** *Demo — answered 2026-10-12:* the developer's laptop (RTX 3050 4 GB) with a native Ollama and the §7.6 configuration; it meets both deadlines up to 8 stops (§9.3). *Production — open:* it needs a GPU host, or asynchronous explanations on a CPU host. The answer also decides how tuned service settings reach that environment (L3) and when the TM-215 §44 sign-off is needed (§10). | Enabling TripMateAi on a shared environment; not this task. |
| Q3 | **Who reviews the `Capstone_AI` PR.** The scaffold commit was pushed by teammate HoangTrinhyeuoi. | The AI PR. |
| Q4 | **Merge order.** Recommended: merge the `Capstone_AI` PR before this backend PR, because the `ai` profile builds that repository. | Merging the backend PR. |
| Q5 | **Plain HTTP for TripMateAi on any host** (V6). Option A, recommended for this task: accept it, with the operational rule of §10. Option B: a TDD follow-up that accepts `http://` only for hosts without a dot (Docker service names). Option B changes patch code and needs the developer's decision. | Nothing, if Option A is chosen. |
| Q6 | **Service-side repair** (§8.7) against TM-217 §10. Confirm that the "no repair, no truncation" rule binds the backend only, so the service may repair model output before answering. | Nothing in the backend; the AI PR. |
| Q7 | **Quality bar for go/no-go:** how many of about 20 reviewed explanations must be acceptable. | The runbook's verdict; not implementation. |
| Q8 | **Answered 2026-10-12: Option A (D9).** **Recommended patch changes R1–R11** (§8.9; R12 is proposed as A2 instead). Option A: keep D2 and apply the patch unchanged, then do R1–R11 as a separate TDD follow-up. Option B: adopt some of them as adaptations in this task (each one becomes an A-row in §6.2 and a plan task). Recommended: Option A, with R1, R2, R3, R5 and R11 first, because they are configuration only. | Nothing, if Option A is chosen. |
| Q9 | **Traveler preferences use case** (§17): who owns it and when, and which interest vocabulary the clients, the tag catalog and the POIs share. | The visible effect of TripMateAi ranking; not this task. |

### 16.1 Drafting assumptions (confirm or correct)

- The developer's local `.env` (in `D:\FPTUCapstone\Capstone_BE`) may be copied into this worktree for
  the smoke test. It is git-ignored.
- The smoke test uses a disposable database in this worktree's Compose project, the Da Nang seed and
  a locally signed token, rather than a developer account.
- The runbook is written in English, like the other files in `docs/operations/`.
- The new tests go into new files next to the patch's test file; the patch's file is not edited.
- Smoke evidence is recorded in §1 of this spec and in the PR description, not in a separate
  document.
- The developer makes every commit (plan §7). The patch commit keeps the patch subject and author
  date, as the commits of patches 0001, 0002 and 0004 did.

## 17. Dependency: saving traveler preferences (out of scope)

Personalization only works when the backend knows what the traveler likes. Today it does not, for
any provider (Gemini or TripMateAi). This section records what a separate use case must deliver; it
is not part of this task (§4.2, L10, Q9).

### 17.1 How preferences are used once they exist

```
App/web: the traveler picks interests ──> PUT /api/v1/traveler/preferences ──> dbo.TravelerProfiles.interest_tags_json
Scheduling request ──> TravelerPreferenceScoring parses and normalizes the tags into preference tokens
   ├─ base score: 0.30 × category match + 0.20 × min(1, matching tags / 3) + behavior/scenic/photo
   │    (PersonalizationFeatureBuilder.cs:24-34, PersonalizationBaseScorer.cs:12-19); matching is exact
   │    after lowercasing and stripping diacritics (TravelerPreferenceScoring.cs:58-65)
   ├─ AI score: rank-pois embeds the tokens and the POI text (TM-215; restored diacritics, §8.2)
   └─ blend 0.60 × base + 0.40 × AI (PersonalizationRankingOptions.cs:21-23) ──> desirability ──> CSP or heuristic
Explanation: the same tokens reach explain-itinerary as `preferenceTokens`, so the text can say "matches your interest in …"
```

### 17.2 What is missing (verified 2026-10-12)

| Part | State |
| --- | --- |
| Mobile | The interests screen keeps an English enum in memory (`beach`, `heritage`, `museum`, `localFood`, `nature`, `nightlife`; `Capstone_Mobile/lib/features/traveler/presentation/cubit/travel_preferences_cubit.dart:8`); nothing is sent. |
| Web | `INTEREST_OPTIONS` with ids `culture`, `nature`, `food`, `adventure`, `relaxation`, `shopping`, `museum`, `nightlife` and Vietnamese labels (`Capstone_FE/src/features/account/preferences/travelPreferencesTypes.ts:72-121`); saved to localStorage; the preferences API is marked not implemented (`travelPreferencesApi.ts:21-67`). |
| Backend | No endpoint or command writes `TravelerProfiles` (`TravelerProfile.Create` is called by tests only). `catalog.Tags` and `catalog.POITagMap` have no seed rows; the seeded categories are English (`Museum`, `Natural attraction`, `Theme park`, `Water park`). |

Consequences: real travelers send no tokens, so the interest part of the base score is 0, the TripMateAi
ranking returns the neutral 0.5 for every candidate, and explanations cannot mention interests.
Even with tokens, the base score (and the +100 `PreferenceScore` that Regenerate and Adjust use, L12)
needs **exact** name matches, so the three vocabularies above would
not match each other.

### 17.3 What the preferences use case must provide

1. **One interest vocabulary** shared by mobile, web and the tag catalog, preferably the Vietnamese
   tag names stored in `catalog.Tags` (for example "Bãi biển", "Ẩm thực", "Văn hóa", "Lịch sử",
   "Thiên nhiên"). Clients send tag names or tag ids from that catalog, not their own enum values.
2. **Tags on POIs**: seed `catalog.Tags` and `catalog.POITagMap` for the demo POIs. A possible
   source is the OSM catalog of the AI repository (`Capstone_AI/training/llm/poi_catalog_osm.json`,
   3,628 POIs in Da Nang, Hoi An and Hue with Vietnamese categories and tags, ODbL).
3. **API**: `GET`/`PUT /api/v1/traveler/preferences`, validated against the tag catalog, writing
   `interest_tags_json` (at most 1,000 characters, `tripmate_schema_v7.sql:225`).
4. **Clients**: the mobile and web interest screens load the options from the catalog and save them
   through the API.
5. **Tests**: a scheduling request for a traveler with interests shows a non-zero interest part in the
   base score, TripMateAi `aiScore` values that differ by POI, and explanations that mention a matching
   interest.

Until then, the backend integration of this task is still useful: the explanation provider works
without preferences, and the ranking provider is ready for the day tokens arrive.

## 18. Interaction with the CSP Solver and Personalization

The AI providers change scores and texts; the solvers decide the plan (TM-216). This section records
how the two meet, for the review of this task and of the CSP default switch (PR #65).

### 18.1 Score flow in a fresh generation

```
preference tokens (TravelerProfiles) + POI category/tags/scenic/photo/behavior
  → base score 0..1 (PersonalizationBaseScorer)
  → provider pool: top MaxProviderCandidates (60) optional POIs by base score; optional POIs outside the
    pool are dropped from planning; mandatory POIs are not ranked
  → ranking provider (Gemini or TripMateAi) → aiScore 0..1
  → EffectiveDesirabilityScore = 0.6 × base + 0.4 × aiScore   (base alone on any ranking fallback, L11)
  → MatrixCandidateSelector → heuristic (OptionalRouteOptimizer) → CSP (R1a: heuristic plan = initial incumbent)
  → explanation provider (gets the final items, their CSP/heuristic reasons and the tokens)
```

### 18.2 What each solver uses

| Solver | Uses desirability as | Effect of a uniform shift (all aiScore = 0.5) |
| --- | --- | --- |
| Heuristic (`OptionalRouteOptimizer.cs:520`; `MatrixCandidateSelector.cs:57`) | Rank order only | None: the order is unchanged |
| CSP product rule (`CspProductRule`) | Rank order of optional POIs; decides which plan is better | None: the order is unchanged |
| CSP search and MiniRouting (`RoutingContext.SkipPenalty`, `RoutingContext.cs:115-131`, `:216`; `CspItinerarySolver.cs:563-564`, `:993-994`) | Skip penalty proportional to `score / maxScore`; the CSP tries values in the order `travel − penalty` | Penalties change (for example, with a best base score of 1, a 0-score POI goes from the minimum penalty to 25% of the maximum). A CSP search that stops at its node budget or time limit, and the MiniRouting solver, can return a different plan. The heuristic plan (the CSP incumbent and timeout fallback) is unchanged. |

Hence L10 and A2. A complete CSP search returns the same plan whatever the value order, but the
benchmark shows the node budget is reached on larger instances (`specs/UC-10-csp-itinerary-solver-spec.md`).
With real, differing `aiScore` values every solver follows the new order, which is the intended
personalization.

### 18.3 Latency of one fresh generation

| Step | Bound | Laptop GPU, measured or expected |
| --- | --- | --- |
| OpenRouteService matrix | outside this task | seconds |
| Ranking | `Personalization:ProviderTimeout` 5 s | 2.2 s cold (60 candidates), 0.01 s cached |
| Heuristic + CSP | CSP limit 2.5 s (`CspOptions.TimeLimitMilliseconds`, on develop; a hard deadline across all stages after PR #65) | a few hundred ms typical |
| Explanation | 10 s per attempt, 20 s overall | 1.7–7.2 s for 2–8 stops |

Worst case added by AI and CSP: about 5 + 2.5 + 20 s; on the laptop GPU, typically under 10 s. S14
records the real total.

### 18.4 Shared demo host

On the demo laptop the backend, SQL Server, Redis, the CSP and Ollama share one machine. Ollama runs on
the GPU, but the CSP is CPU-bound, and the CSP spec saw its time limit fire under heavy parallel load
(`specs/UC-10-csp-itinerary-solver-spec.md:296`; the PR #65 measurement: 1 CPU with 4 concurrent
generations). During S11–S14 record the solver outcome `heuristic_fallback_csp_time_limit`
(`CspStatistics.TimeLimitReached`, activity tag `csp.time_limit_reached`) as well as the AI outcomes; a time-limit fallback is safe (the heuristic plan) but should stay rare.

### 18.5 Determinism summary

- **Replay:** returns the stored itinerary; no provider or solver runs again.
- **Fresh generation, same request:** can differ by the ranking outcome (L11), by the CSP time limit
  (CSP spec R5), and by host (L13). Each difference falls back to a valid plan that is never worse
  than the heuristic under the product rule.
