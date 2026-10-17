> Explicit Developer approval recorded 2026-10-16 for RAW-ONLY R001-R012 (RO01-RO12), including test access if needed. No Product/BA signoff. This approved colocated snapshot controls implementation.

# TM-70 RAW-ONLY SQL reader integration — proposed specification

Date: 2026-10-16, Asia/Saigon.
Status: DEVELOPER_APPROVED_RAW_ONLY_2026_10_16.
Baseline observed: origin/develop d7cff8cabd2e411957b41250ce95a8815f9f42ce (PR83 merged).
TM211_CONTRACT_STATUS = CONTRACT_PENDING.

## Authority and supersession

The user selected RAW-ONLY preparation as Developer/Implementer and explicitly did not claim Product/BA authority. The user subsequently explicitly approved this revised specification and atomic TDD plan for implementation. This version supersedes the earlier full reader integration proposal; its threshold/sort/display tasks must not be implemented under this slice. R-SOURCE remains approved and must not be reopened or duplicated.

## Objective

Enrich the existing unregistered TourSearchV2IndependentReader's internal page items with SQL RawAverage supplied by TripReviewAggregateReader.QueryTourRatings(). Demonstrate integration and SQL translation without deciding or enabling rating eligibility, rating ordering, display mapping or a public contract.

RAW-ONLY here means technical raw-value transport. It does not mean executing minimumRating or sort=rating with an assumed policy. Those executions remain guarded until separately approved by the accountable Owner/BA.

## Exact behavior and representation for approval

1. Keep the existing guards rejecting Keyword, MinimumRating, SortRelevance and SortRating. Keep their behavior and existing guard regression assertions. Preserve existing request parser/validator bounds and normalization unchanged; pending future business policy does not authorize changing these validations. Do not add a hidden option, test-only mode, callback or alternate entry point to bypass them.
2. Preserve existing visibility/status/published/operator/account predicates, other filters, representative schedule logic and current departure/price ordering and ties. Review values must not change membership, order, totalCount or totalPages. Unrated Tours remain present according to existing non-rating eligibility; this is preservation, not approval of a future rating policy.
3. Within the reader-owned existing Serializable transaction, construct QueryTourRatings(context, tours) from the complete existing filtered Tour IQueryable. Reuse its result in a one-row-per-Tour join with candidate rows before the existing SQL page materialization. Carry only nullable RawAverage; do not consume DisplayAverage or ReviewCount and do not duplicate source/AVG expressions.
4. Keep LongCountAsync on the existing filtered Tour universe because ratings cannot change membership in this slice. Keep global non-rating SQL predicates/order before Skip/Take, existing empty-page shortcut and oversized in-range offset exception. Do not imply this proves future rating-filtered totals or semantic global coverage.
5. Preserve IndependentTourSearchItem's positional constructor. Add `internal decimal? RawAverage { get; init; }`, initialized from each materialized SQL row. No new display property, ToContract() overload, rounding call or average/count public field. Existing ToContract(decimal? approvedDisplayAggregateRating) remains unchanged and independent of raw; the existing explicit caller-supplied display boundary remains external.
6. Integration tests need direct access to the internal carrier. Add only `InternalsVisibleTo Include="TripMate.Api.IntegrationTests"` to the Application csproj using its established friend-assembly pattern. This grants test assembly access, not public HTTP access; no new package/framework/global layer. No reflection-based assertions. Include this narrow tooling change in explicit spec approval.
7. Internal property must not serialize in default JSON. Test both internal item serialization and mapped public DTO JSON: no rawAverage/reviewCount leak and no change to the current always-present nullable aggregateRating DTO shape. The DTO shape itself remains unchanged, not newly adopted as the live Search contract.
8. Materialize only page rows; preserve batched destinations and primary thumbnails. No per-Tour ReadTourAsync, separate per-item queries, full-corpus memory scan, new cache, or remote call. Verify a fixed command count as page/scoped Tour population grows. Count may legitimately omit unused rating joins under SQL optimization.
9. Keep one clock snapshot/asOfUtc, reader-owned transaction lifetime and terminal cancellation propagation. Verify pre-cancelled calls and cancellation during page execution if the existing interceptor harness supports it, without new generic infrastructure. Do not claim stable data across distinct page requests.
10. No public route/MediatR/DI registration, API/DTO/schema/config/data mutation, POI/TM79 public behavior change, E5/Qdrant/semantic worker modification or production activation. No new consumer selects/ranks by the internal raw field in this slice.

## Acceptance criteria and automated UAT

| ID | Setup / actual assertion | Required evidence |
|---|---|---|
| RO01 | Reader page Tour with contributions4,5,5 carries SQL decimal4.666666, not display4.7; existing approved source selection reused | Provider SQL reader test; generated AVG/UNION ALL; direct internal property assertion |
| RO02 | Reader page unrated Tour carries null; its preexisting non-rating membership/count remain unchanged; raw is never replaced by zero | SQL reader null fixture and old expected page/total assertions |
| RO03 | Canonical Published/eligible legacy mixed fixture, including overlapping legacy, produces same raw as merged projection; service/commerce namespace and equal-valued preservation remain green | Reader mixed fixture plus existing aggregate suite; no new source predicate |
| RO04 | Same Tour scope with different review values/no reviews yields identical IDs/order/count/pages under existing departure/price sorts | Before/after fixture comparison of reader results, varying only reviews |
| RO05 | Multiple pages, existing ties, beyond-last empty, visibility exclusions and one raw value per returned Tour; hidden/unrelated Tour reviews cannot attach to another Tour | Provider reader tests against current approved non-rating ordering; no new rating tie rule |
| RO06 | MinimumRating and sort=rating remain NotSupportedException; keyword/relevance likewise; no threshold/null-last-rating policy encoded as accepted behavior | Existing guard test preserved and explicit rating-guard assertions |
| RO07 | ToContract(decimal?) unchanged; raw4.666666 cannot automatically become display; caller null stays null and caller4.7 stays4.7;4.25 rejection remains | Existing projection test plus raw-bearing item mapping/JSON test; no new rounding adoption |
| RO08 | SQL page contains provider AVG/UNION ALL from reusable projection, SQL order/page and no display ROUND; no I/O during projection construction, no per-Tour query growth | Captured SQL/command counter; compare small/larger page scope; existing count+page+2batched enrichment stages remain bounded |
| RO09 | Primary thumbnail/schedules/destinations and all current visibility/date/price/category behaviors unchanged; null raw adds no filtering | Existing SQL regressions plus focused reader fixtures |
| RO10 | One asOfUtc/Serializable transaction and cancellation preserved; zero rows/large pages retain current behavior | Clock counter, pre-cancelled/provider cancellation tests where supported, existing paging tests |
| RO11 | No raw/count public serialization, unchanged public DTO/production GET, unchanged POI/TM79 behavior and no registration/activation | JSON assertions, complete diff review, legacy API/TM79/POI regressions |
| RO12 | Mandatory checks pass on exact implementation HEAD; all failures/skips honestly reported | Restore/build/format, focused SQL/Redis/full sequential TRX; two-pass review; CI before Ready |

RO01 tests provider finite decimal representation of14/3, not infinite precision or minimumRating behavior. RO04/05 validate preservation of existing order/count/pages only. Historical R03/R11/R12 projection tests may be rerun for compatibility, but their test composition does not approve reader-level rating policy. Automated UAT maps RO01..12; manual/business/client semantic E2E remain NOT_EXECUTED or outside scope.

## Exact proposed files

1. specs/TM-70-independent-raw-rating-spec.md — approved colocated RAW-ONLY copy.
2. plans/TM-70-independent-raw-rating-plan.md — approved atomic plan.
3. src/TripMate.Application/Features/Tours/Search/V2/TourSearchV2IndependentReader.cs — raw join/carrier only, guards preserved.
4. src/TripMate.Application/TripMate.Application.csproj — single test friend assembly entry.
5. tests/TripMate.Api.IntegrationTests/Tours/TourSearchV2SqlServerTests.cs — provider reader/command/cancellation assertions, reuse seed/harness.
6. tests/TripMate.Api.IntegrationTests/Tours/TourSearchV2ProjectionTests.cs — unchanged explicit display mapper and raw-bearing item boundary.
7. tests/TripMate.Api.IntegrationTests/Tours/TourSearchV2ContractTests.cs — only if needed for unchanged JSON serialization boundary.

No TripReviewAggregateReader/Projection membership change, new schema, DTO source edit or API code. New implementation branch from freshly fetched develop only after explicit approval and clean baseline. Any required additional behavior/file scope is reviewed before expansion.

## Business decisions still PENDING

- Rating threshold: inclusive/exclusive comparison, scale/validation and raw precision interpretation at policy boundary — accountable TM70 Owner/Product/BA.
- Unrated eligibility with/without threshold and rating-sort null placement — same accountable authority.
- Rating direction and tie-breaking/collation — Owner/BA plus Backend/DB review. Existing non-rating ties are only preserved.
- R-ROUND: public display precision/midpoint policy — Review/TM211/TM70 Owner/Product/BA.
- Search R-PLACEMENT: whether/where rating is exposed; nullable aggregateRating, counts/raw fields and client compatibility — Product/BA/API/Web/Mobile owners.
- Detail fields/count and compatibility — TM210/TM72 and client owners; R-POI Recommendations membership/ownership — separate POI/Recommendations owners.
- SEM-D1..5, taxonomy/data mapping, official SRS/API activation, staging/E2E/performance/recovery and G-ROLLOUT — applicable owners/release authority.

None of these gates is closed by RAW-ONLY implementation approval. They do not block this transport-only slice while all corresponding guards/public boundaries remain intact. They still block subsequent rating execution/public adoption.

## Approval and delivery boundary

Explicit Developer approval of this revised specification and the matching colocated plan was recorded before code, satisfying AGENTS1.2. The Developer is not asked to sign business policy. After approval: isolated branch, clean baseline, TDD, fresh two-pass review, automated internal UAT, sequential full regression, publish focused PR and CI. Ready only after CI PASS; member independently approves/merges. No self-approve/merge/deploy/Jira/production changes.


Independent document review2026-10-16: PASS, no blocking/actionable findings; confirmed transport-only scope and preserved guards. This document review itself did not execute tests; implementation/test evidence is recorded separately in the PR.
