# UC-50 Approve Tour Operator Application

## Status

Ready for implementation planning — all open decisions resolved.

## Scope

This task covers the Administrator review detail surface and the two available actions for a Tour Operator application:

- Read one application and its business/legal information for review.
- Approve a pending application.
- Reject a pending application with a required rejection reason.

The task does not include the application list, search, filtering, pagination, a separate Admin dashboard, or the email delivery transport layer itself. The detail endpoint is addressed by application ID/user ID supplied by the caller or a future list/view feature.

UC-51 is the owning use case for rejection behavior. The Reject endpoint stub is included here only as a UI dependency to allow the review surface to compile; its full business logic is implemented by the UC-51 task.

## Actor

- Administrator

## Preconditions

- The caller is authenticated.
- The caller has the `Administrator` role.
- The target account has role `TourOperator`.
- The target account has an associated `OperatorProfile`.

## Detail Endpoint

```http
GET /api/v1/admin/tour-operator-applications/{userId}
```

### Detail response

The response must expose only data required for Administrator verification:

```json
{
  "userId": 123,
  "role": "TourOperator",
  "accountStatus": "PendingApproval",
  "applicationStatus": "PendingApproval",
  "companyName": "Example Travel Co.",
  "taxCode": "TAX-001",
  "businessLicenseNumber": "LICENSE-001",
  "contactPhone": "0900000000",
  "contactAddress": "Da Nang",
  "documents": [
    {
      "documentId": 456,
      "documentType": "BusinessLicense",
      "fileUrl": "https://...",
      "status": "Submitted",
      "uploadedAt": "2026-09-08T00:00:00Z"
    }
  ],
  "reviewedBy": null,
  "reviewedAt": null,
  "rejectionReason": null
}
```

The API must not expose password hashes, refresh tokens, or unrelated account data.

## Approve Action

```http
POST /api/v1/admin/tour-operator-applications/{userId}/approve
```

### Success behavior

When the application is valid and still pending, the system must persist all of the following in a single `SaveChangesAsync` call:

- Change `Users.status` from `PendingApproval` to `Active`.
- Change `OperatorProfiles.approval_status` from `PendingApproval` to `Approved`.
- Preserve `Users.role` as `TourOperator`.
- Set `OperatorProfiles.reviewed_by` to the current Administrator ID.
- Set `OperatorProfiles.reviewed_at` to the current UTC timestamp.
- Clear any stale `rejection_reason`.
- Change all `OperatorDocuments.status` records for this operator from `Submitted` to `Approved`.
- Insert one `AuditLogs` row with `action_type = 'ApproveOperatorApplication'`, `affected_entity = 'OperatorProfile'`, `affected_entity_id = userId`, `actor_user_id` = current Administrator ID, `before_data` = JSON snapshot of previous statuses, `after_data` = JSON snapshot of new statuses.
- Insert one `Notifications` row targeting the operator's account with `channel = 'Email'`, `type = 'OperatorApplicationApproved'`, `status = 'Pending'`. The email body must include the company name and communicate that the account is now active. Actual email delivery is handled by a background service outside this handler; a delivery failure must not roll back the approval.
- Return the approved account/application status and review timestamp.

The Administrator-facing success message is `MSG114`: `Tour Operator "{Company_Name}" approved. Account activated.`

### Approval validation

The operation must fail when:

- The caller is not an Administrator.
- The target user does not exist.
- The target user is not a Tour Operator.
- The OperatorProfile does not exist.
- The account or application is not in `PendingApproval`.
- Both mandatory documents are not present or have status `Rejected`. The two mandatory documents are `BusinessLicense` and `TaxCode`. The document type previously labelled `TaxCertificate` is not used; the correct type value in `dbo.OperatorDocuments` is `TaxCode`.
- Required profile fields are missing, including Company Name, tax code value, or business licence number.

## Reject Action

```http
POST /api/v1/admin/tour-operator-applications/{userId}/reject
```

### Request

```json
{
  "reason": "The submitted license information could not be verified."
}
```

### Success behavior

UC-51 owns the rejection business logic. This endpoint is a stub that allows the review UI surface to compile and submit. The handler must return a `501 Not Implemented` or delegate to the UC-51 command when that task is merged. No business state must be changed by this stub.

The Administrator-facing confirmation message after rejection is `MSG116`: `Application rejected. Notification sent to operator.`

## Response and error contract

Use the existing `Result`/`Result<T>` pattern and the controller's standard failure handling. Do not introduce a synthetic response envelope.

Suggested domain/application error codes:

- `admin.tour_operator_application_not_found`
- `admin.tour_operator_application_wrong_role`
- `admin.tour_operator_application_not_pending`
- `admin.tour_operator_application_incomplete`
- `admin.tour_operator_application_document_invalid`
- `admin.tour_operator_application_rejection_reason_required`
- `admin.tour_operator_application_forbidden`

Expected HTTP mapping:

- `401 Unauthorized`: no authenticated caller.
- `403 Forbidden`: caller is not an Administrator.
- `404 Not Found`: target application/account does not exist.
- `409 Conflict`: application is no longer pending or another review already completed it.
- `422 Unprocessable Entity`: application data or rejection reason fails business validation.

## Data and consistency rules

- Commands must use tracked entities; do not use `AsNoTracking()` for entities that will be changed.
- All approval writes — `Users`, `OperatorProfiles`, `OperatorDocuments`, `AuditLogs`, and `Notifications` — must be persisted in a single `SaveChangesAsync` call so that a partial failure leaves no inconsistent state.
- The handler must re-check the pending state at command execution time so a second Administrator cannot approve an already processed application.
- All timestamps are UTC and must follow the project's explicit `datetime2` mapping convention.
- Database-First policy applies: no EF migrations. Any schema change must be an explicit SQL script.
- `IApplicationDbContext` must expose `DbSet<AuditLog>` and `DbSet<Notification>` before this handler can be implemented. These sets are added to the interface and `ApplicationDbContext` as part of this task.
- The `Notifications` row created by the approve handler only records the intent (`status = 'Pending'`). A separate background email-delivery service reads `Pending` rows and sends the email to the operator's registered email address using `Users.email`. That delivery service is outside this task's scope.

## Acceptance criteria

1. An Administrator can retrieve one pending Tour Operator application detail without receiving sensitive authentication data.
2. A non-Administrator cannot retrieve or mutate the application through the Admin endpoints.
3. Approving a valid pending application changes `Users.status` to `Active` and `OperatorProfiles.approval_status` to `Approved`.
4. Approval never changes the `TourOperator` role.
5. Approval stores the reviewing Administrator ID and UTC timestamp in `OperatorProfiles`.
6. Approving a valid application changes all associated `OperatorDocuments.status` from `Submitted` to `Approved`.
7. Approval rejects an application where `BusinessLicense` or `TaxCode` document is missing or has status `Rejected`.
8. Approval cannot be repeated after the application leaves `PendingApproval`.
9. Approval writes one `AuditLogs` row with the action, entity, before/after data, and Administrator ID.
10. Approval inserts one `Notifications` row with `channel = 'Email'` and `status = 'Pending'` targeting the operator.
11. All expected business failures use the standard Result failure path.
12. Existing tests remain green.

## Resolved decisions

| # | Decision | Resolution |
|---|---|---|
| 1 | `OperatorDocuments.status` on approval | **Resolved:** all documents for the operator change from `Submitted` to `Approved` in the same transaction. |
| 2 | Audit log | **Resolved (Solution A):** one `AuditLogs` row is written in the same `SaveChangesAsync` call as the approval. |
| 3 | Notification channel | **Resolved:** one `Notifications` row with `channel = 'Email'` and `status = 'Pending'` is inserted on approval. Email delivery to `Users.email` (Gmail or any address) is handled by a background service outside this handler. |
| 4 | Mandatory documents | **Resolved:** `BusinessLicense` and `TaxCode` are both mandatory. The `TaxCertificate` document type name is not used in this project. |
| 5 | Reject scope | **Resolved:** the Reject endpoint is a UI-dependency stub in this task. Full business logic for rejection is owned by UC-51. |
