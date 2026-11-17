# UC-75 Respond to Partial Refund Proposal: Backend Specification

## Status

Ready for implementation. Scope and gap decisions confirmed by the developer on 2026-11-15.

Source of truth: the official SRS (Google Doc `1VwPkqRyC6ZlbXBQC0ztdektt0vAyE3qt`), sections
§2.2.2 UC-75, §3.8.6.3, §3.1.4 non-screen function #16 (Complaint Deadline Job), §3.1.5 entity
#34 PostTripComplaint, screen #97, BR-143, BR-153, MSG126, MSG127, MSG164, MSG165 and
MSG166–MSG168 (MSG166–168 added by this decision record).

## Scope

In scope (only what §3.8.6.3 and job #16 state):

1. A Traveler reads the partial refund proposal of a complaint they own: proposed amount,
   response deadline and status (Data Processing step 1, Normal Flow step 2).
2. The Traveler accepts or declines it once, before the deadline.
   - Accept: complaint becomes `Approved` with the proposed amount. A refund request is created
     for UC-42 (BR-143, §3.8.4.3 trigger 2). No money moves in UC-75.
   - Decline: complaint becomes `Declined`. The outcome is handled under the partnership contract.
     No refund request is created.
   - Both: the Tour Operator is notified (PC-02).
3. The 3-day branch of the Complaint Deadline Job: an unanswered proposal past its deadline becomes
   `Declined`, and both parties are notified (BR-153, job #16).

Out of scope:

| Item | Owner |
|---|---|
| PostTripComplaint entity, table, submission, evidence | UC-68 / TM-222 |
| Proposing the amount, BR-150, 7-day `Expired` branch of job #16 | UC-69 / TM-222 |
| RefundRequest entity, Operator confirmation, return share, Staff reconciliation | UC-42 / TM-88 |
| Paying the Traveler, Refund Progress screen | UC-67 / TM-117 |
| Handling under the partnership contract after `Declined` | Outside the system (SRS defines no screen, actor or data) |
| Counter-offer, changing an answer, deadline extension, audit log | Not in SRS |

## Dependencies

PostTripComplaint (TM-222) and RefundRequest (TM-88) are not on `develop`. UC-75 depends on two
Application ports. Each port has a temporary Infrastructure implementation until its owner merges:

- `IPartialRefundProposalStore`: reads a proposal and applies the conditional status change. The
  temporary implementation finds no complaint, so every endpoint returns 404 and the job finds
  nothing to close.
- `IComplaintRefundRequestCreator`: creates the UC-42 refund request for an accepted proposal. The
  temporary implementation throws, but it cannot be reached while the store finds no complaint.

TM-222 and TM-88 replace these implementations against their entities, inside the caller's
`IApplicationDbContext` transaction. The old `Refund.CreatePending` path must never be used here,
because it pays the Traveler directly and so breaks BR-143.

## Domain

`PostTripComplaintStatus` (SRS §3.8.6.2 PC-01 and §3.8.6.3 PC-01): `AwaitingOperatorResponse`,
`AwaitingTravelerResponse`, `Approved`, `Rejected`, `Expired`, `Declined`. TM-222 must reuse this
enum.

`PartialRefundProposalRules`:

- `ResponseWindow = 72 hours`: BR-153 "3 days" counts calendar time, not working days (G4). UC-69
  sets the deadline with `DeadlineFrom(proposedAtUtc)`.
- `IsOpen(status, deadlineUtc, nowUtc)` is true only when the status is
  `AwaitingTravelerResponse` and `nowUtc < deadlineUtc`. Exactly at the deadline, the proposal is
  closed (G5).

## API

Role `Traveler`. Routes go under `api/v1/traveler/complaints`.

### GET `{complaintId}/partial-refund-proposal`

Returns `200`:

```json
{ "complaintId": 1, "bookingId": 2, "bookingCode": "BK-1", "proposedAmount": 500000,
  "responseDeadlineUtc": "2026-11-09T03:00:00Z", "status": "AwaitingTravelerResponse",
  "canRespond": true }
```

`canRespond` = `IsOpen(...)` evaluated at request time.

### POST `{complaintId}/partial-refund-proposal/response`

Body `{ "decision": "Accept" | "Decline" }`. The SRS input data has no Idempotency-Key. A repeated
call after the first one has been recorded is rejected with MSG165 (G1).

Returns `200`:

```json
{ "complaintId": 1, "status": "Approved", "approvedAmount": 500000,
  "respondedAtUtc": "2026-11-07T03:00:00Z" }
```

`approvedAmount` is null for a decline. The client shows MSG164 on success (G2).

### Errors

| Condition | Code | HTTP | MSG |
|---|---|---|---|
| Not signed in as a Traveler | `complaint.partial_refund.unauthorized` | 401 | — |
| Complaint not found | `complaint.not_found` | 404 | — |
| Signed-in Traveler does not own the booking (1.a2) | `complaint.partial_refund.forbidden` | 403 | MSG126 |
| Status is not `AwaitingTravelerResponse` (G1), or `now >= deadline` before the job has run (1.a1, G5), or a concurrent writer closed it first | `complaint.partial_refund.response_closed` | 409 | MSG165 |
| Unknown `decision` value | model validation | 400 | — |
| Persistence failure (4.a1) | global handler | 5xx | MSG127; the status is not changed |

## Command flow

Everything runs in one `ExecuteInSerializableTransactionAsync`:

1. Load the proposal, then check owner, then check `IsOpen`.
2. `TryCloseAsync(complaintId, Approved | Declined, now)`. This is conditional on the status still
   being `AwaitingTravelerResponse`. A `false` result returns MSG165.
3. Accept only: `IComplaintRefundRequestCreator.CreateForAcceptedComplaintAsync(complaintId,
   bookingId, proposedAmount, now)`. The amount is fixed here and is never recalculated (UC-67).
4. Add the Operator notification: MSG166 on accept, MSG167 on decline.
5. `SaveChangesAsync`.

## Complaint Deadline Job: 3-day branch

`PartialRefundProposalDeadlineProcessor.CloseOverdueAsync(now)` lists proposals with status
`AwaitingTravelerResponse` and `deadline <= now`. For each proposal it runs `TryCloseAsync(Declined)`
and, when the close succeeds, notifies the Traveler and the Operator with MSG168. Each proposal
runs in its own transaction, so one failure does not block the others.

A hosted service runs the processor every `SweepInterval`. The default is 1 hour, because job #16
is hourly.

## Notifications

Channel `Push`, `RelatedEntityType = "PostTripComplaint"`, `RelatedEntityId = complaintId`.

| MSG | Type | Recipient | Content |
|---|---|---|---|
| MSG166 | `PartialRefundProposalAccepted` | Tour Operator | The traveler accepted your partial refund proposal of {Proposed_Amount} VND for booking {Booking_Code}. Please confirm the refund request. |
| MSG167 | `PartialRefundProposalDeclined` | Tour Operator | The traveler declined your partial refund proposal for booking {Booking_Code}. The complaint will be handled under the partnership contract. |
| MSG168 | `PartialRefundProposalExpired` | Traveler and Tour Operator | The partial refund proposal for booking {Booking_Code} received no response within 3 days and has been closed as declined. The complaint will be handled under the partnership contract. |

## Gap decisions (developer, 2026-11-15)

| # | Decision |
|---|---|
| G1 | An already-answered proposal answered again → MSG165, no change (mirrors §3.8.6.2 1.a1) |
| G2 | Success → toast MSG164. The SRS MSG table must add 3.8.6.3 to MSG164 |
| G3 | Notification content uses the unused slots MSG166–MSG168 (Push notification) |
| G4 | Deadline = proposal time + exactly 72 hours |
| G5 | A late answer is rejected at submit time (MSG165), even when the hourly job has not run yet; the job still closes the proposal and sends MSG168 |

## Database

No schema change in UC-75. The columns are owned by TM-222 (PostTripComplaint) and TM-88
(RefundRequest).
