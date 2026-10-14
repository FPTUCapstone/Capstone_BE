# UC-03 Resubmit Tour Operator Application — Backend Specification

## Status and scope

**Revised draft — 2026-10-08.** UC-03 is a shared Web and Mobile use case. This document is the API and business-rule source of truth for both clients. Implementation follows `plans/UC-03-plan.md` on `feature/linhnv-resubmit-tour-operator-application`.

UC-03 lets an authenticated Tour Operator whose application was rejected correct the same application and submit it for review again. It must not create another account or `OperatorProfile`.

## Related behavior

- UC-02 creates the account, profile, and initial documents.
- UC-51 rejection changes both `User.Status` and `OperatorProfile.ApprovalStatus` to `Rejected`, records the rejection metadata, and marks submitted documents `Rejected`.
- UC-50 approval requires a reviewable Business License document whose status is not `Rejected`.
- UC-03 therefore transitions both account and profile back to `PendingApproval` and makes one Business License reviewable again.

**Implementation dependency:** the approved BE UC-02 operator-registration/storage work must be present on the UC-03 baseline. At the time of this document review, the checked-out BE branch did not yet expose the UC-02 `RegisterOperator` feature, so the implementation plan treats synchronization with that dependency as a blocking Task 0 check.

## Locked messages

UC-03 reuses the UC-02 validation messages and introduces only the two lifecycle messages that are missing from the catalog.

| Code | Text | Use |
| --- | --- | --- |
| MSG01 | This field is required. | Required input |
| MSG127 | TripMate is temporarily unable to process your request. Please check your connection and try again. | Safe infrastructure failure |
| MSG157 | Please upload the required business licence document. | No existing or new Business License |
| MSG158 | The uploaded file type is not supported or the file exceeds the size limit. | Invalid upload |
| MSG159 | This business licence number or tax code is already registered. | Identifier conflict |
| **MSG161** | Only rejected applications can be resubmitted. | Invalid application state |
| **MSG162** | Application resubmitted successfully. It is now pending administrator review. | Success |

The conflicting SRS references MSG19/22/23/26 are not reused. Draft MSG140/141/142/144/145 are retired.

## Business rules and decisions

1. **Actor and state.** The caller must be an authenticated `TourOperator`. Both `User.Status` and `OperatorProfile.ApprovalStatus` must be `Rejected`. A missing profile returns `404`; any other state returns `409` with MSG161.
2. **Single application.** Update the existing `User` and `OperatorProfile`; never create a second account or profile.
3. **State transition.** A successful resubmission atomically changes `User.Status` and `OperatorProfile.ApprovalStatus` to `PendingApproval`.
4. **Review metadata.** Current `RejectionReason`, `ReviewedBy`, and `ReviewedAtUtc` are cleared when the application becomes pending. Their old values remain in the audit event's before-state.
5. **Identifiers.** Normalize with `Trim()` before validation. `TaxCode` and `BusinessLicenseNo` must satisfy the same format rules as UC-02 and be unique among other users. Database unique-index violations caused by a race map to MSG159.
6. **Documents.** The existing document status enum remains `Submitted`, `Approved`, `Rejected`; UC-03 does not invent `Superseded` or delete history.
   - If a new Business License is uploaded, retain old rejected rows for history and insert the new row as `Submitted`.
   - If no new Business License is uploaded, the latest existing rejected Business License is changed back to `Submitted` so UC-50 can review it.
   - If neither an existing nor a new Business License exists, return MSG157.
   - Existing rejected supporting documents remain historical. New supporting documents are inserted as `Submitted`.
   - At most five supporting documents may be submitted in the current review cycle. Historical rejected documents do not count toward this limit.
7. **File validation and privacy.** Each file is at most 5 MiB and must be PDF, JPG, or PNG. The backend validates extension, declared MIME, and file signature/content. Files are stored as Cloudinary authenticated/private assets; the database stores an opaque asset reference, not a public URL. Download URLs are short-lived signed URLs returned only after authorization.
8. **Upload cleanup.** New uploads use the durable cleanup reservation/outbox mechanism established by UC-02. A reservation is completed only after the database transaction commits. Failed deletes remain retryable. A worker must re-check the reservation state under the SQL locking protocol before deleting, preventing deletion of a file committed to an `OperatorDocument`.
9. **Concurrency.** The handler takes a database lock or uses an equivalent conditional transition over the current application. Two simultaneous resubmissions produce exactly one success; the loser returns `409` MSG161. Only the winner creates documents and one audit event.
10. **Audit.** Add `AuditActionTypes.OperatorApplicationResubmit` with stored value `ResubmitOperatorApplication`. Record `AffectedEntity = OperatorProfile`, numeric `AffectedEntityId = userId`, the authenticated actor, before-state, and after-state in the same transaction. Before-state contains the rejection reason, reviewer, review time, account/profile states, company fields, and document states.
11. **Resubmission count.** Count successful audit events by `ActionType == AuditActionTypes.OperatorApplicationResubmit` and numeric `AffectedEntityId == userId`; no new counter column is required.

## API contract

### GET `/api/v1/operator/application`

Authorization: `TourOperator`.

Example `200 OK`:

```json
{
  "userId": 12,
  "userStatus": "Rejected",
  "approvalStatus": "Rejected",
  "companyName": "Sapa Trekking Co.",
  "businessLicenseNo": "79-0123/2026/TCDL-GPLHQT",
  "taxCode": "0101234567",
  "businessAddress": "123 Muong Hoa, Sa Pa",
  "contactPerson": "Nguyen Van A",
  "contactPhone": "0987654321",
  "rejectionReason": "The scan is unreadable.",
  "reviewedAtUtc": "2026-10-05T08:30:00Z",
  "resubmissionCount": 0,
  "documents": [
    {
      "documentId": 101,
      "documentType": "BusinessLicense",
      "status": "Rejected",
      "uploadedAtUtc": "2026-10-01T10:00:00Z",
      "downloadUrl": "https://signed-short-lived-url",
      "downloadUrlExpiresAtUtc": "2026-10-08T10:10:00Z"
    }
  ]
}
```

The response never exposes the stored Cloudinary asset reference. The schema does not persist original file names, so `fileName` is not part of the required contract.

Responses: `401` unauthenticated, `403` wrong role, `404` missing application, `503` safe infrastructure failure.

### PUT `/api/v1/operator/application/resubmit`

Authorization: `TourOperator`. Content type: `multipart/form-data`.

| Field | Rule |
| --- | --- |
| `companyName` | required, trimmed, max 200 |
| `businessLicenseNo` | required, trimmed, UC-02 licence format, max 100 |
| `taxCode` | required, trimmed, 10 digits or `10 digits-3 digits`, max 50 |
| `businessAddress` | optional, trimmed, max 300 |
| `contactPerson` | required, trimmed, max 150; updates `User.FullName` |
| `contactPhone` | optional, trimmed, UC-02 phone validation, max 20 |
| `businessLicenseDocument` | optional replacement; PDF/JPG/PNG, max 5 MiB |
| `supportingDocuments` | optional repeated key; at most five current-cycle files, each max 5 MiB |

Example `200 OK`:

```json
{
  "userId": 12,
  "userStatus": "PendingApproval",
  "approvalStatus": "PendingApproval",
  "updatedAtUtc": "2026-10-08T10:00:00Z",
  "messageCode": "MSG162",
  "message": "Application resubmitted successfully. It is now pending administrator review.",
  "resubmissionCount": 1
}
```

Errors:

- `400`: field/file validation (MSG01, MSG157, MSG158).
- `401`: unauthenticated.
- `403`: wrong role.
- `404`: application not found.
- `409`: application not rejected (MSG161) or duplicate identifier (MSG159).
- `503`: safe storage/database failure (MSG127).

## Acceptance criteria

1. Web and Mobile consume the same GET and PUT contracts and receive equivalent outcomes.
2. Only the owner of a rejected Tour Operator application can resubmit it.
3. A success updates both account and profile to `PendingApproval`, clears current review metadata, and preserves the rejected state in one audit event.
4. Exactly one reviewable Business License exists after resubmission, either a new `Submitted` row or the latest retained row reset to `Submitted`.
5. Historical rejected documents and their assets remain available for authorized audit/review; no public Cloudinary URL is returned.
6. Text is normalized before length/format/uniqueness validation. Own unchanged identifiers are accepted; another user's identifiers return `409` MSG159.
7. All database changes and the audit event commit atomically. Fault injection proves rollback leaves account, profile, documents, and audit unchanged.
8. SQL Server concurrency tests with two contexts/requests prove exactly one success, one `409`, one audit event, and one winning document set.
9. If upload succeeds but persistence fails, durable cleanup eventually removes the unreferenced asset; a committed document can never be deleted by the worker.
10. UC-03 does not claim an Admin review-list endpoint. Review-queue visibility remains a separate dependency; the resulting `PendingApproval` state is compatible with that future/list feature.
