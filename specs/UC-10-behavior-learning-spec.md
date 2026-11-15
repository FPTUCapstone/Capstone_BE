# UC-10 AI behavior learning (AI level 2)

- **Status:** decisions D1–D8 confirmed by the developer on 2026-11-11 (D8 after the first benchmark run). Implemented and measured locally.
- **Branch:** `feature/phuctv-uc10-behavior-learning`, based on the AI ranking branch at `20258aac` (`specs/UC-10-ai-ranking-tripmateai-spec.md`).
- **Companion change:** Capstone_AI branch `feature/phuctv-ai-behavior-profile` (`/v1/rank-pois` accepts a `behavior` field). The backend works without it: the AI service ignores an unknown field, and without AI the cooldown and the count-based affinity still apply.

## Problem

The ranking only knew what a traveler declared. What they did afterwards never reached it:

- `social.RecommendationBehaviorEvents` existed, but no client sends Like/Dislike events, so the table stayed empty.
- Itinerary edits and navigation progress were stored but not read by the ranking.
- Regenerate ranked with rules only, never through `PoiRankingOrchestrator`.
- A stop the traveler removed was suggested again on the next trip.

## Decisions

| # | Decision |
|---|---|
| D1 | Signals are derived from what travelers already do: stop removed while adjusting = dislike, stop added = like, stop skipped in navigation = dislike, stop reached = weak like. Explicit Like/Dislike events are still accepted. |
| D2 | Learning is semantic (bge-m3): the AI scores candidates against liked and disliked POIs. The count-based affinity stays. No training. |
| D3 | A signal weighs half as much every 90 days; at most 50 POIs per side are sent. |
| D4 | Regenerate ranks like create. Manual adjust is unchanged. |
| D5 | Acceptance: a simulated-traveler benchmark; hit rate +10 points from the first to the last trip, no regression on the first trip. |
| D6 | A POI marked mandatory when creating a request is a weak like. |
| D7 | Signals are appended to the existing event table with three new capture sources. |
| D8 | A stop the traveler removed or skipped is not suggested automatically again for 14 days, unless they choose it again. |
| D9 | The shipped AI ranking budget is 60 calls a minute and 4 at once (it was 4 and 2, sized for Gemini; above it the AI was silently skipped). `AiRanking:RateLimitPerMinute` and `AiRanking:MaxConcurrency` in `appsettings.json`, pinned by `ShippedAppSettings_SizeTheRankingBudgetForTheTeamAiService_NotForGemini`. Lower them again if the provider is ever set back to Gemini. |

## Change

### Signals (D1, D6, D7)

`DerivedBehaviorSignalRecorder` appends events in the caller's transaction, after the caller saved its own change.

| Trigger | Event | Source | Weight |
|---|---|---|---|
| Adjust: visit present before, absent after | Dislike | `ItineraryEdit` | 1.0 |
| Adjust: visit absent before, present after | Like | `ItineraryEdit` | 1.0 |
| Navigation: visit skipped | Skip | `Navigation` | 0.7 |
| Navigation: visit reached | Like | `Navigation` | 0.3 |
| Create: mandatory POI | Like | `SchedulingRequest` | 0.5 |
| Existing endpoint: Like / Dislike / Skip | unchanged | unchanged | 1.0 / 1.0 / 0.5 |

- Only visits count. Rest stops are placed by the system and recomputed on every adjust, so they never become signals (found by the local E2E).
- Each signal has a deterministic `ClientEventId` (SHA-256 of source, action key, POI and type), so a retried action writes nothing new.
- A storage failure (CHECK constraint before the migration, missing table) is logged as `derived_behavior_signal_failed`, the rows are detached, and the traveler's action succeeds. A deadlock is rethrown for the caller's retry.
- Creating a request with mandatory POIs does one more `SaveChanges`.

### Database

- `database/migrations/20261111_uc10_behavior_event_sources.sql` relaxes `CK_RecommendationBehaviorEvents_SourceContext` and `CK_RecommendationBehaviorEvents_TypeSource` for the new sources. No rows are touched; safe to rerun.
- `database/migrations/20260929_add_recommendation_behavior_events.sql` (TM-214) is edited: it verifies its constraints character by character, and `apply-schema.sh` reruns every migration on an existing database, so it now also accepts the shape the new migration leaves. This follows `20261025_uc21_navigation_access_revoked_reason.sql`.

### Behavior profile for the AI (D2, D3)

- `BehaviorProfileBuilder` reads the traveler's Like/Dislike/Skip events of the last 365 days (newest 500), weighs each by kind and age (`weight × 0.5^(ageDays / 90)`), nets them per POI, and keeps the 50 heaviest liked and disliked POIs with their name, category and tags.
- `PoiRankingContext` gains `Behavior` (`PoiRankingBehavior` → `PoiRankingBehaviorPoi`: POI id, name, category, tags, weight). It never carries the traveler's identity, dates or scores; two contract-guard tests pin the approved fields.
- `PoiRankingOrchestrator` calls the provider when there are declared interests **or** a behavior profile. `TripMateAiPoiRankingProvider` sends `behavior` and omits it when there is none, so the request is unchanged for travelers without history. The Gemini provider ignores it.
- The profile is not part of the generation snapshot hash: its weights decay continuously, so hashing them would report a false data change between prepare and finalize.

### Removed-stop cooldown (D8)

- A POI is cooling down when the traveler's latest Like/Dislike/Skip signal for it in the last `RemovedStopCooldownDays` (14) is a Dislike or a Skip. A later Like (added back, marked mandatory, reached) lifts it.
- `PoiRankingOrchestrator` leaves cooling-down POIs out before it selects the provider pool; create and regenerate both drop optional POIs outside the pool. It applies with the AI provider disabled too.
- If every candidate is cooling down, all are suggested. Mandatory POIs and manual adjust are unaffected.

### Regenerate (D4)

- `ItineraryVersionService.CreateRegeneratedVersionAsync` ranks optional places through `PoiRankingOrchestrator` and maps the snapshot with `CreateSchedulingRequestCommandHandler.ToCandidate`, like create. `PoiRankingInputCandidate.From` is shared by both flows.
- The "other places first" tiers still decide before any score; the ranking orders places within a tier.
- With AI ranking disabled (production today), regenerate now orders by the orchestrator's base score instead of `CalculatePreferenceScore`, the same as create.
- The provider call runs inside the regenerate transaction, as the routing call already does (at most `ProviderTimeout`).

### Configuration (`Personalization` section, defaults in code)

| Key | Default |
|---|---|
| `BehaviorHalfLifeDays` | 90 |
| `BehaviorLookbackDays` | 365 |
| `MaxBehaviorEvents` | 500 |
| `MaxBehaviorPoisPerSide` | 50 |
| `RemovedStopCooldownDays` | 14 (0 turns the rule off) |
| `BehaviorSignalWeights` | `ExplicitLike` 1.0, `ExplicitDislike` 1.0, `ExplicitSkip` 0.5, `StopAdded` 1.0, `StopRemoved` 1.0, `StopReached` 0.3, `StopSkipped` 0.7, `MandatoryChosen` 0.5 |

## Measured (local, `docs/benchmarks/UC-10-behavior-learning-2026-11-11.md`)

`weighted-gate --behavior-learning <endpoint>`: 12 simulated travelers with a hidden taste, 5 consecutive days, real AI service (bge-m3). After each day the traveler removes the stops they hate and visits the rest. First the real Da Nang fixture (19 POIs), where the acceptance criteria are judged:

| Ranking | Day 1 hit / hate | Day 5 hit / hate |
|---|---|---|
| A. Rules only | 39% / 34% | 39% / 34% |
| A1. Rules + behavior counting | 39% / 34% | 41% / 30% |
| A2. Rules + counting + cooldown | 39% / 34% | 59% / 0% |
| B. AI, no behavior | 37% / 37% | 37% / 37% |
| C1. AI + behavior profile | 37% / 37% | 49% / 20% |
| **C. AI + behavior profile + cooldown (shipped)** | 37% / 37% | **54% / 0%** |

- **Acceptance passed:** hit rate +17 points, hate rate 37% → 0% from day 3; day 1 equals today's AI ranking.
- A ranking call with a profile takes a median of 6 ms (warm cache).

The same run on a **simulated city** (`LookAlikeCityFactory`: 54 stops in seven families of look-alike places, real place names, synthetic positions; as in production only the 40 best-ranked candidates reach the solver):

| Ranking | Day 1 hit / hate | Day 2 | Day 5 | Stops removed over 5 days |
|---|---|---|---|---|
| A. Rules only | 28% / 35% | 28% / 35% | 28% / 35% | 125 |
| A1. Rules + behavior counting | 28% / 35% | 25% / 28% | 29% / 26% | 98 |
| A2. Rules + counting + cooldown | 28% / 35% | 26% / 23% | 34% / 3% | 55 |
| B. AI, no behavior | 26% / 33% | 26% / 33% | 26% / 33% | 120 |
| C1. AI + behavior profile | 26% / 33% | 29% / 19% | 28% / 17% | 76 |
| **C. AI + behavior profile + cooldown (shipped)** | 26% / 33% | **30% / 14%** | **34% / 1%** | **46** |

## Verified by simulation (local, 2026-11-11)

- **API + AI service + SQL Server copy:** after removing Cầu Rồng, a new request and a regenerate no longer suggest it; one `ItineraryEdit` event is written; the AI call takes 7–31 ms and the whole create request 0.9–1.2 s.
- **The phone's navigation calls, AI ranking off (production's current setting):** start a session, reach one stop, skip another, finish. One `Navigation` Like and one `Navigation` Skip are written; a repeated skip writes nothing; the next request no longer suggests the skipped stop and still allows the reached one.
- **The Azure migration, rehearsed with `database/apply-azure.sh` on a local database built from the previous commit:** without `reapply_changed` the run stops with `BLOCKED-changed` and changes nothing (dry run and real run); with it, the edited script is re-verified and the new one applied; a further run has nothing to do; the constraints accept the new sources.
- Not verified: a real device, the hosted environments, and real travelers.

## Findings

1. **What the AI profile adds depends on how many look-alike places a city has.**
   - Without the cooldown the profile learns clearly more than counting on both data sets (real fixture C1 49% / 20% against A1 41% / 30%; simulated city hate rate 17% against 26%).
   - On the 19-POI fixture the cooldown alone does as well as the shipped ranking (A2 59% / 0% against C 54% / 0%): once a traveler's 3–5 hated stops are excluded, nothing similar is left.
   - On the 54-stop city the shipped ranking learns faster and leaves less work to the traveler: 14% hated stops on day 2 against 23% for A2, and 46 stops removed over five days against 55 (16% fewer). The hit rate ends the same (34%).
   - So the measured gain of the AI profile is fewer unwanted suggestions, sooner. It does not raise the share of liked stops: the system learns mainly what to avoid, because a place that is never suggested can never be liked.
2. **Removing a stop for lack of time also starts the cooldown.** It ends after 14 days, or at once when the traveler adds the stop back.
3. **Travelers who lose short filler stops get fewer stops per day** (for example 5 → 4), because the remaining stops are longer visits.
4. **The first calls after the AI service starts can exceed the 5-second provider timeout** while it embeds new POI texts; those requests use the fallback score. Known from the AI ranking spec, and a large profile makes it longer: 60 candidates plus 100 never-seen behavior POIs took 9.4 s cold on CPU. The service caches each finished batch of 32 texts, so two or three requests warm it. Warm, the same request takes a median of 22 ms (p95 27 ms; 10 ms without a profile), and 16 concurrent requests all succeed (p95 384 ms).
5. **No client sends explicit Like/Dislike yet.** Buttons on Web and Mobile would add stronger signals.

## Deploy

1. Run "DB migrate (Azure SQL)" **before** merging this branch, with `reapply_changed=20260929_add_recommendation_behavior_events.sql` (a recorded migration changed; it only re-verifies). Without the input the run stops with `BLOCKED-changed` and changes nothing.
2. Merge the backend. Until the migration is applied, signals fail the CHECK constraint, are logged, and nothing else breaks.
3. Deploy the Capstone_AI branch for semantic learning. Web and Mobile need no change.
4. Rollback: `Personalization__RemovedStopCooldownDays=0` turns the cooldown off; `AiRanking__Enabled=false` stops the provider (the profile is then not built).
