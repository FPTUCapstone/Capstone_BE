# TM-79 — UC-33 implementation plan

Status: **R1–R7 COMPLETE / COMMITTED LOCALLY — R9 COMPLETE IN THIS LOCAL
CHECKPOINT — R8 CONTRACT DEPENDENCY FROZEN**

Spec: `specs/TM-79-spec.md`. Prepared 2026-09-27, Asia/Ho_Chi_Minh.
Owner: Mai Nguyen Tien Dat. Actor: authenticated Active Traveler.
Delivery: Backend, Flutter Mobile, responsive Next.js Web and API contract docs.

## R1 remediation baseline (owner approved 2026-10-07)

This section controls the remediation sequence. It supersedes older statements
below that limit delivery to `commerce.Bookings`, defer BR-94 screening, select
an external moderation dependency, or treat `poiRatings[]` as the canonical POI
path. The older Task 0–17 plan remains as dated evidence for the already-built
commerce/media subset; it is not the current task order where it conflicts with
R1.

Canonical source: `Report3_Software-Requirement-Specification-V2.docx`. The
owner confirmed that UC-32 and UC-33 are unchanged from the previous Report 3,
so V2 introduces no unresolved artifact delta. The requirement classifications,
typed identity, policy, database direction, aggregates and client handoff are
frozen in `specs/TM-79-spec.md` and `docs/TM-79-api-contract.md`.

### Remediation sequence

| Task | Scope | Current checkpoint |
| --- | --- | --- |
| R1 | Freeze the authoritative requirement, identity, route, subject, summary, policy, error, schema, aggregate and UC-32 handoff contracts. Documentation only. | **COMPLETE; committed locally at `3e2684f`** |
| R2 | Implement deterministic local `tm79-review-text-v1` evaluation with `Accepted`, `Rejected`, `Unavailable`; Vietnamese/English/mixed support; a publication-screening capability for later R5 Submit/Edit orchestration; fail-closed behavior; safe logging. Begin with RED tests. | **COMPLETE; committed locally at `39d9241`** |
| R3 | Extend `social.TripReviews` using Option A: nullable typed parent FKs, canonical POI subject, check constraints, separate filtered uniqueness and namespace-safe locking. Begin with migration/inventory RED tests. | **COMPLETE; committed locally at `a286886`** |
| R4 | Extend the owner context with `ReviewableRecordRef`, typed subject and server-derived summary for commerce and service bookings, including null-POI unsupported behavior. Begin with RED tests. | **COMPLETE; committed locally at `5cecea2`** |
| R5 | Add distinct ServiceBooking GET/POST/PUT routes and align commerce routes with the shared typed application flow, duplicate recovery and seven-day lifecycle. Begin with RED tests. | **COMPLETE; committed locally at `e9e4cce`** |
| R6 | Implement canonical POI aggregate reads with safe non-overlapping legacy contributions; retain canonical Tour aggregate behavior and explicit overlap handling. Begin with RED tests. | **COMPLETE; committed locally at `dea9bea`** |
| R7 | Integrate the approved optional Tour/itinerary compatibility behavior without inventing standalone itinerary identity or aggregate semantics. Begin with RED tests. | **COMPLETE; committed locally at `dea9bea`; no standalone itinerary route/aggregate added** |
| R8 | Preserve only the TM-79 contract dependency: UC-32 supplies `ReviewableRecordRef`, `canReview` and existing-review state. Full UC-32 implementation remains TM-78-owned. | **CONTRACT DEPENDENCY FROZEN; FULL UC-32 OUTSIDE TM-79** |
| R9 | Reconcile contract/verification documents and run the full backend compliance matrix after R2–R7. | **COMPLETE; included in the atomic local R9 checkpoint commit** |

Mobile and Web are frozen during backend remediation. Record their downstream
contract changes but do not edit either client until separately authorized.
PR #30 is unchanged throughout Backend remediation.

### Backend remediation closure checkpoint

```text
R1 COMPLETE
R1 COMMITTED LOCALLY

R2 COMPLETE
R2 COMMITTED LOCALLY
R3 COMPLETE / COMMITTED LOCALLY
R4 COMPLETE / COMMITTED LOCALLY
R5 COMPLETE / COMMITTED LOCALLY
R6 COMPLETE / COMMITTED LOCALLY
R7 COMPLETE / COMMITTED LOCALLY
R8 CONTRACT DEPENDENCY FROZEN
R8 FULL UC-32 IMPLEMENTATION NOT PART OF TM-79
R9 COMPLETE / COMMITTED LOCALLY IN THIS CHECKPOINT

Mobile FROZEN
Web FROZEN
PR #30 UNCHANGED
```

### Frozen execution rules for R2–R9

- Identity is `ReviewableRecordRef { kind, id }`, with allowed current kinds
  `commerceBooking` and `serviceBooking`; never use a bare ID across namespaces.
- Commerce uses `/api/v1/bookings/{bookingId}/review`; service uses
  `/api/v1/service-bookings/{serviceBookingId}/review` for GET/POST/PUT.
- Commerce resolves a Tour or supported authoritative booking-linked Itinerary;
  service resolves a POI only through `Services.poi_id`. Null POI is
  `409 trip_review.unsupported_subject`; no inference or backfill is in TM-79.
- Only `Accepted` local policy outcomes publish. Re-screen edits when normalized
  written content changes or the stored policy version is not active. Rejected
  and unavailable decisions do not mutate accepted state or log raw text.
- Option A extends `social.TripReviews`; require one typed parent and one aligned
  subject, separate filtered uniqueness and namespace-safe locks.
- Tour and POI aggregates combine published canonical contributions with safe
  non-overlapping legacy contributions. On a same-booking canonical/legacy
  overlap, the canonical record wins, the legacy contribution is excluded and
  a mandatory internal integrity warning is emitted. Never hide legitimate
  equal-valued rows with `DISTINCT` or create legacy mirror rows.
- Standalone itinerary review, `cspRating`, and `poiRatings[]` as a canonical POI
  review are deferred. Route pacing and public-display controls remain optional
  extensions. Full UC-32 remains a TM-78 dependency.

### Future RED contract inventory

Before production changes, the responsible remediation task must add failing
tests for commerce/service typed references, service routes, POI subject and
summary, null-POI unsupported subject, rejected/unavailable moderation,
changed/outdated-policy edit re-screening, stale edit, duplicate recovery,
same-number cross-namespace IDs, separate uniqueness, and Tour/POI canonical +
legacy aggregate overlap/edit/rollback behavior.

## Historical v3 plan below

The remaining sections preserve the staged 2026-09-27 through 2026-10-04 plan
and execution record. Treat commerce-only and moderation-deferral language as
historical when it conflicts with the R1 remediation baseline above.

## 1. Scope conclusion and current evidence

TM-79 is a write workflow, not a rating widget. It includes owned/completed-trip
eligibility, one canonical trip-review submission per supported booking identity,
with one TripReview parent per supported commerce.Bookings booking_id,
overall stars, title and
content, optional POI ratings and photos, publication,
seven-day editing, aggregate reads, both clients and end-to-end verification.
The owner explicitly retained route pacing and CSP quality as separate inputs.
Report 3 BR-94 pre-publication content screening is intentionally deferred by
the owner for current delivery; this is a documented source-scope deviation.

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
C4 inclusion and the staged G-SCOPE are approved. G-POLICY is deferred for
current delivery; G-VISITS and G-LEGACY remain evidence gates for their work.
These decisions are not blanket
authorization to guess missing sources or behavior.

| Decision | Execution consequence |
| --- | --- |
| D1 | Separate optional pacing/CSP; authoritative booking-trip route context required for pacing, server-proven CSP provenance for CSP. Distinct capability/reason flags; reject non-null ineligible values; no aggregate mixing. |
| D2 | Approved commerce.Bookings first-delivery subset; Completed booking is completion authority, no added TripSession gate. ServiceBookings and standalone no-booking CSP trips are explicitly deferred, not full UC-33 coverage. |
| D3 | 5,000,000 bytes per image; JSON edit of overall/title/content plus approved D5 display preference; no edit-media/POI/C4 replacement. |
| D4 | Owner deferred Report 3 BR-94 automated/AI pre-publication screening from current delivery on 2026-09-28. G-POLICY = DEFERRED FOR CURRENT DELIVERY / FUTURE ENHANCEMENT, not a current blocker. Task 7 deferred; prior policy/corpus/External research retained for future only. No production provider/model selected. |
| D5 | Defined author initials/consent/snapshot behavior; G-VISITS for authoritative historical visit evidence. Unavailable POI capability is not completed POI UAT. |
| D6 | New+legacy duplicate detection; 409/GET recovery; rowversion body token; exclusive original seven-day UTC boundary. |
| D7 | One new parent, only POI children in Reviews; legacy create protection, Tour AND POI aggregate compatibility, and G-LEGACY rollout/edit disposition. |

G-SCOPE is approved and recorded in the spec and Task 0 ledger. The later
2026-09-28 owner decision supersedes the prior partial G-POLICY approval **for
current delivery only**: automated screening is deferred and no AI key,
provider implementation, paid moderation API or provider-specific config is
required for the current submit/edit path. This knowingly departs from Report 3
BR-94. The approved policy and candidate research remain future evidence in
the decision record, not current publication acceptance criteria. Re-activation
requires a new owner decision; Task 7 no longer gates Tasks 8–17 solely on
moderation.
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
port. It journals/uploads where applicable, then enters a short
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

### Historical Task 1 — Freeze scope, DTO and acceptance matrix

Depends: approved D1–D7 recommendations and resolved G-SCOPE.
G-POLICY is a deferred future gate, not a current Task 1 blocker.
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
Freeze normalization -> structural validation -> persistence of identical
normalized title/content, and early-duplicate ordering before media work.
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

Owner approved staged execution on 2026-09-28: **Task 2a** covers only the
canonical parent, unchanged legacy Reviews, rating/C4, unique booking,
rowversion and full affected inventory parity. **Task 2b** retains the
child/visit-evidence/media lifecycle contract after its prerequisites are
resolved. The parent-only migration is `20260928_add_trip_review_parent.sql`.
Completing 2a does not complete original Task 2 in full.

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

Approved staged **Task 3a** implements only the parent tested by Task 2a.
It must not alter legacy Reviews, add child linkage or invent visit/media
evidence tables. Remaining Task 3b depends on Task 2b and its approved decisions.

Files: `database/migrations/20260927_add_trip_reviews.sql` (verify unused name
and adjust date/order at execution); `database/tripmate_schema_v7.sql`;
`database/README.md`. No EF migrations.
Implement approved schema in a guarded transaction; validate prior shape;
fail with actionable preflight errors rather than rewriting legacy data.
Migration cannot fabricate booking associations/title/publication of old rows.
Run Task 2 command. Done: parity diff empty, rerun and preservation pass,
wrong-shape failure leaves original DB intact.

### Task 4 — Domain and EF persistence

Owner approved staged Task 4a on 2026-09-28: implement only the canonical
TripReview parent and RoutePacingFeedback, parent EF configuration/DbSets,
domain invariants and real SQL persistence/concurrency tests. Task 4b retains
children/media/operation entities and consistency/navigation tests after
Tasks 2b/3b and their evidence gates. Task 4a does not complete original Task 4.
No API, moderation implementation or POI/media workflow belongs to Task 4a.

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

Owner requested one-pass implementation of the presently verifiable read
context on 2026-09-28 (Task 5a). This includes authentication/ownership,
commerce.Bookings identity, parent/legacy state, exclusive deadline/version,
current-profile preview and owned CSP request provenance. Route pacing and
POI capabilities stay unavailable at this base: no booking-attributed historical
route version/visit evidence contract exists. No endpoint is exposed here.
Task 5b retains positive historical route/visit cases, child/media reads and
the unresolved legacy ambiguity/edit disposition. Do not claim full Task 5
or full UC-33 UAT from this stage. Tasks 6+ are not authorized by this step.

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

### Task 6 — Structural validators (dormant moderation port retained)

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
For create/edit assert normalization handles padded and all-whitespace cases.
Tasks 9/10 subsequently prove persisted values match the normalized values.
Existing accepted/rejected/unavailable and cancellation port tests are
historical/future-capability tests, not current-delivery screening evidence.
Run Application project `--filter "FullyQualifiedName~TripReviewValidation|FullyQualifiedName~TripReviewModerationPolicy"`.
Done: approved bounds/errors covered; test doubles explicitly limited to tests.

Execution subdivision (2026-09-28; no business-rule change): Task 6a contains
the normalized command values, structural validators, server-context optional
field checks and provider-independent moderation port/result contract. It
does not introduce a screening orchestrator or register a production moderator.
Current-delivery Task 6 still needs Tasks 9/10 to verify ordinary validation,
duplicate-first ordering and exact persisted text, **not** moderator execution
or zero-call instrumentation. The dormant port and test-only RecordingModerator
must remain unregistered and unused by current handlers; its tests do not
prove current publication behavior. HTTP member rejection and size binding
remain Task 12, decode remains Task 8. Task 6b below reconciles the parent
policy invariant; it does not complete handler integration.

### Task 6b — Current-delivery publication invariant reconciliation

Owner explicitly authorized this subdivision on 2026-09-29. Status: COMPLETE.
Focused GREEN: 49 domain + 36 SQL tests. Full SQL-enabled regression: 1232
passed, 0 failed, 0 skipped, exit 0. Independent reviewer supplied an initial
clean code/schema assessment but hit quota before final review completion;
two-pass self-review completed. See verification ledger for that limitation.
Only domain/EF/SQL policy nullability and focused tests are in scope.
`PolicyVersion = null` means unscreened publication; a non-null version may
only describe an actual applied policy for that exact text. Domain rejects
blank/unnormalized non-null versions and preserves genuine versions exactly.
An unscreened edit clears the earlier text's acceptance. SQL rejects empty
and every .NET whitespace-only value while allowing NULL. No fake default,
public DTO field, moderator registration or handler is introduced.

Files: TripReview entity/configuration; canonical SQL and existing parent
migration; TripReviewTests, TripReviewPersistenceTests, TripReviewMigrationTests;
the frozen pre-nullable parent fixture and test-project asset mapping.
The existing full schema inventory reader remains applicable: assert nullable
policy shape, compare fresh/upgrade inventories, preserve existing values and
rowversions, and reject wrong shapes without repair. The known pre-deferral
parent may upgrade only after its complete canonical inventory is verified;
incompatible stored values fail validation and roll back without rewriting.

Verification: RED then GREEN on the three named test classes with isolated
SQL enabled, scoped format/verify, Release build, full solution regression,
database/log cleanup and independent spec/code review. Record final counts
and status in `docs/TM-79-verification.md`. Task 7 remains deferred; Tasks
8/9/10/12 are not started by this subdivision.

### Historical Task 7 — external/AI policy direction (superseded by remediation R2)

Not required for current delivery; does **not** block Tasks 8–17 solely on
moderation. No current AI call, provider key/config or paid moderation API.
Report 3 BR-94 screening is a documented scope deviation, not silently met.
Task 7 NOT STARTED. The policy/corpus, External direction and provider
research below are retained **only as future implementation notes**.

Future re-activation depends on a new owner decision and G-POLICY resolution.
Policy/version/categories/languages/corpus
and External direction are approved; the exact provider/model or stable
contract, capability evidence, provider-specific permission/data handling,
safe credentials/configuration, request/response/error mapping and availability/
operational evidence require resolution and owner approval. Local is not
selected. Provider research/selection is suspended for the current delivery;
it requires a new owner decision as future work, not Task 7 implementation now.
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
future task. Provider-specific gate items remain unresolved; stop Task 7 until
separately re-approved, but do not stop current publication solely for this.

### Task 8 — Review-owned image upload and crash recovery

Owner-approved 8c/8d continuation (2026-09-29): R1–R12 are recorded verbatim
in the spec. Base is local commit 3a0ee58, not amended. A later fresh fetch
succeeded and confirmed origin/develop remained
e8013f98a49b9fa1fbe4fcc412b562f7db8c37eb. Delivery/merge reconciliation is
still a separate later step.

Execution sequence, without ordinary intermediate approval stops:
1. Recovery migration tests RED: complete inventory, parity, rerun, invalid
   shape rollback and existing 8b row preservation; guarded additive SQL GREEN.
2. Review storage adapter RED/GREEN using inspected TM-207 provider plumbing,
   review namespace and safe outcome classifications; no Tour business rules.
3. Coordinator RED/GREEN: inspect entire batch, reserve, fenced upload outside
   transactions, record current-fence success, all-or-nothing preparation.
4. Scanner/worker RED/GREEN: complete abandoned batch, fenced claims, retries,
   exhaustion, positive cleanup confirmation and new-context SQL verification.
5. Deterministic late-upload/stale-worker/adoption race tests; focused GREEN.
6. Format/build, real development Cloudinary smoke with absence verification,
   full SQL-enabled regression, fresh independent compliance/security reviews.
7. Record exact counts, residue and task state; no 8c/8d commit/push/PR changes.

R7 late-upload amendment is approved and recorded in the spec. Unknown remote
outcomes remain CleanupPending through bounded retries/manual recovery; expiry
and repeated absence are not certainty. Deterministic scenarios A/B/C now guard
terminal cleanup. Task 8c and Task 8d are COMPLETE as of 2026-09-30; exact test,
provider-smoke, independent-review and residue evidence is in the verification
ledger. Task 9 remains NOT STARTED.

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
Execution (2026-09-29): Task 8a COMPLETE. Focused GREEN: 34 passed;
SQL-enabled full regression: 1266 passed, 0 failed, 0 skipped, exit 0.
Independent spec/code-quality review completed after two findings were fixed.
Commands, limitations and cleanup are recorded in `docs/TM-79-verification.md`.
Task 8 overall is COMPLETE: Tasks 8a, 8b, 8c and 8d are COMPLETE. Task 9 is NOT STARTED.
8b: real-SQL operation journal/adoption tests and minimal persistence.
The owner explicitly approved M1–M5 and continuous execution of 8b.1–8b.4
after the 2026-09-29 media-only preparation. Task 8b is COMPLETE:
2b-media/3b-media/4b-media and 8b.4 passed focused tests, independent review
and full SQL regression (1336 passed, 0 failed, 0 skipped, exit 0).
Original full Tasks 2/3/4 remain partial for gated POI/visit-dependent work.
8c COMPLETE: provider upload/reconcile/cleanup tests and implementation.
8d COMPLETE: isolated real-provider smoke and residue check.
Reuse Task 6 structural validation; enforce actual supported image content and
resource bounds at this media boundary before upload, allocate review-owned IDs,
track pending uploads durably, never accept arbitrary URL/public ID, reconcile
timeouts/crashes, protect adopted assets from cleanup races, and scope deletion
to assets owned by that failed operation. Reuse provider plumbing where safe;
do not generalize or change TourMedia business rules.
Run corresponding project filters for all four test classes above.
Done: partial batch/provider/DB/crash cases reclaim assets with durable retry;
real Cloudinary smoke covers upload/readability/cleanup in a dedicated test folder.

### Task 8b approved atomic sequence — media prerequisites only

Dependency correction: 8b cannot assume the deferred 2b/3b/4b media schema
already exists. Extract only the following media portions; the POI/visit/
legacy-dependent remainder of those tasks stays gated and incomplete.
Execute continuously within the approved media-only boundary; no additional
approval is needed between green substeps. Record final evidence in the ledger.

1. **8b.1 / 2b-media — SQL migration contract RED.** New
   `tests/TripMate.Api.IntegrationTests/Reviews/TripReviewMediaMigrationTests.cs`
   and scoped `Fixtures/Database/tm79_pre_media_schema.sql` reflecting current
   parent-only schema with nonempty parent/legacy data. Reuse existing inventory
   reader. Verify isolated local SQL identity before running. Test full fresh/
   upgrade inventory parity (columns/types/length/nullability/collation/keys/
   FK/check/index), idempotency, wrong-shape rollback, preservation, restrictive
   deletes, all M2/M3 checks, duplicate public ID/slots, zero-media parent and
   1/5 valid slot boundaries. RED must be missing media objects/migration, not
   a skipped test, unavailable server or compilation failure. No GREEN SQL yet.
2. **8b.2 / 3b-media — SQL GREEN.** Proposed
   `database/migrations/20260929_add_trip_review_media.sql` (verify unused name
   and ordering first), canonical full schema and database README. Guarded
   idempotent transaction with full existing-shape preflight; no repair/drop,
   EF migrations or changes to old Reviews. Run 8b.1 to GREEN. This completes
   only the media portion, not original Tasks 2/3.
3. **8b.3 / 4b-media — Domain and EF TDD.** TripReviewMediaOperation,
   TripReviewMedia entities, their configurations, scoped DbSets/test contexts
   and parent navigation as needed. New
   `tests/TripMate.Application.UnitTests/Domain/TripReviewMediaOperationTests.cs`
   and API `Reviews/TripReviewMediaSqlServerTests.cs`. First RED on lifecycle,
   immutable ownership/metadata, input vs stored byte semantics, UTC roundtrip,
   rowversion and parent+media navigation insert; then minimal GREEN.
4. **8b.4 — Journal/adoption persistence TDD.** Application
   `Features/TripReviews/Media/IReviewMediaJournal.cs`; Infrastructure
   `Reviews/Media/SqlServerReviewMediaJournal.cs` and DI. Extend real-SQL tests
   for atomic reserve; foreign booking/author/batch; exact/conflicting upload
   replay; stale versions; adopt whole batch; repeat same-parent adoption;
   reject other-parent/partial adoption; caller transaction enforcement;
   rollback after parent/link insert on SQL exceptions AND business-result
   rejection (no caller commit of an orphan parent); concurrent adoption/cleanup marking
   in independent contexts with barriers, tested in both winner orderings;
   cleanup marking for mixed Reserved/Uploaded batch, exact pending replay,
   stale/missing/foreign/partly-adopted batch rejection with zero partial writes.
   No Cloudinary fake may be reported as provider success. No worker or handler.

Execute one numbered step at a time without intermediate approval stops; report RED/GREEN, exact
counts/exit codes and independent spec/code-quality review between steps.
Focused command (verify discovery first):

```powershell
dotnet test tests/TripMate.Api.IntegrationTests/TripMate.Api.IntegrationTests.csproj -c Release --filter "FullyQualifiedName~TripReviewMediaMigrationTests|FullyQualifiedName~TripReviewMediaSqlServerTests" --logger "console;verbosity=minimal"
dotnet test tests/TripMate.Application.UnitTests/TripMate.Application.UnitTests.csproj -c Release --filter "FullyQualifiedName~TripReviewMediaOperationTests" --logger "console;verbosity=minimal"
```

Completion requires scoped format, Release solution build, SQL-enabled full
solution final result (no unexplained skips), diff check, temporary DB/log
cleanup, fresh independent two-stage review and updated verification ledger.
Run only approved isolated local SQL; never shared/Azure. No 8c/8d or Task 9
starts implicitly. No commit/push without a separate delivery request.

### Task 9 — Submit transaction and duplicate recovery

Depends on verified Task 6b nullable-policy reconciliation. Current-delivery
handlers supply null for unscreened publications; do not inject a fabricated
policy approval or call the dormant moderator.

Files: Application `Submit/SubmitTripReviewCommandHandler.cs`;
`Common/ITripReviewWriteLock.cs`; Infrastructure `Persistence/SqlServerTripReviewWriteLock.cs`.
Tests: Application `Features/TripReviews/SubmitTripReviewTests.cs`;
API `Reviews/SubmitTripReviewSqlServerTests.cs`.
9a: handler validation/orchestration RED -> GREEN.
9b: real-SQL new+legacy duplicate and rollback RED -> GREEN.
9c: independent-context races, response-loss recovery and asset cleanup.
Integrate authenticate/context -> early new+legacy duplicate check -> normalized
input validation/media inspection -> journal/upload -> final
locked transaction. Verify ordering with zero inspection, journal
allocation and upload calls for an already-reviewed booking, new or legacy.
Persist exactly the normalized values structurally validated; recheck eligibility
in the final transaction. Save one parent, target-rating rows and adopted media
atomically. Deterministic duplicate handling maps only the expected unique
violation; never convert arbitrary DB errors to duplicate success.
Check unlinked legacy Reviews.booking_id as well as the new parent under
one booking-scoped write protocol. Old reviewed bookings must not reach
media upload on the normal early duplicate path. Audit and serialize all
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
Task 9 is COMPLETE and committed as `e8ca568` as of 2026-09-30.

### Task 10 — Edit transaction and deadline/version boundaries

Status: **COMPLETE for canonical new `TripReview` edit (2026-09-30)**.
The application handler and real-SQL tests cover the exclusive original
deadline, canonical rowversion/stale conflicts, booking-scoped concurrency,
active-owner rechecks, normalized persistence, nullable policy clearing,
approved public-name snapshot behavior, rollback and immutable media/C4/
subject/timestamps. The current schema has no linked POI-child contract, so
the SQL gate proves that edit does not invent legacy/child `Reviews`; positive
linked-child preservation remains behind G-VISITS and the deferred child
schema. Legacy edit remains fail-safe gated and G-LEGACY stays OPEN. No HTTP,
aggregate, AI or provider work was added. Exact evidence is in the verification
ledger. Task 10 is committed as `23ee868` as of 2026-09-30.

Files: Application `Edit/EditTripReviewCommandHandler.cs`;
Application `Features/TripReviews/EditTripReviewTests.cs`;
API `Reviews/EditTripReviewSqlServerTests.cs`.
Use controlled clock at before/equal/after deadline and recheck in the final
transaction. Keep initial submission time/deadline immutable. Reject stale
version and preserve old publication/media on validation/conflict/rollback. Under D3,
only overall rating/title/content and the approved D5 display preference are
editable. Media, child evaluations and C4 remain unchanged. Cover preference
changes, ordinary edit snapshot preservation, and account rename behavior.
Verify padded input is normalized once and persisted identically;
failed structural/concurrency/business validation never changes the previous
publication.
Resolve G-LEGACY before claiming still-in-window legacy reviews are editable;
unknown old fields cannot be invented to force them through the new DTO.
Run Application/API project filters `FullyQualifiedName~EditTripReview`.
Done: competing edits cannot silently overwrite; deadline, public identity
and unchanged-media behavior pass. No untested legacy-edit promise.

### Task 11 — Published aggregates and legacy compatibility

Status: **PARTIAL — Tour and legacy POI slices implemented; linked POI-child
slice blocked by G-VISITS (2026-10-01)**. The canonical schema/model has no
`social.Reviews.trip_review_id` or equivalent authoritative parent link and no
producer establishes one. Task 11 does not invent a column, infer links, or
claim the gated linked-child cases. Tour aggregation combines canonical
Published parents with legitimate legacy Tour rows, rejects same-booking
overlap with `trip_review.legacy_conflict`, and creates no mirror. POI Detail
and Explore share the same legacy-only source filter and retain established
one-decimal AwayFromZero presentation and bounded query counts.

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

Status: **COMPLETE for the currently supported TM-79 Backend delivery
(2026-10-01)**. The authenticated Active-Traveler GET/POST/PUT surface is
implemented through the real application path. POST uses the bounded frozen
multipart contract; PUT enforces the closed five-member JSON contract without
changing global JSON options; feature-scoped RFC 7807 mappings and OpenAPI are
covered through real HTTP. Linked new POI-child behavior remains unavailable
behind G-VISITS and is not fabricated. Exact RED/GREEN, full-regression and
cleanup evidence is in the verification ledger. Independent review findings on
authorization-before-binding, validation ProblemDetails, raw multipart UTF-8/
byte enforcement and OpenAPI constraints were corrected and regression-tested.
Task 12 changes remain uncommitted for owner review.

Files: `src/TripMate.Api/Controllers/V1/TripReviewsController.cs`;
`Controllers/V1/Requests/TripReviewRequests.cs`; endpoint-scoped authorization,
multipart binding and OpenAPI support only where required.
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
must still produce 400 trip_review.invalid_edit_payload and no
write. A valid JSON edit succeeds; serializer regressions on unrelated routes
are excluded by scoped configuration. Authentication precedes business details.
Run API filter `FullyQualifiedName~TripReviewEndpoint|FullyQualifiedName~TripReviewOpenApi`.
Done: runtime/Docs/OpenAPI contract matrix agrees; no fake-auth-only acceptance.

### Task 13 — Backend regression and reproducible handoff

Status: **COMPLETE on 2026-10-03 for the currently supported Backend
delivery**. The final handoff uses signed JWT requests through the real HTTP
surface and isolated SQL scopes for create -> recovery GET -> edit -> final GET
-> Tour aggregate. It also closes the GET-media visibility gap found by the
handoff test, proves separate CSP-positive/pacing-negative behavior, exercises
duplicate recovery and cleanup-pending failure state, and records a separately
executed real Cloudinary upload/probe/delete smoke. Reproduction and cleanup
steps are in `docs/TM-79-smoke-test.md`; detailed counts and gate limitations
are in `docs/TM-79-verification.md`.

The current branch has read-only drift against `origin/develop` and a synthetic
merge-tree check reports conflicts in shared API error mapping, infrastructure
DI, and `TripMateApiFactory`. Task 13 does not merge or rebase; this is a
delivery integration follow-up, not evidence that the frozen TM-79 contract is
invalid on the verified branch.

Files: verification ledger; `docs/TM-79-smoke-test.md`; test-only owned fixture
under `tests/TripMate.Api.IntegrationTests/Fixtures/Database/` when needed.
Run Release build, format check, focused SQL tests with zero unexpected skips,
full solution tests and `git diff --check`. Record exact commands/exit/counts.
Run create -> new-context read -> edit -> aggregate with SQL and required
media storage, including C4 eligible/ineligible records and cleanup
verification. No AI provider dependency is required.
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
unknown outcome, stale version, structural rejection and dispose/cancel.
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

Record all repository SHAs, API origin and isolated DB identity. Use the same sanitized valid payloads in
both clients. Cover owned Completed trip; foreign/non-completed trip; C4
eligibility; null/absent optional values; invalid image; structural rejection;
double submit; lost response after commit; existing new/legacy review;
legacy+new Tour/POI totals; missing visit evidence; author name preference;
immutable edit fields; edit deadline;
stale edit; storage outage; aggregate and image visibility after save.
Refresh from a new session/context to prove persistence and verify cleanup.
Trace each AC to test or manual evidence; any dependency has an owner and
explicit acceptance disposition. Independent review and CI use final heads.
Done: full current-delivery TM-79 only when BE + both clients + entry/return +
approved storage implementation and all current source-scope gates meet the AC;
deferred automated screening is explicitly reported as a source-scope deviation.
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
classes; `ReviewMedia` filters must be added for the media task. Historical
`ReviewModeration` tests do not establish current-delivery screening. Never
count skipped SQL/media-provider tests as executed. Full suite runs at baseline
and delivery, and when shared changes warrant them; scoped gates run between
atomic steps. Docs-only planning needs content/path/whitespace review, not a
fabricated application test run.

Historical v3 progress through 2026-10-04 (superseded for current task ordering
by the R1 remediation baseline): **Task 0 COMPLETE; Task 1 COMPLETE; Tasks
2a/3a/4a/5a/6a/6b COMPLETE**.
Spec/plan v3 and G-SCOPE are approved. Task 0's logging-isolation correction
produced two reproducibly green full SQL-enabled baselines. Task 1 froze the
first-delivery Backend wire contract and test matrix in
`docs/TM-79-api-contract.md`; its central Docs publication remains a separate
cross-repository handoff. G-POLICY is DEFERRED FOR CURRENT DELIVERY / FUTURE
ENHANCEMENT (not a current blocker); G-VISITS and
G-LEGACY remain OPEN. Approved parent-only Task 2a has completed RED; Task 3a
has completed focused GREEN, independent review and final SQL-enabled full
regression (1059 passed, 0 failed, 0 skipped, exit 0). Original Tasks 2/3 are
not complete: deferred Tasks 2b/3b remain gated.
Task 4a parent domain/EF passed focused, independent review and full SQL-enabled
regression (1098 passed, 0 failed, 0 skipped, exit 0). Original full Task 4
remains incomplete: Task 4b is deferred behind child/media schema evidence.
Task 5a owner read-context passed focused tests, independent review and final
full SQL-enabled regression (1140 passed, 0 failed, 0 skipped, exit 0).
Original Task 5 remains partial: Task 5b retains the gated historical route/
visit, child/media and legacy-disposition work. Task 6a passed 68 focused tests,
independent review and final full SQL-enabled regression (1208 passed,
0 failed, 0 skipped, exit 0). Original Task 6 remains partial for current
handler validation/ordering and normalized-persistence checks in Tasks 9/10;
moderation-only tests are historical/future evidence. Task 8 is COMPLETE:
Tasks 8a/8b/8c/8d are COMPLETE with final SQL/provider/full-regression evidence
recorded in the verification ledger. Task 9 is COMPLETE with create-side
ordering, persisted-normalized-text, booking-scoped lock, rollback, media
winner/loser and lost-response evidence. Task 10 is COMPLETE for canonical-new
edit with deadline/version, rollback and immutable-related-data evidence.
Task 11 is PARTIAL as documented above; Tasks 12 and 13 are COMPLETE for the
currently supported Backend delivery; Tasks 14–17 have not started. Task 7 is
DEFERRED, not part of current-delivery
completion. Task 6 current-delivery structural ordering and normalized
persistence evidence is complete across Tasks 9 and 10; its dormant moderation
port remains future-only.
See the verification ledger.
The handoff prompt is docs/TM-79-implementation-prompt.md; its existence or
use does not itself mark proposals approved.
