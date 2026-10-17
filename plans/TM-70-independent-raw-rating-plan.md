> Explicit Developer approval recorded 2026-10-16 for RAW-ONLY R001-R012 (RO01-RO12), including test access if needed. No Product/BA signoff. This approved colocated snapshot controls implementation.

# TM-70 RAW-ONLY SQL reader integration — atomic TDD plan

Date:2026-10-16. Status: DEVELOPER_APPROVED_RAW_ONLY_2026_10_16.
Controlling spec: specs/TM-70-independent-raw-rating-spec.md, RO01..RO12. Supersedes full rating-policy/display plan. Explicit Developer approval was recorded before implementation; approval is technical only, no business gates closed.

## Task0 — entry and clean baseline

After explicit Developer approval of revised spec/plan: fetch develop, inspect focused drift from observed d7cff8ca, create dedicated feature/tm-70-raw-only-reader branch/worktree. Preserve user changes. Copy approved RAW-ONLY documents into the two listed colocated paths. Restore and verify existing reader/aggregate/provider baseline before source edits. If baseline fails, diagnose before coding. Logs/TRX outside Git; no production resource/volume resets.

## Task1 — raw transport (RO01/02/03)

Files: reader, Application.csproj, TourSearchV2SqlServerTests.cs.
RED first: reader-level fixture expects an internal RawAverage carrier with provider4.666666 for4,5,5 and null for unrated. Add only test friend-assembly entry needed to compile direct access; record missing-carrier failure before production logic. Reuse existing seed/harness and approved shared-source contribution fixtures.
GREEN: add internal nullable init property without changing constructor; call QueryTourRatings(context,tours), join one rating row per candidate in SQL before existing page materialization, carry raw into returned internal page items. Keep existing count universe and guards. No display/count mapping. Run new cases plus existing aggregate/reader suite.
Two-pass fresh review: source reuse/guard scope, then SQL/architecture quality. Blocking findings fixed before next task.

## Task2 — preservation, pages and bounded queries (RO04/05/08/09)

Files: SQL reader tests; reader only if required within approved transport behavior.
RED where missing: vary only reviews and prove identical IDs/count/pages/current non-rating sort; multi-page visible/unrated/hidden/unrelated fixtures; command count at growing scope. Assert raw attaches to right Tour exactly once and primary thumbnails remain intact. Capture actual page/count SQL.
GREEN minimal correction only: no business predicate, rating sort, or duplicate AVG. Keep existing SQL non-rating order/filter before pagination; preserve count shortcut and batched enrichment. Inspect generated SQL and no per-Tour read/materialization loop. Do not require unused AVG in count SQL.
Verify exact test failures are meaningful; if existing code already satisfies a preservation assertion, record it as a green regression check rather than fabricate RED. Review scope and quality before advancing.

## Task3 — blocked policies and serialization (RO06/07/11)

Files: TourSearchV2ProjectionTests.cs, existing guard test, optionally TourSearchV2ContractTests.cs.
Add raw-bearing item mapper assertions: ToContract(null) still null; explicit4.7 still4.7; explicit4.25 still rejected. No no-argument mapper/new rounding adoption. JSON of internal item excludes internal raw; mapped public DTO shape unchanged and raw/count not leaked. MinimumRating/rating-sort/keyword/relevance remain blocked, independent of raw value availability.
No product code unless a genuine approved boundary regression is detected; do not change public DTO to pass an assertion. Fresh review of guard/serialization evidence.

## Task4 — cancellation/asOf/empty/paging edges (RO10)

Files: SQL reader tests; reader only for a necessary scoped regression fix.
Reuse counting clock, existing Serializable infrastructure and interceptor. Verify one asOfUtc; pre-cancelled call and cancellation during execution if harness supports a narrow callback; empty and extreme-page behavior including existing int-offset limitation. Do not create a generic cancellation framework or change isolation/timeout. Each new fix follows failing test first. Report any harness-limited check as NOT_EXECUTED, not PASS.

## Task5 — final review, UAT and checks (RO12)

Review exact diff against refreshed target and approved seven-file scope. Two separate passes: requirement/scope and code quality/architecture. Automated UAT matrix RO01..12 maps input/expected/actual/method/TRX/status. Distinguish automated internal acceptance from manual/business/WebMobile semantic E2E.

Run sequentially to avoid prior SQL fixture contention:
1. dotnet restore TripMate.slnx
2. dotnet build TripMate.slnx -c Release --no-restore
3. scoped dotnet format TripMate.slnx --no-restore --verify-no-changes --include changed C# files
4. focused reader/projection/contract SQL and merged aggregate/TM79/POI suites
5. Redis integration
6. full solution with configured local providers, correct opt-in semantic URLs if available
7. git diff --check and public/API/POI/schema/DI/embedding boundary checks

Existing local SQL harness uses disposable test databases, not production. Record exact SHA/commands/provider/versions/counters/TRX/logs/generated SQL/warnings. Cloudinary opt-in skips explicit; no retry-only result labeled clean full. Failures investigated without hiding them via code/timeouts. No test thresholds/sorts introduced for pending reader rating policy.

## Task6 — focused PR delivery after approval

Commit only intended files with Conventional Commit and Codex coauthor after tests/review. Push feature branch, create one focused PR per the user's conditional delivery request. Attach meaningful review/UAT evidence without secrets/massive logs. Verify exact head/base/files/CI/conversations. Keep Draft if a mandatory technical blocker remains; Ready only after CI PASS. Never self-approve, merge, deploy, change Jira or activate Search. Member independently reviews/approves/merges.

Definition of done: RO01..RO12 evidence satisfied with honest limits, RAW-ONLY scope preserved, every policy guard intact, clean regression and published-head CI PASS. Overall TM211 contract/business gates remain PENDING. Explicit Developer approval covers this RAW-ONLY execution plan only; future policy/activation work needs separate authorization.
