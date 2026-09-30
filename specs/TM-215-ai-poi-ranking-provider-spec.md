# TM-215 Backend Technical Specification

## 1. Document Control

- Ticket: TM-215 / [UC-10] Implement AI POI Ranking Provider, Contract & Fallback
- Repository: `D:\FPTUCapstone\Capstone_BE`
- Branch: `feature/phuctv-tm214-recommendation-feedback-signals`
- Baseline HEAD: `4de7443fef8984f3e6a14382be0ad9419bde1199` (verified clean at spec creation)
- Architecture freeze: TM-215 Steps 1, 2, 2B, 2C, 2D, 2E, 2F — FINAL PASS
- Correction passes: Step 3A (ranking snapshot, no Phase-2 recompute, explicit score carriers, interest-test semantics, cleanups 1–8) and Step 3B final consistency audit (snapshot-vs-dynamic generator ordering wording, top-K proof scope, implementation-work dependency alignment) applied to this revision
- Related artifacts: `specs/TM-213-*`, `specs/TM-214-*` (landed), `plans/TM-215-ai-poi-ranking-provider-plan.md`

## 2. Executive Summary

TM-215 adds a TripMate-owned personalization layer (TM-213 explicit interests + TM-214 personal behavioral signals + POI quality → deterministic `TripMateBaseScore`), a bounded candidate-retrieval pool (`MaxProviderCandidates` = 60), an optional provider-neutral semantic AI reranker (`AiScore` ∈ [0,1]), local 60/40 fusion into `EffectiveDesirabilityScore`, a **complete immutable Phase-1 ranking snapshot** (scores + all frozen Phase-1/matrix-preselection tie-break fields), and Phase-2 authoritative revalidation — all without touching scheduling/hard-constraint semantics, existing HTTP contracts, the database schema, or TM-213/TM-214 behavior. No DB migration is required. Phase 2 never recomputes personalization.

## 3. Scope

### In Scope
Local personalization feature model and scorer; batch behavior aggregation; BaseScore; complete ranking snapshot; provider-neutral ranking contract + orchestrator + output validation; bounded provider pool; two-phase handler integration; score carriers (explicit, no defaults); generator/preselection ordering keys; failure/fallback/cancellation; configuration + validation; observability; tests.

### Out of Scope
Feasibility/scheduling decisions; itinerary mutation; reorder UI; TM-217 presentation; ML/Learning-to-Rank; ranking-impression persistence; DB migration; vendor SDK in Application/Scheduling; changes to TM-213 scoring, TM-214 write path, replay/idempotency, or any existing HTTP contract.

## 4. Current Verified Backend Architecture

| Step | Location (verified at `4de7443`) | Responsibility |
|---|---|---|
| HTTP entry | `src/TripMate.Api/Controllers/V1/SchedulingRequestsController.cs:16–22` | `[Authorize(Roles = nameof(UserRole.Traveler))]`, `POST api/v1/scheduling-requests` |
| Handler | `src/TripMate.Application/Features/Scheduling/Create/CreateSchedulingRequestCommandHandler.cs:30–201` | canonicalization, `ComputeRequestHash` (:36), serializable transaction + `ISchedulingRequestLock` (:38–43), replay (:45–53) |
| TM-213 read | handler:57–62 | `TravelerProfiles.InterestTagsJson` live-read; `TravelerPreferenceScoring.ParsePreferenceTokens` |
| Candidate query | handler:63–69 | Active POIs + Category/OpeningHours/PoiTags→Tag |
| Prefilter | handler:100–128 | `IsPlanningReady` + equirectangular radius → `selectablePois` |
| Matrix selection | handler:141 → `SelectMatrixCandidates` :251–281 | mandatory by `Id` + optional top-N by `PreferenceScore`→Scenic→Photo→distance→cost→`Id`, `Take(remainingCapacity)` |
| Carrier mapping | handler:282–299 `ToCandidate` (**target-typed `new(...)`, the only production construction site**) | `GenerationCandidate` with `PreferenceScore` at :298 |
| Scheduler | handler:160–161 → `ItineraryGenerationService.GenerateAsync` (`…/Scheduling/Common/ItineraryGenerationService.cs`) | hard constraints + greedy/permutation plan |
| Persistence | handler:169–199 | `Itinerary.CreateCspGenerated`, `AddItem`, `SchedulingRequest.Complete` |

Matrix limit verified: `EffectiveMaxMatrixCandidates` = 40 (default), min 6 (`SchedulingGenerationOptions.cs:6–17`). Scheduler verified: greedy heuristic + exhaustive mandatory permutation — **not** CP-SAT/OR-Tools/CSP (grep-verified; `"CSPGenerated"` is only `Itinerary.SourceType`).

**GenerationCandidate construction-site audit (complete):**
1. `CreateSchedulingRequestCommandHandler.ToCandidate` (handler:282–299) — production; maps mandatory and optional POIs.
2. `ItineraryGenerationServiceTests.Candidate(...)` fixture factory (`tests/TripMate.Application.UnitTests/Features/Scheduling/ItineraryGenerationServiceTests.cs:369–378`) — test fixture factory used by all generator scenarios.
No other construction sites exist (grep-verified). Both become explicit-score construction sites (Correction 3).

## 5. Target Architecture

Two-phase (Step-2C/2E frozen): **Phase 1** (outside the write transaction) builds features, computes `TripMateBaseScore` for all optionals, selects the bounded pool, calls the optional AI, validates, blends, and produces the **complete immutable ranking snapshot** (scores + ranking tie-break fields per POI). **Phase 2** (existing serializable transaction + lock) runs the authoritative replay check, re-reads authoritative POI state, retains only valid pool candidates with their frozen Phase-1 ranking data, selects the final matrix, schedules, and persists. **Phase 2 never recomputes personalization.**

## 6. Responsibility Boundaries

- **TM-213**: `PreferenceScore` semantics unchanged (compat/explainability feature only).
- **TM-214**: capture/write path unchanged; read-only feature source.
- **TM-215**: optional-POI desirability, ordering, bounded retrieval, optional AI rerank, Effective score, optional reason, complete ranking snapshot.
- **Scheduler (TM-216 boundary)**: opening hours, route/matrix, budget, available time, mandatory inclusion, final feasibility — authoritative.
- **TM-217**: full explanation presentation (TM-215 reason is optional, opaque, ≤500 chars).

## 7. Functional Requirements

- **FR-215-001** `CategoryAffinity` ∈ [0,1]: 1 iff normalized POI category ∈ normalized tokens, else 0; reuse `TravelerPreferenceScoring.NormalizePreferenceToken`; null/empty/malformed `InterestTagsJson` → empty token set → 0.
- **FR-215-002** `TagAffinity = min(1, matchingTagCount/3)`: normalize category + all tags, dedupe tags, remove tag == normalized category, count unique token-matching non-category tags.
- **FR-215-003** `PersonalBehaviorAffinity` via frozen formula (skipWeight 0.5, priorWeight 2.0): `raw = (L − D − 0.5·S)/(L + D + 0.5·S + 2.0)`; affinity = `(clamp(raw,−1,1)+1)/2`; Reorder excluded from counts and history.
- **FR-215-004** Behavior cascade: Tier 1 direct POI (current traveler+POI, L+D+S > 0) → formula; Tier 2 category (current traveler, `CategoryId` pool, `DistinctInteractedPoiCount ≥ 2` AND pooled L+D+S > 0) → same formula; Tier 3 → 0.5.
- **FR-215-005** `TripMateBaseScore = 0.300·Cat + 0.200·Tag + 0.250·Beh + 0.125·Scenic + 0.125·Photo` (decimal; Scenic/Photo = value/10, NULL → 0.5); deterministic; no clamping beyond FR-215-003.
- **FR-215-006** Behavior aggregation: **at most two** grouped traveler-scoped queries (A: per-POI by candidate PoiIds; B: per-category by candidate CategoryIds with `DistinctInteractedPoiCount`); zero candidates → zero behavior queries; empty PoiId set → skip Query A; empty CategoryId set → skip Query B; Reorder + other travelers excluded; AsNoTracking; query count is O(1), never O(candidates).
- **FR-215-007** Complete Phase-1 ranking snapshot per pool candidate (`PoiRankingSnapshotEntry`): `PoiId`, `TripMateBaseScore`, `EffectiveDesirabilityScore`, `ScenicScoreForRanking`, `PhotoRatingForRanking`, `ExplorationDistanceForRanking`, `EstimatedVisitCostForRanking`. The snapshot is the **only source of frozen personalization and matrix-preselection ranking data** in Phase 2. The generator's current matrix/current-route distance is intentionally dynamic scheduling context and is not part of the frozen Phase-1 snapshot.
- **FR-215-008** `ProviderPoolPoiIds`: deterministic top `MaxProviderCandidates` optionals by `TripMateBaseScore DESC` → `ScenicScoreForRanking DESC` → `PhotoRatingForRanking DESC` → `ExplorationDistanceForRanking ASC` → `EstimatedVisitCostForRanking ASC` → `PoiId ASC`; frozen for the request; mandatory excluded.
- **FR-215-009** Provider receives only `PreferenceTokens` + per-candidate `PoiId/Name/CategoryName/TagNames` (semantic fields only).
- **FR-215-010** Provider output validated all-or-nothing per Spec §20.
- **FR-215-011** `EffectiveDesirabilityScore = 0.60·Base + 0.40·Ai` (valid AI) or `= Base`; blend local-only (orchestrator).
- **FR-215-012** Phase-2 eligibility: optional candidates only from `ProviderPoolPoiIds` AND current authoritative validity. Phase 2 re-checks **Active**, **planning-ready**, and **current request-radius eligibility using current POI coordinates**; any failure → dropped. New/outside-pool candidates → **ignored, never backfilled**; no second AI call; **no Phase-2 personalization recomputation**. `ExplorationDistanceForRanking` remains the frozen Phase-1 ranking tie-break and is never replaced by the Phase-2 radius-check distance.
- **FR-215-013** Ranking data ownership: Phase-1 snapshot owns `TripMateBaseScore`, `EffectiveDesirabilityScore`, and every **frozen Phase-1 / matrix-preselection** ranking tie-break (`ScenicScoreForRanking`, `PhotoRatingForRanking`, `ExplorationDistanceForRanking`, `EstimatedVisitCostForRanking`). Phase-2 current state owns validity/feasibility inputs (Active/planning-ready, **current coordinates for radius eligibility**, opening hours, matrix travel, current `EstimatedVisitCost` for budget). The generator's current matrix/current-route distance remains scheduler-owned dynamic context.
- **FR-215-014** `GenerationCandidate` requires explicit ranking fields at construction (no defaults): `TripMateBaseScore`, `EffectiveDesirabilityScore`, `ScenicScoreForRanking`, `PhotoRatingForRanking`, and `EstimatedVisitCostForRanking`. Mandatory candidates explicitly receive neutral ranking values where those fields are unused by mandatory permutation semantics. `ExplorationDistanceForRanking` stays handler/snapshot-only because it is used only for matrix preselection.
- **FR-215-015** Provider disabled → no provider call, Base-only ranking, no configuration requirements beyond `Enabled = false`.
- **FR-215-016** Final optional matrix selection uses only current-valid pool candidates and the frozen Phase-1 preselection order: `EffectiveDesirabilityScore DESC` → `ScenicScoreForRanking DESC` → `PhotoRatingForRanking DESC` → `ExplorationDistanceForRanking ASC` → `EstimatedVisitCostForRanking ASC` → `PoiId ASC`, then `Take(remainingCapacity)`.
- **FR-215-017** Generator optional ordering uses explicit snapshot ranking fields carried by `GenerationCandidate`: `EffectiveDesirabilityScore DESC` → `ScenicScoreForRanking DESC` → `PhotoRatingForRanking DESC` → current matrix/current-route distance ASC → `EstimatedVisitCostForRanking ASC` → `PoiId ASC`. Current cost used for budget feasibility remains separate from `EstimatedVisitCostForRanking`.
- **FR-215-018** Replay/idempotency semantics remain unchanged: ranking never enters `request_hash`; Phase-1 pre-check hit skips personalization/provider work and proceeds to authoritative Phase-2 replay; concurrent false misses may duplicate one provider call but cannot duplicate the authoritative result.
- **FR-215-019** Runtime provider operational failures (timeout/429/5xx/network/invalid response) fall back to Base-only ranking; caller cancellation propagates; local feature/DB failures remain normal application failures and are never hidden as AI fallback.

## 8. Non-Functional Requirements

- **NFR-215-001 Performance**: **at most two** grouped behavior queries per normal Phase-1 execution (zero allowed for empty candidate sets); BaseScore O(N) in-memory; pool sort O(N log N); payload ≤ 60; no DB query per candidate.
- **NFR-215-002 Determinism**: identical inputs/snapshot → identical ordering; ordering is a strict total order (ends on `PoiId`).
- **NFR-215-003 Fallback**: every AI failure mode → snapshot/Base; AI failure never yields HTTP 500 when local ranking works.
- **NFR-215-004 Privacy**: payload per §18; no identity/secrets/raw events; logs per §36.
- **NFR-215-005 Transaction safety**: no provider network I/O under the serializable transaction/app lock; Phase 2 unchanged except snapshot application.
- **NFR-215-006 Cancellation**: caller cancellation propagates; provider timeout (5 s default) → snapshot/Base.
- **NFR-215-007 Observability**: outcome categories (§36); no secrets/payloads logged.
- **NFR-215-008 Configuration safety**: invalid options fail startup; `Enabled = false` is a valid, credential-free configuration.

## 9. Personalization Feature Model

| Feature | Source | Normalization | Range | Missing |
|---|---|---|---|---|
| CategoryAffinity | POI.Category vs tokens | token-normalized equality | [0,1] | 0 |
| TagAffinity | POI.Tags vs tokens (dedup, category-excluded) | min(1, n/3) | [0,1] | 0 |
| PersonalBehaviorAffinity | TM-214 events (current traveler) | frozen formula (§12) | (0,1) | cascade → 0.5 |
| ScenicQuality | `PointOfInterest.ScenicScore` (`decimal?`, 0–10) | value/10 | [0,1] | 0.5 |
| PhotoQuality | `PointOfInterest.PhotoRating` (`decimal?`, 0–10) | value/10 | [0,1] | 0.5 |

## 10. CategoryAffinity

`1.0m` iff `tokens.Contains(NormalizePreferenceToken(poi.Category.Name))` else `0.0m`. Reuses `TravelerPreferenceScoring` normalization — no duplicated semantics. Duplicate tokens impossible (ordinal `HashSet`).

## 11. TagAffinity

`normalizedCategory = Normalize(poi.Category.Name)`; `normalizedTags = poi.PoiTags.Select(t => Normalize(t.Tag.Name)).Distinct().Where(t => t != normalizedCategory)`; `matchingTagCount = tokens.Intersect(normalizedTags).Count()`; `TagAffinity = min(1m, matchingTagCount / 3m)`. 0→0, 1→0.333, 2→0.667, ≥3→1. Exact category/tag duplicates cannot double-count; broader semantic correlation is a documented V1 limitation.

## 12. PersonalBehaviorAffinity

Frozen formula (FR-215-003) with `skipWeight = 0.5m`, `priorWeight = 2.0m` from options. `decimal` arithmetic; internal clamp defensive only. Deterministic; `OccurredAtUtc` never affects the score.

## 13. Behavior Cascade

Tier 1 direct POI → Tier 2 category (`DistinctInteractedPoiCount ≥ 2` AND pooled L+D+S > 0) → Tier 3 neutral 0.5. Resolved in memory from the two aggregate dictionaries. No tag-level behavioral fallback in V1.

## 14. ScenicQuality / PhotoQuality

Verified: `PointOfInterest.ScenicScore`/`PhotoRating` are `decimal?` mapped to `DECIMAL(3,1) NULL CHECK (BETWEEN 0 AND 10)` (`tripmate_schema_v7.sql:352–353`), NULL on creation (TM-98). Quality = value/10; NULL → `0.5m`.

## 15. TripMateBaseScore

`decimal` sum per FR-215-005. No intermediate rounding; no clamping (mathematically ∈ [0,1]). Weights are V1 explainable heuristic product constants (not ML/trained).

## 16. Behavioral Data Access Specification

**Query A (per-POI)** — `dbContext.RecommendationBehaviorEvents.AsNoTracking().Where(e => e.TravelerUserId == t && candidatePoiIds.Contains(e.PointOfInterestId) && (e.EventType == Like || Dislike || Skip)).GroupBy(e => e.PointOfInterestId).Select(g => new { PoiId, LikeCount, DislikeCount, SkipCount })` → dictionary. Existing indexes (`IX_…_POI`, `IX_…_Traveler_OccurredAt`) sufficient — no migration.

**Query B (per-category)** — join to `PointsOfInterest`, filter `poi.CategoryId IN candidateCategoryIds` (+ traveler + L/D/S), group by `poi.CategoryId`, project counts + `DistinctInteractedPoiCount`. Includes historical POIs outside the candidate set. **Known limitation:** events carry no CategoryId-at-event-time snapshot — aggregation joins the POI's *current* CategoryId (documented; no migration).

**Execution rules (frozen):** zero optional candidates → zero behavior queries · no candidate PoiIds → skip Query A · no candidate CategoryIds → skip Query B · normal non-empty case → Query A once + Query B once · query count is O(1), never O(candidates) · no query inside any loop.

## 17. AI Provider Application Contract

```csharp
namespace TripMate.Application.Features.Scheduling.Personalization;

public interface IPoiRankingProvider
{
    Task<Result<PoiRankingResult>> RankAsync(PoiRankingRequest request, CancellationToken cancellationToken);
}
public sealed record PoiRankingRequest(IReadOnlyCollection<PoiRankingCandidate> Candidates, PoiRankingContext Context);
public sealed record PoiRankingContext(IReadOnlyCollection<string> PreferenceTokens);
public sealed record PoiRankingCandidate(long PoiId, string Name, string CategoryName, IReadOnlyCollection<string> TagNames);
public sealed record PoiRankingResult(IReadOnlyCollection<PoiRankingItem> Ranked);
public sealed record PoiRankingItem(long PoiId, decimal AiScore, string? Reason);
```

Repository alignment: `Result<T>` (`TripMate.Application.Common.Models`), `sealed record`s, `IReadOnlyCollection<>`, `long` identity, namespace `Features/Scheduling/Personalization`.

## 18. AI Request Payload

Request-level: `PreferenceTokens` (normalized traveler-chosen semantic preferences, with no direct traveler identity attached). Candidate-level: `PoiId`, `Name`, `CategoryName`, `TagNames`. Nothing else — no `TripMateBaseScore`, no `PersonalBehaviorAffinity`, no `BehaviorEvidence`, no `PreferenceScore`, no quality/cost/duration, no identity/coordinates/dates (Spec §35).

## 19. AI Response Contract

`PoiRankingItem(PoiId, AiScore, Reason?)`: `AiScore` finite decimal ∈ [0,1]. `Reason` is optional metadata only: normalize with `Trim()`, truncate to the configured/constant maximum of 500 characters, and never use it for scoring, feasibility, routing, or scheduling. Reason length/content alone does not invalidate otherwise valid scores; structurally malformed provider output still follows §20. TM-215 V1 does not persist the reason and does not carry it into `GenerationCandidate`; TM-217 owns any future user-facing explanation contract.

## 20. AI Response Validation

Owner: `PoiRankingOrchestrator`. Pool of N → exactly N unique items; PoiIds ⊆ pool; no unknown/duplicate/missing/extra; `AiScore` finite decimal ∈ [0,1]; Reason optional and normalized per §19 (trim/truncate); reason length alone is not a score-validation failure. Any structural/identity/score violation → **entire provider result rejected → entire pool falls back to snapshot Base**. No partial trust in V1.

## 21. PoiRankingOrchestrator

Owns the ranking work performed **after the handler's replay pre-check and candidate read**: invoke the batch behavior aggregator for the supplied optional candidate IDs/categories → assemble personalization features → compute BaseScore for all optionals → deterministic pool selection → provider invocation when enabled/non-empty → provider-output validation → local blend → `PoiRankingSnapshot` = `ProviderPoolPoiIds` + one `PoiRankingSnapshotEntry` per pool candidate (FR-215-007) + outcome category for logging. **Snapshot owner: `PoiRankingOrchestrator`** (in-memory only; no persistence). A replay pre-check hit means the handler does **not call the orchestrator at all**. The orchestrator itself skips the external provider when the provider is disabled or the optional pool is empty. It never persists scores and performs at most one provider call per invocation.

## 22. Base/AI Fusion

`Effective = 0.60m·Base + 0.40m·Ai` (valid AI); `Effective = Base` otherwise. Fusion local-only; provider neither knows the weights nor blends.

## 23. Configuration

| Option | Default | Validation | Owner | Purpose |
|---|---|---|---|---|
| CategoryAffinityWeight | 0.300m | ≥ 0 | PersonalizationRankingOptions | Base component |
| TagAffinityWeight | 0.200m | ≥ 0 | 〃 | Base component |
| BehaviorAffinityWeight | 0.250m | ≥ 0 | 〃 | Base component |
| ScenicQualityWeight | 0.125m | ≥ 0 | 〃 | Base component |
| PhotoQualityWeight | 0.125m | ≥ 0 | 〃 | Base component |
| (Base sum) | == 1 | \|sum − 1\| ≤ 0.001m | 〃 | integrity |
| BaseWeight / AiWeight | 0.60 / 0.40 | ≥ 0; \|sum − 1\| ≤ 0.001m | 〃 | fusion |
| SkipWeight / PriorWeight | 0.5 / 2.0 | ≥ 0 / > 0 | 〃 | behavior formula |
| MinCategoryDistinctPoiCount | 2 | ≥ 1 | 〃 | category gate |
| MaxProviderCandidates | 60 | ≥ EffectiveMaxMatrixCandidates | 〃 | pool cap |
| ProviderTimeout | 00:00:05 | > 0 | 〃 | bounded AI wait |

`PoiRankingProviderOptions` (Infrastructure): `Enabled` (default **false**), `Endpoint`, `ApiKey`, and adapter-specific settings such as `ModelName` when the selected provider requires one. **Configuration semantics (frozen):** `Enabled = false` → provider-specific fields may be absent — intentionally disabled, no provider call, Base-only ranking, startup valid. `Enabled = true` → `Endpoint` and every credential/model field required by the selected adapter MUST be present and valid; a missing required field causes **startup options-validation failure** (never silently converted to runtime Base fallback). Runtime operational failures (timeout/429/5xx/network/invalid response) → Base fallback per §33. Validation via `IValidateOptions<T>` + `ValidateOnStart`. ApiKey/credentials come from user-secrets/environment/secret storage; never committed, logged, or returned.

## 24. Provider Candidate Retrieval

Base for ALL optionals → deterministic sort (Base DESC → `ScenicScoreForRanking` DESC → `PhotoRatingForRanking` DESC → `ExplorationDistanceForRanking` ASC → `EstimatedVisitCostForRanking` ASC → PoiId ASC) → `Take(MaxProviderCandidates)` → pool + snapshot. Before AI; AI never used for retrieval.

## 25. ProviderPoolPoiIds

Immutable per request; the only optional universe eligible for final matrix selection. Outside-pool: no AiScore, no Effective, cannot re-enter in Phase 2, cannot be AI-rescued. Mandatory excluded.

### 25.1 AI-off retrieval-equivalence scope

For AI disabled/failed, let `O` be the frozen Phase-1 matrix-preselection order: `TripMateBaseScore DESC` → `ScenicScoreForRanking DESC` → `PhotoRatingForRanking DESC` → `ExplorationDistanceForRanking ASC` → `EstimatedVisitCostForRanking ASC` → `PoiId ASC`. If `N = MaxProviderCandidates`, `K = remaining optional matrix capacity`, `N >= K`, **and the candidate eligibility set is unchanged between Phase-1 retrieval and final matrix preselection**, then `topK(topN(S,O),O) = topK(S,O)`. This proves that the pool cap by itself does not alter the Base-only top-K.

This equivalence **does not apply after Phase-2 invalidation**. If pooled candidates become inactive, lose planning readiness, or move outside the request radius, TM-215 intentionally drops them and performs **no backfill**; the final candidate set may therefore be smaller/different than a fresh full-universe top-K. That is an explicit V1 candidate-universe policy, not a contradiction in the retrieval proof. The proof also applies only to matrix preselection, not to the generator's later greedy ordering, whose route-distance tie-break is dynamic.

## 26. Score Carrier Model

**`PoiRankingSnapshotEntry`** (new immutable record, file `src/TripMate.Application/Features/Scheduling/Personalization/PoiRankingSnapshot.cs`, owner `PoiRankingOrchestrator`): `PoiId`, `TripMateBaseScore`, `EffectiveDesirabilityScore`, `ScenicScoreForRanking`, `PhotoRatingForRanking`, `ExplorationDistanceForRanking`, `EstimatedVisitCostForRanking`. **Ranking vs feasibility ownership (frozen):** all seven snapshot fields are *ranking* data; Phase-2 current DB owns validity/feasibility (Active/planning-ready, opening hours, matrix travel, **current `EstimatedVisitCost` for budget feasibility**). `EstimatedVisitCost` therefore has two usages: Phase-1 `EstimatedVisitCostForRanking` (tie-break only) vs Phase-2 current cost (budget) — ranking never uses Phase-2 cost after the pool freezes.

**`GenerationCandidate`** gains the ranking data required by the generator as **required explicit constructor parameters — no default values**: `TripMateBaseScore` (`decimal`), `EffectiveDesirabilityScore` (`decimal`), `ScenicScoreForRanking` (`decimal?`), `PhotoRatingForRanking` (`decimal?`), and `EstimatedVisitCostForRanking` (same decimal type used by the current cost field). Mandatory candidates explicitly receive neutral ranking values (`0m` for Base/Effective; current/snapshot-equivalent tie-break values may be passed explicitly but remain unused by mandatory permutation semantics). `PreferenceScore` remains unchanged. `AiScore` is not carried. `ExplorationDistanceForRanking` remains in `PoiRankingSnapshotEntry`/handler only because it is consumed by matrix preselection before `GenerationCandidate` creation. The existing current scheduling/cost fields remain available separately for hard feasibility; in particular, budget logic must continue to read the **current** Phase-2 cost, not `EstimatedVisitCostForRanking`.

## 27. Phase 1 Read/Rank Flow

Outside the write transaction, AsNoTracking: (1) handler performs replay existence **pre-check** (optimization only) — if an existing request row is found, **skip all personalization** and do not call the orchestrator; proceed directly to Phase 2's authoritative replay path; (2) on a miss, handler reads preference/profile inputs and current selectable POIs, preserving the existing planning-ready/radius retrieval semantics and separating mandatory from optional candidates; (3) handler invokes `PoiRankingOrchestrator` once for the optional set; (4) inside the orchestrator, Query A + Query B execute through `PersonalBehaviorFeatureAggregator` as applicable; (5) features → BaseScore for all optionals; (6) deterministic retrieval freezes `ProviderPoolPoiIds` and the Base-order tie-break snapshot; (7) optional provider call uses a linked-CTS timeout; (8) validate provider output; (9) blend locally and return the **complete immutable ranking snapshot** (FR-215-007). The provider is skipped when disabled or the optional pool is empty.

## 28. Phase 2 Transactional Flow

Existing body preserved: `ExecuteInSerializableTransactionAsync` + `ISchedulingRequestLock.AcquireAsync` → **authoritative** replay check (`ReplayAsync` unchanged: stored result / conflict) → authoritative POI re-read → re-apply current validity gates (**Active + planning-ready + current request-radius eligibility using current coordinates**) → retain only currently valid candidates whose IDs are in `ProviderPoolPoiIds` with frozen Phase-1 ranking data → `SelectMatrixCandidates` (pool-only optionals, snapshot ordering) → `ToCandidate`/`GenerationInput` → `GenerateAsync` → persist. A POI that moved outside the request radius after Phase 1 is dropped with **no backfill**; its frozen `ExplorationDistanceForRanking` is not used to pass the current radius gate. **There is no "no snapshot → compute Base in Phase 2" path.** If the Phase-1 pre-check hit but the authoritative record is missing: **unreachable under current persistence semantics** — verified: `planning.SchedulingRequests.traveler_user_id REFERENCES dbo.Users(user_id)` with no `ON DELETE CASCADE` (v7:691), no `Remove` calls on the DbSet in `src`, no deletion endpoint; the FK blocks user deletion while requests exist. Defensive posture if a future deletion feature appears: rollback Phase 2, restart Phase-1 ranking exactly once (outside the transaction), never compute ranking under the write transaction, never retry more than once (documented, not implemented).

## 29. Race / Staleness Semantics

| Change between phases | Behavior |
|---|---|
| POI inactive / not planning-ready | drop (even if pooled) |
| POI coordinates change so it is outside the request radius | drop using current coordinates; no backfill; frozen `ExplorationDistanceForRanking` remains ranking-only |
| POI coordinates change but it remains inside the request radius | keep if otherwise valid; ranking keeps the Phase-1 `ExplorationDistanceForRanking` |
| New POI appears (newly Active) | ignore for this request |
| Outside-pool POI improves | ignore for this request |
| Pool candidate disappears | drop; no backfill; fewer optionals allowed |
| Category/tags change | Phase-1 ranking snapshot retained (Base not recomputed) |
| Scenic/photo change | Phase-1 `ScenicScoreForRanking`/`PhotoRatingForRanking` retained |
| Exploration center/cost change (ranking) | Phase-1 `ExplorationDistanceForRanking`/`EstimatedVisitCostForRanking` retained |
| Estimated cost change (feasibility) | Phase-2 current cost authoritative for budget |
| Opening hours / travel time / hard-feasibility change | Phase-2 authoritative |
| TravelerProfile / TM-214 behavior change | Phase-1 ranking snapshot retained |

No snapshot versioning in V1; bounded staleness accepted. Personalization never recomputed in Phase 2.

## 30. Replay / Idempotency

Phase-1 pre-check is an optimization only: hit → skip personalization entirely → Phase-2 authoritative `ReplayAsync` (stored result / conflict). No personalization work on the pre-check path. Pre-check false miss (concurrent race) → both may call the provider once → Phase-2 resolves correctness (accepted V1 cost). Ranking never enters `request_hash`. Pre-check hit + authoritative record missing → unreachable (§28, evidence cited).

## 31. Final Matrix Selection

Mandatory first; `remainingCapacity = EffectiveMaxMatrixCandidates − mandatoryCount`; optional universe = valid pool candidates only; ordering uses the **snapshot** fields: `EffectiveDesirabilityScore DESC` → `ScenicScoreForRanking DESC` → `PhotoRatingForRanking DESC` → `ExplorationDistanceForRanking ASC` → `EstimatedVisitCostForRanking ASC` → `PoiId ASC`; `Take(remainingCapacity)`; never merge outside-pool candidates.

## 32. Generator Ordering

`EffectiveDesirabilityScore DESC` → `ScenicScoreForRanking DESC` → `PhotoRatingForRanking DESC` → **current route-matrix travel time from the original request start ASC** (generator-computed `MatrixMinutesFromStart = matrix.GetMinutes(0, candidateIndex)`, where matrix row `0` is the original request start) → `EstimatedVisitCostForRanking ASC` → `PoiId ASC`. `MatrixMinutesFromStart` is fixed for the optional ordering pass: it does not use the evolving `previousIndex`, and the optional candidates are not re-sorted after each insertion. The evolving `previousIndex` remains part of greedy travel feasibility only. These ranking-only fields are explicitly carried into `GenerationCandidate`; the existing current `EstimatedVisitCost` (or equivalent current scheduling-cost field) remains separate and is still used for budget feasibility. **Scope of the AI-off topK proof:** it applies to **matrix preselection only**, uses the frozen order `O` defined in §25.1, and additionally assumes the candidate eligibility set is unchanged between retrieval and final preselection. It does **not** apply after Phase-2 invalidation/no-backfill and does **not** extend to generator behavior.

## 33. Failure / Fallback Matrix

| Condition | Expected behavior | HTTP impact | Log/metric |
|---|---|---|---|
| Provider disabled (`Enabled = false`) | Base-only, no call, no config requirements | none | provider-skipped |
| Enabled = true + missing/invalid adapter-required provider config | **startup options-validation failure** (not runtime fallback) | n/a | config-invalid |
| Timeout | Base for pool | none | timeout |
| 429/quota | Base for pool | none | quota |
| Network error / 5xx | Base for pool | none | network/server-error |
| Malformed/invalid response | Base for pool | none | invalid-response category |
| Caller cancellation | propagate | request aborted | cancelled |
| Local feature/DB failure | normal application failure | per existing error handling | system-error (NOT AI fallback) |

## 34. Cancellation Semantics

Orchestrator creates `CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)` + `CancelAfter(ProviderTimeout)`. On `OperationCanceledException`: caller token cancelled → **propagate**; else → provider timeout → Base fallback. Adapter respects the token end-to-end.

## 35. Privacy / Security

Payload = §18 only. ApiKey from secure config (user-secrets/env), never committed/logged/returned. `HttpClient` via `IHttpClientFactory` (typed client; ORS precedent `DependencyInjection.cs:66`). Response validated before trust. Authorization header never logged.

## 36. Logging / Observability

Structured fields: provider name, enabled state, candidate count, pool count, latency (ms), outcome category (success/timeout/quota/network/server-error/invalid-response category), fallback-used, timeout flag. **Never logged**: API key, Authorization header, JWT, email, password, `TravelerUserId` where avoidable, raw events, full request body/prompt, full external response. PoiIds debug-level only. No ranking-telemetry persistence in V1.

## 37. Persistence

**DB migration required? NO — verified.** TM-214 events table exists (`20260929_add_recommendation_behavior_events.sql`) with sufficient indexes; scores are per-request; no impressions; no new table/columns. **Deletion-semantics evidence:** `planning.SchedulingRequests` has no application deletion path (no `Remove` calls; FK to Users without cascade, v7:691) — supporting the §28 unreachability conclusion.

## 38. Performance / Query Analysis

Behavior access: **at most 2** round trips (0 allowed for empty sets). BaseScore: O(optionals). Pool sort: O(N log N). Payload ≤ 60. Fallback resolution: O(1) dictionary lookups. Phase-1 duration: reads + provider ≤ timeout. Phase-2: unchanged + dictionary lookups. O(N) behavior queries explicitly rejected.

## 39. Existing Test Semantic Impact

Distinction frozen: **FEATURE CONTRIBUTION TEST** (verifies a signal positively contributes) vs **GLOBAL ORDERING INVARIANT** (verifies unconditional final order — no longer guaranteed).

- **KEEP / REINTERPRET**: `Handle_UsesSavedTravelerInterestTagsToRankOptionalPois` — fixture verified (`SeedPreferenceRankingPoisAsync`: Nature fallback + Culture preferred, **no Scenic/Photo** → neutral 0.5 quality; token `culture`): Base = 0.30·1 + 0.125·0.5 + 0.125·0.5 = **0.55** vs fallback **0.25** → assertion passes under the frozen BaseScore. Semantics: "Saved InterestTags positively influence ranking **in this concrete fixture**" — not universal dominance. Add a clarifying comment/assertion note.
- **KEEP**: `Handle_ProfileWithoutMatchingInterest_PreservesFallbackOrdering` — no tokens → both POIs Base = 0.25 (equal) → the identical tie-break chain (Scenic NULL → Photo NULL → distance → cost → Id) resolves exactly as today (verified: fixture differs only in coordinates/Id) → passes unchanged.
- **UPDATE INTENTIONALLY**: `Generate_AddsMultipleOptionalPoisInDeterministicPreferenceOrder` (fixture S2/P3 vs S5/P4 → blend preserves order: 0.0625 vs 0.1125 — verified; fixture must now set explicit scores; add scenic/photo trade-off cases where the blend reorders by design). `ItineraryGenerationServiceTests.Candidate(...)` fixture factory (:369) must require explicit scores.
- **KEEP unchanged**: `Handle_MoreThanFortyEligiblePois_BoundsMatrixAndRetainsMandatoryPois`, all replay/idempotency tests, `Handle_PublicTransit_*`, all generator gate/rest tests, `TravelerPreferenceScoringTests`, TM-214 suites, validator tests, persistence suites.

## 40. Required New Automated Tests

Base score (determinism, normalization, dedup, saturation, NULL→0.5, weight validation, startup failure) · behavior math (§6 table; Reorder excluded; timestamp-inert) · cascade (direct wins; 1-POI no generalize; ≥2 generalize; Reorder-only; other travelers excluded) · batch queries (at-most-two, skip rules, no per-candidate query, category history beyond candidate set) · **ranking snapshot / AI-off retrieval proof** (1 Scenic tie-break frozen if DB Scenic changes; 2 Photo same; 3 exploration distance same; 4 cost snapshot frozen while budget uses current cost; 5 unchanged-eligibility AI-off case proves `topK(topN(S,O),O) = topK(S,O)`; 6 Phase-2 invalidation case explicitly does **not** expect full-universe equivalence and instead verifies drop + no-backfill) · **Phase-2 radius validity** (7 pooled POI moved outside radius → dropped/no backfill; 8 pooled POI moved but still inside radius → retained while snapshot exploration distance remains unchanged) · **replay** (9 pre-check hit → no provider invocation; 10 pre-check hit → authoritative replay returned; 11 no Phase-2 BaseScore reconstruction path exists; 12 unreachability documented per §28 evidence) · **GenerationCandidate** (13 every optional explicitly receives Base/Effective + Scenic/Photo/Cost ranking fields; 14 mandatory explicitly sets required ranking fields; 15 no call site relies on defaults — compile-enforced; 16 generator fixture factory is structurally updated in the same work item as the required constructor change; 17 generator fixtures set all ranking fields explicitly; 18 budget feasibility reads current cost rather than `EstimatedVisitCostForRanking`) · **TM-213 semantics** (19 InterestTags positive contribution; 20 no universal-dominance assertion) · **config** (21 Enabled=false + absent credentials → valid, Base-only; 22 Enabled=true + missing adapter-required config → startup failure) · **query count** (23 empty candidate set → zero behavior queries; 24 normal → at most one Query A + one Query B; 25 count does not scale with candidates) · **reason metadata** (null accepted; whitespace trimmed; >500 chars truncated; reason never changes AiScore/fallback and is not persisted/carried to `GenerationCandidate`) · pool/blend/failure/Phase-2/privacy tests per §24–§36.

## 41. Integration Tests

SQL integration (pattern `CreateSchedulingRequestSqlServerTests` / `CaptureRecommendationFeedbackSqlServerTests`): seeded travelers/POIs/itineraries/events; AI-stub on/off ordering; retry-after-inactive replay; pool-boundary cases; snapshot-staleness cases (DB quality/cost change between phases); **coordinate/radius race cases** (pooled POI moves outside radius → drop/no backfill; remains inside → keep snapshot ranking distance) against real tables. OpenAPI: no contract change.

## 42. Manual E2E Acceptance Scenarios

E2E-01 cold start/no behavior · E2E-02 explicit interest match · E2E-03 direct Like/Dislike affects same POI · E2E-04 one category POI does not generalize · E2E-05 two distinct category POIs activate fallback · E2E-06 AI success semantic rerank · E2E-07 AI disabled → Base · E2E-08 provider timeout → Base · E2E-09 invalid provider response → Base · E2E-10 >60 optionals → provider bounded · E2E-11 pool candidate inactive → dropped · E2E-12 new/outside-pool POI → ignored · E2E-13 AI-high candidate infeasible → scheduler rejects/skips · E2E-14 replay → no provider call · E2E-15 caller cancellation propagates · **E2E-16 pooled POI coordinates change outside request radius between phases → dropped/no backfill while ranking snapshot remains untouched**. Each documents preconditions, DB setup, request, provider stub mode, expected API/DB/ranking/scheduler behavior, evidence.

## 43. Known V1 Limitations

No behavior recency decay · heuristic weights/blend/thresholds · category/tag semantic correlation beyond exact dedup · current-Category aggregation reinterprets history if CategoryId changes · top-60 bounded retrieval (Base-ranked #61+ never AI-rescued) · AI-off `topK(topN())` equivalence applies only while eligibility is unchanged · no Phase-2 backfill · no second AI call · slight Phase-1 staleness · possible duplicate concurrent AI call · no ranking-impression/LTR dataset · no persisted ranking scores.

## 44. Product / Configuration Sign-Offs

Base weights 0.300/0.200/0.250/0.125/0.125 · blend 0.60/0.40 · `MaxProviderCandidates` 60 · `ProviderTimeout` 5 s · `MinCategoryDistinctPoiCount` 2 · AI vendor/endpoint/key · privacy approval for sending PreferenceTokens + POI Name/CategoryName/TagNames. Product approvals, not architecture defects. Vendor selection required before building the real adapter, not for this spec.

## 45. Open Issues / Deviations

None. All frozen decisions verified implementable; the Step-3A corrections (ranking snapshot, no Phase-2 recompute, explicit score carriers, test-semantics classification) are incorporated without contradictions. §28 unreachability is evidence-backed, not assumed.

## 46. Capstone Defense Notes

Three layers: (1) TripMate-owned rule-based personalization — TM-213 interests + TM-214 personal behavior + POI quality → deterministic, explainable `TripMateBaseScore` (heuristic weights, NOT machine learning); (2) independent semantic AI — `PreferenceTokens` + POI name/category/tags → `AiScore` (never sees local scores or raw behavior); (3) controlled fusion + hard scheduling — 0.60/0.40 blend (NOT trained) → existing heuristic scheduler (NOT CP-SAT/CSP) whose hard constraints always override ranking; the AI never generates the itinerary. Evolution path: Learning-to-Rank behind the same contract once impression data exists (out of V1 scope).

## 47. Acceptance Criteria

AC-1 BaseScore deterministic + component-correct (dedup, saturation, NULL→0.5). AC-2 Behavior formula/cascade per §12–13 with traveler isolation. AC-3 Aggregation: at most two grouped queries, skip rules, no N+1, Reorder/other-travelers excluded, category history beyond candidate set. AC-4 Provider payload semantic-only (§18 exclusions asserted). AC-5 Pool ≤ 60, mandatory-excluded, deterministic, frozen per request, outside-pool excluded from matrix; AI-off `topK(topN())` equivalence is asserted only for unchanged eligibility, while Phase-2 invalidation follows drop/no-backfill semantics. AC-6 Blend local 0.60/0.40; AI-off → Base. AC-7 Output validation all-or-nothing (§20). AC-8 Failure matrix §33 incl. config semantics (§23). AC-9 Phase-2 per §28–29: pool-only universe, **current Active/planning-ready/radius eligibility rechecked**, outside-radius candidates dropped without backfill, no second AI call, no personalization recomputation, feasibility authoritative, snapshot ranking retained. AC-10 `GenerationCandidate` explicitly carries Base/Effective + Scenic/Photo/Cost ranking fields; generator ordering uses ranking fields while budget feasibility uses current cost. AC-11 Replay unchanged; ranking not in `request_hash`; pre-check hit → no provider call. AC-12 No DB migration; TM-213/214 write paths untouched. AC-13 Existing suites green except the deliberately-updated ordering tests; new matrix (§40) green. AC-14 Logs/payloads contain no secrets/identity/raw events. AC-15 Configuration invalid (Enabled=true, missing adapter-required provider config) → startup failure; Enabled=false → valid Base-only deployment.
