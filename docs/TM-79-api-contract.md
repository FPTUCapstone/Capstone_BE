# TM-79 Backend API contract — Task 1

Status: frozen for the approved commerce.Bookings first delivery, 2026-09-28.
Authority: `specs/TM-79-spec.md` v3 (D1–D7, approved G-SCOPE),
`plans/TM-79-plan.md`, Report 3 §3.7.2. This document freezes the public
boundary, not an implemented endpoint. G-POLICY, G-VISITS and G-LEGACY remain
open evidence gates. Tasks 2–13 must not claim gated behavior complete from a
fixture or test double.

## 1. Route, authentication and supported identity

`GET`, `POST`, `PUT /api/v1/bookings/{bookingId}/review` require an authenticated
Active Traveler. `bookingId` is a positive signed 64-bit ID in
`commerce.Bookings`, never a `commercial.ServiceBookings` ID. Identity comes
from auth, never a body field. Missing/foreign booking is 404 without revealing
ownership. Wrong/inactive role is 403; absent/invalid session is 401.

`commerce.Bookings.status = 'Completed'` is the completion authority. No
payment, itinerary-status or TripSession-state requirement is added. A valid
TourSchedule resolves a Tour. An itinerary-only booking has a reviewable trip
but no Tour aggregate. With both links, the Tour remains the overall target;
the itinerary must be owned by the same Traveler and any `BookedTour`
`source_tour_id` must agree with the scheduled Tour. Neither link, broken links
or contradictory ownership/source is an inconsistent context (409 on writes;
GET returns an unavailable capability/context without foreign data). A
ServiceBooking or standalone no-booking CSP itinerary is outside this route;
the staged delivery is not full UC-33.

### Wire conventions

- All JSON keys are exact `camelCase`; enum/discriminator values below are
  case-sensitive. Success returns direct DTOs, not `{success,data,...}`.
- Defined nullable response properties are present with JSON `null`; arrays
  are present as `[]`, including capability-unavailable lists. There is no
  ambiguous omission of a defined response field.
- Timestamps are ISO 8601 UTC strings with `Z`. Deadline calculations use UTC;
  clients display in `Asia/Ho_Chi_Minh`. IDs are JSON integers (`int64`).
- Failures are RFC 7807 ProblemDetails with numeric `status` and stable string
  `errorCode`. Validation failures may additionally contain field `errors`.
  Human-readable `title`/`detail` are not client routing keys.
- No Idempotency-Key or If-Match protocol is promised. A lost POST response is
  recovered by owner GET; a subsequent POST may return 409 even if the original
  committed. Clients must not infer which payload won from 409 alone.

## 2. GET owner context and DTO dictionary

GET 200 returns this direct owner-only `TripReviewContext` shape. It remains
readable after the edit deadline. Ineligible capabilities never imply a user
actually visited zero POIs.

| Property | Wire type / nullability | Meaning |
| --- | --- | --- |
| `bookingId` | integer, required | Owned commerce booking. |
| `bookingStatus` | string, required | Existing DB value: `PendingPayment`, `Confirmed`, `Cancelled`, `Completed`, or `Expired`; not a new enum. |
| `subject` | object or null | `{ "kind": "tour", "tourId": integer }` or `{ "kind": "itinerary", "itineraryId": integer }`; null if context inconsistent. Exactly one kind. |
| `canSubmit` | boolean, required | False for non-completed, inconsistent or any existing review. |
| `submitUnavailableReason` | string or null | `inconsistentContext`, `legacyConflict`, `alreadyReviewed`, `bookingNotCompleted`; null only when `canSubmit=true`. |
| `existingReviewKind` | string, required | `none`, `new`, or `legacy`. |
| `review` | `TripReview` or `LegacyReview` or null | Null iff kind is `none`; discriminated by `existingReviewKind`. |
| `routePacing` | capability object | `{ "available": boolean, "reason": string|null }`. |
| `cspRating` | capability object | Same shape, independently derived. |
| `poiRatings` | capability object | Same shape; not inferred from pacing. |
| `eligiblePois` | array of `{ "poiId": integer, "name": string }` | Distinct verified Visited POIs only; `[]` while G-VISITS open. |
| `editableFields` | string[] | New review within window: `overallRating`, `title`, `content`, `publishDisplayName`; otherwise `[]`. |
| `displayNamePreview` | object | `{ "initials": string, "fullNameWithConsent": string|null }`; owner-only, never part of public DTO. |

Capability reason strings are exact: route `routeContextUnavailable` or
`bookingNotCompleted` or `inconsistentContext`; CSP `cspProvenanceUnavailable`
or `bookingNotCompleted` or `inconsistentContext`; POI
`visitEvidenceUnavailable` or `bookingNotCompleted` or `inconsistentContext`.
`reason=null` means available. If several conditions hold, choose one
deterministically: `submitUnavailableReason` precedence is
`inconsistentContext` > `legacyConflict` (ambiguous/conflicting legacy rows) >
`alreadyReviewed` > `bookingNotCompleted`; each capability reason precedence
is `inconsistentContext` > `bookingNotCompleted` > its own provenance/evidence
reason. Thus an owned incomplete booking with an existing unambiguous review
reports `alreadyReviewed`; a broken link always reports
`inconsistentContext`. These are feature-scoped reason values, not
shared MSG catalogue IDs. Do not synthesize a reason from TourMatch scores,
planned `Kind=Visit` or current mutable Tour content.

`TripReview` direct success/GET shape:

| Property | Wire type / nullability | Meaning |
| --- | --- | --- |
| `kind` | string `new` | Distinguishes new from legacy. |
| `reviewId`, `bookingId` | integer, required | New parent identity. |
| `subject` | object, required | `tour`/`itinerary` shape above. |
| `overallRating` | integer 1–5 | Canonical overall score. |
| `title`, `content` | nonempty strings | Stored normalized text. |
| `routePacing` | `tooTight` / `wellPaced` / `tooLoose` or null | Independent C4 evaluation. |
| `cspRating` | integer 1–5 or null | Independent CSP quality score. |
| `poiRatings` | array `{ "poiId": integer, "rating": integer }` | Distinct eligible POI children; empty while evidence unavailable. |
| `media` | array `{ "mediaId": integer, "deliveryUrl": string }` | Server-owned review images only; no Cloudinary public ID in response. |
| `publishDisplayName` | boolean | Persisted explicit preference. |
| `publicDisplayName` | string | Persisted snapshot; safe for public exposure. |
| `publicationStatus` | string `published` | New successful submissions in this scope. No invented moderation history for legacy. |
| `createdAtUtc`, `editDeadlineUtc`, `updatedAtUtc` | UTC strings | Original creation/deadline never move on edit. |
| `version` | Base64 string | Canonical Base64 encoding of the 8 raw SQL rowversion bytes; required on PUT. No `W/`, ETag quotes or padding removal. |

`LegacyReview` has only `kind:"legacy"`, `bookingId` (integer), and
`entries` (array of `{ "reviewId": integer, "targetType": string,
"targetId": integer, "rating": integer, "comment": string|null,
"createdAtUtc": UTC string }`). `targetType` preserves existing SQL values
`Tour`, `POI`, `RouteSegment`, `Operator`; it is not the new subject kind.
Do not invent title, consent, policy, edit deadline or rowversion for old rows.
Conflicting or ambiguous legacy entries make create unavailable with
`legacyConflict`; POST maps that case to 409 `trip_review.legacy_conflict`,
whereas an unambiguous legacy-linked review maps to 409
`trip_review.duplicate`. Detailed compatibility disposition remains G-LEGACY.

## 3. POST create

`Content-Type: multipart/form-data`; exactly one UTF-8 JSON part named
`metadata` and 0–5 binary parts named `files`. `metadata` is at most 64,000
UTF-8 bytes; total HTTP body at most 26,000,000 bytes including multipart
framing. Each nonempty file is at most 5,000,000 actual bytes. Accepted media
are JPEG (`image/jpeg`, `.jpg`/`.jpeg`), PNG (`image/png`, `.png`) and **static**
WebP (`image/webp`, `.webp`) after bounded byte read, signature, full decode
and MIME/extension/content consistency check. Animated WebP, GIF, SVG, corrupt
or invalid images are rejected. Declared length alone is not authority.

`metadata` is exactly this JSON object (no `authorId`, provider ID/URL,
`media`, `newFiles` or `retainedMediaIds`):

```json
{
  "overallRating": 5,
  "title": "Chuyến đi đáng nhớ",
  "content": "Lộ trình phù hợp.",
  "poiRatings": [],
  "routePacing": null,
  "cspRating": null,
  "publishDisplayName": false
}
```

`overallRating` is required integer 1–5; `title`/`content` are required
strings. Optional `poiRatings` defaults to `[]` when omitted; explicit null
is invalid. Each entry is `{ "poiId": positive int64, "rating": integer 1–5 }`,
with distinct IDs. `routePacing` and `cspRating` are optional and default to
null; explicit null is equivalent to omission. `publishDisplayName` is optional
and defaults to false; explicit null is invalid. Unknown/create-forbidden
metadata members fail 400 `trip_review.invalid_input` rather than being used.

Title/content are trimmed **once** using the same server normalization for
POST and PUT. Validate normalized UTF-16 length (title 1–200, content
1–1000); moderate exactly those normalized strings and persist exactly the
same values. All-whitespace or wrong-type input fails before screening. A
post-moderation semantic change requires re-screening. Overall 1–5 is not
weakened by negative text or low rating.

Image resource bound: the currently fetched TM-207 inspector defines a
24,000,000-pixel area ceiling, positive width/height, and RGBA8888 decode;
the corresponding pixel buffer is at most 96,000,000 bytes (excluding codec
overhead). TM-79 reuses this authoritative pixel-area guard but applies its
own stricter 5,000,000 encoded-byte limit. No separate maximum edge dimension
is defined by TM-207; Task 8 must prove the bounded decoder behavior and may
not claim an additional edge-specific limit. This is a media implementation
dependency on `origin/develop` at `4afd56c`, not code currently in this branch.

After successful creation: HTTP 201 and direct `TripReview`, with
`Location: /api/v1/bookings/{bookingId}/review`. A known new or unlinked
legacy booking review returns 409 before moderation, inspection, journal or
upload; a final transaction rechecks new+legacy duplicate under a booking
write lock. No second Tour mirror rating row is created.

### Independent capability guards

`routePacing` needs a booking-attributable historical itinerary/route version
or booking-specific Tour-route snapshot. A TourSchedule pointing to the
**current mutable** Tour is insufficient. The present base has no confirmed
immutable booking route snapshot; therefore schedule-only pacing remains
unavailable until Task 5 establishes provenance. This capability is
independent of Visited POI evidence. Omitted/null is allowed; supplied
non-null when unavailable is 400 `trip_review.route_pacing_unavailable`.

`cspRating` needs a booking-linked owned itinerary with `source_type` =
`CSPGenerated` and a scheduling request owned by the same Traveler. Client
flags and similarity scores are never proof. Null/omitted is allowed;
non-null without provenance is 400 `trip_review.csp_ineligible`.

`poiRatings` needs historically authoritative, booking-attributable Visited
evidence, checked again in the final transaction. The current model has
planned `Kind=Visit` and a SQL `status=Visited` value, but no confirmed writer,
historical immutability or booking-attribution contract. G-VISITS is OPEN:
GET reports `visitEvidenceUnavailable` and `eligiblePois:[]`; omitted/empty
ratings are accepted for overall review, nonempty ratings fail 400
`trip_review.poi_unavailable`. No evidence schema is frozen here.

## 4. PUT edit and public identity

`Content-Type: application/json`; allowed root property names are **exactly**
`overallRating`, `title`, `content`, `publishDisplayName`, `version`. All five
are required on PUT. Types/bounds match POST; `version` is the Base64 8-byte
rowversion token from owner GET/POST. Missing/invalid token is 400; stale token
is 409. There is no partial PATCH. HTTP 200 returns direct `TripReview`.

Any unknown or create-only/immutable member, including `poiRatings`,
`routePacing`, `cspRating`, `media`, `files`, `newFiles`, `retainedMediaIds`,
`createdAtUtc` or `publicDisplayName`, returns 400
`trip_review.invalid_edit_payload` **before** moderation or write, even if its
value is null, empty, unchanged or equal to stored data. This strict binding is
limited to the TM-79 PUT request; global JSON serializer behavior is unchanged.
Invalid media content type for PUT is 415.

The edit window ends at `createdAtUtc + 7 × 24 hours`; accept only if
`nowUtc < editDeadlineUtc`, rechecked inside the final write transaction.
At equality it is read-only. Editing never extends the original timestamp or
deadline. POI/C4/media remain unchanged. Failed moderation, version conflict
or rollback keeps the prior published review intact.

`publishDisplayName=false` is the default. The public label is the first
Unicode text element of each whitespace-delimited token of the authenticated
account full name, with dots and spaces (e.g. `Nguyễn Tiến Đạt` →
`N. T. Đ.`). A blank name yields a persisted neutral `Traveler` fallback;
clients may localize that **label** for display, but stored/transport identity
is stable and no untranslated full name is exposed. `true` requires explicit
consent and snapshots the current full name as public label. With false, do
not persist a hidden full-name snapshot. Account rename alone does not mutate
the public label. During a valid edit, **changing** the preference regenerates
the snapshot from the current profile; an edit retaining the same preference
does not refresh it. Public review payloads may expose only
`publicDisplayName`, not the owner's full name, email, phone or private preview.

## 5. Error matrix

| HTTP | `errorCode` | Condition |
| --- | --- | --- |
| 400 | `trip_review.invalid_input` | Malformed/invalid POST data, missing/wrong-type/bounds, media format or invalid PUT value/version. Field `errors` where available. |
| 400 | `trip_review.invalid_edit_payload` | Any unknown/immutable PUT member, including null/unchanged. |
| 400 | `trip_review.route_pacing_unavailable` | Non-null pacing without authoritative route provenance. |
| 400 | `trip_review.csp_ineligible` | Non-null CSP rating without server-proven CSP provenance. |
| 400 | `trip_review.poi_unavailable` | Nonempty POI scores while visit evidence capability unavailable. |
| 400 | `trip_review.poi_ineligible` | POI ID not in currently verified eligible set. |
| 400 | `trip_review.policy_rejected` | Approved content policy rejects normalized title/content. |
| 401 | `trip_review.unauthorized` | Missing/invalid auth; no booking disclosure. |
| 403 | `trip_review.forbidden` | Inactive/wrong role. |
| 404 | `trip_review.booking_not_found` | Missing or foreign booking. |
| 409 | `trip_review.duplicate` | New parent or legacy booking-linked row already exists. Recover with GET. |
| 409 | `trip_review.stale_version` | PUT token no longer current. |
| 409 | `trip_review.edit_expired` | PUT at/after original seven-day deadline. |
| 409 | `trip_review.booking_not_completed` | POST on owned non-Completed booking. |
| 409 | `trip_review.inconsistent_context` | Broken/contradictory booking/subject references. |
| 409 | `trip_review.legacy_conflict` | Ambiguous/conflicting legacy booking rows; no auto-repair. |
| 413 | `trip_review.body_too_large` | Total POST body, metadata or file byte bound exceeded. |
| 415 | `trip_review.unsupported_media_type` | Unsupported endpoint Content-Type. |
| 503 | `trip_review.policy_unavailable` | Approved policy provider unavailable/timeout; no publication. |
| 503 | `trip_review.storage_unavailable` | Image storage unavailable; no partial published review. |

Generic unexpected failures remain sanitized 500 ProblemDetails via the
repository mapper, not a falsely successful feature code. Existing shared MSG
catalogue identifiers are not modified or redefined.

## 6. Publication, aggregation and legacy gates

Only a successful approved-policy create is published. G-POLICY must still
provide policy version, prohibited categories, supported languages, legitimate
negative-review examples, rejection/unavailable mapping, owner-approved
corpus, and production implementation choice before Task 7. Behavior for
unsupported languages is **unresolved**; neither auto-accept nor auto-reject
is promised. An always-allow double cannot satisfy publication acceptance.

New parent overall scores contribute to a Tour only when Published and the
subject is that Tour. Tour average/count = published new Tour parents **UNION
ALL** unlinked legacy `social.Reviews` of `target_type=Tour`. Itinerary-only
overall scores do not contribute to a Tour aggregate. POI average/count =
unlinked legacy POI rows **UNION ALL** linked POI children with Published
parent. Linked children are not double-counted. Route pacing/CSP/scenic/photo
scores never enter overall averages. Empty aggregate is `{average:null,
count:0}`; POI rounding follows the existing POI query. Editing replaces one
overall contribution without changing count; rollback changes none.

Old rows keep original IDs, timestamps, scores and rating visibility without
fabricated consent, title or moderation state. Any unlinked legacy row with
the same booking ID blocks POST. Same-author reviews on distinct bookings are
not deduplicated. A new-parent/legacy overlap on one booking is an invariant
violation to report, not conceal with `DISTINCT`. G-LEGACY still needs an
authorized non-production sample and disposition for within-window legacy
edits; this contract does not silently make those records read-only.

## 7. Acceptance matrix and ownership

| Contract rule / cases | Planned test owner |
| --- | --- |
| Fresh/upgrade full inventory parity, idempotency, wrong-shape rollback, legacy preservation, rating/unique/FK/UTC constraints | Task 2 RED, Task 3 GREEN |
| Parent/child navigation, version and UTC round-trip in new context; separate C4 fields; no Tour mirror | Task 4 |
| Owned/foreign/non-Completed schedule-only, itinerary-only/both, stale/missing links; separate route/CSP/POI capabilities and unavailable reasons | Task 5 |
| Rating/text/UTF-16 and trim boundary, null/omitted, duplicate POI, file count/declared bytes, normalization identical for moderation | Task 6 |
| Approved allow/reject corpus, negative reviews, languages, provider timeout/unavailable and policy version; G-POLICY must close first | Task 7 |
| Actual byte/decode/resource limits, MIME/static WebP/polyglot fixtures, request-owned media journal and cleanup/provider smoke | Task 8 |
| Early new+legacy duplicate with zero expensive calls; transaction/barrier race, loser cleanup, rollback, 409 + GET recovery | Task 9 |
| Strict before/equal/after deadline, body rowversion stale/missing, snapshot preference changes, edit failure preserves publication | Task 10 |
| New/legacy Tour and POI sums, no linked-child double count, itinerary-only exclusion, mixed/overlap and edit count | Task 11 |
| Real auth/status/DTO, multipart 201, closed JSON PUT, immutable null/empty/unchanged rejection, scoped OpenAPI, body/metadata 413 | Task 12 |
| Full SQL/provider regression, real create→read→edit→aggregate, explicit gate disposition and reproducible handoff | Task 13 |
| Mobile/Web typed parser, nullable C4/capability state, duplicate GET recovery, immutable edit UI, Asia/Ho_Chi_Minh display and error preservation | Tasks 14–16; Task 17 cross-repo UAT |

### OpenAPI target (Task 12, not implemented here)

The PUT request schema is closed: five required properties, no additional
properties (`additionalProperties: false` or equivalent scoped schema filter).
The runtime endpoint must enforce this independently of documentation.
POST uses `multipart/form-data` with the JSON `metadata` part and repeating
`files` parts; PUT consumes JSON only. Three direct success schemas and
ProblemDetails status/errorCode responses must match this document. Do not
apply a strict unmapped-member policy to unrelated endpoints.

### Cross-repository Docs handoff

Canonical central Docs target is `Capstone_Docs/api/contracts/submit-trip-review-api.md`
on a **dedicated Docs branch/PR**. This Backend commit does not create a fake
Docs path or mutate another repository. Copy/reconcile this exact contract
after checking the Docs repo's own instructions and current API standard;
record both commit SHAs in the future cross-repo handoff. The dependency
remains open until the real Docs artifact exists and matches runtime/OpenAPI.
