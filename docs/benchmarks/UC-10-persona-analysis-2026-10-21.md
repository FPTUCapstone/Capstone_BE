# UC-10 Personalization Verification: Persona Jaccard, Ablation Study & Sensitivity Analysis

> **Status**: **MEASUREMENT COMPLETE — PERSONALIZATION GATE NOT MET**

## 1. Executive Summary & Metadata

- **Run Date**: 2026-10-21
- **Configuration**: Release
- **OS / Runtime**: Microsoft Windows 10.0.26200 / .NET 10.0.0
- **Machine**: HAN (12 logical cores)
- **Git Commit**: `b7eba74ddc8afe3521d860c62eb83976426718d3` (dirty)
- **Evaluation Mode**: Recorded BGE-M3 Replay (deterministic)
- **Configured Embedding Model**: `bge-m3:latest`
- **Runtime Model Identity**: not exposed by provider contract
- **Fixture Provenance SHA-256**: `371409083827D5135ECFF8B18A478EB10A0E09F4F51E123DFD767893C1C2671D`
- **Service Endpoint**: `Recorded fixture (local deterministic)`
- **Target Scenario**: `danang-real-1day` (19 POIs)
- **Objective Mode**: `WeightedUtility` (Csp)
- **Personalization Weights**: BaseWeight = 0.60, AiWeight = 0.40
- **Personalization Gate Threshold**: Jaccard < 0.40
- **Baseline Provider Outcomes**: 4 Success, 1 ProviderSkipped, 0 Fallback
- **Personas Evaluated**: 5
- **Total Persona Pairs**: 10
- **Pairs Meeting Gate (< 0.40)**: 1 / 10
- **Pairs Exceeding Gate (>= 0.40)**: 9 / 10
- **Ablation Variants**: 6 variants across 5 personas (30 runs)
- **Sensitivity Probes**: 7 parameters (105 runs)

## 2. End-to-End Data-Flow Audit (Like/Dislike/Skip & Personalization)

### 2.1 Production Scheduling Pipeline

The end-to-end data flow for personalization in production was verified from source code:

```text
1. User Interactions (Like, Dislike, Skip)
   -> RecommendationBehaviorEvents table (PostgreSQL/EF Core)

2. CreateSchedulingRequestCommandHandler
   -> PoiRankingOrchestrator.BuildRankingAsync(PoiRankingInput)

3. PersonalBehaviorFeatureAggregator.AggregateAsync
   -> Grouped counts by POI ID and Category ID

4. PersonalBehaviorAffinityScorer.Score(poiId, categoryId, aggregation)
   -> Computes behaviorAffinity in [0, 1] (PriorWeight=2.0, SkipWeight=0.5, Neutral=0.5)

5. PersonalizationBaseScorer.Score(features, behaviorAffinity)
   -> BaseScore = 0.30*CatAffinity + 0.20*TagAffinity + 0.25*BehaviorAffinity + 0.125*Scenic + 0.125*Photo

6. AI Blending / Fallback
   -> EffectiveDesirabilityScore = 0.60 * BaseScore + 0.40 * AiScore
      (When AI disabled/skipped/timed out: fallbackAiScore = 0.60 for personalized, 0.50 for default)

7. WeightedRequestInputsFactory.Create
   -> Extracts EffectiveDesirabilityScore as BaseByPoiId
   -> PersonalParamsFactory creates lambda, gamma, rho from TravelerProfile
   -> RealTimeFitProviderFactory builds diurnal time-of-day suitability curves

8. CspItinerarySolver (WeightedUtility Objective)
   -> Objective = sum(Base * TimeFit * gamma^k) - lambda * TravelMinutes - soft rho penalties
```

### 2.2 Reachability in Benchmark Environment

- **Production Status**: **Fully wired**. The code path from database events through affinity scoring to CSP objective inputs is completely implemented in production.
- **Benchmark Status**: **Neutral baseline**. The shadow benchmark corpus operates purely in memory without an active database or historical event seeds. Consequently, `aggregation` has 0 events and `PersonalBehaviorAffinityScorer` returns `NeutralAffinity = 0.5m`. There is no fake data injected.

## 3. Personas Evaluated

| Persona ID | Display Name | Pacing | Risk | Preference Tags | Categories | Description |
|---|---|---|---|---|---|---|
| `CultureEnthusiast` | Culture Enthusiast | Moderate | Medium | culture, museum, history, heritage, art | Culture, Museum | Ưu tiên di tích, bảo tàng, lịch sử; nhịp độ vừa phải (tối đa 5 điểm/ngày). |
| `FoodLoverRelaxed` | Food Lover (Relaxed) | Relaxed | Low | food, coffee, cuisine, culinary | Food & Drink | Ưu tiên trải nghiệm ẩm thực, nghỉ ngơi; nhịp độ thong thả (tối đa 3 điểm/ngày). |
| `ActiveExplorerFast` | Active Explorer (Fast) | Fast | High | nature, adventure, outdoor, hiking, natural attraction | Nature, Natural attraction | Ưu tiên thiên nhiên ngoài trời, tốc độ nhanh (tối đa 8 điểm/ngày). |
| `ScenicPhotography` | Scenic & Photography | Moderate | Low | photography, scenic, beach, sightseeing, nature | Culture, Nature, Beach | Ưu tiên danh lam thắng cảnh, điểm chụp ảnh đẹp, nhịp độ vừa phải. |
| `DefaultGeneralist` | Default Generalist (Cold Start) | Moderate | Medium | (none / cold start) | (none) | Khách vãng lai chưa có hồ sơ; áp dụng cấu hình mặc định của hệ thống. |

## 4. Persona Baseline Itineraries (Csp, WeightedUtility Mode)

| Persona | Feasible | Stops | Travel (min) | Diversity Groups | Midday Outdoor | Model Objective | Visited POI IDs |
|---|---|---:|---:|---:|---:|---:|---|
| `CultureEnthusiast` | Feasible | 7 | 32 | 2 | 0 | 29650 | 5,7,6,3,10,1,8 |
| `FoodLoverRelaxed` | Feasible | 3 | 18 | 2 | 0 | 9228 | 5,8,1 |
| `ActiveExplorerFast` | Feasible | 5 | 50 | 2 | 2 | 24817 | 5,13,12,1,8 |
| `ScenicPhotography` | Feasible | 5 | 38 | 3 | 1 | 23124 | 8,9,3,1,5 |
| `DefaultGeneralist` | Feasible | 5 | 35 | 3 | 0 | 12879 | 5,9,3,1,8 |

## 5. Persona Jaccard Similarity Matrix (5x5)

authoritative definition: `J(A, B) = |VisitedPoiIds(A) ∩ VisitedPoiIds(B)| / |VisitedPoiIds(A) ∪ VisitedPoiIds(B)|`

| Persona | CultureEnthusiast | FoodLoverRelaxed | ActiveExplorerFast | ScenicPhotography | DefaultGeneralist |
|---|:---:|:---:|:---:|:---:|:---:|
| `CultureEnthusiast` | 1.00 | 0.43 | 0.33 | 0.50 | 0.50 |
| `FoodLoverRelaxed` | 0.43 | 1.00 | 0.60 | 0.60 | 0.60 |
| `ActiveExplorerFast` | 0.33 | 0.60 | 1.00 | 0.43 | 0.43 |
| `ScenicPhotography` | 0.50 | 0.60 | 0.43 | 1.00 | 1.00 |
| `DefaultGeneralist` | 0.50 | 0.60 | 0.43 | 1.00 | 1.00 |

## 6. Pairwise Jaccard Breakdown & Threshold Analysis

Threshold: `Jaccard < 0.40` is considered strong personalization differentiation.

| Persona A | Persona B | Stops A | Stops B | Intersection | Union | Jaccard | Gate (< 0.40) | Route A vs Route B |
|---|---|---:|---:|---:|---:|---:|---|---|
| `CultureEnthusiast` | `FoodLoverRelaxed` | 7 | 3 | 3 | 7 | **0.4286** | FAIL (>= 0.40) | `[5,7,6,3,10,1,8]` vs `[5,8,1]` |
| `CultureEnthusiast` | `ActiveExplorerFast` | 7 | 5 | 3 | 9 | **0.3333** | PASS | `[5,7,6,3,10,1,8]` vs `[5,13,12,1,8]` |
| `CultureEnthusiast` | `ScenicPhotography` | 7 | 5 | 4 | 8 | **0.5000** | FAIL (>= 0.40) | `[5,7,6,3,10,1,8]` vs `[8,9,3,1,5]` |
| `CultureEnthusiast` | `DefaultGeneralist` | 7 | 5 | 4 | 8 | **0.5000** | FAIL (>= 0.40) | `[5,7,6,3,10,1,8]` vs `[5,9,3,1,8]` |
| `FoodLoverRelaxed` | `ActiveExplorerFast` | 3 | 5 | 3 | 5 | **0.6000** | FAIL (>= 0.40) | `[5,8,1]` vs `[5,13,12,1,8]` |
| `FoodLoverRelaxed` | `ScenicPhotography` | 3 | 5 | 3 | 5 | **0.6000** | FAIL (>= 0.40) | `[5,8,1]` vs `[8,9,3,1,5]` |
| `FoodLoverRelaxed` | `DefaultGeneralist` | 3 | 5 | 3 | 5 | **0.6000** | FAIL (>= 0.40) | `[5,8,1]` vs `[5,9,3,1,8]` |
| `ActiveExplorerFast` | `ScenicPhotography` | 5 | 5 | 3 | 7 | **0.4286** | FAIL (>= 0.40) | `[5,13,12,1,8]` vs `[8,9,3,1,5]` |
| `ActiveExplorerFast` | `DefaultGeneralist` | 5 | 5 | 3 | 7 | **0.4286** | FAIL (>= 0.40) | `[5,13,12,1,8]` vs `[5,9,3,1,8]` |
| `ScenicPhotography` | `DefaultGeneralist` | 5 | 5 | 5 | 5 | **1.0000** | FAIL (>= 0.40) | `[8,9,3,1,5]` vs `[5,9,3,1,8]` |

### 6.1 Pairs Meeting Personalization Gate (J < 0.40)

- **`CultureEnthusiast` vs `ActiveExplorerFast`**: Jaccard = **0.3333** (Intersection: 3, Union: 9). High divergence in POI selections.

### 6.2 Pairs Exceeding Threshold (J >= 0.40) & Structural Analysis

- **`CultureEnthusiast` vs `FoodLoverRelaxed`**: Jaccard = **0.4286** (Intersection: 3, Union: 7)
  - **Observed Structure:** **Subset containment**. The 3-stop itinerary of `FoodLoverRelaxed` is a subset of the 7-stop itinerary of `CultureEnthusiast`.
- **`CultureEnthusiast` vs `ScenicPhotography`**: Jaccard = **0.5000** (Intersection: 4, Union: 8)
  - **Observed Structure:** **Partial overlap**. Personas share 4 stops and differ on 4 stops.
- **`CultureEnthusiast` vs `DefaultGeneralist`**: Jaccard = **0.5000** (Intersection: 4, Union: 8)
  - **Observed Structure:** **Partial overlap**. Personas share 4 stops and differ on 4 stops.
- **`FoodLoverRelaxed` vs `ActiveExplorerFast`**: Jaccard = **0.6000** (Intersection: 3, Union: 5)
  - **Observed Structure:** **Subset containment**. The 3-stop itinerary of `FoodLoverRelaxed` is a subset of the 5-stop itinerary of `ActiveExplorerFast`.
- **`FoodLoverRelaxed` vs `ScenicPhotography`**: Jaccard = **0.6000** (Intersection: 3, Union: 5)
  - **Observed Structure:** **Subset containment**. The 3-stop itinerary of `FoodLoverRelaxed` is a subset of the 5-stop itinerary of `ScenicPhotography`.
- **`FoodLoverRelaxed` vs `DefaultGeneralist`**: Jaccard = **0.6000** (Intersection: 3, Union: 5)
  - **Observed Structure:** **Subset containment**. The 3-stop itinerary of `FoodLoverRelaxed` is a subset of the 5-stop itinerary of `DefaultGeneralist`.
- **`ActiveExplorerFast` vs `ScenicPhotography`**: Jaccard = **0.4286** (Intersection: 3, Union: 7)
  - **Observed Structure:** **Partial overlap**. Personas share 3 stops and differ on 4 stops.
- **`ActiveExplorerFast` vs `DefaultGeneralist`**: Jaccard = **0.4286** (Intersection: 3, Union: 7)
  - **Observed Structure:** **Partial overlap**. Personas share 3 stops and differ on 4 stops.
- **`ScenicPhotography` vs `DefaultGeneralist`**: Jaccard = **1.0000** (Intersection: 5, Union: 5)
  - **Observed Structure:** **Identical POI selection (J = 1.00)**. Both personas selected the exact same 5 POIs (`[8,9,3,1,5]`).

## 7. Ablation Study (One-Factor-at-a-Time)

Each ablation modifies exactly one component while keeping all other parameters at baseline:

| Variant | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Full) | Added POIs | Removed POIs | Notes |
|---|---|---|---:|---:|---:|---:|---:|---:|---|---|---|
| Base-only ablation | `CultureEnthusiast` | Yes | 6 | 31 | 1 | 0 | 26841 | 0.63 | 2 | 10,8 | Evaluates itinerary selection driven purely by deterministic heuristic base score |
| Production fallback | `CultureEnthusiast` | Yes | 6 | 28 | 2 | 0 | 25929 | 0.63 | 2 | 6,10 | Evaluates itinerary selection when AI provider is offline and deterministic fallback score is applied |
| No category decay | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 39401 | 1.00 | - | - | Diminishing returns removed; repeats within same diversity group face no discount |
| No soft rho penalties | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 36150 | 1.00 | - | - | Soft pacing/effort/outdoor penalties neutralized; hard limits and travel cost remain active |
| No travel penalty | `CultureEnthusiast` | Yes | 7 | 35 | 2 | 0 | 33647 | 1.00 | - | - | Travel minutes ignored by objective function; solver only constrained by available day minutes |
| No TimeFit | `CultureEnthusiast` | Yes | 6 | 27 | 2 | 1 | 28440 | 0.63 | 2 | 6,10 | Diurnal preference curve removed; arrival time does not scale POI desirability |
| Base-only ablation | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 6453 | 1.00 | - | - | Evaluates itinerary selection driven purely by deterministic heuristic base score |
| Production fallback | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9399 | 1.00 | - | - | Evaluates itinerary selection when AI provider is offline and deterministic fallback score is applied |
| No category decay | `FoodLoverRelaxed` | Yes | 7 | 33 | 2 | 1 | 13141 | 0.43 | 10,3,6,7 | - | Diminishing returns removed; repeats within same diversity group face no discount |
| No soft rho penalties | `FoodLoverRelaxed` | Yes | 6 | 34 | 3 | 1 | 14056 | 0.50 | 7,6,9 | - | Soft pacing/effort/outdoor penalties neutralized; hard limits and travel cost remain active |
| No travel penalty | `FoodLoverRelaxed` | Yes | 3 | 24 | 2 | 0 | 13104 | 0.50 | 10 | 5 | Travel minutes ignored by objective function; solver only constrained by available day minutes |
| No TimeFit | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 1 | 8695 | 0.50 | 10 | 5 | Diurnal preference curve removed; arrival time does not scale POI desirability |
| Base-only ablation | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 21040 | 1.00 | - | - | Evaluates itinerary selection driven purely by deterministic heuristic base score |
| Production fallback | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 21433 | 1.00 | - | - | Evaluates itinerary selection when AI provider is offline and deterministic fallback score is applied |
| No category decay | `ActiveExplorerFast` | Yes | 7 | 63 | 2 | 1 | 33136 | 0.50 | 10,6,7 | 12 | Diminishing returns removed; repeats within same diversity group face no discount |
| No soft rho penalties | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | - | - | Soft pacing/effort/outdoor penalties neutralized; hard limits and travel cost remain active |
| No travel penalty | `ActiveExplorerFast` | Yes | 5 | 82 | 2 | 1 | 29260 | 1.00 | - | - | Travel minutes ignored by objective function; solver only constrained by available day minutes |
| No TimeFit | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 25804 | 1.00 | - | - | Diurnal preference curve removed; arrival time does not scale POI desirability |
| Base-only ablation | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 19253 | 1.00 | - | - | Evaluates itinerary selection driven purely by deterministic heuristic base score |
| Production fallback | `ScenicPhotography` | Yes | 5 | 40 | 3 | 1 | 20376 | 0.67 | 10 | 3 | Evaluates itinerary selection when AI provider is offline and deterministic fallback score is applied |
| No category decay | `ScenicPhotography` | Yes | 7 | 33 | 2 | 1 | 28573 | 0.50 | 10,6,7 | 9 | Diminishing returns removed; repeats within same diversity group face no discount |
| No soft rho penalties | `ScenicPhotography` | Yes | 6 | 34 | 3 | 1 | 28443 | 0.57 | 7,6 | 3 | Soft pacing/effort/outdoor penalties neutralized; hard limits and travel cost remain active |
| No travel penalty | `ScenicPhotography` | Yes | 5 | 68 | 3 | 1 | 28882 | 0.67 | 11 | 3 | Travel minutes ignored by objective function; solver only constrained by available day minutes |
| No TimeFit | `ScenicPhotography` | Yes | 5 | 32 | 3 | 3 | 25015 | 0.67 | 7 | 3 | Diurnal preference curve removed; arrival time does not scale POI desirability |
| Base-only ablation | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 10209 | 1.00 | - | - | Evaluates itinerary selection driven purely by deterministic heuristic base score |
| Production fallback | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | - | - | Evaluates itinerary selection when AI provider is offline and deterministic fallback score is applied |
| No category decay | `DefaultGeneralist` | Yes | 7 | 32 | 2 | 0 | 18505 | 0.50 | 7,6,10 | 9 | Diminishing returns removed; repeats within same diversity group face no discount |
| No soft rho penalties | `DefaultGeneralist` | Yes | 6 | 34 | 3 | 1 | 14202 | 0.57 | 7,6 | 3 | Soft pacing/effort/outdoor penalties neutralized; hard limits and travel cost remain active |
| No travel penalty | `DefaultGeneralist` | Yes | 5 | 64 | 3 | 1 | 17445 | 0.67 | 13 | 3 | Travel minutes ignored by objective function; solver only constrained by available day minutes |
| No TimeFit | `DefaultGeneralist` | Yes | 5 | 29 | 3 | 2 | 13403 | 0.67 | 7 | 3 | Diurnal preference curve removed; arrival time does not scale POI desirability |

## 8. Sensitivity Analysis (One-at-a-Time Parameter Probes)

### 8.LambdaPerMin Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `LambdaPerMin` | Low | 0.006 | `CultureEnthusiast` | Yes | 7 | 33 | 2 | 0 | 31617 | 1.00 | Travel cost sensitivity probe (0.5x base) |
| `LambdaPerMin` | Base | 0.012 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29650 | 1.00 | Travel cost sensitivity probe (1.0x base) |
| `LambdaPerMin` | High | 0.018 | `CultureEnthusiast` | Yes | 6 | 28 | 2 | 0 | 27854 | 0.63 | Travel cost sensitivity probe (1.5x base) |
| `LambdaPerMin` | Low | 0.009 | `FoodLoverRelaxed` | Yes | 3 | 24 | 2 | 0 | 10944 | 0.50 | Travel cost sensitivity probe (0.5x base) |
| `LambdaPerMin` | Base | 0.018 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Travel cost sensitivity probe (1.0x base) |
| `LambdaPerMin` | High | 0.027 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 7608 | 1.00 | Travel cost sensitivity probe (1.5x base) |
| `LambdaPerMin` | Low | 0.004 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 26817 | 1.00 | Travel cost sensitivity probe (0.5x base) |
| `LambdaPerMin` | Base | 0.008 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Travel cost sensitivity probe (1.0x base) |
| `LambdaPerMin` | High | 0.012 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 22817 | 1.00 | Travel cost sensitivity probe (1.5x base) |
| `LambdaPerMin` | Low | 0.006 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 25404 | 1.00 | Travel cost sensitivity probe (0.5x base) |
| `LambdaPerMin` | Base | 0.012 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 23124 | 1.00 | Travel cost sensitivity probe (1.0x base) |
| `LambdaPerMin` | High | 0.018 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 20844 | 1.00 | Travel cost sensitivity probe (1.5x base) |
| `LambdaPerMin` | Low | 0.006 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 14979 | 1.00 | Travel cost sensitivity probe (0.5x base) |
| `LambdaPerMin` | Base | 0.012 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Travel cost sensitivity probe (1.0x base) |
| `LambdaPerMin` | High | 0.018 | `DefaultGeneralist` | Yes | 5 | 33 | 3 | 0 | 10856 | 0.67 | Travel cost sensitivity probe (1.5x base) |

### 8.GammaDefault Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `GammaDefault` | Low | 0.500 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29650 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Base | 0.700 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29650 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | High | 0.900 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29650 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Low | 0.500 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 8522 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Base | 0.700 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | High | 0.900 | `FoodLoverRelaxed` | Yes | 4 | 25 | 2 | 0 | 10165 | 0.75 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Low | 0.500 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24046 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Base | 0.700 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | High | 0.900 | `ActiveExplorerFast` | Yes | 7 | 63 | 2 | 1 | 28440 | 0.50 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Low | 0.500 | `ScenicPhotography` | Yes | 5 | 40 | 3 | 1 | 22784 | 0.67 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Base | 0.700 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 23124 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | High | 0.900 | `ScenicPhotography` | Yes | 5 | 35 | 3 | 0 | 24905 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Low | 0.500 | `DefaultGeneralist` | Yes | 5 | 33 | 3 | 0 | 11129 | 0.67 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Base | 0.700 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | High | 0.900 | `DefaultGeneralist` | Yes | 5 | 27 | 2 | 0 | 15332 | 0.43 | Diminishing marginal utility discount for non-loved categories |

### 8.GammaLoved Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `GammaLoved` | Low | 0.750 | `CultureEnthusiast` | Yes | 5 | 27 | 2 | 0 | 22744 | 0.50 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Base | 0.900 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29650 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | High | 1.000 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 39401 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Low | 0.750 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Base | 0.900 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | High | 1.000 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Low | 0.750 | `ActiveExplorerFast` | Yes | 5 | 55 | 3 | 1 | 21986 | 0.67 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Base | 0.900 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | High | 1.000 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 26885 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Low | 0.750 | `ScenicPhotography` | Yes | 5 | 35 | 3 | 0 | 22655 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Base | 0.900 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 23124 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | High | 1.000 | `ScenicPhotography` | Yes | 7 | 35 | 2 | 1 | 24923 | 0.50 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Low | 0.750 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Base | 0.900 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | High | 1.000 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Diminishing marginal utility discount for loved categories |

### 8.RhoStops Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `RhoStops` | Low | 0.150 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 32650 | 1.00 | Soft penalty per stop exceeding pace max (0.5x base) |
| `RhoStops` | Base | 0.300 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29650 | 1.00 | Soft penalty per stop exceeding pace max (1.0x base) |
| `RhoStops` | High | 0.450 | `CultureEnthusiast` | Yes | 5 | 27 | 1 | 0 | 28752 | 0.50 | Soft penalty per stop exceeding pace max (1.5x base) |
| `RhoStops` | Low | 0.150 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Soft penalty per stop exceeding pace max (0.5x base) |
| `RhoStops` | Base | 0.300 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Soft penalty per stop exceeding pace max (1.0x base) |
| `RhoStops` | High | 0.450 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Soft penalty per stop exceeding pace max (1.5x base) |
| `RhoStops` | Low | 0.150 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Soft penalty per stop exceeding pace max (0.5x base) |
| `RhoStops` | Base | 0.300 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Soft penalty per stop exceeding pace max (1.0x base) |
| `RhoStops` | High | 0.450 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Soft penalty per stop exceeding pace max (1.5x base) |
| `RhoStops` | Low | 0.150 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 23124 | 1.00 | Soft penalty per stop exceeding pace max (0.5x base) |
| `RhoStops` | Base | 0.300 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 23124 | 1.00 | Soft penalty per stop exceeding pace max (1.0x base) |
| `RhoStops` | High | 0.450 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 23124 | 1.00 | Soft penalty per stop exceeding pace max (1.5x base) |
| `RhoStops` | Low | 0.150 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Soft penalty per stop exceeding pace max (0.5x base) |
| `RhoStops` | Base | 0.300 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Soft penalty per stop exceeding pace max (1.0x base) |
| `RhoStops` | High | 0.450 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Soft penalty per stop exceeding pace max (1.5x base) |

### 8.RhoOutdoor Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `RhoOutdoor` | Low | 0.125 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29650 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (0.5x base) |
| `RhoOutdoor` | Base | 0.250 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29650 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.0x base) |
| `RhoOutdoor` | High | 0.375 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29650 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.5x base) |
| `RhoOutdoor` | Low | 0.125 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (0.5x base) |
| `RhoOutdoor` | Base | 0.250 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.0x base) |
| `RhoOutdoor` | High | 0.375 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.5x base) |
| `RhoOutdoor` | Low | 0.125 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (0.5x base) |
| `RhoOutdoor` | Base | 0.250 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.0x base) |
| `RhoOutdoor` | High | 0.375 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.5x base) |
| `RhoOutdoor` | Low | 0.125 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 23124 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (0.5x base) |
| `RhoOutdoor` | Base | 0.250 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 23124 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.0x base) |
| `RhoOutdoor` | High | 0.375 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 23124 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.5x base) |
| `RhoOutdoor` | Low | 0.125 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (0.5x base) |
| `RhoOutdoor` | Base | 0.250 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.0x base) |
| `RhoOutdoor` | High | 0.375 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.5x base) |

### 8.RhoRest Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `RhoRest` | Low | 0.000 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29650 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | Base | 0.000 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29650 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | High | 0.100 | `CultureEnthusiast` | Yes | 5 | 27 | 1 | 0 | 26403 | 0.50 | Exploratory probe for non-zero rest penalty |
| `RhoRest` | Low | 0.000 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | Base | 0.000 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | High | 0.100 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 8228 | 1.00 | Exploratory probe for non-zero rest penalty |
| `RhoRest` | Low | 0.000 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | Base | 0.000 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | High | 0.100 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 21817 | 1.00 | Exploratory probe for non-zero rest penalty |
| `RhoRest` | Low | 0.000 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 23124 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | Base | 0.000 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 23124 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | High | 0.100 | `ScenicPhotography` | Yes | 5 | 31 | 3 | 2 | 19361 | 0.67 | Exploratory probe for non-zero rest penalty |
| `RhoRest` | Low | 0.000 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | Base | 0.000 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | High | 0.100 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 10556 | 0.67 | Exploratory probe for non-zero rest penalty |

### 8.RhoEffortPerHour Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `RhoEffortPerHour` | Low | 0.025 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29899 | 1.00 | Soft penalty per hour of effort exceeding daily budget (0.5x base) |
| `RhoEffortPerHour` | Base | 0.050 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29650 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.0x base) |
| `RhoEffortPerHour` | High | 0.075 | `CultureEnthusiast` | Yes | 7 | 32 | 2 | 0 | 29399 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.5x base) |
| `RhoEffortPerHour` | Low | 0.025 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9333 | 1.00 | Soft penalty per hour of effort exceeding daily budget (0.5x base) |
| `RhoEffortPerHour` | Base | 0.050 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9228 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.0x base) |
| `RhoEffortPerHour` | High | 0.075 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 9124 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.5x base) |
| `RhoEffortPerHour` | Low | 0.025 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Soft penalty per hour of effort exceeding daily budget (0.5x base) |
| `RhoEffortPerHour` | Base | 0.050 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.0x base) |
| `RhoEffortPerHour` | High | 0.075 | `ActiveExplorerFast` | Yes | 5 | 50 | 2 | 2 | 24817 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.5x base) |
| `RhoEffortPerHour` | Low | 0.025 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 23749 | 1.00 | Soft penalty per hour of effort exceeding daily budget (0.5x base) |
| `RhoEffortPerHour` | Base | 0.050 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 23124 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.0x base) |
| `RhoEffortPerHour` | High | 0.075 | `ScenicPhotography` | Yes | 5 | 38 | 3 | 1 | 22499 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.5x base) |
| `RhoEffortPerHour` | Low | 0.025 | `DefaultGeneralist` | Yes | 5 | 33 | 3 | 0 | 13149 | 0.67 | Soft penalty per hour of effort exceeding daily budget (0.5x base) |
| `RhoEffortPerHour` | Base | 0.050 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12879 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.0x base) |
| `RhoEffortPerHour` | High | 0.075 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 0 | 12629 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.5x base) |

## 9. Key Findings, Invariants & Limitations

### 9.1 Observed Findings
1. **Persona Divergence Range**: Minimum Jaccard similarity is **0.3333** between `CultureEnthusiast` and `ActiveExplorerFast` (Gate: PASS); maximum similarity is **1.0000** between `ScenicPhotography` and `DefaultGeneralist`.
2. **Category Decay Effect (Gamma)**: No category decay changed POI selection for 4/5 personas.

### 9.2 Limitations & Honest Declarations
- **Behavior-signal ablation**: Behavior-signal ablation was not measured because the benchmark corpus contains no seeded historical interaction events.
- **No synthetic tuning**: Production parameters (`BaseWeight`, `AiWeight`, `Gamma`, `Rho`, `Lambda`) were NOT artificially tuned or modified to force Jaccard below 0.40.
- **Cold-start provenance**: `DefaultGeneralist` uses the production cold-start fallback score 0.50; it is not a raw BGE-M3 response.
- **Corpus size limitation**: Scenario `danang-real-1day` contains 19 POIs with mandatory POI constraints bounding minimum intersection.