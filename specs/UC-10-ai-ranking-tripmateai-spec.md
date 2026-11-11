# UC-10 AI ranking with the team AI service (AI level 1)

- **Status:** Approved by the developer on 2026-11-11 ("dùng model AI riêng của tôi, đừng dùng Gemini").
- **Branch:** `feature/phuctv-uc10-ai-ranking-tripmateai`, based on the UC-10 weighted personalization branch at `6c1705b3`.

## Change

- `appsettings.json` makes the team AI service (Capstone_AI) the default provider for both AI features:
  - `AiRanking`: `Provider=TripMateAi`, `Endpoint=http://tripmate-ai:8000/v1/rank-pois`, `ModelName=bge-m3`.
  - `AiExplanation`: `Provider=TripMateAi`, `Endpoint=http://tripmate-ai:8000/v1/explain-itinerary`, `ModelName=tripmate-explainer`.
  - The TripMateAi providers do not send `ModelName`. The service picks its models from its own environment (`EMBEDDING_MODEL`, default `bge-m3`; `LLM_MODEL`, default `qwen3:1.7b`). To serve the team's fine-tuned explainer, set `LLM_MODEL=tripmate-explainer` on Capstone_AI. That model is a QLoRA fine-tune of qwen3:1.7b (Kaggle kernel v5, 2026-10-14) and must be loaded in Ollama first.
- Both stay `Enabled=false`. Enabling one with only the `Enabled` flag can no longer reach Gemini.
- Pinned by:
  - `ShippedAppSettings_SelectTripMateAiForBothFeatures_NeverGemini`;
  - the committed-configuration tests in `AiRankingDependencyInjectionTests` and `ExplanationDependencyInjectionTests`.
- `weighted-gate --ai-ranking <endpoint>` schedules the Da Nang day with AI ranking off (fallback 0.60) and on.
  - It calls the real service through the production `TripMateAiPoiRankingProvider`.
  - It blends exactly like production: 0.55 × base + 0.45 × AI.
  - The API key comes from `TRIPMATE_AI_API_KEY`.

## Measured (local, `docs/benchmarks/UC-10-ai-ranking-2026-11-11.md`)

| Pace | Interest hit rate, off → on | Mean stops | Persona overlap (Jaccard), off → on |
|---|---|---|---|
| Relaxed | 75% → 88% | 3.0 → 3.0 | 0.11 → 0.11 |
| Moderate | 75% → 75% | 5.0 → 5.0 | 0.23 → 0.21 |
| Fast | 70% → 70% | 6.0 → 6.0 | 0.37 → 0.35 |

- **Plans:** AI changed 4 of 12 plans.
  - The food lover now gets Chợ Cồn: its interest hits at Relaxed went from 0/2 to 1/2.
  - The scenic photographer gets Động Âm Phủ instead of an art museum on Fast.
- **Calibration:** stops per pace are unchanged, so the G4 calibration still holds with AI on.
- **AI scores** are sensible: the active explorer's top five are natural attractions, and the food lover's top two are Chợ Cồn and a restaurant.
- **Latency:** a warm call takes 217–291 ms for 19 candidates. The first call after start took 2.9 s, and in one run it exceeded the 5-second provider timeout, so that request silently used the fallback.

## Findings before enabling it in production

1. **The budget is still sized for Gemini.** `AiRanking.RateLimitPerMinute=4` and `MaxConcurrency=2` mean that beyond 4 generations per minute the AI is skipped and the fallback is used. For the team service, this should be raised (for example to 60 per minute and 4 concurrent); that is a deploy setting.
2. **Cold start.** The model and embedding cache warm up on the first call. A warm-up request at service start avoids the first traveler falling back.
3. **Travelers without saved interests skip AI** by design: no preference tokens means the provider is not called.
4. **Gain is modest on this data.** The rule-based base score already matches categories and tags well. AI adds most where tags are sparse or the interest is semantic (for example "food" → a market). Behavior learning (level 2) is where AI personalization grows.
5. **Explanations stay off.** On CPU, qwen3:1.7b needs 10–64 s per itinerary, beyond the 9-second service limit.

## Deploy (when enabling)

- Host Capstone_AI with bge-m3 only.
- Set:
  - `AiRanking__Enabled=true`;
  - `AiRanking__Endpoint` to the service's internal URL;
  - `AiRanking__ApiKey` equal to the service's `AI_API_KEY`;
  - a raised `AiRanking__RateLimitPerMinute`.
- Keep `AiExplanation__Enabled=false`.
