# TM-79 — UC-33 Submit Trip Review

Status: **APPROVED v3 — implementation authorized within the approved Backend plan and recorded gates**
Approved: 2026-09-27 (Asia/Ho_Chi_Minh), explicit owner approval in conversation.
Workspace: D:\CapStone\Capstone_BE_tm79.
Branch: feature/datmnt-submit-trip-review.
Actor: authenticated Active Traveler.
Delivery: Backend, Flutter Mobile and responsive Next.js Web.

The owner approved this revised spec/plan and the staged G-SCOPE: the first
delivery supports commerce.Bookings; ServiceBookings and standalone no-booking
CSP trips are deferred, not represented as complete UC-33 coverage. Route
pacing and CSP quality remain separate evaluations (C4). Implementation is
authorized only within the approved scope and explicit policy/visit/legacy
gates recorded in the plan and verification ledger.

## 1. Evidence and limits

- Report 3 §3.7.2, pages 145–148: owner/completed-booking checks, one review
  per booking, required overall integer rating 1–5, title/content, optional
  photos <= 5 MB, content screening before publication, seven-day editing,
  published Tour/POI aggregates, Mobile and responsive Web.
- The same section names “Tour Name or Service Name”. Excluding service
  bookings is therefore a delivery deferral requiring owner approval, not
  a claim that services are absent from the source requirement.
- Report 3 alternate edit flow names rating, title and content. Its screen
  figure differs on title/text requiredness and includes POI stars, route
  pacing and a display-name checkbox.
- Stitch g_i_nh_gi_ph_n_h_i_tripmate_mobile_uc_33/code.html adds CSP stars,
  Like/Dislike POIs, a 20/500 text hint, five photos and abbreviated-name copy.
  Prototype choices conflicting with Report 3 are proposals, not authority.
- Code inspected: checkout d7269132dab28c36db913a4c6b018a0a4a299156 and local
  fetched origin/develop 525489b61ee2a2791b3d0cf71313e80a4a0445e7. Neither a
  current remote merge status nor a passing baseline is claimed here.
- commerce.Bookings has owner, status, nullable tour_schedule_id/itinerary_id.
  commercial.ServiceBookings has a separate key/service target. An equal
  numeric ID in these tables is not the same booking.
- social.Reviews already has nullable booking_id, Tour/POI/RouteSegment/
  Operator targets, rating and comment; it has no title or moderation history.
  Existing POI reads calculate ratings dynamically.
- planning.Itineraries has CSP provenance. ItineraryItems SQL has a Visited
  status, but the inspected domain model has no mapped status/write workflow.
  Item Kind=Visit means a planned stop. TripLocationLogs contains coordinates,
  not a POI visit identifier. None proves an immutable historical visit.
- No production moderation implementation or booking-linked review writer was
  found in the inspected src snapshot. Re-audit on the implementation base.
- Follow AGENTS.md, TEAM_ENGINEERING_RULES v2.2, and
  Dev_and_CrossReview_Checklist v2.2. Current FE/Mobile dirty work is unrelated.

Local evidence: database/tripmate_schema_v7.sql; src/TripMate.Domain/Entities/
Review.cs, Itinerary.cs, ItineraryItem.cs; associated persistence/query code.
Report extraction: D:\CapStone\.codex-tmp\report3_text.txt, §3.7.2.
Use source section names with rule text: Report 3 reuses some BR/MSG numbers.

## 2. Decision register — proposals for explicit approval

### D1 — Separate route pacing and CSP quality

Confirmed: keep both evaluations, separate from overall and POI stars.
Propose nullable inputs with no default selection:

- routePacing: tooTight / wellPaced / tooLoose; client labels localized;
- cspRating: integer 1–5 only when the booking-linked itinerary is owned,
  CSPGenerated, and linked to a scheduling request owned by the same Traveler.

Route-pacing eligibility proposal: optional only for a supported Completed
booking whose reviewed trip has an authoritative route/itinerary context.
The context must be attributable to that booking's trip: a linked owned
itinerary with verified trip/version provenance, or a booking-specific stored
Tour-route snapshot. A schedule-to-Tour link alone or the Tour's mutable
current template is insufficient. This does not require Visited POI evidence;
route context and actual-visit evidence are separate capabilities.
For a Tour booking without itinerary_id, allow routePacing only if another
authoritative booking-specific route snapshot exists. Otherwise GET marks
route pacing unavailable with a reason; omitted/null is accepted, but a
supplied non-null value returns 400 with code trip_review.route_pacing_unavailable.
Overall review remains available when its own eligibility checks pass.
Resolve the authoritative read/version contract in Tasks 1/5 and recheck it
in the final create transaction. Do not create a route-history workflow here.

A client flag or TourMatch similarity score cannot establish CSP eligibility.
Reject an ineligible supplied CSP score; do not silently drop it.
Neither evaluation contributes to Tour/POI averages. No new CSP/pacing
aggregate or recommendation algorithm is included.

### D2 — Identity and completed-state authority

Recommend a staged first backend contract for commerce.Bookings. This is a
proposed delivery subset, not full coverage of every booking in Report 3:

| Record | Proposed handling |
| --- | --- |
| Owned commerce booking + valid TourSchedule | Booking.status=Completed is completion authority; Tour is resolved through the schedule. No extra TripSession requirement. |
| Owned commerce booking + itinerary only | Booking.status=Completed is completion authority; verify itinerary ownership. Overall review describes that booked trip; no fabricated Tour aggregate. |
| Commerce booking with both links | One review for the booking; Tour is the overall aggregate target. Verify itinerary ownership and any BookedTour source_tour_id consistency. Itinerary provides POI/CSP context only. |
| Both links absent, missing referenced subject, contradictory ownership | Context unavailable/inconsistent; no write. Do not guess a subject. |
| Standalone itinerary/TripSession without booking | Not supported by the proposed booking-only route; no fabricated booking. |
| commercial.ServiceBookings | Deferred in the proposed first delivery, pending service-to-reviewed-POI identity and collision-safe booking kind/key contract. |

No additional payment, itinerary-status or TripSession-state gate is inferred.
Completion workflows are dependencies owned elsewhere; seeded Completed
fixtures validate review behavior but do not prove those workflows.

**G-SCOPE — approved:** staged first delivery supports commerce.Bookings.
ServiceBookings and standalone no-booking CSP trips are deferred. Record this
source-scope delta and follow-up; do not report the subset as full UC-33
completion. If the owner later selects full service-booking coverage, revise
keys/routes/targets before migration tests.
Itinerary-only overall feedback is stored; only eligible POI child scores
contribute to POI aggregates. No public aggregate of personal itineraries.

### D3 — Inputs, media, and edit surface

Propose:

- overallRating: required integer 1–5;
- title: required, trimmed, 1–200 UTF-16 code units;
- content: required, trimmed, 1–1,000 UTF-16 code units;
- optional distinct POI IDs with integer ratings 1–5; no scenic/photo scores;
- 0–5 photos, each <= **5,000,000 bytes (5 MB)**; JPEG/PNG/static WebP.
  Verify decoded content, size and MIME/extension consistency. Reject SVG/GIF,
  animated files, invalid images and provider URLs supplied as uploads.
- A bounded create request: 26,000,000 total bytes including multipart overhead,
  metadata <= 64,000 UTF-8 bytes. These transport bounds are proposals.
- Cloudinary stores binaries; separate review ownership/namespace in SQL
  stores metadata. Reuse approved shared provider plumbing where available,
  never TourMedia rows/folders/locks with a review ID substituted.

Edit within the original window: overallRating, title, content, plus the
explicit author-display preference proposed in D5. That preference is a
proposed addition to the three edit fields named in Report 3.
POI/C4/photo edits are not included in this proposed delivery.
PUT is JSON; no newFiles, retainedMediaIds or media-replacement side effects.
Create remains multipart. Clients make immutable fields visibly read-only.
Server must reject attempts to change immutable fields; never silently accept
and ignore a photo/CSP/POI change. Edit JSON uses strict unmapped-member
handling at the TM-79 request boundary. The allowed member set is exactly
overallRating/title/content/publishDisplayName/version. Any unknown member
or create-only/immutable member (including cspRating, routePacing, poiRatings,
media, newFiles and retainedMediaIds) returns 400 with the stable code
trip_review.invalid_edit_payload, even when its value is null, empty or equal
to the stored value. Do not silently drop it or run moderation/write logic.
Task 1 freezes this contract; Task 12 implements request-scoped strict
deserialization and maps binding errors to the same ProblemDetails code.
Do not change the global serializer behavior of unrelated endpoints.

Validation boundaries: Task 6 checks application structure, photo count,
declared/raw byte-length bounds and metadata, without decoding images or
referencing a decoder/provider SDK. HTTP enforces total-body/metadata limits.
Task 8's infrastructure media inspector enforces actual bounded stream bytes,
magic bytes, decode success, MIME/extension/content consistency, static format
and decoded resource limits before upload. A declared length is not proof of
actual size. The application orchestrates this through a port; it does not
implement image decoding. Reuse the normalized metadata/validation result
instead of maintaining two competing validators.

### D4 — Content policy before implementation choice

Report 3 requires content screening; it does not require a third-party vendor.
Keep IReviewContentModerator as an application port. Choose the implementation
only after agreeing the content policy and acceptance examples.

**Gate G-POLICY, before Task 7:** record policy version, prohibited categories,
supported languages (propose Vietnamese and English), allowed negative-review
examples, rejection/error behavior, and an owner-approved validation corpus.
Then choose a local implementation or external service supported by evidence.
A simplistic word list or always-allow fake is not acceptance evidence. Do not
reject legitimate low-star/critical feedback merely because it is negative.
If external: record timeout/retry limits, credentials source and data handling
before making provider calls. If local: document policy coverage/limitations.
No credentials or real review content in routine logs.

Reject: 400 stable policy error, no new published review or rating.
Unavailable/timeout: 503, preserved client input, no publication.
Failed edit: previous published version remains intact.
Normalize title/content once by trimming before length validation and screening.
Moderation evaluates those exact normalized title/content values that will be
persisted; bind the accepted policy version to the same normalized values.
For example, raw "   Khách sạn tốt   " is screened and saved as "Khách sạn tốt".
No post-moderation transformation may change their semantic content; if the
normalized values change, screening must run again before publication.
This applies to create and edit, including retries. A test double is test-only
and cannot demonstrate production screening.
Scope is written title/content; image format validation is not image moderation.
Any image-policy requirement needs explicit scope before claiming it satisfied.

### D5 — Public name and trustworthy POI context

Proposed public identity:
- publishDisplayName defaults false on create.
- false: initials of each whitespace-delimited name token, Unicode text-element
  aware (Nguyễn Tiến Đạt -> N. T. Đ.); blank name -> localized “Traveler”.
- true: show a snapshot of the authenticated account FullName after explicit
  checkbox consent. Preview the resulting public label before submission.
- Store preference and public label snapshot; no raw full-name snapshot when
  false. Do not return email/phone or a hidden full name in public DTOs.
- Account renaming does not silently change an old review's public label.
  During the edit window, a changed preference regenerates the label from the
  owner's current profile; ordinary text/rating edits preserve the snapshot.
  A same-preference edit does not refresh the label.
- This is pseudonymous display, not a promise of anonymity.

POI proposal: use only explicit Visited items on the booking-linked owned
itinerary, with non-null POI ID; deduplicate repeated POIs. Never use current
Tour templates, planned Visit kinds, or inferred GPS proximity as visit proof.

**Gate G-VISITS:** identify the authoritative visit writer/read contract,
its association with the completed trip, and historical immutability/version
behavior. Recheck eligibility in the final transaction and save selected
item/POI evidence with the review. Changes after GET cannot authorize stale
or foreign choices. Do not add a visit-writing workflow within TM-79.
If evidence is absent, context explicitly returns POI capability unavailable
and no eligible choices. Overall review may proceed, but POI UAT is blocked;
an empty array or seeded SQL test does not mean the POI feature is delivered.

### D6 — Duplicate recovery and seven-day boundary

- Unique new parent per booking. Existing legacy rows linked to that booking
  also prevent create under D7.
- Duplicate create -> 409 with stable code; owner GET returns existing state.
  No Idempotency-Key replay promise; 409 does not mean submitted payload won.
- A lost response triggers GET recovery, not blind repeated uploads.
- Edit uses a body version token backed by SQL rowversion. Missing token is
  invalid; stale token -> 409. Do not mix this with an unspecified If-Match
  contract.
- createdAtUtc is the initial successful submission/publication timestamp,
  not the start of a failed attempt. Deadline = createdAtUtc + seven 24-hour days.
  Accept only when nowUtc < deadline, rechecked in the final write transaction.
  At equality the review is read-only. Editing never extends the deadline.
- Auth-derived author/subject and transaction checks remain mandatory on writes.

### D7 — Persistence and legacy compatibility

- social.TripReviews holds canonical new overall rating, title/content,
  booking/author/derived subject, publication/policy metadata, C4 values,
  display preference/snapshot, UTC timestamps and rowversion.
- Add nullable trip_review_id to social.Reviews for new POI child ratings only.
  Do not create an additional Tour row mirroring the new parent's overall score.
  SQL FK/unique keys protect child parent/POI identity; application protects
  ownership/eligibility. Commit parent, children, media adoption in one transaction.
- Preserve old social.Reviews rows, IDs, ratings and original timestamps.
  Legacy linkage is NULL; do not synthesize title, consent or moderation state.
  Proposed compatibility policy preserves their existing rating visibility;
  this does not certify that legacy content passed the new content policy.
- Before POST, check BOTH new parents and unlinked legacy Reviews.booking_id.
  Recheck under a booking-scoped SQL write lock in the transaction.
  Audit every booking-review writer at the chosen base and make them follow
  the same serialization contract. Parent uniqueness alone is insufficient
  across two tables. Concurrent/direct SQL writers outside the verified
  workflow are not covered by an application lock; if required, add a
  separately designed database-wide guard before claiming that guarantee.
- Owner GET distinguishes new review / legacy review / no review.
  Legacy context returns known original data without invented fields.
  If conflicting legacy authors/targets or multiple ambiguous rows exist,
  block create and report a compatibility conflict; no automatic rewrite.
- **Gate G-LEGACY:** inventory old booking-linked rows on an authorized test
  copy or sanitized sample before rollout. Within-window legacy reviews need
  an approved edit adapter or explicit rollout disposition; do not silently
  make a still-editable review read-only. Do not claim full edit compatibility
  until that case is covered. No production DB scan is implied.

Aggregate formulas (query-derived, no persisted duplicate counters):
- Tour: published new TripReviews with that Tour target UNION ALL unlinked
  legacy social.Reviews with target_type=Tour and that target_id.
- POI: unlinked legacy POI rows UNION ALL new POI children whose parent is
  Published. Never count a linked child again as legacy.
- Exclude route/CSP/scenic/photo dimensions from overall star averages.
- Preserve every legacy contribution; do not assume same author/subject means
  duplicate. New-parent/legacy overlap for the SAME booking is an invariant
  violation to detect/prevent, not hide with a blanket DISTINCT.
- Edit replaces the new overall contribution, count unchanged; rollback leaves
  averages unchanged. Empty average=null, count=0; use existing POI rounding.
- Stable feature ProblemDetails codes. Do not overwrite shared MSG catalogue
  entries; reconcile source identifiers in Docs by section and rule text.

## 3. API and transaction outline

Subject to G-SCOPE; routes use commerce.Bookings identity only:

- GET /api/v1/bookings/{bookingId}/review: owned context, subject, capabilities
  and reasons, eligible POIs, distinct route-pacing/CSP eligibility, new/legacy existing state,
  editable field list, deadline/version, owner-only display-name preview.
- POST same route: multipart metadata plus 0–5 new files; 201 direct DTO.
- PUT same route: JSON overallRating/title/content/publishDisplayName/version;
  200 direct DTO. Existing media/POI/C4 returned unchanged.

401 unauthenticated; 403 inactive/wrong role; 404 missing/foreign booking;
400 invalid input/policy rejection; 409 duplicate/stale/not-completed/expired/
inconsistent context; 413 body limit; 503 unavailable screening/storage.
Freeze concrete feature codes and DTO discriminators in Task 1.

Create business flow:

1. Authenticate and read the owned booking/subject context.
2. EARLY duplicate check: new TripReview plus unlinked legacy Reviews.booking_id.
   A known duplicate returns 409 before moderation, media inspection,
   operation-journal allocation or Cloudinary calls.
3. Normalize title/content once; validate normalized input/structural limits
   and field eligibility; inspect new image bytes through the media boundary.
4. Moderate the normalized title/content that will be persisted.
5. Journal and upload request-owned media.
6. Short SQL transaction: acquire booking write lock, recheck owner/completion,
   new+legacy duplicate, route/POI/CSP evidence; persist the SAME normalized
   values, parent/children and media adoption; commit.

HTTP authentication/binding/malformed-body and size checks can precede the
business flow. The early duplicate check is an optimization and never replaces
the locked transactional recheck: a duplicate may appear during screening or
upload. Test that race and compensate only loser-owned assets. Network calls
remain outside SQL locks. Edit uses strict JSON -> owned review/version/window
checks -> normalization/validation -> screening -> transactional rechecks/save;
it has no upload/cleanup side effect.

## 4. Delivery and verification

BE Tasks 0–13 are an intermediate deliverable; Mobile/Web Tasks 14–17 complete
the agreed client scope. UC-32 entry/return, real completion/visited data, policy
implementation and review media each need evidence, not fixture-only claims.
Client UI uses actual context, field eligibility and immutable edit fields.
No hardcoded booking/ratings/POIs/identity or always-allow production policy.

SQL gates: fresh/upgrade full affected inventory parity; rerun; wrong-shape
rollback; preservation of legacy data; real SQL constraints, UTC round-trip,
separate-context concurrent writes and transaction rollback. Never count skips
as executed SQL tests. Validate new and legacy Tour/POI aggregate contributions,
same-booking duplicate protection and the legacy edit gate explicitly.

Record per task: files, exact commands, exit codes, discovered/pass/fail/skip,
expected RED, GREEN, two-pass review and unresolved gates. Before code, refresh
the chosen base without rewriting shared history and verify baseline on an
identified isolated SQL Server via TRIPMATE_SQLSERVER_TEST_CONNECTION.
Never use a shared business/Azure DB as a destructive integration-test target.

## 5. Approval and scope boundaries

The owner approved D1–D7, spec/plan v3 and the staged G-SCOPE on 2026-09-27.
G-POLICY, G-VISITS and G-LEGACY remain evidence gates for their dependent
behavior: G-POLICY before production screening; G-VISITS before real POI
acceptance; G-LEGACY before claiming legacy rollout/edit compatibility.
Approving the plan does not silently waive these gates or claim full UC-33
service-booking coverage.

Excluded: booking/payment/completion/visit-writing workflows, full UC-32,
admin moderation UI/appeals, operator replies, CSP engine changes, review-photo
replacement after publish, and extra TM-70 search/detail APIs.
No commit, push, PR or merge without explicit owner request.
See plans/TM-79-plan.md and docs/TM-79-implementation-prompt.md.
