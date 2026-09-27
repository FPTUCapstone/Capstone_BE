# TM-79 — UC-33 implementation plan

Status: **APPROVED v3 — implementation authorized within the approved Backend plan and recorded gates**

Spec: `specs/TM-79-spec.md`. Prepared 2026-09-27, Asia/Ho_Chi_Minh.
Owner: Mai Nguyen Tien Dat. Actor: authenticated Active Traveler.
Delivery: Backend, Flutter Mobile, responsive Next.js Web and API contract docs.

## 1. Scope conclusion and current evidence

TM-79 is a write workflow, not a rating widget. It includes owned/completed-trip
eligibility, one canonical trip-review submission per supported booking identity,
with one TripReview parent per supported commerce.Bookings booking_id,
overall stars, title and
content, optional POI ratings and photos, content screening, publication,
seven-day editing, aggregate reads, both clients and end-to-end verification.
The owner explicitly retained route pacing and CSP quality as separate inputs.

Evidence inspected:

- Report 3 section 3.7.2, pages 145-148 and the UC-33 figure on page 146;
  local `Report3_Software-Requirement-Specification` and extracted text.
- Stitch `g_i_nh_gi_ph_n_h_i_tripmate_mobile_uc_33/code.html`.
- TEAM_ENGINEERING_RULES v2.2 and Dev_and_CrossReview_Checklist v2.2,
  especially G0-G5, C18/C63, I02, D01/D02/D05, U01/U02/U05 and V01/V02.
- Existing Review entity/configuration and POI detail/explore rating queries.
- Latest fetched BE `origin/develop`: `525489b61ee2a2791b3d0cf71313e80a4a0445e7`.
  It has SQL Bookings, but no Booking entity/mapping/DbSet or moderation adapter.
  It has CSP itinerary provenance and scheduling code.
- TM-79 checkout: `D:\CapStone\Capstone_BE_tm79`, branch
  `feature/datmnt-submit-trip-review`, fast-forwarded to
  `525489b61ee2a2791b3d0cf71313e80a4a0445e7` after approval. See
  `docs/TM-79-verification.md` for baseline status and current evidence gates.
- Current FE/Mobile worktrees contain unrelated uncommitted changes. The
  untracked Mobile `tour_reviews` folder has a summary widget, not submission.
  Client `origin/develop` references were inspected as local references, not
  freshly fetched in this assessment; refetch before client implementation.

Excluded: booking/payment/completion implementation; full Trip History UC-32;
admin moderation console; operator replies; CSP engine changes; new Tour
search/detail rating fields; migration of legacy reviews using invented data.

## 2. Decisions and gates

The canonical D1–D7 rules are in specs/TM-79-spec.md §2. Do not reconstruct
business rules from an older response or duplicate a competing contract here.
C4 inclusion and the staged G-SCOPE are approved. G-POLICY, G-VISITS and
G-LEGACY remain evidence gates for their dependent work; they are not blanket
authorization to guess missing sources or behavior.

| Decision | Execution consequence |
| --- | --- |
| D1 | Separate optional pacing/CSP; authoritative booking-trip route context required for pacing, server-proven CSP provenance for CSP. Distinct capability/reason flags; reject non-null ineligible values; no aggregate mixing. |
| D2 | Approved commerce.Bookings first-delivery subset; Completed booking is completion authority, no added TripSession gate. ServiceBookings and standalone no-booking CSP trips are explicitly deferred, not full UC-33 coverage. |
| D3 | 5,000,000 bytes per image; JSON edit of overall/title/content plus approved D5 display preference; no edit-media/POI/C4 replacement. |
| D4 | Policy-first screening, implementation may be local or external; G-POLICY before Task 7. Test doubles do not pass this gate. |
| D5 | Defined author initials/consent/snapshot behavior; G-VISITS for authoritative historical visit evidence. Unavailable POI capability is not completed POI UAT. |
| D6 | New+legacy duplicate detection; 409/GET recovery; rowversion body token; exclusive original seven-day UTC boundary. |
| D7 | One new parent, only POI children in Reviews; legacy create protection, Tour AND POI aggregate compatibility, and G-LEGACY rollout/edit disposition. |

G-SCOPE is approved and recorded in the spec and Task 0 ledger. G-POLICY may remain
explicitly pending during Tasks 0–6; it cannot be treated as waived.
G-VISITS can block POI acceptance without preventing independently valid
overall-review tests. G-LEGACY requires within-window edit behavior to be
resolved when applicable, not an undocumented read-only fallback.
Track each gate with evidence, responsible owner and disposition.

## 3. Base, persistence and API

Reuse this BE worktree, preserve documents and unrelated edits, inspect the
current branch/worktree and fetch develop before selecting a base. Fast-forward
only when safe; do not rewrite shared history, force push, reset or auto-stash.
If the branch cannot advance safely, report the exact Git situation.
Recheck TM-207 availability before media reuse; previous PR status is historical.
TM-208 thumbnail API is not required for review submission.

Read booking data via ITripReviewContextReader (reuse a suitable mapped model
if now present). Do not add booking/completion/visit-writing workflows.
Review test seeds establish isolated acceptance examples, not upstream UAT.

Proposed objects, finalized in Task 1:
- social.TripReviews: unique booking, owner/derived subject, overall rating,
  title/content, publication/policy metadata, C4 values, author-display
  preference/public snapshot, UTC created/deadline/updated, rowversion.
- social.Reviews: nullable trip_review_id FK and unique linked parent/POI
  rating identity. New child rows are POI only; no duplicate new Tour score.
- Review-owned media and operation journal; validate the relationship between
  linked child author/booking and parent. Binaries stay in Cloudinary.
- Review evidence identifies selected itinerary items/POIs and provenance.
  Freeze evidence shape only after G-VISITS source/version assessment.
- No automatic legacy data repair. Application write locks serialize the
  verified writers; a new-parent unique index cannot protect a legacy table
  writer that ignores that protocol. Audit writers before claiming coverage.

| Route | Proposed behavior |
| --- | --- |
| GET /api/v1/bookings/{bookingId}/review | Owner context; new/legacy/none existing state; capabilities/reasons; eligible POIs/CSP; editable fields; deadline/version and safe owner previews. |
| POST same route | Multipart metadata + optional new files; 201 DTO. Derived author/subject. 409 duplicate followed by owner GET recovery. |
| PUT same route | JSON overallRating/title/content/publishDisplayName/version; 200 DTO. No files or retainedMediaIds. Reject attempted immutable changes. |

Use spec §3 HTTP statuses and freeze stable error codes/DTO discriminators in
Task 1. No client-supplied provider IDs/delivery URLs or author identity.
Create authenticates/reads owned context, performs an EARLY new+legacy duplicate
check, then normalizes/validates input and inspects images through the media
port. It screens those normalized values, journals/uploads, then enters a short
transaction that rechecks ownership/completion, new+legacy duplicates under
lock, route/POI/CSP evidence and atomically adopts assets while saving the same
normalized text. An early check never replaces the transactional recheck.
Network calls remain outside SQL locks.
A failed create reclaims only its own assets; edit has no upload side effects.
Legacy/new Tour and POI aggregate formulas are exactly D7.

## 4. Atomic Backend tasks

Each implementation task begins with focused failing tests, then minimal
implementation and refactor. Do not mark RED complete for missing environment
or undiscovered tests. File names below are planned new files unless existing
paths are explicitly identified. Resolve prerequisite decisions before their
dependent code. Each completed step gets spec-compliance and code-quality
review under AGENTS.md; use a fresh reviewer agent when available.

### Task 0 — Prepare approved base and real test environment

Files: `docs/TM-79-verification.md` (new evidence ledger).
Record explicit spec/plan approval and G-SCOPE disposition before execution.
Re-audit current Review writers, Booking mapping and source dependencies.
Inventory legacy cases on an authorized isolated copy/sample without exposing
personal data; absent access leaves G-LEGACY unverified. Never scan production
or assume an empty integration-test database describes deployed legacy data.
Inventory `git status --short --branch`, `git worktree list`, base/head;
preserve documents, sync the dedicated feature branch, then baseline restore,
Release build and tests. Configure `TRIPMATE_SQLSERVER_TEST_CONNECTION` for an
identified isolated SQL Server; inspect `SqlServerTestDatabase` creation/drop
behavior and record only server/database identity. Do not target shared Azure
or business data. Record all pass/fail/skip counts and existing failures.
Commands: `dotnet restore TripMate.slnx`; `dotnet build TripMate.slnx -c Release
--no-restore`; `dotnet test TripMate.slnx -c Release --no-build --no-restore`.
Done: reproducible baseline and safe SQL target, not merely a TCP connection.

### Task 1 — Freeze scope, DTO and acceptance matrix

Depends: approved D1–D7 recommendations and resolved G-SCOPE.
G-POLICY implementation selection may remain a named later gate.
Files: spec; `docs/TM-79-api-contract.md`; central Docs
`api/contracts/submit-trip-review-api.md` in a dedicated Docs branch.
Specify exact enum wire names, null/empty rules, input lengths/file bounds,
subject identity, supported booking cases, eligible POIs, C4 semantics,
create multipart versus edit JSON, public-name snapshot/initials behavior,
version/duplicate recovery including legacy data, error codes and both
Tour/POI legacy+new aggregate formulas. Specify per-file/total/metadata byte
limits, immutable-field rejection and POI capability-unavailable semantics.
Freeze route-pacing source/version provenance separately from Visited evidence:
TourSchedule without an authoritative route snapshot is pacing-unavailable;
owned itinerary context must be attributable to this booking's trip.
Freeze request-scoped strict PUT unmapped-member handling: unknown and
create-only/immutable members, even null/empty/unchanged, return 400 with
trip_review.invalid_edit_payload. Keep unrelated endpoint serializers intact.
Freeze normalization -> validation -> moderation -> persistence of identical
normalized title/content, and early-duplicate ordering before external work.
Define decoded image dimension/pixel/memory limits using existing safe media
configuration where available; record explicit limits before inspector tests.
Map each requirement to tests in Tasks 2-13 and both clients. Date display
uses Asia/Ho_Chi_Minh; deadlines and timestamps use UTC.
Check: manually compare contract matrix to SQL/domain/DTO proposals and the
approved decisions; `git diff --check` plus untracked Markdown whitespace scan.
Done: no unspecified business rule reaches schema or public API implementation.
G-VISITS must identify the evidence storage/read contract before dependent
schema is frozen; if unavailable, stop affected schema decisions rather than
invent a snapshot. G-LEGACY edit behavior is a recorded acceptance gate.

### Task 2 — Migration contract tests (RED)

Files: `tests/TripMate.Api.IntegrationTests/Reviews/TripReviewMigrationTests.cs`;
`tests/TripMate.Api.IntegrationTests/Fixtures/Database/tm79_pre_migration_schema.sql`.
Reuse the existing Tour migration inventory normalizer, extracting a shared
`Infrastructure/SqlSchemaInventory.cs` only if needed. Fixture reflects the
actual chosen baseline, including nonempty legacy Tour/POI/other Reviews,
booking-linked and unlinked rows, repeated author/subject across bookings,
and ambiguous legacy rows that must be preserved, not automatically repaired.
Test fresh/upgrade full affected inventory parity, rerun, absent/correct/wrong
shape, rollback on preflight failure, preservation of data/identity/FKs,
unique parent per booking, rating ranges, independent C4 constraints, nullable
legacy linkage, linked-child POI-only constraint and parent/POI uniqueness,
media lifecycle and delete restrictions for historical rows.
Run: `dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj
-c Release --filter "FullyQualifiedName~TripReviewMigrationTests"`.
Done: discovered SQL tests fail specifically for missing TM-79 objects/rules.

### Task 3 — SQL migration (GREEN)

Files: `database/migrations/20260927_add_trip_reviews.sql` (verify unused name
and adjust date/order at execution); `database/tripmate_schema_v7.sql`;
`database/README.md`. No EF migrations.
Implement approved schema in a guarded transaction; validate prior shape;
fail with actionable preflight errors rather than rewriting legacy data.
Migration cannot fabricate booking associations/title/publication of old rows.
Run Task 2 command. Done: parity diff empty, rerun and preservation pass,
wrong-shape failure leaves original DB intact.

### Task 4 — Domain and EF persistence

Files: `src/TripMate.Domain/Entities/TripReview.cs`, `TripReviewMedia.cs`,
`TripReviewMediaOperation.cs`; `Enums/RoutePacingFeedback.cs`; matching
Infrastructure `Persistence/Configurations/*Configuration.cs`; existing
Review entity/config, `IApplicationDbContext.cs`, `ApplicationDbContext.cs`
and test contexts only where required.
Tests: `tests/TripMate.Application.UnitTests/Domain/TripReviewTests.cs`;
`tests/TripMate.Api.IntegrationTests/Reviews/TripReviewPersistenceTests.cs`.
Guard independent scores, original deadline, public-name snapshot/preference
and child-parent author/booking consistency; insert parent/children via
navigation; UTC converters; version conflicts; retain legacy Review factories.
Run the two project filters for `TripReviewTests` and `TripReviewPersistenceTests`.
Done: new-context SQL round-trip proves IDs, UTC, separate C4 values and version.

### Task 5 — Read authorized review context

Files: Application `Features/TripReviews/Common/ITripReviewContextReader.cs`,
`TripReviewContextDto.cs`, `TripReviewErrorCodes.cs` and
`GetContext/GetTripReviewContextQuery.cs`, `GetTripReviewContextQueryHandler.cs`;
Infrastructure `Persistence/SqlServerTripReviewContextReader.cs` and DI.
Tests: Application `Features/TripReviews/GetTripReviewContextTests.cs`;
API integration `Reviews/TripReviewContextSqlServerTests.cs`.
Query only required booking/subject/itinerary data; enforce Active Traveler,
owner and approved identity/visited-stop rules; derive separate route-pacing
and CSP eligibility on server. Test schedule-only with/without authoritative
route snapshot, valid linked itinerary, foreign/stale/unverifiable context,
and absence of Visited data not automatically disabling valid route context.
Return new/legacy/none explicitly; old booking-linked rows block new create.
Map authoritative Visited status through a scoped query if no domain mapping
exists; planned Kind=Visit is never visit evidence. Persist no fake visits.
Missing visit workflow is G-VISITS, not a passing POI acceptance case.
Existing review remains readable after deadline. Invalid source links must
not leak foreign data or silently choose a target. New read models remain read-only.
Run project filters `FullyQualifiedName~TripReviewContext` and
`FullyQualifiedName~GetTripReviewContext` as applicable.
Done: SQL fixtures cover every supported/unsupported identity and C4 eligibility.

### Task 6 — Validators and provider-independent content policy

Files: Application `Features/TripReviews/Submit/SubmitTripReviewCommand.cs`,
`SubmitTripReviewCommandValidator.cs`; `Edit/EditTripReviewCommand.cs`,
`EditTripReviewCommandValidator.cs`; `Common/IReviewContentModerator.cs`.
Tests: Application `Features/TripReviews/TripReviewValidationTests.cs` and
`TripReviewModerationPolicyTests.cs`.
Application scope: normalize/trim once, validate resulting UTF-16 length,
1/5 boundaries, separate pacing/CSP enum/null/eligibility, duplicate POIs,
photo count <= 5, declared/raw length <= 5,000,000 bytes, metadata structure,
and forbidden client provider URL/public ID. No image decoding, magic-byte
parsing or image/provider SDK in Application validators. HTTP body/metadata
limits belong to the endpoint; actual bounded-byte/decode inspection is Task 8.
Test Unicode initials and explicit public-name consent. Application commands
contain only editable fields; strict wire-member rejection is Task 12.
For create/edit assert normalization returns the same values supplied to the
moderator, including padded and all-whitespace cases. Tasks 9/10 subsequently
prove actual persisted values match. Define accepted/rejected/unavailable
results and cancellation; no screening call for unauthorized/invalid input.
Run Application project `--filter "FullyQualifiedName~TripReviewValidation|FullyQualifiedName~TripReviewModerationPolicy"`.
Done: approved bounds/errors covered; test doubles explicitly limited to tests.

### Task 7 — Approved content-policy implementation

Depends: G-POLICY resolved with policy/version/categories/languages,
acceptance corpus and implementation choice. An external provider is optional.
Files: Infrastructure Reviews/Moderation implementation/options/DI;
Infrastructure tests Reviews/ReviewModerationPolicyImplementationTests.cs.
Add placeholder configuration only when the chosen implementation needs it.
7a: RED tests using approved allow/reject examples including legitimate
negative reviews, Vietnamese/English variants and malformed/ambiguous results.
7b: minimal approved implementation; policy version/result mapping.
7c: unavailable/cancel/timeout behavior where applicable, no automatic approval
on missing configuration, no raw review text/secrets in logs.
Run Infrastructure project --filter "FullyQualifiedName~ReviewModeration".
Done: real implementation passes the approved corpus; external implementation
also has opt-in provider smoke evidence. Test-only doubles cannot complete this
task. If policy remains unspecified, report this precise gate and stop dependent
publication work.

### Task 8 — Review-owned image upload and crash recovery

Depends: media decisions and TM-207 code availability assessed in Task 0.
Files: Application `Features/TripReviews/Media/IReviewMediaStorage.cs`,
`IReviewImageInspector.cs`, `ReviewMediaCoordinator.cs`;
Infrastructure `Reviews/Media/ReviewImageInspector.cs`,
`Media/Cloudinary/CloudinaryReviewMediaStorage.cs`,
`Reviews/ReviewMediaCleanupWorker.cs`, review operation mapping/store and DI.
Tests: Application `Features/TripReviews/ReviewMediaCoordinatorTests.cs`;
Infrastructure `Media/CloudinaryReviewMediaStorageTests.cs`;
Infrastructure `Reviews/ReviewImageInspectorTests.cs`;
API `Reviews/TripReviewMediaSqlServerTests.cs`.
Execute in small verified substeps:
8a: infrastructure image-inspection port implementation and tests: actual
bounded stream size, magic bytes, complete decode, MIME/extension/content
consistency, JPEG/PNG/static WebP, animated WebP and corrupt/known polyglot
fixtures rejected; dimension/pixel/decoded-memory resource limits as frozen
in Task 1. Use the chosen decoder's supported validation; do not claim a
universal polyglot-detection guarantee from a file signature check.
Application orchestrates the inspector via a port; it does not decode.
8b: real-SQL operation journal/adoption tests and minimal persistence.
8c: provider upload/reconcile/cleanup tests and implementation.
8d: isolated real-provider smoke and residue check.
Reuse Task 6 structural validation; enforce actual supported image content and
resource bounds at this media boundary before upload, allocate review-owned IDs,
track pending uploads durably, never accept arbitrary URL/public ID, reconcile
timeouts/crashes, protect adopted assets from cleanup races, and scope deletion
to assets owned by that failed operation. Reuse provider plumbing where safe;
do not generalize or change TourMedia business rules.
Run corresponding project filters for all four test classes above.
Done: partial batch/provider/DB/crash cases reclaim assets with durable retry;
real Cloudinary smoke covers upload/readability/cleanup in a dedicated test folder.

### Task 9 — Submit transaction and duplicate recovery

Files: Application `Submit/SubmitTripReviewCommandHandler.cs`;
`Common/ITripReviewWriteLock.cs`; Infrastructure `Persistence/SqlServerTripReviewWriteLock.cs`.
Tests: Application `Features/TripReviews/SubmitTripReviewTests.cs`;
API `Reviews/SubmitTripReviewSqlServerTests.cs`.
9a: handler validation/orchestration RED -> GREEN.
9b: real-SQL new+legacy duplicate and rollback RED -> GREEN.
9c: independent-context races, response-loss recovery and asset cleanup.
Integrate authenticate/context -> early new+legacy duplicate check -> normalized
input validation/media inspection -> moderation -> journal/upload -> final
locked transaction. Verify ordering with zero moderation, inspection, journal
allocation and upload calls for an already-reviewed booking, new or legacy.
Persist exactly the normalized values passed to moderation; recheck eligibility
in the final transaction. Save one parent, target-rating rows and adopted media
atomically. Deterministic duplicate handling maps only the expected unique
violation; never convert arbitrary DB errors to duplicate success.
Check unlinked legacy Reviews.booking_id as well as the new parent under
one booking-scoped write protocol. Old reviewed bookings must not reach
moderation/upload on the normal early duplicate path. Audit and serialize all
supported booking-linked review writers; document SQL-writer assumptions.
Test ambiguous/mismatched legacy records fail safely without rewriting them.
Use barriers so two requests pass the early duplicate check before either
commits; the final lock/recheck admits only one and cleans up loser-owned media.
Use independent contexts for simultaneous submits. Inject a
child failure after SQL parent insert; verify full rollback from a new context.
Simulate lost response after commit: GET returns the one existing review;
409 does not imply that a different submitted payload was accepted.
Run Application/API project filters `FullyQualifiedName~SubmitTripReview`.
Done: one review survives a race, no orphan business rows or loser-owned assets.

### Task 10 — Edit transaction and deadline/version boundaries

Files: Application `Edit/EditTripReviewCommandHandler.cs`;
Application `Features/TripReviews/EditTripReviewTests.cs`;
API `Reviews/EditTripReviewSqlServerTests.cs`.
Use controlled clock at before/equal/after deadline and recheck after provider
latency. Keep initial submission time/deadline immutable. Reject stale version
and preserve old publication/media on rejection/outage/rollback. Under D3,
only overall rating/title/content and the approved D5 display preference are
editable. Media, child evaluations and C4 remain unchanged. Cover preference
changes, ordinary edit snapshot preservation, and account rename behavior.
Verify padded input is normalized once, screened and persisted identically;
failed screening never changes the previous publication.
Resolve G-LEGACY before claiming still-in-window legacy reviews are editable;
unknown old fields cannot be invented to force them through the new DTO.
Run Application/API project filters `FullyQualifiedName~EditTripReview`.
Done: competing edits cannot silently overwrite; deadline, public identity
and unchanged-media behavior pass. No untested legacy-edit promise.

### Task 11 — Published aggregates and legacy compatibility

Files: Application `Features/TripReviews/Common/TripReviewAggregateDto.cs`,
query/reader implementation as required; existing POI Detail/Explore handlers
only if publication/linkage requires a filter adjustment.
Tests: API `Reviews/TripReviewAggregateSqlServerTests.cs` plus existing POI tests.
Use the D7 formulas exactly: new Published TripReviews + unlinked legacy
Tour rows; unlinked legacy POI rows + linked POI rows with Published parent.
Do not create or count a second Tour mirror row. Itinerary-only overall scores
do not contribute to Tour average. Other legacy target types are excluded.
Test legacy-only/new-only/mixed datasets, linked child exclusion from legacy,
different bookings by the same author, same-booking overlap detection,
edit count stability and rollback. No blanket DISTINCT or fabricated legacy
moderation history. Existing legacy contributions remain unchanged.
Empty average=null/count=0; reuse existing POI rounding.
Return agreed aggregate DTOs; do not extend TM-70 or add unrelated endpoints.
Run API filters "FullyQualifiedName~TripReviewAggregate|FullyQualifiedName~PointsOfInterest".
Done: SQL-backed exact counts/averages match the approved legacy+new contract.

### Task 12 — HTTP endpoints and OpenAPI

Files: `src/TripMate.Api/Controllers/V1/TripReviewsController.cs`;
`Requests/SubmitTripReviewRequest.cs`, `EditTripReviewRequest.cs`;
OpenAPI configuration only where required.
Tests: API `Reviews/TripReviewEndpointTests.cs`, `TripReviewOpenApiTests.cs`.
Thin controller uses normal Result/HandleFailure and real auth path. Verify
create multipart and JSON-only edit, file/metadata/body limits before expensive
work, reject immutable-field writes, name-preview/public-response separation,
legacy context/duplicate recovery, token failures/role/owner,
duplicate/version/deadline status, nullable C4/media/context fields, request
cancellation and safe failure bodies. Record created/read/edit examples.
Implement strict unmapped-member handling scoped to TM-79 Edit request/endpoint,
not global JSON options. Test real HTTP requests with valid baseline fields plus
an unknown field or each create-only/immutable field; null/empty/unchanged values
must still produce 400 trip_review.invalid_edit_payload, no screening and no
write. A valid JSON edit succeeds; serializer regressions on unrelated routes
are excluded by scoped configuration. Authentication precedes business details.
Run API filter `FullyQualifiedName~TripReviewEndpoint|FullyQualifiedName~TripReviewOpenApi`.
Done: runtime/Docs/OpenAPI contract matrix agrees; no fake-auth-only acceptance.

### Task 13 — Backend regression and reproducible handoff

Files: verification ledger; `docs/TM-79-smoke-test.md`; test-only owned fixture
under `tests/TripMate.Api.IntegrationTests/Fixtures/Database/` when needed.
Run Release build, format check, focused SQL tests with zero unexpected skips,
full solution tests and `git diff --check`. Record exact commands/exit/counts.
Run create -> new-context read -> edit -> aggregate with SQL and configured
providers, including C4 eligible/ineligible records and cleanup verification.
Record G-SCOPE/G-POLICY/G-VISITS/G-LEGACY dispositions; skipped/mocked source
workflows cannot be reported as real POI, legacy or publication readiness.
Self-review source/SQL/docs and limits, then independent review. BE can be
ready for review at this point; full TM-79 still needs Tasks 14-17.

## 5. Client delivery tasks

Use clean dedicated Mobile/FE worktrees on fetched develop; preserve existing
dirty work. Read each repo's current instructions and baseline before edits.
Write colocated client spec/plan against the frozen BE contract; the paths
below are planned feature boundaries and must be reconciled with new shared
auth/history changes before coding. Each substep is independently testable.

### Task 14 — Mobile contract and state

Root: `lib/features/trip_review/`, corresponding `test/features/trip_review/`.
14a: domain review/context/submission models and repository/use cases; data
models and centralized Dio datasource; parser/transport tests for null C4,
multipart media, direct DTO and ProblemDetails.
14b: `presentation/cubit/trip_review_cubit.dart`, state and tests covering
loading/editing/submitting/read-only, preserved input, duplicate GET recovery,
unknown outcome, stale version, provider rejection and dispose/cancel.
Run: `flutter test test/features/trip_review` after each substep.
Done: real contract consumed; duplicate and unknown-outcome actions are distinct.

### Task 15 — Mobile UI and navigation

Root: `lib/features/trip_review/presentation/pages/trip_review_page.dart` and
widgets; existing `lib/app/router/app_routes.dart`, `app_router.dart`,
`lib/core/di/service_locator.dart`.
15a: map Report 3/Stitch to form: overall stars, title/content, POI stars,
separate pacing selector and CSP stars, photos, explicit name consent/preview,
submit/cancel and field-level read-only. Render POI capability-unavailable
separately from a verified trip with no visited POIs. Edit UI matches JSON DTO.
Use server context, no prefilled demo ratings/POIs/name/identity promises.
15b: protected direct route and UC-32 entry/return when the real history route
is available; mark dependency if unavailable, never claim full flow complete.
Run focused widget/route tests, `dart format --output=none --set-exit-if-changed .`,
`flutter analyze`, `flutter test`, `flutter build apk --debug` for Android.
Done: 320/360/390/430 logical-pixel checks, keyboard/text scale/accessibility,
real backend smoke and screenshots for create/edit/errors/C4 applicability.

### Task 16 — Web integration and responsive screen

Root: `src/features/traveler/trip-review/` with typed model, parser/API service,
form state and components; proposed route `app/bookings/[bookingId]/review/page.tsx`;
proposed BFF `app/api/bookings/[bookingId]/review/route.ts` if existing auth uses
a BFF. Resolve Traveler session contract first; do not repurpose admin cookies.
16a: protected transport/parser and proxy tests, no-store private responses,
version/legacy-duplicate handling, multipart create limits and JSON-only edit;
secrets remain server-side.
16b: responsive form with the same fields/rules as Mobile, runtime schema
validation, retained input, image previews/errors and accessible controls.
16c: real Traveler entry/return with UC-32, session expiry and unknown-outcome
recovery. If no history/session integration exists, delivery remains gated.
Run focused Vitest/Node tests using actual package scripts, then `npm run
typecheck`, `npm run lint`, `npm test`, `npm run build` and browser smoke.
Done: verified 320/390/768/1024/1440 widths and BE -> BFF -> UI -> DB evidence.
Do not label a local prototype or dev-server-only run as production readiness.

### Task 17 — Cross-repo UAT and review package

Record all repository SHAs, API origin, isolated DB identity and provider test
configuration without credentials. Use the same sanitized valid payloads in
both clients. Cover owned Completed trip; foreign/non-completed trip; C4
eligibility; null/absent optional values; invalid image; content rejection;
double submit; lost response after commit; existing new/legacy review;
legacy+new Tour/POI totals; missing visit evidence; author name preference;
immutable edit fields; edit deadline;
stale edit; provider outage; aggregate and image visibility after save.
Refresh from a new session/context to prove persistence and verify cleanup.
Trace each AC to test or manual evidence; any dependency has an owner and
explicit acceptance disposition. Independent review and CI use final heads.
Done: full TM-79 only when BE + both clients + entry/return + actual approved
policy/storage implementations and all source-scope gates meet the approved AC.
A commerce-only delivery is reported as that subset, not full Report 3 coverage. Commit/push/PR/merge follow the owner's delivery instruction.

## 6. Execution and verification conventions

Backend focused commands use the actual test project from the task:

```powershell
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --filter "FullyQualifiedName~TripReview"
dotnet test tests/TripMate.Infrastructure.UnitTests/TripMate.Infrastructure.UnitTests.csproj -c Release --filter "FullyQualifiedName~Review"
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~TripReview"
dotnet format TripMate.slnx --verify-no-changes --no-restore
dotnet build TripMate.slnx -c Release --no-restore
dotnet test TripMate.slnx -c Release --no-build --no-restore --logger "console;verbosity=minimal"
git diff --check
git status --short --branch
```

Before relying on a focused filter, verify discovery includes the task's named
classes; `ReviewMedia`/`ReviewModeration` filters must be added for those tasks.
Never count skipped SQL/provider tests as executed. Full suite runs at baseline
and delivery, and when shared changes warrant them; scoped gates run between
atomic steps. Docs-only planning needs content/path/whitespace review, not a
fabricated application test run.

Progress: **Task 0 IN PROGRESS**.
Spec/plan v3 and G-SCOPE are approved.
The isolated SQL Server has been verified and the full baseline completed on
2026-09-28. Task 0 remains blocked on three full-suite log-file-lock failures
(the same three tests pass in a fresh focused run). See the verification ledger.
Tasks 1-17 have not started; do not advance to Task 1 until Task 0 meets its
definition of done.
The handoff prompt is docs/TM-79-implementation-prompt.md; its existence or
use does not itself mark proposals approved.
