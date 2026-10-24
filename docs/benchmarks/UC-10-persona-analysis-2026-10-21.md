# UC-10 Personalization Verification: Persona Jaccard, Ablation Study & Sensitivity Analysis

> **Status**: **PASS — PERSONALIZED PAIRS MEET JACCARD GATE (< 0.40) AND COLD-START QUALITY GATE**

## 1. Executive Summary & Metadata

- **Run Date**: 2026-10-21
- **Configuration**: Release
- **OS / Runtime**: Microsoft Windows 10.0.26200 / .NET 10.0.0
- **Machine**: HAN (12 logical cores)
- **Git Commit**: `f2585ab80ac9ef3c08a49275edd3d0d7d5b40099` (dirty)
- **Evaluation Mode**: Recorded BGE-M3 Replay (deterministic)
- **Configured Embedding Model**: `bge-m3:latest`
- **Runtime Model Identity**: not exposed by provider contract
- **Fixture Provenance SHA-256**: `D50E09CC6900D908D93B62068BDE138EDB35DE12FB5B4241D3EB8110986BC4AC`
- **Service Endpoint**: `Recorded fixture (local deterministic)`
- **Target Scenario**: `danang-real-1day` (40 POIs)
- **Objective Mode**: `WeightedUtility` (Csp)
- **Personalization Weights**: BaseWeight = 0.55, AiWeight = 0.45
- **Personalization Gate Threshold**: Jaccard < 0.40
- **Baseline Provider Outcomes**: 4 Success, 1 ProviderSkipped, 0 Fallback
- **Personas Evaluated**: 5
- **Total Persona Pairs**: 10
- **Personalized Pairs Meeting Gate (< 0.40)**: 6 / 6
- **Personalized Pairs Exceeding Gate (>= 0.40)**: 0 / 6
- **Cold-Start Comparison Pairs (observational)**: 4
- **Cold-Start Quality Gate**: PASS (feasible, non-empty, at least 2 diversity groups)
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
   -> BaseScore = 0.450*CatAffinity + 0.350*TagAffinity + 0.100*BehaviorAffinity + 0.050*Scenic + 0.050*Photo

6. AI Blending / Fallback
   -> EffectiveDesirabilityScore = 0.55 * BaseScore + 0.45 * AiScore
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
| `CultureEnthusiast` | Feasible | 6 | 30 | 1 | 0 | 29405 | 7,5,1,2,3,6 |
| `FoodLoverRelaxed` | Feasible | 3 | 22 | 2 | 0 | 6794 | 10,8,1 |
| `ActiveExplorerFast` | Feasible | 5 | 51 | 2 | 1 | 23989 | 13,12,14,1,8 |
| `ScenicPhotography` | Feasible | 5 | 33 | 3 | 3 | 22191 | 8,9,1,39,5 |
| `DefaultGeneralist` | Feasible | 5 | 32 | 3 | 1 | 8250 | 5,7,8,1,9 |

## 5. Persona Jaccard Similarity Matrix (5x5)

authoritative definition: `J(A, B) = |VisitedPoiIds(A) ∩ VisitedPoiIds(B)| / |VisitedPoiIds(A) ∪ VisitedPoiIds(B)|`

| Persona | CultureEnthusiast | FoodLoverRelaxed | ActiveExplorerFast | ScenicPhotography | DefaultGeneralist |
|---|:---:|:---:|:---:|:---:|:---:|
| `CultureEnthusiast` | 1.00 | 0.13 | 0.10 | 0.22 | 0.38 |
| `FoodLoverRelaxed` | 0.13 | 1.00 | 0.33 | 0.33 | 0.33 |
| `ActiveExplorerFast` | 0.10 | 0.33 | 1.00 | 0.25 | 0.25 |
| `ScenicPhotography` | 0.22 | 0.33 | 0.25 | 1.00 | 0.67 |
| `DefaultGeneralist` | 0.38 | 0.33 | 0.25 | 0.67 | 1.00 |

## 6. Pairwise Jaccard Breakdown & Threshold Analysis

Threshold: `Jaccard < 0.40` is considered strong personalization differentiation.

| Persona A | Persona B | Stops A | Stops B | Intersection | Union | Jaccard | Personalized Gate (< 0.40) | Route A vs Route B |
|---|---|---:|---:|---:|---:|---:|---|---|
| `CultureEnthusiast` | `FoodLoverRelaxed` | 6 | 3 | 1 | 8 | **0.1250** | PASS | `[7,5,1,2,3,6]` vs `[10,8,1]` |
| `CultureEnthusiast` | `ActiveExplorerFast` | 6 | 5 | 1 | 10 | **0.1000** | PASS | `[7,5,1,2,3,6]` vs `[13,12,14,1,8]` |
| `CultureEnthusiast` | `ScenicPhotography` | 6 | 5 | 2 | 9 | **0.2222** | PASS | `[7,5,1,2,3,6]` vs `[8,9,1,39,5]` |
| `CultureEnthusiast` | `DefaultGeneralist` | 6 | 5 | 3 | 8 | **0.3750** | OBSERVED (not gated) | `[7,5,1,2,3,6]` vs `[5,7,8,1,9]` |
| `FoodLoverRelaxed` | `ActiveExplorerFast` | 3 | 5 | 2 | 6 | **0.3333** | PASS | `[10,8,1]` vs `[13,12,14,1,8]` |
| `FoodLoverRelaxed` | `ScenicPhotography` | 3 | 5 | 2 | 6 | **0.3333** | PASS | `[10,8,1]` vs `[8,9,1,39,5]` |
| `FoodLoverRelaxed` | `DefaultGeneralist` | 3 | 5 | 2 | 6 | **0.3333** | OBSERVED (not gated) | `[10,8,1]` vs `[5,7,8,1,9]` |
| `ActiveExplorerFast` | `ScenicPhotography` | 5 | 5 | 2 | 8 | **0.2500** | PASS | `[13,12,14,1,8]` vs `[8,9,1,39,5]` |
| `ActiveExplorerFast` | `DefaultGeneralist` | 5 | 5 | 2 | 8 | **0.2500** | OBSERVED (not gated) | `[13,12,14,1,8]` vs `[5,7,8,1,9]` |
| `ScenicPhotography` | `DefaultGeneralist` | 5 | 5 | 4 | 6 | **0.6667** | OBSERVED (not gated) | `[8,9,1,39,5]` vs `[5,7,8,1,9]` |

### 6.1 Pairs Meeting Personalization Gate (J < 0.40)

- **`CultureEnthusiast` vs `FoodLoverRelaxed`**: Jaccard = **0.1250** (Intersection: 1, Union: 8). High divergence in POI selections.
- **`CultureEnthusiast` vs `ActiveExplorerFast`**: Jaccard = **0.1000** (Intersection: 1, Union: 10). High divergence in POI selections.
- **`CultureEnthusiast` vs `ScenicPhotography`**: Jaccard = **0.2222** (Intersection: 2, Union: 9). High divergence in POI selections.
- **`FoodLoverRelaxed` vs `ActiveExplorerFast`**: Jaccard = **0.3333** (Intersection: 2, Union: 6). High divergence in POI selections.
- **`FoodLoverRelaxed` vs `ScenicPhotography`**: Jaccard = **0.3333** (Intersection: 2, Union: 6). High divergence in POI selections.
- **`ActiveExplorerFast` vs `ScenicPhotography`**: Jaccard = **0.2500** (Intersection: 2, Union: 8). High divergence in POI selections.

### 6.2 Pairs Exceeding Threshold (J >= 0.40) & Structural Analysis

None. All personalized persona pairs met the gate (< 0.40).

### 6.3 Cold-Start Comparisons (Observational Only)

Cold start has no preference tokens, so its pairwise Jaccard values are reported but do not determine the personalization gate.

- **`CultureEnthusiast` vs `DefaultGeneralist`**: Jaccard = **0.3750**.
- **`FoodLoverRelaxed` vs `DefaultGeneralist`**: Jaccard = **0.3333**.
- **`ActiveExplorerFast` vs `DefaultGeneralist`**: Jaccard = **0.2500**.
- **`ScenicPhotography` vs `DefaultGeneralist`**: Jaccard = **0.6667**.

## 7. Ablation Study (One-Factor-at-a-Time)

Each ablation modifies exactly one component while keeping all other parameters at baseline:

| Variant | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Full) | Added POIs | Removed POIs | Notes |
|---|---|---|---:|---:|---:|---:|---:|---:|---|---|---|
| Base-only ablation | `CultureEnthusiast` | Yes | 5 | 31 | 1 | 0 | 27250 | 0.57 | 4 | 5,6 | Evaluates itinerary selection driven purely by deterministic heuristic base score |
| Production fallback | `CultureEnthusiast` | Yes | 5 | 31 | 1 | 0 | 24089 | 0.57 | 4 | 5,6 | Evaluates itinerary selection when AI provider is offline and deterministic fallback score is applied |
| No category decay | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 39408 | 1.00 | - | - | Diminishing returns removed; repeats within same diversity group face no discount |
| No soft rho penalties | `CultureEnthusiast` | Yes | 7 | 35 | 2 | 2 | 32925 | 0.63 | 8,10 | 2 | Soft pacing/effort/outdoor penalties neutralized; hard limits and travel cost remain active |
| No travel penalty | `CultureEnthusiast` | Yes | 5 | 68 | 1 | 0 | 31884 | 0.57 | 22 | 3,6 | Travel minutes ignored by objective function; solver only constrained by available day minutes |
| No TimeFit | `CultureEnthusiast` | Yes | 6 | 29 | 1 | 1 | 28542 | 1.00 | - | - | Diurnal preference curve removed; arrival time does not scale POI desirability |
| Base-only ablation | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 1378 | 1.00 | - | - | Evaluates itinerary selection driven purely by deterministic heuristic base score |
| Production fallback | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 6499 | 0.50 | 5 | 10 | Evaluates itinerary selection when AI provider is offline and deterministic fallback score is applied |
| No category decay | `FoodLoverRelaxed` | Yes | 4 | 27 | 1 | 0 | 8447 | 0.40 | 7,5 | 8 | Diminishing returns removed; repeats within same diversity group face no discount |
| No soft rho penalties | `FoodLoverRelaxed` | Yes | 6 | 30 | 2 | 2 | 9401 | 0.50 | 7,5,39 | - | Soft pacing/effort/outdoor penalties neutralized; hard limits and travel cost remain active |
| No travel penalty | `FoodLoverRelaxed` | Yes | 3 | 24 | 2 | 0 | 11096 | 1.00 | - | - | Travel minutes ignored by objective function; solver only constrained by available day minutes |
| No TimeFit | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 1 | 6786 | 1.00 | - | - | Diurnal preference curve removed; arrival time does not scale POI desirability |
| Base-only ablation | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 19223 | 1.00 | - | - | Evaluates itinerary selection driven purely by deterministic heuristic base score |
| Production fallback | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 0 | 19974 | 1.00 | - | - | Evaluates itinerary selection when AI provider is offline and deterministic fallback score is applied |
| No category decay | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 26718 | 0.67 | 5 | 14 | Diminishing returns removed; repeats within same diversity group face no discount |
| No soft rho penalties | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | - | - | Soft pacing/effort/outdoor penalties neutralized; hard limits and travel cost remain active |
| No travel penalty | `ActiveExplorerFast` | Yes | 6 | 104 | 2 | 0 | 29618 | 0.38 | 38,5,7 | 12,14 | Travel minutes ignored by objective function; solver only constrained by available day minutes |
| No TimeFit | `ActiveExplorerFast` | Yes | 5 | 52 | 2 | 1 | 24522 | 0.67 | 5 | 14 | Diurnal preference curve removed; arrival time does not scale POI desirability |
| Base-only ablation | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 16254 | 1.00 | - | - | Evaluates itinerary selection driven purely by deterministic heuristic base score |
| Production fallback | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 18700 | 1.00 | - | - | Evaluates itinerary selection when AI provider is offline and deterministic fallback score is applied |
| No category decay | `ScenicPhotography` | Yes | 6 | 37 | 3 | 1 | 23254 | 0.83 | 7 | - | Diminishing returns removed; repeats within same diversity group face no discount |
| No soft rho penalties | `ScenicPhotography` | Yes | 6 | 34 | 3 | 2 | 27321 | 0.83 | 7 | - | Soft pacing/effort/outdoor penalties neutralized; hard limits and travel cost remain active |
| No travel penalty | `ScenicPhotography` | Yes | 5 | 57 | 3 | 1 | 24455 | 0.67 | 11 | 39 | Travel minutes ignored by objective function; solver only constrained by available day minutes |
| No TimeFit | `ScenicPhotography` | Yes | 5 | 31 | 3 | 2 | 23687 | 1.00 | - | - | Diurnal preference curve removed; arrival time does not scale POI desirability |
| Base-only ablation | `DefaultGeneralist` | Yes | 4 | 20 | 2 | 0 | 2222 | 0.80 | - | 9 | Evaluates itinerary selection driven purely by deterministic heuristic base score |
| Production fallback | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | - | - | Evaluates itinerary selection when AI provider is offline and deterministic fallback score is applied |
| No category decay | `DefaultGeneralist` | Yes | 5 | 27 | 2 | 0 | 12271 | 0.67 | 2 | 9 | Diminishing returns removed; repeats within same diversity group face no discount |
| No soft rho penalties | `DefaultGeneralist` | Yes | 6 | 33 | 3 | 2 | 9284 | 0.83 | 39 | - | Soft pacing/effort/outdoor penalties neutralized; hard limits and travel cost remain active |
| No travel penalty | `DefaultGeneralist` | Yes | 5 | 64 | 3 | 1 | 12915 | 0.67 | 13 | 7 | Travel minutes ignored by objective function; solver only constrained by available day minutes |
| No TimeFit | `DefaultGeneralist` | Yes | 5 | 30 | 3 | 3 | 8851 | 1.00 | - | - | Diurnal preference curve removed; arrival time does not scale POI desirability |

## 8. Sensitivity Analysis (One-at-a-Time Parameter Probes)

### 8.LambdaPerMin Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `LambdaPerMin` | Low | 0.006 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 31205 | 1.00 | Travel cost sensitivity probe (0.5x base) |
| `LambdaPerMin` | Base | 0.012 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29405 | 1.00 | Travel cost sensitivity probe (1.0x base) |
| `LambdaPerMin` | High | 0.018 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 27605 | 1.00 | Travel cost sensitivity probe (1.5x base) |
| `LambdaPerMin` | Low | 0.009 | `FoodLoverRelaxed` | Yes | 3 | 24 | 2 | 0 | 8936 | 1.00 | Travel cost sensitivity probe (0.5x base) |
| `LambdaPerMin` | Base | 0.018 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Travel cost sensitivity probe (1.0x base) |
| `LambdaPerMin` | High | 0.027 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 4814 | 1.00 | Travel cost sensitivity probe (1.5x base) |
| `LambdaPerMin` | Low | 0.004 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23241 | 1.00 | Travel cost sensitivity probe (0.5x base) |
| `LambdaPerMin` | Base | 0.008 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Travel cost sensitivity probe (1.0x base) |
| `LambdaPerMin` | High | 0.012 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 21249 | 0.67 | Travel cost sensitivity probe (1.5x base) |
| `LambdaPerMin` | Low | 0.006 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 24171 | 1.00 | Travel cost sensitivity probe (0.5x base) |
| `LambdaPerMin` | Base | 0.012 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22191 | 1.00 | Travel cost sensitivity probe (1.0x base) |
| `LambdaPerMin` | High | 0.018 | `ScenicPhotography` | Yes | 5 | 31 | 3 | 2 | 19509 | 1.00 | Travel cost sensitivity probe (1.5x base) |
| `LambdaPerMin` | Low | 0.006 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 2 | 10153 | 1.00 | Travel cost sensitivity probe (0.5x base) |
| `LambdaPerMin` | Base | 0.012 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Travel cost sensitivity probe (1.0x base) |
| `LambdaPerMin` | High | 0.018 | `DefaultGeneralist` | Yes | 5 | 22 | 2 | 1 | 6748 | 0.67 | Travel cost sensitivity probe (1.5x base) |

### 8.GammaDefault Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `GammaDefault` | Low | 0.500 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29405 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Base | 0.700 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29405 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | High | 0.900 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29405 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Low | 0.500 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6293 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Base | 0.700 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | High | 0.900 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 7294 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Low | 0.500 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23414 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Base | 0.700 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | High | 0.900 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 24045 | 0.67 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Low | 0.500 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22191 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Base | 0.700 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22191 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | High | 0.900 | `ScenicPhotography` | Yes | 5 | 31 | 3 | 2 | 21943 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Low | 0.500 | `DefaultGeneralist` | Yes | 5 | 35 | 3 | 2 | 6900 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | Base | 0.700 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Diminishing marginal utility discount for non-loved categories |
| `GammaDefault` | High | 0.900 | `DefaultGeneralist` | Yes | 5 | 27 | 2 | 0 | 10590 | 0.67 | Diminishing marginal utility discount for non-loved categories |

### 8.GammaLoved Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `GammaLoved` | Low | 0.750 | `CultureEnthusiast` | Yes | 5 | 27 | 2 | 0 | 21574 | 0.57 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Base | 0.900 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29405 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | High | 1.000 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 39408 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Low | 0.750 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Base | 0.900 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | High | 1.000 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Low | 0.750 | `ActiveExplorerFast` | Yes | 5 | 58 | 3 | 1 | 19541 | 0.43 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Base | 0.900 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | High | 1.000 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 26231 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Low | 0.750 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 20194 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Base | 0.900 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22191 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | High | 1.000 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 23660 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Low | 0.750 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | Base | 0.900 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Diminishing marginal utility discount for loved categories |
| `GammaLoved` | High | 1.000 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Diminishing marginal utility discount for loved categories |

### 8.RhoStops Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `RhoStops` | Low | 0.150 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 30905 | 1.00 | Soft penalty per stop exceeding pace max (0.5x base) |
| `RhoStops` | Base | 0.300 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29405 | 1.00 | Soft penalty per stop exceeding pace max (1.0x base) |
| `RhoStops` | High | 0.450 | `CultureEnthusiast` | Yes | 5 | 28 | 1 | 0 | 28863 | 0.83 | Soft penalty per stop exceeding pace max (1.5x base) |
| `RhoStops` | Low | 0.150 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Soft penalty per stop exceeding pace max (0.5x base) |
| `RhoStops` | Base | 0.300 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Soft penalty per stop exceeding pace max (1.0x base) |
| `RhoStops` | High | 0.450 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Soft penalty per stop exceeding pace max (1.5x base) |
| `RhoStops` | Low | 0.150 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Soft penalty per stop exceeding pace max (0.5x base) |
| `RhoStops` | Base | 0.300 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Soft penalty per stop exceeding pace max (1.0x base) |
| `RhoStops` | High | 0.450 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Soft penalty per stop exceeding pace max (1.5x base) |
| `RhoStops` | Low | 0.150 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22191 | 1.00 | Soft penalty per stop exceeding pace max (0.5x base) |
| `RhoStops` | Base | 0.300 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22191 | 1.00 | Soft penalty per stop exceeding pace max (1.0x base) |
| `RhoStops` | High | 0.450 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22191 | 1.00 | Soft penalty per stop exceeding pace max (1.5x base) |
| `RhoStops` | Low | 0.150 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Soft penalty per stop exceeding pace max (0.5x base) |
| `RhoStops` | Base | 0.300 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Soft penalty per stop exceeding pace max (1.0x base) |
| `RhoStops` | High | 0.450 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Soft penalty per stop exceeding pace max (1.5x base) |

### 8.RhoOutdoor Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `RhoOutdoor` | Low | 0.125 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29405 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (0.5x base) |
| `RhoOutdoor` | Base | 0.250 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29405 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.0x base) |
| `RhoOutdoor` | High | 0.375 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29405 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.5x base) |
| `RhoOutdoor` | Low | 0.125 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (0.5x base) |
| `RhoOutdoor` | Base | 0.250 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.0x base) |
| `RhoOutdoor` | High | 0.375 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.5x base) |
| `RhoOutdoor` | Low | 0.125 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (0.5x base) |
| `RhoOutdoor` | Base | 0.250 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.0x base) |
| `RhoOutdoor` | High | 0.375 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.5x base) |
| `RhoOutdoor` | Low | 0.125 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22191 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (0.5x base) |
| `RhoOutdoor` | Base | 0.250 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22191 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.0x base) |
| `RhoOutdoor` | High | 0.375 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22191 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.5x base) |
| `RhoOutdoor` | Low | 0.125 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (0.5x base) |
| `RhoOutdoor` | Base | 0.250 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.0x base) |
| `RhoOutdoor` | High | 0.375 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Soft penalty for consecutive unsheltered outdoor visits (1.5x base) |

### 8.RhoRest Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `RhoRest` | Low | 0.000 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29405 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | Base | 0.000 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29405 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | High | 0.100 | `CultureEnthusiast` | Yes | 5 | 28 | 1 | 0 | 25859 | 0.57 | Exploratory probe for non-zero rest penalty |
| `RhoRest` | Low | 0.000 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | Base | 0.000 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | High | 0.100 | `FoodLoverRelaxed` | Yes | 3 | 18 | 2 | 0 | 5315 | 0.50 | Exploratory probe for non-zero rest penalty |
| `RhoRest` | Low | 0.000 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | Base | 0.000 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | High | 0.100 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 20249 | 0.67 | Exploratory probe for non-zero rest penalty |
| `RhoRest` | Low | 0.000 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22191 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | Base | 0.000 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22191 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | High | 0.100 | `ScenicPhotography` | Yes | 5 | 31 | 3 | 2 | 18369 | 1.00 | Exploratory probe for non-zero rest penalty |
| `RhoRest` | Low | 0.000 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | Base | 0.000 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Inactive at baseline (baseline = 0.0) |
| `RhoRest` | High | 0.100 | `DefaultGeneralist` | Yes | 4 | 20 | 2 | 0 | 6701 | 0.80 | Exploratory probe for non-zero rest penalty |

### 8.RhoEffortPerHour Probes

| Parameter | Level | Value | Persona | Feasible | Stops | Travel (min) | Groups | Midday | Objective | Jaccard (vs Base) | Notes |
|---|---|---:|---|---|---:|---:|---:|---:|---:|---:|---|
| `RhoEffortPerHour` | Low | 0.025 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29592 | 1.00 | Soft penalty per hour of effort exceeding daily budget (0.5x base) |
| `RhoEffortPerHour` | Base | 0.050 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29405 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.0x base) |
| `RhoEffortPerHour` | High | 0.075 | `CultureEnthusiast` | Yes | 6 | 30 | 1 | 0 | 29217 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.5x base) |
| `RhoEffortPerHour` | Low | 0.025 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 7023 | 1.00 | Soft penalty per hour of effort exceeding daily budget (0.5x base) |
| `RhoEffortPerHour` | Base | 0.050 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6794 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.0x base) |
| `RhoEffortPerHour` | High | 0.075 | `FoodLoverRelaxed` | Yes | 3 | 22 | 2 | 0 | 6565 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.5x base) |
| `RhoEffortPerHour` | Low | 0.025 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Soft penalty per hour of effort exceeding daily budget (0.5x base) |
| `RhoEffortPerHour` | Base | 0.050 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.0x base) |
| `RhoEffortPerHour` | High | 0.075 | `ActiveExplorerFast` | Yes | 5 | 51 | 2 | 1 | 23989 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.5x base) |
| `RhoEffortPerHour` | Low | 0.025 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22691 | 1.00 | Soft penalty per hour of effort exceeding daily budget (0.5x base) |
| `RhoEffortPerHour` | Base | 0.050 | `ScenicPhotography` | Yes | 5 | 33 | 3 | 3 | 22191 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.0x base) |
| `RhoEffortPerHour` | High | 0.075 | `ScenicPhotography` | Yes | 5 | 31 | 3 | 2 | 20869 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.5x base) |
| `RhoEffortPerHour` | Low | 0.025 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8312 | 1.00 | Soft penalty per hour of effort exceeding daily budget (0.5x base) |
| `RhoEffortPerHour` | Base | 0.050 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8250 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.0x base) |
| `RhoEffortPerHour` | High | 0.075 | `DefaultGeneralist` | Yes | 5 | 32 | 3 | 1 | 8187 | 1.00 | Soft penalty per hour of effort exceeding daily budget (1.5x base) |

## 9. Key Findings, Invariants & Limitations

### 9.1 Observed Findings
1. **Persona Divergence Range**: Minimum Jaccard similarity is **0.1000** between `CultureEnthusiast` and `ActiveExplorerFast` (Gate: PASS); maximum similarity is **0.6667** between `ScenicPhotography` and `DefaultGeneralist`.
2. **Category Decay Effect (Gamma)**: No category decay changed POI selection for 4/5 personas.

### 9.2 Limitations & Honest Declarations
- **Behavior-signal ablation**: Behavior-signal ablation was not measured because the benchmark corpus contains no seeded historical interaction events.
- **Weight calibration**: Base-score component weights favor explicit category/tag preferences while retaining behavior, scenic, and photo signals. No POI- or persona-specific boosts are used to force Jaccard below 0.40.
- **Cold-start provenance**: `DefaultGeneralist` uses the production cold-start fallback score 0.50; it is not a raw BGE-M3 response.
- **Corpus size limitation**: Scenario `danang-real-1day` contains 40 POIs with mandatory POI constraints bounding minimum intersection.