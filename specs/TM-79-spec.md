# TM-79 — UC-33 Submit Trip Review

## R1 authoritative requirement and contract freeze (2026-10-07)

Status: **OWNER APPROVED; BACKEND R1–R7 IMPLEMENTED; R9 VERIFIED
2026-10-09; R8 REMAINS A TM-78 DEPENDENCY**.

The Backend runtime now implements the frozen typed identity, service-to-POI
route, deterministic local moderation, Option A schema and canonical Tour/POI
aggregate contract. Mobile and Web remain downstream and frozen. Full UC-32
Trip History remains owned by TM-78; this Backend closure does not implement it.

This section is the controlling TM-79 contract for remediation Tasks R2–R9.
It supersedes earlier statements in this file that limited the feature to
`commerce.Bookings`, deferred Report 3 BR-94 screening, selected an external
moderation direction, or treated `poiRatings[]` as the canonical POI-review
path. Those passages remain below only as dated implementation history. Existing
media, concurrency, recovery and legacy-safety decisions remain valid where
they do not conflict with this section.

Canonical source: `Report3_Software-Requirement-Specification-V2.docx`. The
owner-confirmed comparison establishes that UC-32 and UC-33 are identical in
the previous Report 3 and V2. The named V2 artifact is no longer a blocker, and
the gaps addressed here are existing compliance gaps rather than V2 changes.

### Requirement classification

| Classification | Frozen items |
| --- | --- |
| `REPORT_3_V2_REQUIRED` | Active Traveler; owner-only access; Completed trip; at most one review per reviewable-record identity; required overall integer rating 1–5; required title; required content; optional photos; each photo <= 5 MB; content-policy screening before publication; seven-day edit window; read-only after expiry; published-review Tour/POI aggregate recalculation; duplicate opens the existing review; Trip History entry/return semantics; Flutter Mobile and responsive Web delivery. |
| `APPROVED_IMPLEMENTATION_EXTENSION` | `routePacing` when an authoritative non-blocking capability exists; `publishDisplayName`; `publicDisplayName`; `displayNamePreview`; duplicate GET recovery; unknown-outcome recovery; legacy read-only compatibility. |
| `TECHNICAL_CONSTRAINT` | SQL rowversion and optimistic concurrency; multipart POST; bounded request handling; media inspection/storage/journal/cleanup/recovery; maximum five photos; title <= 200 UTF-16 code units; content <= 1,000 UTF-16 code units; JPEG, PNG and static WebP only. The last four bounds/formats are not Report 3 requirements. |
| `DEFERRED_EXTENSION` | Standalone itinerary-only review identity/route/completion/duplicate/aggregate behavior; `cspRating`; `poiRatings[]` as a canonical POI review or aggregate source. |
| `OUT_OF_CURRENT_SCOPE` | Full UC-32 implementation, which belongs to TM-78; service-to-POI master-data backfill; manual moderation/admin appeals; post-publication photo replacement. |
| `DOCUMENTATION_DEFECT` | Colliding Report 3 BR/MSG identifiers. TM-79 does not renumber or reinterpret them. Runtime behavior uses stable semantic `trip_review.*` error codes, and UI copy maps from semantics rather than ambiguous numeric identifiers. |

The SRS requires the ability to attach optional photos and the per-photo 5 MB
limit. The maximum count, text lengths and supported image formats are approved
Backend constraints and must not be described as SRS requirements.

### Reviewable record identity

The canonical identity value object is:

```text
ReviewableRecordRef {
  kind: commerceBooking | serviceBooking,
  id: positive int64
}
```

Identity is the pair `(kind, id)`. An `id` alone is never globally meaningful
across booking namespaces. The server and clients must not infer or convert
between `tripId`, `bookingId`, `serviceBookingId` and `itineraryId`.

| Record kind | Entry identity | Ownership source | Completion authority | Duplicate key |
| --- | --- | --- | --- | --- |
| `commerceBooking` | `commerce.Bookings.booking_id` | `commerce.Bookings.traveler_user_id` | `commerce.Bookings.status = 'Completed'` | `(commerceBooking, booking_id)` |
| `serviceBooking` | `commercial.ServiceBookings.service_booking_id` | `commercial.ServiceBookings.traveler_user_id` | `commercial.ServiceBookings.status = 'Completed'` | `(serviceBooking, service_booking_id)` |

An itinerary may participate only behind an authoritative supported commerce
booking. Standalone itinerary review is deferred. TM-79 must not invent its
completion authority, duplicate identity, route or aggregate behavior.

### Routes and subject alignment

| Reviewable record | Canonical routes | Allowed subject |
| --- | --- | --- |
| `commerceBooking` | `GET`, `POST`, `PUT /api/v1/bookings/{bookingId}/review` | `tour`, or the previously supported authoritative `itinerary` compatibility extension |
| `serviceBooking` | `GET`, `POST`, `PUT /api/v1/service-bookings/{serviceBookingId}/review` | `poi` only |

The commerce route never accepts a ServiceBooking ID. The service route never
accepts a commerce Booking ID. A `serviceBooking` is reviewable only when its
Service resolves through the existing `commercial.Services.poi_id` relationship
to an authoritative canonical POI. A null `poi_id` produces unsupported-subject
behavior. Do not infer a POI from name, category, coordinates, text matching or
client input; master-data backfill is outside TM-79.

Canonical subject values are `tour` and `poi`. `itinerary` remains only the
existing commerce-booking compatibility extension and has no fabricated public
aggregate. The current `poiRatings[]` child array is not a canonical POI review
subject and does not satisfy the POI aggregate requirement.

### Owner context and summary

Owner GET eventually exposes these server-authoritative values:

```text
reviewableRecord: ReviewableRecordRef
subject: { kind: tour | poi | itinerary, id: positive int64 }
summary: {
  name: nonempty string,
  departureAtUtc: UTC timestamp,
  bookingReference: nonempty display string
}
```

For commerce bookings, the summary derives from Booking, Tour/Schedule or the
supported booking-linked itinerary context. For service bookings, it derives
from ServiceBooking and Service. `summary.bookingReference` is display data,
not a review identity. Clients must not construct these fields from unrelated
Trip History or UI records.

### BR-94 content-policy contract

The approved implementation is a deterministic local evaluator. It requires
no external provider, credential, network call or production review-data
transfer. The active policy version is `tm79-review-text-v1`.

The six approved implementation-policy categories are:

1. threat or call for physical harm;
2. targeted degrading harassment;
3. hate or exclusion based on a protected group characteristic;
4. explicit sexual content unrelated to a travel review;
5. targeted private-contact or residential disclosure/solicitation for harm;
6. unrelated advertising spam, phishing, scams or fraudulent solicitation.

These categories are an approved implementation policy; do not claim that
Report 3 V2 itself defines this six-category taxonomy. The evaluator supports
Vietnamese, English and mixed Vietnamese/English and evaluates the exact
normalized title/content pair as untrusted data. Legitimate criticism, a low
rating, refund requests, negative words, URLs alone, and quoted/reported threats
are not automatic violations.

Outcomes:

- `Accepted`: only this outcome may proceed toward publication. A successful
  create or qualifying edit persists the active policy version for the exact
  normalized text.
- `Rejected`: do not publish a create and do not mutate an accepted existing
  review. Return `400 trip_review.policy_rejected`.
- `Unavailable`: fail closed; do not publish or mutate. Return
  `503 trip_review.policy_unavailable`. Caller cancellation remains cancellation,
  not a moderation verdict.

Create screens normalized written content after authentication, ownership,
completion, duplicate and structural checks, and before publication. Later task
design must keep screening before media/provider work where practical so a
known rejection does not create external assets. The final transaction still
rechecks mutable business state.

Edit re-screens when normalized title/content changes or when the stored
`policyVersion` differs from the active version. Rejection/unavailability leaves
the prior accepted review, version, deadline, media and aggregate contribution
unchanged. Historical rows with null policy version remain historical; do not
rewrite them to claim screening. A qualifying edit of such a row must screen
the current normalized text before mutation. Never persist a fake version.

Logs may contain decision, policy version, approved category code, duration,
correlation ID and sanitized failure class. They must not contain title,
content, matched offending text, display name, account/booking identity,
photos, secrets or raw exception/provider payloads.

### Error-contract delta

| HTTP | Semantic `errorCode` | Meaning |
| --- | --- | --- |
| 400 | `trip_review.policy_rejected` | Normalized title/content violates the active policy; revise and resubmit. |
| 503 | `trip_review.policy_unavailable` | The evaluator could not produce a trustworthy decision; fail closed. |
| 409 | `trip_review.unsupported_subject` | The completed owned record has no supported canonical subject, including a Service with null `poi_id`. |

Existing semantic codes remain stable, including unauthorized, forbidden,
booking not found, booking not completed, duplicate, stale version, expired
edit, invalid input/edit payload, storage unavailable, body too large,
unsupported media type and legacy conflict. Do not introduce service-specific
duplicates unless semantics actually differ. Missing or foreign records remain
404 without disclosing ownership.

### Approved database direction for R3

Use Option A: extend `social.TripReviews`; do not introduce a parallel
`ServiceReview` parent and do not retrofit the legacy `social.Reviews` table as
the canonical parent.

Planned shape:

- make `booking_id` nullable;
- add nullable `service_booking_id` referencing
  `commercial.ServiceBookings(service_booking_id)`;
- add nullable `poi_id` referencing `catalog.POIs(poi_id)`;
- require exactly one reviewable-record parent identity;
- require exactly one review subject;
- align commerce parent with Tour or supported Itinerary subject;
- align service parent with POI subject only;
- use separate filtered unique indexes for non-null `booking_id` and non-null
  `service_booking_id`;
- use namespace-safe lock resources `commerce-booking:<id>` and
  `service-booking:<id>`.

R1 does not write the migration. R3 must begin with RED migration/inventory
tests and preserve every current row, rowversion and nullable historical policy
version.

### Aggregate semantics

Aggregates remain query-derived unless a later separately approved performance
decision changes the implementation.

- Tour aggregate: published canonical Tour reviews `UNION ALL` safe
  non-overlapping legacy Tour reviews.
- POI aggregate: published canonical POI reviews `UNION ALL` safe
  non-overlapping legacy POI reviews.
- A legacy row that overlaps a canonical row on the same typed authoritative
  booking is excluded according to an explicit canonical-wins rule and reported
  as a data-integrity event. Never hide overlap with blanket `DISTINCT`.
- Do not mirror canonical reviews into `social.Reviews` merely to feed reads.
- `poiRatings[]`, route pacing, CSP, scenic and photo dimensions do not
  contribute to the canonical Tour/POI overall aggregate.
- A successful commit affects the next query; edit replaces one contribution;
  rollback changes none; empty aggregate is average null/count zero.

### UC-32/TM-78 dependency and client handoff

TM-78 owns the full UC-32 Trip History list/controller/query. TM-79 defines only
the handoff required to enter UC-33. Each future Trip History record must expose
an authoritative `ReviewableRecordRef`, `canReview` and existing-review state.
Clients navigate with that reference and return to Trip History after success.
R1 and R8 do not authorize implementation of the full UC-32 feature.

Mobile remains frozen. Its later delta is to consume the typed reference, route
commerce/service correctly, parse POI subjects, support the three new semantic
errors, preserve multipart/photos/rowversion/seven-day and recovery behavior,
and stop treating `poiRatings[]` as canonical POI review data.

Web remains frozen. Its later delta is to replace `tripId` review identity and
`/api/v1/reviews`, consume the typed reference, use canonical GET/multipart
POST/versioned PUT routes, trust server eligibility/summary, support photos,
policy errors, duplicate recovery, draft preservation and Trip History return.

### Future RED contract inventory

R2–R5 must begin with failing tests for, at minimum: commerce and service
`ReviewableRecordRef`; service routes; POI subject; service summary; null-POI
unsupported subject; policy rejected; policy unavailable; changed/outdated-
policy edit re-screening; stale edit; duplicate recovery; equal numeric IDs in
the two namespaces; and separate filtered uniqueness. Later aggregate tests
must cover canonical-only, legacy-only, mixed, overlap, edit and rollback for
both Tour and POI subjects.

### Historical material below

The following dated sections record earlier staged implementation and recovery
decisions. They are useful evidence for code that already exists, but they are
not authority for service scope, moderation scope, canonical POI identity, or
the R1–R9 remediation order when they conflict with the freeze above.

## Task 8c/8d owner-approved recovery decisions (2026-09-29)

Recorded from the owner's R1–R12 instruction. Approval is not implementation
or provider-verification evidence. Task 8b commit 3a0ee58 remains unchanged.
The following contract supplements M5; any identified recovery contradiction
requires an owner decision before implementation.

R1 — REVIEW-SPECIFIC CLOUDINARY NAMESPACE
==================================================

Review media uses the existing Cloudinary account/provider infrastructure,
but MUST have its own review namespace.

Tracked configuration key:

Cloudinary__ReviewMediaFolderRoot

Approved development/default example value:

tripmate/reviews

Use existing Cloudinary credentials/configuration already used by the
repository.

Do NOT introduce a second Cloudinary account or another credential set.

Each operation already owns the preallocated opaque publicId created under
M1/M2.

The client NEVER supplies:

- publicId
- folder
- delivery URL
- provider asset identity

Provider asset identity is formed only from:

configured ReviewMediaFolderRoot
+
the persisted server-generated opaque operation publicId

Do not include in provider identity:

- traveler ID
- booking ID
- review text
- title
- original filename
- email
- user name

If the existing 8b representation requires a narrow adjustment to make this
provider identity deterministic, perform it with TDD and migration/inventory
safety.

Do not reuse:

tripmate/tours/{tourId}

Review media is separate from TourMedia.

==================================================
R2 — PROVIDER UPLOAD AND FENCING
==================================================

Upload must use an operation-scoped lease/fence.

Approved defaults:

Provider upload timeout:
30 seconds

Upload lease duration:
2 minutes

The upload lease must have an opaque fencing token.

Only the holder of the current unexpired upload fence may persist a successful
provider result.

Provider I/O remains OUTSIDE SQL transactions.

Required behavior:

1. claim/fence operation
2. commit short SQL claim
3. upload outside SQL transaction
4. persist result only when the same fence is still current

If provider success returns after the upload fence has become stale:

- do NOT transition the operation to Uploaded
- do NOT allow later adoption from that stale result
- make a best-effort destroy of that exact operation-owned asset
- durable recovery still owns final cleanup

The stale uploader may never overwrite newer recovery state.

==================================================
R3 — TIMEOUT / CANCELLATION / UNKNOWN OUTCOME
==================================================

A timeout or caller cancellation AFTER provider work has started is an
UNKNOWN provider outcome.

Never assume:

"timeout/cancelled => asset does not exist"

For such an operation:

- do not fabricate Uploaded metadata
- preserve the all-NULL provider-result tuple allowed by M3
- arrange durable CleanupPending recovery
- cleanup is performed using the already persisted exact publicId

Caller cancellation must propagate to inspection/upload.

However, once provider work may have occurred, the system may use a short
internal bounded token solely to persist durable cleanup intent.

It must not continue the business submission after caller cancellation.

If SQL is temporarily unavailable and cleanup intent cannot immediately be
persisted, the durable Reserved/Uploaded operation must remain recoverable by
the abandoned-operation scanner after its lease/staleness window.

==================================================
R4 — ABANDONED BATCH RECONCILIATION
==================================================

Task 8c must recover process crashes between:

- reserve and upload
- upload and record-upload-result
- upload completion and future Task 9 adoption

A batch may be considered abandoned only when:

- it is not Adopted/Cleaned
- there is no valid upload lease for any member
- no member has been updated for at least 5 minutes

Approved abandonment grace period:

5 minutes

The reconciliation path operates on the COMPLETE batch.

It must use the existing Task 8b atomic mark-cleanup-pending contract.

Allowed abandoned source states:

- Reserved
- Uploaded
- mixed Reserved + Uploaded

Never automatically cleanup:

Adopted

Do not introduce partial-batch cleanup.

==================================================
R5 — CLEANUP WORKER LEASE
==================================================

Cleanup worker claims must also be fenced.

Approved defaults:

Worker poll interval:
30 seconds

Cleanup lease duration:
2 minutes

Maximum operations/batches per run:
20

Use stable ordering when claiming work.

Only one current cleanup fence may finalize one cleanup attempt.

Provider delete happens outside the SQL claim transaction.

A stale cleanup worker must not update state after another worker has acquired
a newer fence.

Adoption and cleanup must continue to respect the race guarantees already
proved by Task 8b.

==================================================
R6 — CLEANUP RETRY POLICY
==================================================

Approved automatic cleanup retry policy:

Maximum attempts:
8

Initial retry delay:
1 minute

Backoff:
exponential

Maximum retry delay:
24 hours

Use deterministic bounded retry calculation.

Transient provider/network failures:

=> remain CleanupPending
=> schedule next attempt

Provider "asset already absent":

=> successful cleanup only subject to R7 terminal-upload certainty

Confirmed successful provider deletion:

=> successful cleanup only subject to R7 terminal-upload certainty

Do not re-upload during cleanup.

Do not reactivate a failed submission.

==================================================
R7 — CLEANED CONFIRMATION
==================================================

OWNER-APPROVED LATE-UPLOAD AMENDMENT (takes precedence over the original
confirmation wording below): SQL fencing protects SQL state only. For a
dispatched upload with UNKNOWN outcome, AlreadyAbsent or successful deletion
alone cannot prove no earlier remote upload will materialize. Keep
CleanupPending, preserve uncertainty, never set cleanedAtUtc, and use bounded
reconciliation; exhaustion remains CleanupPending plus manual recovery.
Lease expiry, timeout, cancellation, crash, replacement fence, repeated absence
and elapsed grace are NOT terminal-upload evidence.
Cleaned requires a current cleanup fence, no active upload lease, positive
cleanup AND terminal-upload certainty: known upload completion then exact
deletion; known terminal no-asset completion then absence; authoritative
provider terminal evidence then cleanup/absence; or provably never dispatched
then absence. Late success while CleanupPending must never be adopted; delete
the exact asset and finalize only under the current cleanup fence after safe
confirmation. No new business lifecycle state. R9 AlreadyAbsent is only an
outcome classification. R11 may add dispatch, unknown, fencing and exhaustion
metadata. Deterministic tests cover unknown/absent -> late success/delete;
repeated unknown absence -> exhausted NOT Cleaned; terminal no-asset -> absent
under current cleanup fence -> Cleaned.

State:

Cleaned

means provider cleanup has been positively resolved by one of:

1. Cloudinary confirms the exact asset was deleted
2. Cloudinary confirms the exact asset is already absent

This result may only be accepted:

- after upload lease/fence is no longer active
- after the operation/batch is legitimately CleanupPending
- by the current cleanup fence owner

Then persist:

CleanupPending -> Cleaned

with cleanedAtUtc.

Never mark Cleaned merely because:

- upload timed out
- request was cancelled
- process crashed
- provider request threw
- asset existence is unknown

M3 remains unchanged:

Cleaned is terminal.
Adopted is terminal and never cleanup eligible.

==================================================
R8 — PERMANENT / EXHAUSTED CLEANUP FAILURE
==================================================

Do NOT add another business lifecycle state merely for provider exhaustion.

Keep the operation:

CleanupPending

Add narrowly scoped operational recovery metadata if required by the
implementation/migration.

After:

- a provider failure classified as permanent, OR
- 8 automatic attempts are exhausted

record an exhausted/manual-recovery marker and stop automatic retry.

There is NO admin/manual recovery UI in TM-79.

This is operational evidence only.

Do not transition to Cleaned.

Do not lose:

- operation ownership
- publicId
- known upload metadata
- failure timestamp / safe error classification

Never store:

- raw Cloudinary response
- secrets
- signatures

A future operational tool may recover these records, outside TM-79.

==================================================
R9 — STORAGE FAILURE CLASSIFICATION
==================================================

Reuse the existing TM-207 Cloudinary SDK/package and provider-level error
classification where it is genuinely generic and safe.

Do NOT reuse:

- Tour lifecycle rules
- Tour media limits
- Tour folder naming
- Tour cleanup retention business rules
- Tour operator authorization

Review adapter must remain review-specific.

Classify provider outcomes only as needed by this contract:

- Success
- AlreadyAbsent
- TransientFailure
- PermanentFailure
- UnknownOutcome

Do not expose raw provider errors publicly.

Task 12 will own HTTP mapping.

==================================================
R10 — COORDINATOR FAILURE RULE
==================================================

ReviewMediaCoordinator operates on the entire requested image batch.

Expected sequence:

inspect all inputs
-> reserve durable batch
-> upload operations
-> record confirmed upload results
-> return uploaded batch references for future Task 9 adoption

If any member cannot complete the upload stage:

- do not return a partially successful media batch for publication
- transition/recover the entire batch toward CleanupPending
- preserve confirmed metadata for already-uploaded members
- preserve all-NULL metadata for never-confirmed members
- cleanup remains operation-owned

Zero photos:

- no journal batch
- no provider call
- successful empty media preparation result

Task 9, NOT Task 8c, owns final TripReview publication/adoption.

==================================================
R11 — ADDITIVE SQL MIGRATION
==================================================

M5 explicitly allowed an additive 8c recovery migration.

If fields are required for:

- upload fence
- upload lease expiry
- cleanup fence
- cleanup lease expiry
- attempt count
- next attempt
- exhausted/manual-recovery marker
- safe provider failure classification

add them through:

- migration contract RED
- guarded additive migration GREEN
- full canonical schema
- complete inventory validation
- fresh/upgrade parity
- rerun/idempotency
- wrong-shape rollback
- existing-row preservation

Do NOT:

- rewrite existing 8b ownership
- change M1 batch semantics
- weaken M3 states
- add EF migrations
- repair incompatible DB shapes automatically

Exact SQL/EF column names may follow repository naming conventions.

Behavioral semantics above are frozen.

==================================================
R12 — TASK 8D REAL CLOUDINARY SMOKE
==================================================

After Task 8c is GREEN, execute a real opt-in development-account smoke.

Use:

- generated harmless synthetic image only
- dedicated review smoke namespace under ReviewMediaFolderRoot
- generated opaque asset identity
- no user image
- no booking/user data

Verify:

1. upload succeeds
2. returned delivery URL is HTTPS
3. provider reports/readability confirms the uploaded asset exists
4. exact asset cleanup succeeds
5. subsequent provider check confirms no smoke asset remains

No credential/public secret may be printed.

Redact/hash generated public ID in evidence if necessary.

Task 8d is COMPLETE only after this real provider smoke succeeds and residue
is confirmed absent.

A fake/stub test does NOT complete Task 8d.

==================================================


Status: **APPROVED v3 — implementation authorized within the approved Backend plan and recorded gates**
Approved: 2026-09-27 (Asia/Ho_Chi_Minh), explicit owner approval in conversation.
Workspace: D:\CapStone\Capstone_BE_tm79.
Branch: feature/datmnt-submit-trip-review.
Actor: authenticated Active Traveler.
Delivery: Backend, Flutter Mobile and responsive Next.js Web.

The 2026-10-07 owner decision supersedes the earlier commerce-only and
moderation-deferral scope. The remediation target supports typed commerce and
service booking identities, requires deterministic local BR-94 screening, and
defers only standalone itinerary review. Route pacing remains optional,
`cspRating` is deferred, and the old `poiRatings[]` child proposal is not the
canonical POI-review path. Historical verification of the existing commerce
subset remains valid evidence but is not the target contract.

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

### D2 — Identity and completed-state authority (superseded by R1 freeze)

Recommend a staged first backend contract for commerce.Bookings. This is a
proposed delivery subset, not full coverage of every booking in Report 3:

| Record | Proposed handling |
| --- | --- |
| Owned commerce booking + valid TourSchedule | Booking.status=Completed is completion authority; Tour is resolved through the schedule. No extra TripSession requirement. |
| Owned commerce booking + itinerary only | Booking.status=Completed is completion authority; verify itinerary ownership. Overall review describes that booked trip; no fabricated Tour aggregate. |
| Commerce booking with both links | One review for the booking; Tour is the overall aggregate target. Verify itinerary ownership and any BookedTour source_tour_id consistency. Itinerary provides POI/CSP context only. |
| Both links absent, missing referenced subject, contradictory ownership | Context unavailable/inconsistent; no write. Do not guess a subject. |
| Standalone itinerary/TripSession without booking | Deferred; no fabricated booking or standalone itinerary review identity. |
| commercial.ServiceBookings | Planned canonical support through `(serviceBooking, service_booking_id)` and the distinct `/api/v1/service-bookings/{serviceBookingId}/review` route; reviewable only when `Services.poi_id` resolves the authoritative POI subject. |

No additional payment, itinerary-status or TripSession-state gate is inferred.
Completion workflows are dependencies owned elsewhere; seeded Completed
fixtures validate review behavior but do not prove those workflows.

**R1 scope — approved 2026-10-07:** remediation supports both
`commerce.Bookings` and `commercial.ServiceBookings` through distinct typed
identities and routes. Standalone itinerary-only review remains deferred. An
authoritative booking-linked itinerary remains a compatibility subject only and
has no public aggregate. Canonical POI contribution comes from a published
POI-subject TripReview, not from the current POI child array.

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
to the stored value. Do not silently drop it or run write logic.
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

### D4 — Deterministic local content screening (updated by R1)

Report 3 §3.7.2 / BR-94 requires written review content to be screened before
publication, including create and qualifying edit flows. Ordinary field
validation and image inspection do not satisfy that requirement. The
2026-10-07 owner decision supersedes the 2026-09-28 deferral and selects the
deterministic local `tm79-review-text-v1` policy described in the controlling
R1 section. No external provider, key, network call or production review-data
transfer is required.

Only `Accepted` may publish. `Rejected` returns
`400 trip_review.policy_rejected`; `Unavailable` fails closed with
`503 trip_review.policy_unavailable`. Create screens the normalized title and
content before publication. Edit re-screens when normalized written content
changes or the stored policy version differs from the active version. A failed
screening edit preserves the previously accepted review unchanged. Logs must
not contain review text or matched offending text.

The existing six-category proposal is the approved implementation baseline for
Vietnamese, English and mixed-language content. It is an implementation policy,
not a taxonomy asserted to come from Report 3. The policy, test corpus and
historical provider research remain useful inputs, but R2 must implement and
verify the local deterministic evaluator before publication behavior can be
claimed complete.

**Task 6b reconciliation:** historical `PolicyVersion = null` continues to mean
that no policy approval was applied to that publication. Those rows are not
rewritten to claim screening. A successful new publication or qualifying edit
must persist exactly the active version; a rejected or unavailable decision
must not persist a fake version or clear the previous accepted version. The
nullable schema remains necessary for truthful historical data.

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

The earlier `poiRatings[]` proposal used explicit Visited items on a
booking-linked owned itinerary. R1 defers that child-rating extension and it is
not the canonical POI-review or POI-aggregate path. Its historical G-VISITS
evidence gate remains relevant only if the extension is separately reactivated.

The canonical POI subject now comes from a completed owned `serviceBooking`
whose Service has a non-null authoritative `poi_id`. Recheck that relationship
in the final transaction. Do not infer a POI from visits, Tour templates, GPS,
names, categories, coordinates, text or client input. Missing canonical POI
context returns `trip_review.unsupported_subject`; master-data correction is
outside TM-79.

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

- `social.TripReviews` remains the canonical parent. R3 makes `booking_id`
  nullable, adds nullable `service_booking_id` and `poi_id`, and enforces exactly
  one typed reviewable-record parent plus exactly one aligned subject. Preserve
  author, publication/policy metadata, approved optional values, UTC timestamps,
  media ownership and rowversion.
- Separate filtered unique indexes protect non-null commerce and service parent
  IDs. Namespace-safe locks serialize `commerce-booking:<id>` independently
  from `service-booking:<id>`, including when their numeric IDs are equal.
- The earlier nullable `trip_review_id` POI-child direction is not required for
  canonical service-to-POI reviews. Do not create a legacy Tour or POI mirror
  row for a new canonical parent. Existing child/evidence schema remains
  historical unless the deferred `poiRatings[]` extension is reactivated.
- Preserve old social.Reviews rows, IDs, ratings and original timestamps.
  Legacy linkage is NULL; do not synthesize title, consent or moderation state.
  Proposed compatibility policy preserves their existing rating visibility;
  this does not certify that legacy content passed the new content policy.
- Before POST, check the typed canonical parent and applicable safe legacy
  identity. Recheck under the corresponding namespace-safe write lock in the
  transaction.
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
- POI: published canonical POI-subject TripReviews UNION ALL safe non-overlapping
  legacy POI rows. The deferred `poiRatings[]` children do not count as the
  canonical POI-review path.
- Exclude route/CSP/scenic/photo dimensions from overall star averages.
- Preserve every safe legacy contribution; do not assume the same author and
  subject means duplicate. Canonical/legacy overlap for the same typed
  authoritative booking is an invariant violation handled by explicit
  canonical-wins exclusion and observability, never hidden with `DISTINCT`.
- Edit replaces the new overall contribution, count unchanged; rollback leaves
  averages unchanged. Empty average=null, count=0; use existing POI rounding.
- Stable feature ProblemDetails codes. Do not overwrite shared MSG catalogue
  entries; reconcile source identifiers in Docs by section and rule text.

## 3. API and transaction outline

### Task 8b media-only addendum — APPROVED, implementation authorized

Preparation authorized by the owner on 2026-09-29 after local Task 8a commit
`0a29671d9ccccf91f7b1488a3766c38d7400caa7`. The subsequent owner instruction
explicitly approved M1–M5 and continuous execution of 8b.1–8b.4. Existing v3
rules remain in force. These are owner-approved implementation decisions,
not claims that Report 3 specifies these storage details.

**Boundary:** extract only media prerequisites from Tasks 2b/3b/4b. No changes
to social.Reviews, POI children, visit evidence, legacy rows, API wire contract,
Cloudinary integration, workers, publication handlers or review moderation.
G-VISITS/G-LEGACY remain OPEN; Task 7 remains DEFERRED. An isolated SQL test
parent is persistence evidence, not a completed submission workflow.

**M1 — Durable ownership before external work.** One server-generated request
batch ID groups 1–5 asset operations. Zero photos creates no journal rows.
Each operation owns exactly one future provider asset, with a server-generated
operation ID and preallocated review-only public identifier persisted before
any upload. IDs are never accepted from the client, reused for another request,
or taken from TourMedia. Duplicate submissions may have separate batches;
batch IDs do not introduce an HTTP idempotency-key promise. Adopt the entire
batch, not a client-selected subset. Existing early-duplicate/final-lock rules
remain owned by Task 9.

**M2 — Approved SQL objects and shape.** SQL stores metadata only. Technical
string sizes below are approved storage bounds, not additional image limits.
All UTC instants use datetime2(7)/AsUtcDateTime2; concurrency uses rowversion.
All fields are NOT NULL unless explicitly marked nullable below. Use
`Latin1_General_100_BIN2` for public_id, state, content_type and extension;
use `Vietnamese_100_CI_AS` for delivery_url. No invented default timestamps,
state or ownership; trusted application code supplies them explicitly.

| Object | Approved fields and constraints |
| --- | --- |
| social.TripReviewMediaOperations | operation_id uniqueidentifier PK; batch_id uniqueidentifier; booking_id/traveler_user_id bigint restrictive FKs to existing booking/user; sort_order tinyint 0–4; public_id varchar(255) case-sensitive BIN2 unique, nonblank; state varchar(16); content_type varchar(10) restricted to image/jpeg, image/png, image/webp; extension varchar(5) canonical .jpg/.png/.webp and consistent with MIME; input_byte_length bigint 1–5,000,000; stored_byte_length bigint nullable positive; width/height int positive with bigint product <=24,000,000; delivery_url nvarchar(2048) nullable nonblank; created_at/updated_at datetime2(7); uploaded_at/adopted_at/cleaned_at nullable datetime2(7); version rowversion. Unique (batch_id, sort_order); index (state, updated_at, operation_id) for future recovery. |
| social.TripReviewMedia | media_id bigint IDENTITY PK; trip_review_id bigint restrictive FK; operation_id uniqueidentifier restrictive FK and UNIQUE; sort_order tinyint 0–4; created_at datetime2(7); unique (trip_review_id, sort_order). Metadata comes from the immutable adopted operation, not duplicated mutable URL/public-ID columns. |

Use FK NO ACTION (no cascade) for both objects and their parent references:
normal review editing never removes/replaces photos, and SQL deletion must not
erase evidence needed to identify provider assets. No purge, retention period,
review hard-delete or public removal workflow is introduced. Existing rows
are preserved; migration must fail rather than repair incompatible shapes.
Do not apply a 5m CHECK to re-encoded stored bytes: Task 8a bounds input bytes,
and normalized output can differ. No raw bytes, secrets or user filenames in SQL.

**M3 — State machine and null consistency.**

- Reserved -> Uploaded -> Adopted (normal path).
- Reserved or Uploaded -> CleanupPending -> Cleaned (failed/abandoned path).
- CleanupPending never returns to Uploaded/Adopted. Adopted is immutable and
  never eligible for failed-request cleanup. Cleaned is terminal; retain its
  ownership tombstone. Unsupported transitions fail deterministically.
- Reserved: URL/stored size/upload/adopt/clean timestamps all NULL.
- Uploaded/Adopted: URL, stored size and uploaded_at required; adopted_at
  required only for Adopted; cleaned_at NULL.
- CleanupPending/Cleaned: preserve either the full known upload metadata tuple
  or an all-NULL tuple for an uncertain/never-confirmed upload; adopted_at NULL;
  cleaned_at required only for Cleaned. Never invent an upload-success URL.
- CHECKs enforce enum/ranges/metadata/timestamp null consistency; persistence
  conditional updates enforce legal transitions and optimistic concurrency.
  Cross-table adoption and batch ownership are transactional store invariants,
  not a claim that CHECK constraints protect arbitrary direct SQL writers.

**M4 — SQL-only persistence contract.** A feature-specific journal port exposes
reserve-batch, record-upload-result, adopt-batch and mark-cleanup-pending.
No generic repository or provider SDK in Application. Results distinguish
success, missing/foreign operation, invalid state, metadata conflict and stale
version without defining new public HTTP codes.
Reserve validates one booking/owner per batch and unique contiguous slots 0..n-1;
verify that the booking actually belongs to that authenticated owner, not just
that repeated input IDs match. All rows commit atomically. Record-upload accepts only Reserved with the expected
rowversion; an exact persisted-result replay is a no-op success, conflicting
metadata or a terminal state is not. Validate HTTPS delivery metadata in the
trusted adapter/store boundary; provider origin/namespace validation belongs
to 8c, not arbitrary client URLs.

Adopt uses the caller's existing SQL transaction; it must never open/commit an
independent transaction around the publication write. Check the entire batch
matches the parent's booking/author, every row is Uploaded, all versions match,
and no media is already linked elsewhere. Insert links using the new parent's
navigation (not default identity ID); update every operation to Adopted in the
same transaction. Any failure rolls back parent, links and state changes.
This includes rejected business-result paths, not only SQL exceptions: the
store must roll back the supplied publication transaction before returning
failure; the caller must not subsequently commit an orphan parent. The port
documents this transaction ownership consequence and tests assert it.
Replay succeeds only when the complete same batch is already adopted by the
same parent. Mixed/partial adoption is a conflict, not repaired silently.

Adoption versus mark-cleanup-pending must serialize through the same operation
row locks, acquired in stable operation-ID order. Two independent contexts
must demonstrate exactly one winner, no partial batch and no adopted asset
eligible for cleanup. Expected rowversions fence stale updates. Ownership and
published-parent checks are not delegated to client input.

Mark-cleanup-pending operates on the entire persisted batch under the same
transaction/ordered locks. Verify booking/owner and exact operation/version
set; require every row to be Reserved or Uploaded, allowing a mixed batch
after partial upload. Transition all rows atomically, preserving known metadata.
An exact already-entirely-CleanupPending replay is a no-op success for the same
owner/batch, without rewriting versions. Any Adopted/Cleaned row, mixed pending
and active state, missing/extra row, foreign owner or stale expected version
rejects the whole transition. Replay exceptions for record-upload/adoption/
cleanup marking apply only to the identical persisted outcome, not conflicting
arguments. No transition performs a provider delete in 8b.

**M5 — Provider recovery boundary.** 8b proves durable SQL transitions only.
It does not claim that an unconfirmed upload is absent or that cleanup occurred.
Do not expose a production mark-Cleaned path until 8c defines verified provider
deletion/reconciliation, in-flight late-upload handling, leases/fencing, retries
and scheduling policy. 8c may add a separately reviewed additive migration for
those fields; no guessed timeout/retention/lease constants in 8b. 8d alone proves
real upload/readability/cleanup. A test-only SQL lifecycle fixture is not smoke.

M1–M5 and the media-only sequence are approved. Task 8b is COMPLETE;
final evidence is recorded in the verification ledger before completion.

Subject to G-SCOPE; routes use commerce.Bookings identity only:

- GET /api/v1/bookings/{bookingId}/review: owned context, subject, capabilities
  and reasons, eligible POIs, distinct route-pacing/CSP eligibility, new/legacy existing state,
  editable field list, deadline/version, owner-only display-name preview.
- POST same route: multipart metadata plus 0–5 new files; 201 direct DTO.
- PUT same route: JSON overallRating/title/content/publishDisplayName/version;
  200 direct DTO. Existing media/POI/C4 returned unchanged.

401 unauthenticated; 403 inactive/wrong role; 404 missing/foreign booking;
400 invalid input; 409 duplicate/stale/not-completed/expired/inconsistent
context; 413 body limit; 503 unavailable storage (not policy-provider outage).
Freeze concrete feature codes and DTO discriminators in Task 1.

Create business flow:

1. Authenticate and read the owned booking/subject context.
2. EARLY duplicate check: new TripReview plus unlinked legacy Reviews.booking_id.
   A known duplicate returns 409 before media inspection,
   operation-journal allocation or Cloudinary calls.
3. Normalize title/content once; validate normalized input/structural limits
   and field eligibility; inspect new image bytes through the media boundary.
4. Journal and upload request-owned media where applicable.
5. Short SQL transaction: acquire booking write lock, recheck owner/completion,
   new+legacy duplicate, route/POI/CSP evidence; persist the SAME normalized
   values, parent/children and media adoption; commit.

HTTP authentication/binding/malformed-body and size checks can precede the
business flow. The early duplicate check is an optimization and never replaces
the locked transactional recheck: a duplicate may appear during media work.
Test that race and compensate only loser-owned assets. Network calls remain
outside SQL locks. Edit uses strict JSON -> owned review/version/window checks
-> normalization/structural validation -> transactional rechecks/save; it has
no upload/cleanup or content-screening side effect.

## 4. Delivery and verification

The historical BE Tasks 0–13 are an intermediate deliverable; the remediation
sequence is R1–R9 in the plan. UC-32 entry/return, real completion data and
review media each need evidence, not fixture-only claims. R2 implemented the
approved deterministic local BR-94 policy, and R3–R7 implemented the remaining
Backend contract. R1 itself was the documentation-only freeze.
Client UI uses actual context, field eligibility and immutable edit fields.
No hardcoded booking/ratings/POIs/identity or fabricated moderation approval.

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

The owner approved D1–D7 and the staged v3 plan on 2026-09-27, then approved
the R1 remediation freeze on 2026-10-07. The later decision supersedes the
commerce-only scope and moderation deferral without erasing their historical
evidence. Current remediation includes typed ServiceBooking-to-POI support and
deterministic local screening. Standalone itinerary review, `cspRating`, and
`poiRatings[]` as a canonical POI-review path remain deferred. G-LEGACY still
gates legacy rollout/edit compatibility; G-VISITS applies only to the deferred
POI-child extension.

Excluded: booking/payment/completion/visit-writing workflows, full UC-32,
admin moderation UI/appeals, operator replies, CSP engine changes, review-photo
replacement after publish, and extra TM-70 search/detail APIs.
No commit, push, PR or merge without explicit owner request.
See plans/TM-79-plan.md and docs/TM-79-implementation-prompt.md.

## 6. Future backlog, not current TM-79 implementation

- **External moderation provider:** the deterministic local
  `tm79-review-text-v1` evaluator is the current R2 requirement. Replacing or
  supplementing it with an external provider remains a future decision that
  would require separate data-handling, configuration and regression evidence.
- **Manual post-publication review moderation:** proposed future workflow is
  Traveler report -> Administrator inspect/keep/logically hide or remove ->
  hidden/removed reviews no longer public or counted in published aggregates.
  This is a direction for a separate use case/contract, **not** an existing
  TM-79 feature. No Review Report/Review Moderation/Remove Review use case or
  matching review-report API/schema was identified in the inspected Report 3
  sections and current BE source/schema. Report 3's administration moderation
  examples concern accounts/tour posts; MSG124's generic “Review removed
  successfully” text alone does not define the missing workflow.

The later feature must decide report source, moderator authority, reason,
actor/timestamp/audit, exact logical status and aggregate exclusion, and any
restoration/appeal behavior. Prefer a logical state over destructive hard
delete for audit, but do not freeze names/enum values, endpoints or columns
here. No UC number or Jira ID is assigned by this note.
