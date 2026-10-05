# Specification: [UC-10] Pre-Generation Personalized POI Recommendations

| Item | Details |
| :--- | :--- |
| **Ticket ID** | UNASSIGNED |
| **Scope** | UC-10 — Pre-Generation Personalized POI Recommendations |
| **Status** | FROZEN SPECIFICATION — BACKEND IMPLEMENTED AND AUDITED |
| **Target Route** | `POST /api/v1/poi-recommendations` |
| **Authorization** | Traveler Role (`UserRole.Traveler`) |

---

## 1. Executive Summary & Product Flow

### 1.1 Scope & Purpose
UC-10 pre-generation personalized POI recommendations provide an informational, personalized discovery surface prior to itinerary generation. Travelers enter exploration coordinates and a search radius, receiving a personalized, ordered list of candidate points of interest (POIs). The traveler can inspect these recommendations and optionally select 0 to 6 POIs to become `mandatoryPoiIds` in the subsequent `POST /api/v1/scheduling-requests` endpoint.

```text
Traveler planning inputs (Exploration Center + Radius)
  ↓
POST /api/v1/poi-recommendations (Informational & Deterministic)
  ↓
Traveler inspects recommendations & selects 0..6 POIs
  ↓
Selected POIs map to mandatoryPoiIds
  ↓
POST /api/v1/scheduling-requests (Generation Pipeline: TM-215 AI Ranking → deterministic constraint scheduler → TM-217 Explanation)
```

### 1.2 Invariants & System Boundaries
- **No Side Effects:** This endpoint is strictly read-only. It MUST NOT create a `SchedulingRequest`, `Itinerary`, `RecommendationBehaviorEvent`, or any database modification.
- **No AI / LLM Calls:** Pre-generation recommendation MUST NOT invoke `IPoiRankingProvider`, Gemini, or external AI services. Scoring is 100% deterministic.
- **No Constraint Solver:** Pre-generation recommendation does NOT run constraint-based itinerary scheduling, matrix route solver, or schedule validation. Time/budget constraints belong strictly to generation.
- **No Persistence of Mandatory State:** The recommendation endpoint returns candidate POIs. Storing chosen mandatory POIs occurs downstream when calling `POST /api/v1/scheduling-requests`.

---

## 2. API Contract & Routing

### 2.1 Route & Method
- **Method:** `POST`
- **Path:** `/api/v1/poi-recommendations`
- **Headers:** `Content-Type: application/json`, `Authorization: Bearer <JWT>`
- **Role Requirement:** `UserRole.Traveler`

### 2.2 Request Contract (`GetPoiRecommendationsRequest`)

```json
{
  "explorationLatitude": 16.0471,
  "explorationLongitude": 108.2068,
  "searchRadiusKm": 10,
  "limit": 10
}
```

| Field Name | Type | Required / Optional | Constraints / Validation |
| :--- | :--- | :--- | :--- |
| `explorationLatitude` | `decimal` | Required | InclusiveBetween `-90.0` and `90.0` |
| `explorationLongitude` | `decimal` | Required | InclusiveBetween `-180.0` and `180.0` |
| `searchRadiusKm` | `int` | Required | InclusiveBetween `1` and `50` (Canonical UC-10 rule) |
| `limit` | `int?` | Optional | Default `10` if null/omitted; if specified, `InclusiveBetween(1, 20)` |

*Note: Unnecessary scheduling parameters (`startAt`, `availableMinutes`, `budgetVnd`, `transportMode`, `restPreference`) are explicitly excluded as they are constraint inputs, not relevance-ranking signals.*

### 2.3 Response Contract (`PoiRecommendationResultDto`)

```json
{
  "totalAvailable": 12,
  "items": [
    {
      "poiId": 10031,
      "name": "Chùa Cầu Hội An (Lai Viễn Kiều)",
      "categoryName": "Culture",
      "thumbnailUrl": "https://res.cloudinary.com/tripmate/image/upload/v1/chuacau.jpg",
      "distanceKm": 1.25,
      "estimatedVisitCost": 60000.0,
      "averageRating": 4.6,
      "recommendationReason": "Phù hợp với sở thích Culture của bạn"
    }
  ]
}
```

#### Field Specifications (`RecommendedPoiItemDto`)
- `poiId` (`long`): Primary key of the candidate POI.
- `name` (`string`): Display name of the POI.
- `categoryName` (`string`): Category name (e.g., `"Culture"`, `"Museum"`).
- `thumbnailUrl` (`string?`): Primary photo URL if present in `catalog.POIPhotos`, otherwise `null`.
- `distanceKm` (`decimal`): Rounded equirectangular distance in kilometers from `(explorationLatitude, explorationLongitude)` (2 decimal places).
- `estimatedVisitCost` (`decimal?`): Estimated cost in VND, or `null`.
- `averageRating` (`decimal?`): Public traveler review average rating aggregated from `social.Reviews` (`TargetType == Review.TargetTypePoi`), rounded to 1 decimal place, or `null` if no reviews exist for that POI.
- `recommendationReason` (`string?`): Deterministic explanation string generated from feature matches, or `null`.

*Quality Signal Isolation:* `ScenicScore` and `PhotoRating` remain internal ranking quality signals defined on `catalog.POIs` and MUST NOT be presented to users as review ratings or conflated with `averageRating`.

---

## 3. Candidate Eligibility & Screening

A POI in `catalog.POIs` is eligible for recommendation if and only if it satisfies all three static eligibility conditions:

1. **Active Status:** `Status == PointOfInterestStatus.Active`
2. **Planning-Ready Verification:**
   - `SourceUrl != null`
   - `VerifiedAtUtc != null`
   - `OpeningHours.Any(h => !h.IsClosed && h.OpenTime.HasValue && h.CloseTime.HasValue)`
3. **Bounding Box & Radius Constraint:** `Latitude` and `Longitude` fall within bounding box generated by `LocationBounds.From(explorationLatitude, explorationLongitude, searchRadiusKm)` and `GeoDistance.EquirectangularKilometers(explorationLatitude, explorationLongitude, poi.Latitude, poi.Longitude) <= searchRadiusKm`.

*Feasibility Boundary:* Recommendation guarantees static eligibility only. Final feasibility for opening time on the requested trip date/time, available duration, budget, transport/travel time, rest, and mandatory combination remains exclusively authoritative to the constraint-based deterministic itinerary scheduler during `POST /api/v1/scheduling-requests`.

---

## 4. Personalization & Scoring Architecture

### 4.1 Frozen V1 BaseScore Formula
The recommendation ranking strictly reuses the frozen TM-215 Base Score formula without alteration:

$$\text{TripMateBaseScore} = 0.300 \cdot \text{CategoryAffinity} + 0.200 \cdot \text{TagAffinity} + 0.250 \cdot \text{PersonalBehaviorAffinity} + 0.125 \cdot \text{ScenicQuality} + 0.125 \cdot \text{PhotoQuality}$$

### 4.2 Architectural Component Reuse Strategy
To prevent code duplication and avoid inappropriate AI provider pool overhead, the handler directly reuses existing infrastructure building blocks:

- **`PersonalizationFeatureBuilder.Build`**: Computes `CategoryAffinity`, `TagAffinity`, `ScenicQuality`, and `PhotoQuality`.
- **`PersonalBehaviorFeatureAggregator.AggregateAsync`**: Batches historical `Like`, `Dislike`, and `Skip` event counts for the current traveler across candidate POIs and categories.
- **`PersonalBehaviorAffinityScorer`**: Calculates denominator-smoothed heuristic behavior affinity with category fallback and neutral ($0.50$) cold-start handling.
- **`PersonalizationBaseScorer`**: Computes the weighted sum.

*Explicit Non-Use:* `PoiRankingOrchestrator` is **NOT** invoked because it introduces provider pool truncation (`MaxProviderCandidates = 60`), AI provider latency, and provider snapshot telemetry unsuitable for an informational recommendation query.

### 4.3 Quality & Behavior Feature Fallbacks
- `ScenicQuality`: `scenicScore / 10.0` if populated; `0.5` if `NULL`.
- `PhotoQuality`: `photoRating / 10.0` if populated; `0.5` if `NULL`.
- `PersonalBehaviorAffinity`:
  - Direct POI evidence ($\text{Like} + \text{Dislike} + \text{Skip} > 0$): Denominator-smoothed formula $\frac{\text{raw} + 1}{2}$ where $\text{raw} = \frac{\text{Like} - \text{Dislike} - 0.5 \cdot \text{Skip}}{\text{Like} + \text{Dislike} + 0.5 \cdot \text{Skip} + 2.0}$ ($\text{PriorWeight} = 2.0$).
  - Category fallback ($\text{InteractedPois} \ge 2$): Same formula applied to category aggregates.
  - Cold-start / No history: Default neutral affinity $0.50$.

---

## 5. Deterministic Ordering & Tie-Breaking

Candidates are ordered strictly by the canonical TM-215 ordering sequence:

1. `TripMateBaseScore DESC` (Primary personalization rank)
2. `ScenicScore DESC` (`NULL` sorted last $\rightarrow \text{decimal.MinValue}$)
3. `PhotoRating DESC` (`NULL` sorted last $\rightarrow \text{decimal.MinValue}$)
4. `DistanceKm ASC` (Proximity to exploration center)
5. `EstimatedVisitCost ASC` (`NULL` sorted last $\rightarrow \text{decimal.MaxValue}$)
6. `PoiId ASC` (Final deterministic tie-breaker)

---

## 6. Deterministic Recommendation Reason Precedence

To provide clear, privacy-safe, non-LLM explanation strings without inventing claims, the system applies a strict feature-driven precedence chain:

| Priority | Feature Trigger Condition | Generated `recommendationReason` String |
| :--- | :--- | :--- |
| **1** | `CategoryAffinity > 0` AND `TagAffinity > 0` AND `BehaviorAffinity > 0.5` | `"Phù hợp với sở thích [CategoryName], các thẻ quan tâm và lịch sử của bạn"` |
| **2** | `CategoryAffinity > 0` AND `BehaviorAffinity > 0.5` | `"Phù hợp với sở thích [CategoryName] và lịch sử tương tác của bạn"` |
| **3** | `CategoryAffinity > 0` AND `TagAffinity > 0` | `"Phù hợp với danh mục [CategoryName] và các thẻ sở thích của bạn"` |
| **4** | `CategoryAffinity > 0` | `"Phù hợp với sở thích [CategoryName] của bạn"` |
| **5** | `TagAffinity > 0` | `"Phù hợp với các thẻ quan tâm của bạn"` |
| **6** | `BehaviorAffinity > 0.5` | `"Phù hợp với lịch sử tương tác của bạn"` |
| **7** | `(ScenicQuality >= 0.8)` OR `(PhotoQuality >= 0.8)` | `"Địa điểm được đánh giá cao tại khu vực tìm kiếm"` |
| **Fallback** | None of the above conditions met | `null` |

*Privacy Guarantee:* Explanation generation NEVER exposes raw score values, interaction counts, JSON strings, or user identity.

---

## 7. Performance & Query Optimization

To maintain sub-50ms response times and prevent N+1 queries:

1. **Traveler Profile Read:** Single indexed query to fetch `InterestTagsJson` for `travelerUserId`. (1 query)
2. **Candidate Selection Query:** Single filtered `AsNoTracking()` query on `catalog.POIs` joining `Category`, `PoiTags`, and `OpeningHours` using bounding box pre-filtering. (1 query)
3. **Batch Behavior Query:** `PersonalBehaviorFeatureAggregator.AggregateAsync` runs at most TWO grouped queries over `social.RecommendationBehaviorEvents` filtered specifically by `travelerUserId` and candidate POI/Category IDs. (at most 2 queries)
4. **Batch Thumbnail & Review Ratings Query:** Single batched lookup over `catalog.POIPhotos` and `social.Reviews` for candidate POI IDs. (1 query)
5. **In-Memory Scoring:** Vectorized scoring and sorting in memory.

*Worst-Case Query Count:* Worst-case normal non-empty execution is at most 5 batched read-only database queries regardless of candidate volume.

---

## 8. Security, Privacy & Error Handling

### 8.1 Access Control
- Endpoint requires valid JWT authentication.
- Restricted to `UserRole.Traveler`. Unauthenticated calls return `401 Unauthorized`; non-traveler roles return `403 Forbidden`.

### 8.2 Error Responses & HTTP Status Codes
- **Validation Failure (`400 Bad Request`):** Returned via standard RFC-7807 `ValidationProblemDetails` if coordinates, radius, or limit fail FluentValidation rules.
- **Empty Candidate Pool (`200 OK`):** If zero POIs exist within the specified radius/filters, return `200 OK` with `{"totalAvailable": 0, "items": []}`.
- **System Exception (`500 Internal Server Error`):** Returned on unexpected database failure.

---

## 9. Verification & Test Matrix

Implementation MUST be verified by a comprehensive test suite covering:

1. **Authentication Gate:** `401 Unauthorized` when JWT is missing; `403 Forbidden` for non-Traveler roles.
2. **Planning-Ready Filter:** Excludes POIs with `Status != Active`, missing `SourceUrl`, missing `VerifiedAtUtc`, or empty opening hours.
3. **Spatial Filtering:** Excludes POIs outside `searchRadiusKm`.
4. **Preference Scoring:** Asserts `CategoryAffinity` and `TagAffinity` promote matching POIs.
5. **Behavior Signals:** Asserts `Like` promotes POIs, `Dislike` demotes POIs, and `Skip` weakly demotes POIs.
6. **Behavior Fallback:** Asserts category behavior fallback applies when $\ge 2$ interacted POIs exist in the category.
7. **Cold-Start Isolation:** Asserts neutral $0.50$ behavior affinity for travelers without history.
8. **NULL Quality Defaulting:** Asserts `NULL` ScenicScore/PhotoRating default to $0.50$.
9. **Tie-Break Ordering:** Verifies exact 6-level tie-break sequence (`BaseScore` $\rightarrow$ `Scenic` $\rightarrow$ `Photo` $\rightarrow$ `Distance` $\rightarrow$ `Cost` $\rightarrow$ `PoiId`).
10. **Limit Controls:** Defaults to 10 items; caps at requested limit up to 20; rejects limit > 20 with `400 Bad Request`.
11. **Deterministic Explanation:** Verifies reason strings match specified precedence rules without exposing internals.
12. **Zero Side Effects:** Verifies zero database writes or transaction locks.
