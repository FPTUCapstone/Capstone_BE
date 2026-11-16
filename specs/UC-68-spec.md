# UC-68 Submit Post-Trip Complaint — Backend Specification

## Status

Approved. This supersedes the obsolete Audit Log scope previously
stored under this UC number. It implements only Traveler complaint submission.
UC-69 responds to the complaint, UC-75 handles a Traveler decision on a
partial-refund proposal, and UC-42/UC-67 handle an approved refund.

## SRS scope

An authenticated Traveler submits one complaint for an eligible own booking,
with a required category and detail and optional evidence. The system stores
evidence privately, creates an `AwaitingOperatorResponse` complaint, derives
the response deadline required by UC-69, and notifies the owning Tour Operator.

The feature does not create a refund, decide a complaint, or implement
Staff/Admin dispute handling. The approved product extension below permits the
Traveler to view their own private evidence and append evidence once; UC-69
will later provide the equivalent authorised Tour Operator read.

## Rules and eligibility

| SRS rule | Server behaviour |
| --- | --- |
| BR-147 / MSG157 | Caller owns a booking in `Confirmed` or `Completed` status and submits no later than three days after the tour ends. Otherwise, make no mutation. |
| BR-148 / MSG158 | Exactly one complaint exists per booking. A duplicate returns the existing complaint reference without creating records or duplicate notification. |
| BR-149 / MSG159 / MSG160 | Evidence is optional; at most five files, each no larger than 10 MB, no more than 30 MB total, and only JPG, PNG, WebP or PDF after server-side content validation. |
| MSG161 | A successful atomic submission is recorded and the owning Tour Operator is notified. |
| MSG127 | If private evidence storage or persistence cannot complete, record no complaint and make the staged evidence unavailable/eligible for cleanup. |

### Approved product extension — one evidence amendment

The original complaint reason category and detail are immutable. The owning
Traveler may append evidence **once only**, while the complaint remains
`AwaitingOperatorResponse` and before the original three-calendar-day
submission deadline. The combined initial and appended evidence remains subject
to BR-149: at most five files, 10 MB per file and 30 MB total. A successful
append records its timestamp, creates an immutable audit entry and notifies the
Tour Operator. It does not reset either deadline.

Reason category is required and must be one of: `ItineraryNotAsAdvertised`,
`ServiceQuality`, `GuideOrStaffConduct`, `SafetyIssue`,
`ScheduleChangeOrDelay`, or `Other`. Reason detail is required after trimming
and has a maximum length of 2,000 characters.

## Required decisions before implementation

The approved cross-UC policy is: a plain SRS "day" is a calendar day rather
than a working day; the authoritative tour end is `TourSchedule.EndAtUtc`; and
`RefundableAmount` is captured once at submission as
`max(0, VerifiedPaidAmount - total refund amount already committed for the
booking)`. The snapshot is immutable for UC-69 and UC-75. A committed refund
includes a refund already created for the booking, not only a gateway-processed
refund, so concurrent workflows cannot over-refund the verified payment.

## Domain and persistence model

Add `PostTripComplaint` and `ComplaintEvidence` under the commerce complaint
model, shared by UC-68, UC-69 and UC-75.

`PostTripComplaint` contains at least:

- `BookingId` (unique), traveler owner, status, reason category/detail,
  `SubmittedAtUtc` and immutable `ResponseDeadlineUtc`;
- a response-related shape reserved for UC-69/UC-75: action, proposed amount,
  rejection reason, operator/traveler response timestamps and deadlines;
- an immutable `RefundableAmount` snapshot for UC-69/UC-75, captured with the
  approved formula in the preceding policy;
- nullable complaint-created `RefundId`, nullable `EvidenceAmendmentUsedAtUtc`
  and a row-version concurrency token.

`ComplaintEvidence` contains only private storage metadata: complaint FK,
original safe display name, validated content type, byte size, opaque storage
object identifier, checksum and creation time. It never stores a public or
permanent delivery URL. The database enforces one complaint per booking, valid
status/category values, evidence FK/indexes and a positive bounded byte size.

## API contract

The authenticated Traveler may first read the server-authoritative submission
context and any existing safe complaint detail:

```http
GET /api/v1/traveler/bookings/{bookingId}/post-trip-complaint
```

It returns booking/tour timing, `canSubmit`, a safe ineligibility code and an
existing complaint's category, detail, status, timestamps and evidence metadata
only. It never returns an evidence storage key, URL, refundable amount or a
refund. A foreign or missing booking remains `404`.

```http
POST /api/v1/traveler/bookings/{bookingId}/post-trip-complaints
Authorization: Bearer <Traveler JWT>
Idempotency-Key: <UUID>
Content-Type: multipart/form-data
```

Multipart fields:

- `reasonCategory` — required enum value;
- `reasonDetail` — required trimmed string, maximum 2,000 characters;
- `evidence` — zero to five file parts.

The command response returns the complaint ID/reference, booking ID, status,
submission time and response deadline. It must not return a storage key,
permanent evidence URL, payment-gateway data, or a client-supplied monetary
value.

The API follows the platform `Result`/RFC-7807 error shape and requires an
idempotency key. An identical replay returns the original result; the same key
with a different logical payload returns conflict. The database unique
constraint remains the final duplicate-submission defence.

The authenticated Traveler may list safe summaries outside Trip History:

```http
GET /api/v1/traveler/post-trip-complaints
```

The endpoint returns complaint/booking IDs and code, tour title, status,
timestamps and evidence count only.

```http
POST /api/v1/traveler/bookings/{bookingId}/post-trip-complaint/evidence
Authorization: Bearer <Traveler JWT>
Idempotency-Key: <UUID>
Content-Type: multipart/form-data
```

This one-time append endpoint accepts only `evidence` parts and validates the
aggregate limits against files already stored for the complaint.

```http
GET /api/v1/traveler/bookings/{bookingId}/post-trip-complaint/evidence/{evidenceId}/download
Authorization: Bearer <Traveler JWT>
```

It verifies ownership before returning a short-lived private delivery URL or a
private local-development stream. It never exposes the storage key.

## Security and storage

- Require an active Traveler; load booking, traveler ownership, status,
  verified-payment data and schedule end server-side.
- Return the existing forbidden/not-found shape without revealing another
  Traveler's booking or complaint.
- Reject unsupported media by byte-signature/content inspection as well as
  declared MIME type and filename. Do not trust a browser MIME value.
- Stream uploads with request/file limits; do not buffer arbitrary files in
  memory. Enforce limits before durable storage where possible and again on
  the validated stream size.
- Store evidence in private object storage under an opaque server-generated
  key. Logs, audit metadata and responses exclude raw binary, public URL and
  security credentials.
- If database commit fails after a storage write, enqueue/retry compensating
  deletion using the repository's durable media-cleanup convention. A failed
  cleanup never makes an object publicly readable.

## Atomicity, audit and notification

Within one serializable write boundary, re-check eligibility and uniqueness,
  persist the complaint/evidence metadata and immutable audit entry. Create a
  durable post-commit notification/outbox
event to the owning Tour Operator. A notification delivery retry must not
create a second complaint. The storage coordination must guarantee the SRS
outcome: evidence-storage failure leaves no complaint; a failed database write
does not leave accessible evidence.

## Acceptance criteria

1. Only the owning authenticated Traveler can submit for an eligible
   `Confirmed` or `Completed` booking inside the approved complaint window.
2. The handler validates category, trimmed detail, five-file/10-MB/30-MB
   limits and actual allowed content before persistence.
3. One booking produces at most one complaint under duplicate requests and
   concurrent submissions; an identical idempotent replay returns the first
   result.
4. A successful complaint has status `AwaitingOperatorResponse`, a stable
   server-derived response deadline, private evidence metadata and an audit
   record; it emits exactly one durable operator notification event.
5. Evidence-storage/persistence failure creates no accessible evidence or
   complaint, and retry is safe.
6. The response exposes no storage key, permanent evidence URL, payment
   credential, operator-owned field or recomputed refund amount.
7. The eligible owner may append evidence exactly once without changing the
   complaint's original terms or either deadline; aggregate limits,
   idempotency, audit and operator notification are atomic.
8. The owner can list complaints outside Trip History and view their own
   evidence only through authorised private delivery.
9. Unit, API authorization/idempotency, private-storage and SQL Server
   migration tests cover normal, boundary, failure and concurrency paths.

## Out of scope

- Tour Operator response and its read surface (UC-69).
- Traveler partial-proposal acceptance/decline (UC-75).
- Refund confirmation, operator return share or gateway processing (UC-42,
  UC-67).
- Admin/Staff contract dispute handling and public evidence downloads.
