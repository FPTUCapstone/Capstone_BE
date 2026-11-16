# UC-68 Submit Post-Trip Complaint — Backend Specification

## Status

Draft for approval. This supersedes the obsolete Audit Log scope previously
stored under this UC number. It implements only Traveler complaint submission.
UC-69 responds to the complaint, UC-75 handles a Traveler decision on a
partial-refund proposal, and UC-42/UC-67 handle an approved refund.

## SRS scope

An authenticated Traveler submits one complaint for an eligible own booking,
with a required category and detail and optional evidence. The system stores
evidence privately, creates an `AwaitingOperatorResponse` complaint, derives
the response deadline required by UC-69, and notifies the owning Tour Operator.

The feature does not create a refund, decide a complaint, expose evidence to a
client other than through a later authorised read, or implement Staff/Admin
dispute handling.

## Rules and eligibility

| SRS rule | Server behaviour |
| --- | --- |
| BR-147 / MSG157 | Caller owns a booking in `Confirmed` or `Completed` status and submits no later than three days after the tour ends. Otherwise, make no mutation. |
| BR-148 / MSG158 | Exactly one complaint exists per booking. A duplicate returns the existing complaint reference without creating records or duplicate notification. |
| BR-149 / MSG159 / MSG160 | Evidence is optional; at most five files, each no larger than 10 MB, no more than 30 MB total, and only JPG, PNG, WebP or PDF after server-side content validation. |
| MSG161 | A successful atomic submission is recorded and the owning Tour Operator is notified. |
| MSG127 | If private evidence storage or persistence cannot complete, record no complaint and make the staged evidence unavailable/eligible for cleanup. |

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
- nullable complaint-created `RefundId` and a row-version concurrency token.

`ComplaintEvidence` contains only private storage metadata: complaint FK,
original safe display name, validated content type, byte size, opaque storage
object identifier, checksum and creation time. It never stores a public or
permanent delivery URL. The database enforces one complaint per booking, valid
status/category values, evidence FK/indexes and a positive bounded byte size.

## API contract

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
7. Unit, API authorization/idempotency, private-storage and SQL Server
   migration tests cover normal, boundary, failure and concurrency paths.

## Out of scope

- Complaint list/detail/read links and Tour Operator response (UC-69).
- Traveler partial-proposal acceptance/decline (UC-75).
- Refund confirmation, operator return share or gateway processing (UC-42,
  UC-67).
- Admin/Staff contract dispute handling and public evidence downloads.
