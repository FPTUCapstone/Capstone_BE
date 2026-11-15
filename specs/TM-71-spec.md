# TM-71 / UC-25 - Receive Tour Recommendations (Draft)

Status: Approved for implementation by the TM-71 Task Owner on 2026-11-04 (Asia/Saigon)

Approval record: the Task Owner explicitly approved decisions D1-D6, including the deterministic scoring formula, the two-endpoint refresh/latest-snapshot contract, the criteria snapshot input, the documented message handling, and the protected recovery onto a fresh branch from `origin/develop`. Implementation remains subject to the repository's separately approved atomic TDD plan.

Plan approval record: the Task Owner approved `plans/TM-71-plan.md` on 2026-11-04 with three binding amendments: date match is a scoring component and never a Tour-exclusion filter; configuration can never lower the effective strict threshold below 80.00; and latest snapshot follows refresh-request order among committed snapshots even when an older transaction commits later.

## 1. Sources and precedence

1. The team's official source of truth is `Report3_Software-Requirement-Specification.docx`, especially section 3.5.2 and its local BR-20, BR-56 to BR-58 and BR-60 references. Its canonical Drive identity is `1VwPkqRyC6ZlbXBQC0ztdektt0vAyE3qt` (`https://docs.google.com/document/d/1VwPkqRyC6ZlbXBQC0ztdektt0vAyE3qt`). The designated local copy is `D:\CapStone\Report3_Software-Requirement-Specification.docx`. This is the binding functional source. The V2 document is explicitly non-authoritative for TM-71.
2. Jira TM-71 (`[UC-25] Receive Tour Recommendations`) for ticket scope and status only.
3. The canonical SQL schema on current `origin/develop` for the physical `commerce.TourSearchRequests`, `commerce.TourRecommendationLogs`, and `Tour.RecommendationSimilarityThreshold` contracts.
4. Current `origin/develop` code for architecture, visibility, availability, and persistence conventions.
5. Current Web and Mobile code for consumer compatibility. These clients do not define missing business rules.

The Drive document and the designated local DOCX have the same reported filename and byte size (9,645,892 bytes), and the fetched Drive text contains the same UC-25 and application-message content inspected locally. Drive did not expose a checksum, so this spec records content/metadata agreement rather than claiming byte identity. The derived MVP and screen-specification Markdown files are supporting material only. They do not override the official Report 3.

When the sources conflict, this spec records an explicit decision instead of silently choosing one.

### 1.1 Binding behavior extracted from the original DOCX

- The feature is Traveler-only and runs when the recommendations screen opens or is explicitly refreshed.
- At least one valid interest tag is required.
- Travel preferences, interest tags, current planning destination/date/budget criteria, and trip history are score inputs.
- A candidate qualifies only when its final score is strictly greater than 80.00.
- Results contain only public, approved, currently available Tours; unpublished, sold-out, cancelled/completed-only, and past-departure Tours are excluded.
- Results are ordered by score descending and paginated under CR-01 (20 items per page by default).
- Availability is rechecked before rendering; an item that became unavailable is omitted.
- Every displayed item exposes its matching score, and the operation never creates a Booking or mutates preferences.

## 2. Scope

### In scope

- Backend API for an authenticated Traveler to obtain ranked Tour recommendations.
- Strict qualification threshold: similarity score must be greater than 80.00, never equal to 80.00.
- Candidate eligibility: approved, publicly published Tour with at least one future Scheduled departure that has remaining capacity. The optional requested date range affects score only and never removes an otherwise eligible Tour.
- Pagination with a fixed default of 20 items and total count.
- Current availability recheck before an item is returned.
- A visible numeric score in the response so clients can render MSG68.
- Deterministic ordering and pagination.
- RFC 7807 errors through the existing `Result<T>` and `HandleFailure` pipeline.
- Unit, contract, OpenAPI, and SQL Server integration tests.

### Out of scope

- Web or Flutter UI implementation was initially out of scope; the Owner explicitly expanded TM-71 on 2026-11-13, and the resulting client boundary is recorded in section 8.1.
- Booking creation or reservation.
- Editing Traveler preferences.
- Admin algorithm-configuration UI.
- AI/LLM/embedding-based ranking.
- Changes to the canonical SQL schema or EF migrations.
- Favorite, rating, navigation, or payment behavior.

## 3. Source inconsistencies that require decisions

### 3.1 Missing interest tags

The current SRS, as explicitly confirmed by the Task Owner, assigns MSG183 to missing interest tags. The Backend retains typed `422` ProblemDetails code `tour_recommendation.interest_tags_required`; Web and Mobile map it to MSG183 and offer Travel Preferences. This resolves the historical message-index issue and requires no further Owner decision.

### 3.2 Empty recommendations

Current UC-25 and CR-02 use the domain-specific MSG64 for an empty Tour result list. No qualified result is a successful `200` response with `items: []` and `totalCount: 0`; clients show MSG64 and a Tour Search link.

### 3.3 Similarity algorithm is not defined

The original SRS identifies input categories but does not define weights, normalization, missing-input behavior, trip-history affinity, or a mapping from all Traveler preference dimensions to Tour attributes. Current `origin/develop` has no Tour matching engine. It has POI ranking helpers, but those produce unbounded ranking points and are not a 0-100 Tour similarity contract. Implementing a score without approval would invent a business rule.

Proposed deterministic MVP algorithm:

- Interest-tag similarity: 40 points. Jaccard overlap between normalized Traveler interest tags and the union of the Tour category plus categories/tags on POIs in the Tour itinerary.
- Destination match: 20 points. Normalized exact match against `TourDestinations`; omitted destination contributes no component and the remaining configured components are re-normalized.
- Budget match: 15 points. The Tour base price is within the supplied minimum/maximum range; an omitted bound remains open.
- Date match: 15 points. The component similarity is 1 when any currently bookable schedule starts within the supplied inclusive date range and 0 otherwise. With no date range, this component is omitted. Date mismatch never excludes an otherwise eligible Tour.
- Trip-history affinity: 10 points. Jaccard overlap between the candidate category/destinations and the Traveler's completed Tour booking history.
- Components that have no configured Traveler/request input are omitted and the active component weights are re-normalized to 100.
- Round once at the end to two decimal places using `MidpointRounding.AwayFromZero`.
- Retain only scores strictly greater than the effective threshold. The configured threshold is read from `SystemConfigs` key `Tour.RecommendationSimilarityThreshold`, whose schema seed is 80; the effective threshold is `max(configuredThreshold, 80.00)`, so configuration can never admit a score at or below 80.00.

The schema and current entities cannot compare preferred transport mode, pace, risk tolerance, or food preferences to a Tour without another approved mapping. The approved MVP therefore treats interest tags as the only directly comparable profile-preference dimension. This algorithm was explicitly approved under D3 on 2026-11-04 (Asia/Saigon).

### 3.4 Duplicate business-rule identifiers in the original DOCX

The local UC-25 section says BR-57 defines the score inputs, BR-58 defines `> 80`, BR-56 defines approved/public visibility, and BR-60 defines availability. The global Business Rules catalogue reuses those identifiers with different meanings: its BR-57 combines matching and the threshold, BR-58 is availability, BR-60 is read-only behavior, and its BR-20 concerns Tour Operator approval rather than interest tags.

Proposed decision: implementation tests bind to the normative statements in section 3.5.2 and the explicit UC-25 post-conditions, while recording the catalogue conflict as an unresolved SRS defect.

### 3.5 Current planning criteria source is not defined operationally

The SRS says the system reads current planning criteria, while the API input section lists only page number and optional refresh. TM-70 does not persist public searches on current `develop`.

Proposed decision: a refresh request accepts an optional criteria snapshot (`destination`, `startDate`, `endDate`, `minBudget`, `maxBudget`) and persists it to `commerce.TourSearchRequests`; profile interest tags remain authoritative and required. A normal list request reads the Traveler's latest recommendation snapshot. The latest Scheduling Request is not a complete substitute because its current schema does not contain the DOCX destination/date-range/minimum-and-maximum-budget tuple.

## 4. Proposed API contract

### 4.1 Generate or refresh recommendations

`POST /api/v1/tour-recommendations/refresh`

Authorization: active authenticated `Traveler` only.

Request:

```json
{
  "destination": "Da Nang",
  "startDate": "2026-12-01",
  "endDate": "2026-12-10",
  "minBudget": 1000000,
  "maxBudget": 5000000
}
```

All fields are optional. Validation:

- `endDate >= startDate` when both are supplied.
- budgets are whole VND and non-negative.
- `maxBudget >= minBudget` when both are supplied.
- destination is trimmed and limited to the canonical schema length.
- at least one valid Traveler profile interest tag must exist.

Behavior:

1. Resolve the current Traveler from the authenticated principal.
2. Snapshot the submitted criteria and profile interest tags in `TourSearchRequests`.
3. Query only currently eligible Tours and compute the approved score.
4. Persist one `TourRecommendationLogs` row per scored candidate, including candidates at or below the threshold for auditability; set `is_recommended` from the strict threshold.
5. Return the first result page after rechecking current availability.
6. Perform the snapshot and log write atomically; no Booking or preference row is changed.

### 4.2 Read latest recommendation snapshot

`GET /api/v1/tour-recommendations?page=1`

- Page is one-based and must be positive.
- Page size is fixed at 20 by CR-01.
- Uses the latest committed recommendation snapshot for the Traveler by `search_request_id DESC`. Refresh inserts its snapshot row as the first write inside the transaction, so the SQL identity records refresh-request order; rolled-back identities are ignored because only committed rows are visible. An older transaction committing later cannot supersede a newer requested snapshot.
- Rechecks public status and schedule availability; stale/unavailable items are excluded.
- Orders by `similarityScore DESC`, then `tourId ASC` for deterministic pagination.
- Does not mutate recommendation data.

Success response:

```json
{
  "page": 1,
  "pageSize": 20,
  "totalCount": 1,
  "totalPages": 1,
  "asOfUtc": "2026-11-04T00:00:00Z",
  "criteria": {
    "destination": "Da Nang",
    "startDate": "2026-12-01",
    "endDate": "2026-12-10",
    "minBudget": 1000000,
    "maxBudget": 5000000
  },
  "items": [
    {
      "tour": {
        "tourId": "123",
        "title": "Da Nang Discovery",
        "destinations": ["Da Nang"],
        "operatorName": "TripMate Tours",
        "durationDays": 3,
        "basePrice": 2500000,
        "currency": "VND",
        "representativeScheduleId": "456",
        "departureAtUtc": "2026-12-03T01:00:00Z",
        "availabilityStatus": "available",
        "remainingSlots": 8,
        "thumbnailUrl": "https://..."
      },
      "matchingScore": 92.50,
      "matchingReasons": []
    }
  ]
}
```

The nested `tour` plus `matchingScore` and `matchingReasons` shape preserves the current Web `TourRecommendationDto`. Pagination metadata is additive and requires Web to replace its pending-backend stub. Mobile currently has no API/domain contract; its reusable card already accepts the title, operator, price, destination, departure, and integer match percentage needed for presentation.

The API returns data, not localized MSG68 text. Clients render:

`Matching score: {Score}% based on your travel style and preferences.`

## 5. Error contract

| Condition | HTTP | Stable code / representation |
|---|---:|---|
| Anonymous or expired token | 401 | Existing authentication pipeline; client renders MSG125 |
| Authenticated non-Traveler | 403 | Existing authorization pipeline; client renders MSG126 |
| Invalid page or refresh criteria | 400 | `ValidationProblemDetails` |
| No valid interest tag | 422 | `tour_recommendation.interest_tags_required` |
| No prior snapshot on GET | 200 | Empty page with `criteria: null` |
| No score strictly above threshold | 200 | Empty page |
| Unexpected persistence/query failure | 500 | Existing ProblemDetails pipeline; client renders MSG127 |

## 6. Data and architecture

- Reuse current `Tour`, `TourDestination`, `TourItineraryItem`, `TourSchedule`, `TourMedia`, `TravelerProfile`, booking, and POI/tag mappings from `origin/develop`.
- Add only the missing database-first entities/mappings for `TourSearchRequests` and `TourRecommendationLogs` if they remain absent on the implementation baseline.
- Domain entities use private setters/factory methods and contain no EF dependencies.
- Read paths use `AsNoTracking` and cancellation tokens.
- Filtering, ranking, deterministic ordering, and pagination must be SQL-translatable or explicitly bounded before any in-memory scoring. The implementation must not load the complete Tour catalogue.
- Avoid N+1 queries. Page media/schedule/operator data is projected in bounded queries.
- No generic repository.
- No EF migration.

## 7. Acceptance criteria

1. Only an authenticated Traveler can call the endpoints.
2. A Traveler without a valid interest tag cannot generate a personalized snapshot.
3. Exactly 80.00 is excluded; 80.01 is eligible.
4. Draft, Pending, Rejected, Inactive, unpublished, past-only, cancelled-only, completed-only, and sold-out Tours are excluded regardless of score.
5. Results are ordered by score descending and deterministically by Tour ID.
6. Pagination defaults to 20 and includes total count.
7. Every item exposes its current remaining slots and score.
8. Empty qualified results return `200` with an empty list.
9. Refresh creates one criteria snapshot and its recommendation logs atomically.
10. GET does not create or modify recommendation, booking, or preference data.
11. The feature creates no Booking and modifies no Traveler preference.
12. Unit, API contract, OpenAPI, and SQL Server tests cover the happy path, threshold boundary, role boundaries, stale availability, pagination, and rollback.

## 8. Web and Mobile integration boundary

- At the initial Backend design checkpoint, Web returned `PENDING_BE_INTEGRATION` and Mobile had presentation-only recommendation widgets. Those historical gaps defined the client work later authorized by the Owner; section 8.1 records the implementation now present in isolated worktrees.
- Neither client may use demo fixtures in production or apply visibility/availability filtering client-side as a substitute for the Backend rules.

### 8.1 Integration execution record (2026-11-13)

- Backend was integrated in an isolated worktree at `origin/develop` `999e6af6e939f0efdf2f735d20eabcef686843c5`. The upstream Weather and `ITourAvailabilityReader` registrations remain intact; TM-71 adds `ITourRecommendationCandidateReader` alongside them. Upstream's additive `aggregateRating` field is projected inside the existing page query, so the catalogue/query-count bound is unchanged.
- Web was implemented in an isolated worktree at `origin/develop` `126174bb2e1bd0a14b8e1fc3cd5e81114742a9c8`. Production now performs GET latest and, only when `criteria` is null, one deduplicated POST refresh. Explicit refresh uses the current destination/date/budget criteria. Pagination is server-side at 20 items; errors retain the last successful list; reasons are hidden when D7 returns `[]`.
- Mobile was implemented in an isolated worktree at `origin/develop` `8c3ebdc4c6dba598f8a284ba8323b7876b168c3f`. Its prior 0-to-1 demo score assumption was replaced by the Backend's persisted 0-to-100 score contract. Production uses authenticated Dio GET/POST, deduplicates refresh, validates strict `>80.00`, preserves the previous list on MSG127, and refreshes after returning from Travel Preferences.
- Client defensive parsing rejects an unavailable item, a null/missing `matchingReasons`, a missing representative schedule, non-positive remaining slots, or a score outside `(80,100]`. This does not replace Backend eligibility; it fails closed and preserves the last successful client state.

Current SRS confirmation: missing interest tags use MSG183 with Travel Preferences; empty Tour results use MSG64 with Tour Search. The stale local DOCX copy (SHA-256 74D81C867C068CF5312187337AFA6BD5A83F1F8E2A5EF0CC1E19415F4EDC00C8) still has the previous index; the Owner explicitly superseded that index. No message-catalogue approval remains pending.

The implementation/test traceability matrix is maintained in `docs/TM-71-UC-25-matrix.md`.

## 9. Existing worktree recovery note

`feature/tm71-be-db-mappings` currently contains 24 uncommitted paths (including this spec) based on commit `b741db6`, 623 commits behind `origin/develop` at the time of the audit. Several entities and mappings in that change have since been implemented differently on `develop`, and the draft entities expose public setters contrary to current repository rules.

No existing uncommitted file may be discarded. The full worktree, including tracked and untracked files, was copied to `D:\CapStone\_safety_backups\Capstone_BE_tm71_mappings_20261104_180433`; 281 source files were verified with SHA-256, with zero missing, extra, or mismatched files. After this spec is approved, this same worktree path may be switched safely to a fresh feature branch from current `origin/develop`; only the still-missing `TourSearchRequest`/`TourRecommendationLog` concepts and this reviewed spec should be ported through new TDD work. The old branch and the verified backup remain recoverable.

D6 execution record: the 24 dirty paths were saved as recovery stash commit `7175d7ceba5f0bacc8be3a37244fd04db88c0b90`, verified to contain all 4 tracked and 20 untracked paths. The worktree was then switched without rebase/reset/clean to `feature/tm71-tour-recommendations` at `origin/develop` commit `448c8ec14c22f1421a27edb77f0bba0e68b09559`. A separate stash `dff9203aa6fcd42e4a61eb20d2deed136d2afb20` preserves five golden-test artifacts generated by the clean baseline run. Neither stash has been applied or dropped.

## 10. Approval record

Approved by the TM-71 Task Owner on 2026-11-04 (Asia/Saigon):

- D1: backend-only scope;
- D2: two-endpoint refresh/latest-snapshot contract;
- D3: deterministic scoring formula and weights;
- D4: explicit criteria snapshot on refresh;
- D5: missing-interest-tag and empty-result handling despite the SRS message conflicts;
- D6: fresh branch from current `origin/develop` in this same worktree path after storing the dirty state in a recoverable Git stash in addition to the verified external backup; no reset, clean, rebase, push, PR, or merge.

The Owner also requires explicit tests for the 80.00/80.01 boundary, weight normalization when optional criteria or trip history are missing, concurrent refresh latest-snapshot selection, atomic rollback, Traveler isolation, multi-schedule selection, and availability loss before response projection. The two known contract limitations in section 11 must be reported separately after implementation.

Plan amendments approved without another approval gate:

- A1: optional date criteria contribute a 15-point score component but never filter Tour eligibility.
- A2: effective threshold is `max(configuredThreshold, 80.00)` and qualification remains strict `score > effectiveThreshold`.
- A3: latest means highest committed `search_request_id` for the Traveler; snapshot insertion is the first transactional write so request order is preserved even when an older refresh commits later.

D7 approved by the TM-71 Task Owner on 2026-11-05 (Asia/Saigon): because the canonical schema does not persist refresh-time matching reasons or history evidence, `matchingReasons` remains a required, non-null array but is consistently returned as `[]` by both POST refresh and GET latest. GET does not recompute reasons, and this decision adds no column, table, or migration. Web and Mobile hide the explanation area when the array is empty. `matchingScore` remains the persisted snapshot score and must still be displayed for UC-25.

## 11. Approved contract limitations requiring post-implementation review

1. `GET` with no prior snapshot returns an empty page. Web and Mobile must call `POST /api/v1/tour-recommendations/refresh` when the recommendations screen first opens to satisfy the UC-25 trigger behavior.
2. A persisted score can become stale if Tour price/data or Traveler preferences change after its snapshot is created. This implementation does not invent an automatic score-refresh rule. It rechecks public visibility and current schedule availability before returning items, while retaining the persisted score until an explicit refresh creates a newer snapshot.
