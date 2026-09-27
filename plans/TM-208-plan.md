# TM-208 — Public Tour search thumbnail: atomic implementation plan

Status: **approved 2026-09-27; Tasks 0–4 implemented and verified; independent review, TM-207 reconciliation and merge pending**

Spec: `specs/TM-208-spec.md`

Jira: [TM-208](https://tripmate-capstone.atlassian.net/browse/TM-208)

Branch: `feature/datmnt-tour-search-thumbnail` in `D:\CapStone\Capstone_BE_tm208`, based on TM-207 commit `ca9ec3a` while PR #28 is pending.

## Fixed boundaries

- Add nullable `thumbnailUrl` to each public `GET /api/v1/tours` item.
- Use only the `Active` primary TourMedia image of an otherwise eligible
  public Tour. No primary means null; never fall back to another ordered image
  or `catalog.POIPhotos`.
- Preserve the existing TM-70 response, predicates, filters, count, order,
  pagination, anonymous access, no-store behavior and problem responses.
- Use the stored HTTPS delivery URL. Do not fetch Cloudinary or expose a full
  gallery, provider IDs, operator-only metadata or a new media approval state.
- Do not change SQL schema, media writes, UI, rating, recommendation, Tour
  Detail, or publication workflow.

## Task 0 — Baseline and dependency check

1. Confirm clean TM-208 branch, exact TM-207 base SHA and PR #28 status.
2. Run `dotnet restore TripMate.slnx` and baseline Release build/unit tests.
3. For SQL integration, identify an isolated test SQL Server and set
   `TRIPMATE_SQLSERVER_TEST_CONNECTION` without printing credentials. Do not
   use shared/Azure or user-data databases. Record database identity without
   secrets and verify existing `SearchToursSqlServerTests` pass before edits.
4. If PR #28 is merged by then, bring the TM-208 branch onto the merged
   `develop` before implementation; otherwise keep the documented stacked
   dependency and do not mix TM-208 into PR #28.

Definition of done: clean baseline with all test skips/failures reported.

## Task 1 — Contract and SQL behavior tests (Red)

Files:

- `tests/TripMate.Api.IntegrationTests/Tours/SearchToursSqlServerTests.cs`
- `tests/TripMate.Api.IntegrationTests/Tours/SearchToursEndpointTests.cs`
- `tests/TripMate.Api.IntegrationTests/Tours/ToursOpenApiTests.cs`

Add failing tests for:

1. `thumbnailUrl` key present and null on a public Tour without an eligible
   primary image; existing fields remain unchanged in JSON and OpenAPI.
2. An active primary returns its stored HTTPS URL even if its `sort_order` is
   greater than another active non-primary image's order.
3. Deleted former primary, active non-primary-only media, Draft/unpublished
   Tour media, and POI photos cannot supply the thumbnail.
4. Multi-page results retain title/ID ordering, page size and total count;
   off-page media does not appear on the current page.
5. The bounded media query executes once for the selected page, not once per
   Tour; existing count/page snapshot and anonymous response behavior remain.

Use existing `SqlServerTestDatabase` and command-interceptor patterns. Run
only the affected test filters and verify they are discovered and fail for
the missing DTO/projection/OpenAPI behavior, not for environment setup.

Definition of done: expected RED failures and no unrelated failures.

## Task 2 — Minimal read-model implementation (Green)

Files:

- `src/TripMate.Application/Features/Tours/Search/TourSearchItemDto.cs`
- `src/TripMate.Application/Features/Tours/Search/SearchToursQueryHandler.cs`

1. Append `string? ThumbnailUrl` without altering existing JSON names or
   values. Ensure the serializer emits the property when null.
2. Inside the existing serializable snapshot, after obtaining page Tour IDs,
   query only those IDs from `IApplicationDbContext.TourMedia`, filtering
   `LifecycleStatus == Active` and `IsPrimary`, projecting Tour ID and
   `DeliveryUrl` only. Rely on the database's at-most-one-active-primary
   constraint; do not use order-based fallback or load images/whole gallery.
3. Map URLs by Tour ID into page items; preserve zero-item behavior and
   cancellation. Do not create N+1 queries or change the public Tour predicate.

Run the Task 1 filters until green. Review scope compliance and query shape
before advancing.

Definition of done: all focused tests pass against real SQL Server, including
query-count/paging assertions; no extra fields returned.

## Task 3 — Contract documentation

Files:

- `src/TripMate.Api/OpenApi/` only if required for nullable schema fidelity
- `docs/TM-208-public-tour-search-thumbnail.md` in BE
- A dedicated Docs-repo branch/file `api/contracts/search-tours-thumbnail-api.md`
  if the approved source-of-truth addendum's follow-on documentation has not
  already been delivered; do not edit the TM-205 review branch in place.

Document `thumbnailUrl` as always-present nullable HTTPS URL, primary-only
selection, null semantics, preservation of TM-70 fields and anonymous access,
and no gallery/POI fallback. Align Jira's "first ordered" wording with the
owner-confirmed primary-only rule. Assert OpenAPI marks the property nullable.

Definition of done: runtime JSON, OpenAPI and written contract agree. Any
separate Docs change is reported as such and not silently committed to BE.

## Task 4 — Full verification and review

Run:

```powershell
dotnet format TripMate.slnx --verify-no-changes --no-restore
dotnet build TripMate.slnx -c Release --no-restore
dotnet test TripMate.slnx -c Release --no-build --no-restore --logger "console;verbosity=minimal"
git diff --check
git status --short --branch
```

Include opt-in SQL tests with the identified isolated test database and state
their pass/fail/skip counts separately. Review first for spec compliance, then
for code quality, security and performance. Reconcile the branch with
`develop` after TM-207 merges and rerun affected gates. Do not commit, push,
open a PR or merge without a subsequent explicit instruction.

Definition of done: evidence recorded by command and exit code, no unexplained
failures/skips, clean diff, and human review/delivery choice presented.

## Progress — 2026-09-27

- Task 0: baseline Release build and units passed; `SearchToursSqlServerTests`
  passed 5/5 on isolated SQL Server. TM-207 PR #28 remains open, so TM-208 is
  stacked on `ca9ec3a` rather than modifying PR #28.
- Task 1: expected RED was 22 passed and 4 failed, all from missing
  `thumbnailUrl` in runtime DTO/OpenAPI; no test environment failures.
- Task 2: 26/26 focused SQL/API/OpenAPI tests passed. A four-query search
  snapshot now includes exactly one page-bounded media projection. Empty pages
  do not query media.
- Task 3: BE contract and separate Docs-repo contract were added; OpenAPI
  nullable property is covered by a passing integration test.
- Task 4: format and Release build passed. The combined full suite had three
  unrelated UC-04 Windows log-file lock failures; those three passed alone,
  and the other 390 API tests passed in a separate SQL-enabled run. Complete
  evidence is in `docs/TM-208-verification.md`. After delivery, the BE PR #29
  GitHub CI rerun passed both SQL integration and the complete solution suite.
  Docs PR #8 is also open. Independent review and merge remain outstanding.
