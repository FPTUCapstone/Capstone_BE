# UC-69 Respond to Post-Trip Complaint — Backend Specification

## 1. Purpose and scope

This feature lets the owning Tour Operator respond to a Traveler's post-trip complaint. It implements UC-69 only. UC-68 creates the complaint, UC-75 lets the Traveler accept or decline a partial-refund proposal, UC-42 confirms an approved refund, and UC-67 sends the refund through the original payment channel.

The implementation must not introduce a direct payment path. A complaint response only records the commercial decision and, where appropriate, creates the existing refund workflow for UC-42 and UC-67.

## 2. SRS baseline

| SRS requirement | Backend behaviour |
| --- | --- |
| UC-69 | The owning Tour Operator views complaint detail and responds within seven days. |
| BR-150 / MSG162 | A partial amount must not exceed the booking's refundable amount. |
| BR-151 / MSG165 | The response deadline is seven calendar days from complaint submission; a late response makes no change. |
| BR-152 / MSG163 | Reject requires a non-empty trimmed reason. |
| MSG164 | A successful response is recorded and the Traveler is notified. |
| BR-143 | An accepted full refund or accepted partial proposal proceeds through UC-42, then UC-67; no direct gateway request occurs here. |
| BR-174 | Every response and rejected authorization/state-changing attempt is audit logged. |

## 3. Authorization and ownership

- Require an authenticated, active `TourOperator` account.
- Resolve the complaint's booking and tour package server-side. The caller may act only if that package belongs to the caller's operator profile.
- Do not accept `operatorId`, `bookingId`, paid amount, refundable amount, or deadline from the client.
- Return the established forbidden/not-found response shape without revealing another operator's complaint.

## 4. Domain model and state transitions

### 4.1 Required persisted model

`PostTripComplaint` must include at least:

- immutable booking reference, reason category, reason detail and submission timestamp;
- `Status`: `AwaitingOperatorResponse`, `AwaitingTravelerResponse`, `Approved`, `Rejected`, `Declined`, `Expired`;
- `ResponseDeadlineUtc`, derived once as `SubmittedAtUtc + 7 calendar days`;
- nullable `ResponseAction`, `ProposedRefundAmount`, `RejectionReason`, `RespondedAtUtc`, `RespondedByOperatorId`;
- nullable `RefundId`, set only after an accepted decision creates the existing refund workflow;
- optimistic concurrency token.

Evidence remains private. Its records contain storage metadata only; the API returns time-limited read links, never a storage key or a permanent public URL.

### 4.2 Valid actions

| Current status | Action | Result |
| --- | --- | --- |
| `AwaitingOperatorResponse` before deadline | `AcceptFullRefund` | Set `Approved`; create one complaint-sourced refund for the server-calculated refundable amount, pending UC-42. |
| `AwaitingOperatorResponse` before deadline | `ProposePartialRefund` | Validate amount; set `AwaitingTravelerResponse`; do not create a refund yet. |
| `AwaitingOperatorResponse` before deadline | `Reject` | Validate reason; set `Rejected`; no refund is created. |
| any other state or after deadline | any action | Reject without mutation; late pending complaints are atomically marked `Expired`. |

The response command is idempotent by `Idempotency-Key`: an identical replay returns the recorded response; the same key with a different payload returns conflict. A unique database constraint prevents more than one complaint-created refund per complaint.

## 5. API contract

All endpoints are under `/api/v1/operator/post-trip-complaints` and use the platform error envelope.

| Method | Route | Purpose |
| --- | --- | --- |
| `GET` | `/` | Paginated own-operator list; filters: `status`, `page`, `pageSize`. |
| `GET` | `/{complaintId}` | Detail, evidence links, response deadline and permitted actions. |
| `POST` | `/{complaintId}/response` | Record one UC-69 response. Requires `Idempotency-Key`. |

`POST /{complaintId}/response` request:

```json
{
  "action": "AcceptFullRefund | ProposePartialRefund | Reject",
  "proposedRefundAmount": 0,
  "rejectionReason": ""
}
```

Validation rules:

- `AcceptFullRefund`: no client amount is accepted; the service uses its computed `refundableAmount`.
- `ProposePartialRefund`: amount is required, greater than zero, no more than `refundableAmount`, and stored as currency decimal with the project's configured scale.
- `Reject`: `rejectionReason.Trim()` is required and length-limited by a server-side constant.
- Invalid status or expired deadline maps to MSG165; amount excess to MSG162; blank reason to MSG163; generic failure to MSG127.

The detail response exposes `refundableAmount`, `responseDeadline`, `status`, `response`, `evidence[]`, and `availableActions`. It must not expose payment gateway references or bank account data.

## 6. Refund and notification integration

- `AcceptFullRefund` creates an existing `Refund` with source `PostTripComplaint`, amount equal to the server-calculated refundable amount, and the UC-42-compatible initial status.
- It records the complaint-to-refund link in the same database transaction as the response. On failure, neither decision nor refund persists.
- `ProposePartialRefund` creates no refund. UC-75 creates the complaint-sourced refund only when the Traveler accepts the exact stored amount.
- Publish a durable domain/integration event after commit for Traveler notification. Notification failure must be retried and must not duplicate a response or refund.
- Existing UC-42/67 rules decide return share, reconciliation and payment-gateway execution. UC-69 never invokes a payment provider.

## 7. Persistence and migration

Use explicit, forward-only SQL migrations and schema-qualified table names. Include:

1. `booking.PostTripComplaints` with `BookingId` unique (one complaint per booking), foreign keys, status/action checks, deadline/index fields and row version.
2. `booking.ComplaintEvidence` with complaint FK and private object metadata.
3. nullable `ComplaintId`/source discriminator on the existing refund model, with a filtered unique index enforcing one refund per approved complaint.
4. audit/event/outbox records following current project conventions.

The migration must support fresh schema, upgrade schema and repeated execution. Do not use EF migrations or mutate historical payout/refund rows.

## 8. Acceptance criteria

1. An owning Tour Operator can list and read only its own complaints and receives time-limited evidence links.
2. The three allowed actions have the transitions in section 4 and are atomic and idempotent.
3. A partial proposal over the server-calculated refundable amount is rejected with MSG162.
4. A rejection without a trimmed reason is rejected with MSG163.
5. A response after seven days, or from a non-pending state, is rejected with MSG165 and makes no new refund.
6. Full acceptance creates exactly one UC-42-compatible refund; no gateway call is made by UC-69.
7. All successful and rejected sensitive actions are audit logged; Traveler notification is emitted after a successful commit.
8. API, application unit, SQL Server migration and authorization/ownership integration tests pass.

## 9. Out of scope

- Traveler complaint submission (UC-68).
- Traveler decision on partial proposal (UC-75).
- Staff/Administrator contract-dispute resolution after rejection, expiry or decline.
- Changes to cancellation policy, payout calculation or payment-gateway implementation.
